using System.Threading;
using Microsoft.UI.Dispatching;

namespace AdbManager;

/// <summary>
/// 纯代码入口（无 XAML 时不使用 XAML 编译器生成的 Main）。
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 单文件发布时，WinUI / MRM 依赖该变量定位 resources.pri（官方要求在入口最先设置）
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);

        Services.StartupLog.Write("main:enter");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Services.StartupLog.Write("main:comwrappers ok");

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            // 标准模板生成的 Main 同样做了这一步：保证 await 能正确回到 UI 线程
            var synchronizationContext = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);

            new App();
        });
    }
}
