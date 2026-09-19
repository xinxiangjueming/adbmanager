using System.ComponentModel;
using AdbManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AdbManager.Models;

/// <summary>
/// 一个已安装应用。
///
/// 实现 <see cref="INotifyPropertyChanged"/> 是为了支持「流式补全」：列表先用包名落地渲染，
/// 应用名与图标随后分批回填到**已有实例**上。WinUI 没有 Compose 那样的状态自动重组，
/// 不回填通知 UI 就不会刷新；而重建 ItemsSource 会丢选中项与滚动位置，
/// 并让数百条同时播放入场动画（见 AppsView 的 AddDeleteThemeTransition）。
/// </summary>
public sealed class PackageInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>批量通知受影响的绑定属性。</summary>
    private void Raise(params string[] names)
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        foreach (var name in names) handler(this, new PropertyChangedEventArgs(name));
    }

    public string PackageName { get; set; } = "";

    private string _label = "";

    /// <summary>应用显示名（application-label）。未取到时为空，界面回退显示包名。</summary>
    public string Label
    {
        get => _label;
        set
        {
            if (string.Equals(_label, value, StringComparison.Ordinal)) return;
            _label = value;
            Raise(nameof(Label), nameof(DisplayName), nameof(IconPlaceholder),
                  nameof(HasDistinctLabel), nameof(SubtitleText));
        }
    }

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
            // 解码结果失效必须同时通知 IconImage，否则绑定仍停在旧的 null 上，表现为图标永远不出现
            _iconImage = null;
            _iconDecoded = false;
            Raise(nameof(IconBytes), nameof(IconImage));
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

    private bool _isSystem;

    public bool IsSystem
    {
        get => _isSystem;
        set
        {
            if (_isSystem == value) return;
            _isSystem = value;
            Raise(nameof(IsSystem), nameof(KindText), nameof(SubtitleText));
        }
    }

    private bool _isDisabled;

    public bool IsDisabled
    {
        get => _isDisabled;
        set
        {
            if (_isDisabled == value) return;
            _isDisabled = value;
            Raise(nameof(IsDisabled), nameof(StateText), nameof(SubtitleText));
        }
    }

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
