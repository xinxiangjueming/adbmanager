using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace AdbManager;

/// <summary>
/// 纯代码（无 App.xaml）WinUI 3 应用：
/// 必须实现 IXamlMetadataProvider 并转发到框架的 XamlControlsXamlMetaDataProvider，
/// XamlControlsResources 只能在 OnLaunched 中挂载（构造函数中会抛 COMException）。
/// </summary>
public partial class App : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider _metadataProvider = new();

    public static MainWindow MainWindow { get; private set; } = null!;
    public static nint WindowHandle { get; private set; }

    public App()
    {
        UnhandledException += OnUnhandledException;
        Services.StartupLog.Write("app:ctor ok");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services.StartupLog.Write("launched enter");

        try
        {
            // 不在 Application 层冻结 RequestedTheme：它只允许在首窗口创建前设置一次，
            // 之后改不了。若按启动设定冻成浅/深色，之后切「跟随系统」（元素级
            // RequestedTheme = Default）会向上解析回这层冻结值，系统主题切换就永远失效。
            // 初值统一交给 MainWindow.BuildShell 末尾的 ApplyThemeSetting(animate:false)
            // 在元素级完成——窗口 Activate 前生效，无首帧闪烁。
            Services.AppSettings.Load();

            // WinUI 控件主题资源 + miuix 画笔（应用级资源字典）
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Ui.Miuix.Register(Resources);
            Services.StartupLog.Write("app:resources ok");

            MainWindow = new MainWindow();
            WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
            MainWindow.Activate();
            Services.StartupLog.Write("launched ok");
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            throw;
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Services.AppState.Log.Error(Services.LocalizationService.Get("Adb_Err_Unhandled", e.Exception.Message));
        ReportCrash(e.Exception);
        e.Handled = true;
    }

    /// <summary>把启动期 / XAML 线程的致命异常写入磁盘，便于无控制台时诊断。</summary>
    public static void ReportCrash(Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbManager");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch
        {
            // 日志写入失败时静默
        }
    }

    public IXamlType GetXamlType(Type type) => _metadataProvider.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => _metadataProvider.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => _metadataProvider.GetXmlnsDefinitions();
}
