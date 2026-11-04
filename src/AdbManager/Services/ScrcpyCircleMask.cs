using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AdbManager.Services;

/// <summary>
/// 圆形屏幕遮罩：在投屏窗口（scrcpy）上方叠一层鼠标穿透的分层窗口，
/// 把内切圆以外的区域涂成浅白色，圆内完全透明。
///
/// 圆形手表的画面是方形帧（四角是黑的），直接截图分享时观感像一块方形屏；
/// 圆外涂白后表盘轮廓一眼可辨。
///
/// 绑定方式：遮罩创建为 scrcpy 窗口的<b>归属窗口（owned window）</b>——
///   · 系统保证归属窗口永远位于其 owner 之上，且 z 序跟随 owner 同进退：
///     其它窗口盖住投屏时遮罩一并被盖住，不会出现白角浮在别的窗口上；
///   · 最小化 / 隐藏 owner 时遮罩随之隐藏。
/// 归属窗口不会随 owner 自动移动，用 Win32 线程定时器轮询位置与尺寸
/// （仅 UI 线程消息循环内回调，窗口操作天然同线程），移动/缩放实时跟随；
/// 退出投屏或取消勾选后立即销毁。
/// </summary>
public static class ScrcpyCircleMask
{
    // 圆外填充色：浅白 #F5F5F5（各通道相同，BGRA 顺序无所谓）
    private const byte FillColor = 0xF5;

    private const int TimerId = 0x4D53;          // 随手编号，仅本类使用
    private const uint TimerIntervalMs = 150;

    // scrcpy 的窗口由 SDL 创建（SDL2/SDL3 类名均为 SDL_app*），按前缀匹配，
    // 避免把 scrcpy 偶尔弹出的错误对话框（#32770）当成镜像窗口
    private const string SdlClassPrefix = "SDL";

    private const uint WsPopup = 0x80000000;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint UlwAlpha = 2;
    private const byte AcSrcOver = 0, AcSrcAlpha = 1;
    private const int SwShowNa = 8, SwHide = 0;
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const int ErrorClassAlreadyExists = 1410;

    private static bool _enabled;
    private static bool _timerRunning;
    private static Process? _process;   // 正在跟踪的 scrcpy 进程
    private static IntPtr _target;      // scrcpy 主窗口
    private static IntPtr _overlay;     // 遮罩窗口（owner = scrcpy 窗口）
    private static int _lastW, _lastH;  // 已绘制位图尺寸（仅尺寸变化才重绘）

    private static readonly WndProcDelegate WndProc = DefWndProc;
    private static readonly TimerProcDelegate TimerProcCallback = TimerProcImpl; // 静态字段持有，防委托被 GC 回收
    private static bool _classRegistered;
    private const string WindowClassName = "AdbManager_CircleMask";

    /// <summary>
    /// 刷新遮罩状态（仅 UI 线程调用）。process 为 null / 已退出时彻底停止；
    /// enabled=false 时隐藏遮罩但保留进程跟踪，重新勾选无需重找窗口。
    /// </summary>
    public static void Update(Process? process, bool enabled)
    {
        _enabled = enabled;
        if (_process is { HasExited: true }) _process = null;

        if (process is null || process.HasExited)
        {
            StopTracking();
            return;
        }

        if (!ReferenceEquals(_process, process))
        {
            StopTracking(); // 换了新进程：窗口与遮罩全部重来
            _process = process;
        }

        if (!_timerRunning)
        {
            // 线程定时器：回调随消息泵在 UI 线程执行（见类注释）
            if (SetTimer(IntPtr.Zero, TimerId, TimerIntervalMs, TimerProcCallback)) _timerRunning = true;
        }
        if (!_enabled) DestroyOverlay();
    }

    /// <summary>彻底停止：销毁遮罩并停掉轮询（scrcpy 已退出或换新进程时调用）。</summary>
    private static void StopTracking()
    {
        _process = null;
        _target = IntPtr.Zero;
        DestroyOverlay();
        if (_timerRunning)
        {
            KillTimer(IntPtr.Zero, TimerId);
            _timerRunning = false;
        }
    }

    private static void Tick()
    {
        var process = _process;
        if (process is null || process.HasExited) { StopTracking(); return; }
        if (!_enabled) return;

        // scrcpy 窗口在设备握手成功后才出现，未找到时下个周期重试
        if (_target == IntPtr.Zero || !IsWindow(_target))
        {
            _target = FindScrcpyWindow(process.Id);
            if (_target == IntPtr.Zero) return;
            DestroyOverlay(); // 新窗口：遮罩重建（换 owner）
            _lastW = _lastH = 0;
        }

        // owner 最小化 / 隐藏时遮罩由系统一并隐藏，但兜底检查一次
        if (IsIconic(_target) || !IsWindowVisible(_target)) { HideOverlay(); return; }

        if (!GetClientRect(_target, out var rc)) return;
        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return;
        var origin = new POINT();
        if (!ClientToScreen(_target, ref origin)) return;

        EnsureOverlay(w, h);
        if (_overlay == IntPtr.Zero) return;

        if (w != _lastW || h != _lastH)
        {
            if (!Paint(origin.X, origin.Y, w, h)) return;
            _lastW = w;
            _lastH = h;
        }
        else
        {
            MoveTo(origin.X, origin.Y);
        }
        ShowOverlay();
    }

