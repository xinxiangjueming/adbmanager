using AdbManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AdbManager.Models;

public sealed class PackageInfo
{
    public string PackageName { get; set; } = "";

    /// <summary>应用显示名（application-label）。未取到时为空，界面回退显示包名。</summary>
    public string Label { get; set; } = "";

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

    /// <summary>
    /// 列表模板绑定的图标源。
    /// 解码结果按实例缓存——ListView 虚拟化会反复取同一个属性，
    /// 每次重新解码 395 个 PNG 会明显拖慢滚动。
    /// </summary>
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

    public bool IsSystem { get; set; }
    public bool IsDisabled { get; set; }

    /// <summary>列表主标题：优先应用名，取不到时用包名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? PackageName : Label;

    /// <summary>应用名与包名不同时才需要第二行展示包名。</summary>
    public bool HasDistinctLabel => !string.IsNullOrWhiteSpace(Label) &&
                                    !string.Equals(Label, PackageName, StringComparison.OrdinalIgnoreCase);

    private static string Get(string key) => LocalizationService.Get(key);

    public string KindText => IsSystem ? Get("Model_PkgSystem") : Get("Model_PkgThirdParty");
    public string StateText => IsDisabled ? Get("Model_PkgDisabled") : Get("Model_PkgNormal");

    /// <summary>列表次行：已取到应用名时展示「包名 · 类型 · 状态」，否则只展示「类型 · 状态」。</summary>
    public string SubtitleText
    {
        get
        {
            var meta = $"{KindText} · {StateText}";
            return HasDistinctLabel ? $"{PackageName} · {meta}" : meta;
        }
    }

    public override string ToString() => $"{DisplayName}   [{KindText} / {StateText}]";
}
