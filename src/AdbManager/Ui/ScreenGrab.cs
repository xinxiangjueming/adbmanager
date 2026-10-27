using System.Runtime.InteropServices;

namespace AdbManager.Ui;

/// <summary>
/// 从屏幕 DC 直接抓取窗口客户区的真实像素（含系统背景）。
///
/// 为什么不能用 <c>RenderTargetBitmap</c>：本应用的页面底色是全透明的（alpha = 0），
/// 侧栏 / 页边 / 顶栏的观感完全来自窗口背后的系统亚克力（<see cref="AcrylicBackdrop"/> 的
/// DesktopAcrylicController），而 RenderTargetBitmap 只渲染 XAML 视觉树 —— 那些区域截出来
/// 是空的，alpha 全是 0。拿它当「旧主题快照」用时，透明处会把已经切成新主题的实时淋色透出来，
/// 于是圆形揭示还没扫到，侧栏就先变色了。
///
/// 屏幕 DC 是 DWM 合成后的最终画面，亚克力、圆角、窗口内容一并包含，抓到的就是
/// 「用户此刻眼睛看到的」那一帧，所以它能当完美的旧主题快照。
/// </summary>
internal static class ScreenGrab
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    /// <summary>抓取窗口客户区像素，返回 BGRA32（自上而下）字节数组；失败返回 null。
    /// 尺寸是物理像素，与 DIP 的比例由调用方按 Stretch 拉伸即可。</summary>
    public static byte[]? CaptureClient(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (hwnd == IntPtr.Zero) return null;
        if (!GetClientRect(hwnd, out var rc)) return null;

        var cx = rc.Right - rc.Left;
        var cy = rc.Bottom - rc.Top;
        if (cx <= 0 || cy <= 0) return null;

        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return null;

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero, oldObj = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = cx,
                    // 负高度 = 自上而下的行序，省掉一次整幅翻转
                    biHeight = -cy,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;

            oldObj = SelectObject(memDc, dib);
            // CAPTUREBLT：把分层窗口（含 DWM 合成结果）一起抓进来
            if (!BitBlt(memDc, 0, 0, cx, cy, screenDc, origin.X, origin.Y, SRCCOPY | CAPTUREBLT))
                return null;

            var len = cx * cy * 4;
            var bytes = new byte[len];
            Marshal.Copy(bits, bytes, 0, len);

            // GDI 的 32bpp BI_RGB 不定义 alpha 字节；WriteableBitmap 要求预乘 alpha，
            // 统一置为不透明即可（屏幕像素本来就没有透明区域）
            for (var i = 3; i < len; i += 4) bytes[i] = 255;

            width = cx;
            height = cy;
            return bytes;
        }
        finally
        {
            if (memDc != IntPtr.Zero && oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

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
        public int bmiColors;   // 一个 RGBQUAD 占位，32bpp 用不到调色板
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
}