    /// <summary>查找 scrcpy 主窗口：同进程 + 可见 + 有标题 + SDL 系类名。</summary>
    private static IntPtr FindScrcpyWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out var windowPid) == 0 || windowPid != pid) return true;
            if (!IsWindowVisible(hwnd) || GetWindowTextLengthW(hwnd) == 0) return true;
            var className = GetWindowClassName(hwnd);
            if (!className.StartsWith(SdlClassPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new char[64];
        var len = GetClassNameW(hwnd, buffer, buffer.Length);
        return len <= 0 ? string.Empty : new string(buffer, 0, len);
    }

    private static void EnsureOverlay(int w, int h)
    {
        if (_overlay != IntPtr.Zero) return;

        var instance = GetModuleHandleW(null);
        if (!_classRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
                hInstance = instance,
                lpszClassName = WindowClassName
            };
            if (RegisterClassExW(ref wc) == 0 &&
                Marshal.GetLastWin32Error() != ErrorClassAlreadyExists) return;
            _classRegistered = true;
        }

        // 归属窗口：WS_POPUP 的父句柄参数即 owner —— 跨进程不能建子窗口，但可以跨进程设 owner。
        // 不用 WS_EX_TOPMOST：置顶会让白角浮在遮挡投屏的其它窗口上（旧方案的 bug）。
        _overlay = CreateWindowExW(WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow,
            WindowClassName, "", WsPopup, 0, 0, w, h, _target, IntPtr.Zero, instance, IntPtr.Zero);
    }

    /// <summary>重建遮罩位图并用 UpdateLayeredWindow 定位显示。</summary>
    private static bool Paint(int x, int y, int w, int h)
    {
        var bytes = BuildMask(w, h);

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero, oldObj = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return false;

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // 自上而下行序
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };
            dib = CreateDIBSection(screenDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
            oldObj = SelectObject(memDc, dib);
            Marshal.Copy(bytes, 0, bits, bytes.Length);

            var pptDst = new POINT { X = x, Y = y };
            var size = new SIZE { cx = w, cy = h };
            var ptSrc = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            return UpdateLayeredWindow(_overlay, screenDc, ref pptDst, ref size, memDc, ref ptSrc, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (memDc != IntPtr.Zero && oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>生成遮罩像素（BGRA 预乘）：内切圆以外浅白、圆内全透明，边缘约 1.5px 抗锯齿。</summary>
    private static byte[] BuildMask(int w, int h)
    {
        var bytes = new byte[w * h * 4];
        var cx = w / 2.0;
        var cy = h / 2.0;
        var r = Math.Min(w, h) / 2.0 - 0.5; // 略收半像素，避免啃到画面最外圈
        const double aa = 1.5;

        for (var py = 0; py < h; py++)
        {
            var dy = py + 0.5 - cy;
            var row = py * w * 4;
            for (var px = 0; px < w; px++)
            {
                var dx = px + 0.5 - cx;
                var d = Math.Sqrt(dx * dx + dy * dy);
                var t = (d - r) / aa + 0.5; // r 处半透明，向内外各 aa/2 过渡
                if (t <= 0) continue;       // 圆内：保持全透明

                var a = t >= 1 ? 255 : (int)(t * 255);
                var i = row + px * 4;
                bytes[i] = bytes[i + 1] = bytes[i + 2] =
                    a >= 255 ? FillColor : (byte)(FillColor * a / 255); // 预乘 alpha
                bytes[i + 3] = (byte)a;
            }
        }
        return bytes;
    }

    /// <summary>跟随 owner 移动。必须带 SWP_NOZORDER：不能动 z 序（z 序由归属关系维护）。</summary>
    private static void MoveTo(int x, int y) =>
        SetWindowPos(_overlay, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    private static void ShowOverlay()
    {
        if (!IsWindowVisible(_overlay)) ShowWindow(_overlay, SwShowNa);
    }

    private static void HideOverlay()
    {
        if (_overlay != IntPtr.Zero && IsWindowVisible(_overlay)) ShowWindow(_overlay, SwHide);
    }

    private static void DestroyOverlay()
    {
        if (_overlay == IntPtr.Zero) return;
        DestroyWindow(_overlay);
        _overlay = IntPtr.Zero;
        _lastW = _lastH = 0;
    }

    private static void TimerProcImpl(IntPtr hWnd, uint msg, nuint idEvent, uint dwTime) => Tick();

    private static IntPtr DefWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(hWnd, msg, wParam, lParam);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate void TimerProcDelegate(IntPtr hWnd, uint msg, nuint idEvent, uint dwTime);

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll")] private static extern bool SetTimer(IntPtr hWnd, int nIDEvent, uint uElapse, TimerProcDelegate? lpTimerFunc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hWnd, int uIDEvent);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors; // 一个 RGBQUAD 占位，32bpp 用不到调色板
    }
}
