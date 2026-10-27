using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace AdbManager.Ui;

/// <summary>
/// 微信式实时亚克力背景：真正模糊窗口后面的桌面与其他窗口（Mica 只是静态壁纸染色，透不出细节）。
/// 染色浓度自定义为「壁纸颜色清晰可见」档；系统关闭透明效果 / 省电模式时回退到 miuix 纯色。
/// 窗口失焦时保持模糊（IsActive 常开），与微信行为一致。
/// </summary>
public sealed class AcrylicBackdrop : IDisposable
{
    private readonly DesktopAcrylicController _controller = new();
    private readonly SystemBackdropConfiguration _configuration = new() { IsInputActive = true };

    /// <summary>一组染色参数。界面里的页面底色是全透明的（alpha=0），侧栏 / 页边 / 顶栏的观感
    /// 完全由这层淋色决定。</summary>
    public readonly record struct ThemeState(Color Tint, float TintOpacity, Color Fallback);

    /// <summary>挂到窗口。返回 false 表示系统不支持（如 Win10），窗口保持纯色。</summary>
    public bool Attach(Window window)
    {
        if (!DesktopAcrylicController.IsSupported()) return false;
        _controller.AddSystemBackdropTarget((ICompositionSupportsSystemBackdrop)window);
        _controller.SetSystemBackdropConfiguration(_configuration);
        return true;
    }

    /// <summary>立即套用某个主题的淋色。
    /// 深浅色切换时这里可以放心瞬切：过渡期间整屏被抓屏得到的旧画面覆盖层盖着，
    /// 圆外看到的也是快照里的旧淋色像素，所以不会出现「圆形还没扫到、侧栏先变色」。</summary>
    public void SetTheme(ElementTheme theme)
    {
        _configuration.Theme = theme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        ApplyState(StateFor(theme));
    }

    public static ThemeState StateFor(ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        return new ThemeState(
            dark ? Color.FromArgb(255, 22, 22, 24) : Colors.White,
            dark ? 0.55f : 0.45f,
            dark ? Color.FromArgb(255, 16, 16, 18) : Color.FromArgb(255, 244, 245, 247));
    }

    private void ApplyState(in ThemeState s)
    {
        _controller.TintColor = s.Tint;
        _controller.TintOpacity = s.TintOpacity;
        _controller.FallbackColor = s.Fallback;
    }

    public void Dispose() => _controller.Dispose();
}
