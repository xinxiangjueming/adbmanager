using AdbManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AdbManager.Models;

/// <summary>设备上当前运行中的应用进程（按包名聚合，可含多个子进程）。</summary>
public sealed class ProcessInfo
{
    public string PackageName { get; set; } = "";

    /// <summary>应用显示名（application-label）。未取到时为空，界面回退显示包名。</summary>
    public string Label { get; set; } = "";

    public bool IsSystem { get; set; }

    /// <summary>该应用当前是否处于前台（topResumedActivity / mFocusedActivity）。</summary>
    public bool IsForeground { get; set; }

    /// <summary>该包名下的全部进程 PID（主进程在前，子进程如 :push 等随后）。</summary>
    public List<int> Pids { get; } = new();

    /// <summary>聚合一个进程行到该应用：记录 PID 并累加内存。</summary>
    public void Add(int pid, long rssKb)
    {
        Pids.Add(pid);
        RssKb += rssKb;
    }

    /// <summary>全部进程 RSS 之和（KB）。RSS 为常驻内存，跨进程共享页会重复计入，仅作量级参考。</summary>
    public long RssKb { get; set; }

    private byte[]? _iconBytes;
    private ImageSource? _iconImage;
    private bool _iconDecoded;

    /// <summary>应用图标 PNG 字节（96px）。未取到时为 null，界面显示占位色块。</summary>
    public byte[]? IconBytes
    {
        get => _iconBytes;
        set
        {
            _iconBytes = value;
            _iconImage = null;
            _iconDecoded = false;
        }
    }

    /// <summary>解码结果按实例缓存，避免 ListView 虚拟化滚动时反复解码。</summary>
    public ImageSource? IconImage
    {
        get
        {
            if (_iconDecoded) return _iconImage;
            _iconDecoded = true;
            _iconImage = _iconBytes is { Length: > 0 } bytes ? BitmapImageFromBytes(bytes) : null;
            return _iconImage;
        }
    }

    /// <summary>PNG 字节 → BitmapImage。同步解码，不含 await，可在 UI 线程安全调用。</summary>
    private static ImageSource? BitmapImageFromBytes(byte[] bytes)
    {
        try
        {
            using var memory = new MemoryStream(bytes);
            using var randomAccess = memory.AsRandomAccessStream();
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            image.SetSource(randomAccess);
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>占位图标：取应用名首字母（无名称时取包名首字符）。</summary>
    public string IconPlaceholder =>
        DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

    /// <summary>列表主标题：优先应用名，取不到时用包名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? PackageName : Label;

    /// <summary>应用名与包名不同时才需要第二行展示包名。</summary>
    public bool HasDistinctLabel => !string.IsNullOrWhiteSpace(Label) &&
                                    !string.Equals(Label, PackageName, StringComparison.OrdinalIgnoreCase);

    private static string Get(string key) => LocalizationService.Get(key);

    public string KindText => IsSystem ? Get("Model_PkgSystem") : Get("Model_PkgThirdParty");

    /// <summary>内存显示：1 MB 起以 MB 计，保留一位小数。</summary>
    public string MemoryText => RssKb <= 0 ? "" : string.Format(Get("Proc_Memory"), (RssKb / 1024.0).ToString("0.#"));

    /// <summary>PID 展示：多个子进程用逗号连接。</summary>
    public string PidText => string.Join(", ", Pids);

    /// <summary>副标题前的前台标记，模板中单独渲染成红色。</summary>
    public string ForegroundText => Get("Proc_Foreground");

    /// <summary>前台标记显隐。IsForeground 在绑定 ItemsSource 前赋值，无需 INPC。</summary>
    public Visibility ForegroundVisible => IsForeground ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>列表次行：包名 · 类型 · PID · 内存（前台标记独立于本串，单独标红）。</summary>
    public string SubtitleText
    {
        get
        {
            var parts = new List<string>();
            if (HasDistinctLabel) parts.Add(PackageName);
            parts.Add(KindText);
            if (Pids.Count > 0) parts.Add("PID " + PidText);
            if (MemoryText.Length > 0) parts.Add(MemoryText);
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => $"{DisplayName}   [{KindText} / PID {PidText}]";
}
