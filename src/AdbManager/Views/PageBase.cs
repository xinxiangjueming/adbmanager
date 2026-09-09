using System.Diagnostics.CodeAnalysis;
using AdbManager.Models;
using AdbManager.Services;
using Microsoft.UI.Xaml.Controls;

namespace AdbManager.Views;

/// <summary>所有页面的基类：统一提供文案读取与设备校验。</summary>
public abstract class PageBase : UserControl
{
    public virtual Task OnShownAsync() => Task.CompletedTask;

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
