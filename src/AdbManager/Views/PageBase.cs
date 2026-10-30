using System.Diagnostics.CodeAnalysis;
using AdbManager.Models;
using AdbManager.Services;
using Microsoft.UI.Xaml.Controls;

namespace AdbManager.Views;

/// <summary>所有页面的基类：统一提供文案读取与设备校验。</summary>
public abstract class PageBase : UserControl
{
    public virtual Task OnShownAsync() => Task.CompletedTask;

    /// <summary>
    /// true 表示页面按视口高度布局、底部卡片用 * 行吃掉剩余空间（应用/进程/文件页）。
    /// 外壳据此在显示该页时把外层 ScrollViewer 的纵向滚动设为 Disabled：
    /// Disabled 方向按视口高度做有界测量，* 行才能分到剩余空间；
    /// Auto/Visible 时内容按无限高度测量，* 行退化为按内容收缩，永远撑不满。
    /// </summary>
    public virtual bool FillsViewport => false;

    protected static string L(string key) => LocalizationService.Get(key);

    protected static string L(string key, params object[] args) => LocalizationService.Get(key, args);

    protected bool TryGetDevice([NotNullWhen(true)] out AdbDevice? device, bool warn = true)
    {
        device = AppState.CurrentDevice;
        if (device is not null) return true;

        if (warn) MainWindow.Notify(L("Msg_SelectDevice"), InfoBarSeverity.Warning);
        return false;
    }
}
