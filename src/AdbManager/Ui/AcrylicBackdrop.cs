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

    /// <summary>挂到窗口。返回 false 表示系统不支持（如 Win10），窗口保持纯色。</summary>
    public bool Attach(Window window)
    {
        if (!DesktopAcrylicController.IsSupported()) return false;
        _controller.AddSystemBackdropTarget((ICompositionSupportsSystemBackdrop)window);
        _controller.SetSystemBackdropConfiguration(_configuration);
        return true;
    }

    /// <summary>深浅色切换时更新染色浓度；回退色与 miuix 页面底色一致。</summary>
    public void SetTheme(ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        _configuration.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        _controller.TintColor = dark ? Color.FromArgb(255, 22, 22, 24) : Colors.White;
        _controller.TintOpacity = dark ? 0.55f : 0.45f;
        _controller.FallbackColor = dark ? Color.FromArgb(255, 16, 16, 18) : Color.FromArgb(255, 244, 245, 247);
    }

    public void Dispose() => _controller.Dispose();
}
