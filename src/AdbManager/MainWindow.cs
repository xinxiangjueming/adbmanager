using AdbManager.Services;
using AdbManager.Ui;
using AdbManager.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace AdbManager;

public sealed partial class MainWindow : Window
{
    private readonly NavigationView _navigation = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false,
        OpenPaneLength = 200
    };

    private readonly Grid _pageHost = new();
    private readonly InfoBar _infoBar = new()
    {
        IsOpen = false,
        IsClosable = true,
        CornerRadius = new CornerRadius(16),
        Margin = new Thickness(0, 12, 0, 0)
    };
    private readonly TextBlock _statusText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, PageBase> _pages = new();
    private readonly DispatcherQueueTimer _refreshTimer;
    private static MainWindow? _current;

    private static readonly (string Key, string Glyph)[] NavItems =
    {
        ("Nav_Devices", "\uE8EA"),
        ("Nav_Mirror", "\uE7F4"),
        ("Nav_Tools", "\uE90F"),
        ("Nav_Info", "\uE9D9"),
        ("Nav_Fastboot", "\uE945"),
        ("Nav_Apps", "\uE7B8"),
        ("Nav_Files", "\uE8B7"),
        ("Nav_Logs", "\uE8A5"),
        ("Nav_Settings", "\uE713")
    };

    public MainWindow()
    {
        _current = this;
        Title = LocalizationService.Get("App_Title");
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        AppState.Log.Attach(dispatcher);
        AppState.AttachDispatcher(dispatcher);

        TryApplyWindowIcon();

        try
        {
            BuildShell();
        }
        catch (Exception ex)
        {
            App.ReportCrash(ex);
            throw;
        }

        AppState.Adb.Initialize();
        AppState.Log.Info(LocalizationService.Get("Adb_SourceLog", AppState.Adb.SourceText, AppState.Adb.AdbPath));

        _refreshTimer = dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(3);
        _refreshTimer.Tick += async (_, _) => await RefreshDevicesQuietlyAsync();
        _refreshTimer.Start();

        _ = RefreshDevicesQuietlyAsync();
    }

    /// <summary>把内置的应用图标释放到本地并应用到窗口 / 任务栏。</summary>
    private void TryApplyWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AdbManager", "app.ico");

            if (!File.Exists(iconPath))
            {
                using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("app.ico");
                if (stream is null) return;

                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                using var file = File.Create(iconPath);
                stream.CopyTo(file);
            }

            AppWindow.SetIcon(iconPath);
        }
        catch (Exception ex)
        {
            Services.StartupLog.Write("icon failed: " + ex.Message);
        }
    }

    private void BuildShell()
    {
        var root = new Grid();

        // miuix 画笔已在 App.OnLaunched 注册到应用级资源字典，此处直接使用
        Services.StartupLog.Write("shell:resources ok");

        // 统一窗格/内容/窗体底色：修复 NavigationView 左栏展开/收起时窗格与内容宽度动画
        // 不同步导致窗口底色短暂露出（microsoft-ui-xaml#9370，官方不修）——闪烁区被同色填充后不可见
        root.Background = Miuix.Brush("MiuixPageBackground");
        _navigation.Resources["NavigationViewExpandedPaneBackground"] = Miuix.Brush("MiuixPageBackground");
        _navigation.Resources["NavigationViewContentBackground"] = Miuix.Brush("MiuixPageBackground");

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题栏
        var titleBar = new Grid
        {
            Height = 36,
            Padding = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        titleBar.Children.Add(new TextBlock
        {
            Text = LocalizationService.Get("App_Title"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Ui.Miuix.Brush("MiuixTextSecondary")
        });
        SetTitleBar(titleBar);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);
        Services.StartupLog.Write("shell:titlebar ok");

        // 导航 + 内容
        foreach (var (key, glyph) in NavItems)
        {
            var item = new NavigationViewItem
            {
                Content = LocalizationService.Get(key),
                Tag = key,
                Icon = new FontIcon { Glyph = glyph, FontSize = 16 }
            };
            _navigation.MenuItems.Add(item);
        }

        _pageHost.Padding = new Thickness(24, 8, 24, 16);
        // 页面切换入场动画（淡入 + 上滑）
        _pageHost.Transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromVerticalOffset = 28, IsStaggeringEnabled = false }
        };
        var scroll = new ScrollViewer { Content = _pageHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var contentGrid = new Grid();
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(scroll, 0);
        Grid.SetRow(_infoBar, 1);
        _infoBar.Margin = new Thickness(24, 0, 24, 12);
        contentGrid.Children.Add(scroll);
        contentGrid.Children.Add(_infoBar);

        _navigation.Content = contentGrid;
        _navigation.SelectionChanged += OnNavigationSelectionChanged;
        Grid.SetRow(_navigation, 1);
        root.Children.Add(_navigation);

        // 状态栏
        var statusBar = new Border
        {
            Background = Ui.Miuix.Brush("MiuixCardBackground"),
            BorderBrush = Ui.Miuix.Brush("MiuixCardBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 6, 16, 6)
        };
        statusBar.Child = _statusText;
        Grid.SetRow(statusBar, 2);
        root.Children.Add(statusBar);

        Content = root;
        root.ActualThemeChanged += (_, _) =>
        {
            Miuix.ApplyTheme(root.ActualTheme);
            UpdateTitleBarColors(root.ActualTheme);
        };
        Miuix.ApplyTheme(root.ActualTheme);
        UpdateTitleBarColors(root.ActualTheme);

        _navigation.SelectedItem = _navigation.MenuItems[0];
        UpdateStatus();
        ApplyThemeSetting();
        Services.StartupLog.Write("shell:done");
    }

    /// <summary>按用户设置应用主题（跟随系统 / 浅色 / 深色），可实时切换。</summary>
    public void ApplyThemeSetting()
    {
        if (Content is not FrameworkElement root) return;

        root.RequestedTheme = AppSettings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        Miuix.ApplyTheme(root.ActualTheme);
        UpdateTitleBarColors(root.ActualTheme);
    }

    private void UpdateTitleBarColors(ElementTheme theme)
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = theme == ElementTheme.Dark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = Colors.Gray;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            ShowPage(args);
        }
        catch (Exception ex)
        {
            App.ReportCrash(ex);
        }
    }

    private void ShowPage(NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string key) return;

        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "Nav_Devices" => new DevicesView(),
                "Nav_Mirror" => new MirrorView(),
                "Nav_Tools" => new ToolsView(),
                "Nav_Info" => new InfoView(),
                "Nav_Fastboot" => new FastbootView(),
                "Nav_Apps" => new AppsView(),
                "Nav_Files" => new FilesView(),
                "Nav_Logs" => new LogsView(),
                "Nav_Settings" => new SettingsView(),
                _ => new DevicesView()
            };
            _pages[key] = page;
        }

        _pageHost.Children.Clear();
        _pageHost.Children.Add(page);
        Services.StartupLog.Write("page:" + key + " shown");
        _ = page.OnShownAsync();
    }

    private async Task RefreshDevicesQuietlyAsync()
    {
        await AppState.RefreshDevicesAsync();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var count = AppState.Devices.Count;
        var current = AppState.CurrentDevice;
        var text = $"{AppState.Adb.SourceText} · {string.Format(LocalizationService.Get("Status_DeviceCount"), count)}";
        if (current is not null)
            text += " · " + string.Format(LocalizationService.Get("Status_CurrentDevice"), current.DisplayName, current.Serial);
        _statusText.Text = text;
        _statusText.Foreground = Miuix.Brush("MiuixTextSecondary");
    }

    public static void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (_current is null) return;
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is { } q && !q.HasThreadAccess)
        {
            q.TryEnqueue(() => Notify(message, severity));
            return;
        }

        _current._infoBar.Severity = severity;
        _current._infoBar.Title = severity == InfoBarSeverity.Error
            ? LocalizationService.Get("Msg_Failed")
            : LocalizationService.Get("Msg_Done");
        _current._infoBar.Message = message;
        _current._infoBar.IsOpen = true;
    }

    // ---------------- 长操作进度覆盖层 ----------------

    private readonly TextBlock _busyText = new()
    {
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 260
    };
    private Grid? _busyOverlay;
    private int _busyCount;

    /// <summary>在覆盖层中执行一个长操作：淡入加载动画，结束后淡出。返回操作结果。</summary>
    public static async Task<T> RunBusyAsync<T>(string message, Func<Task<T>> work)
    {
        var window = _current;
        if (window is null) return await work();

        window.ShowBusy(message);
        try { return await work(); }
        finally { window.HideBusy(); }
    }

    /// <summary>在覆盖层中执行一个长操作（无返回值）。</summary>
    public static async Task RunBusyAsync(string message, Func<Task> work)
    {
        var window = _current;
        if (window is null) { await work(); return; }

        window.ShowBusy(message);
        try { await work(); }
        finally { window.HideBusy(); }
    }

    private void ShowBusy(string message)
    {
        var queue = DispatcherQueue;
        if (queue is { } q && !q.HasThreadAccess) { q.TryEnqueue(() => ShowBusy(message)); return; }

        EnsureBusyOverlay();
        _busyText.Text = message;
        _busyCount++;
        if (_busyCount > 1) return;

        _busyOverlay!.Opacity = 0;
        _busyOverlay.Visibility = Visibility.Visible;
        BeginFade(_busyOverlay, 1, TimeSpan.FromMilliseconds(180));
    }

    private void HideBusy()
    {
        var queue = DispatcherQueue;
        if (queue is { } q && !q.HasThreadAccess) { q.TryEnqueue(HideBusy); return; }

        _busyCount = Math.Max(0, _busyCount - 1);
        if (_busyCount > 0 || _busyOverlay is not { } overlay) return;

        BeginFade(overlay, 0, TimeSpan.FromMilliseconds(140),
            onCompleted: () => overlay.Visibility = Visibility.Collapsed);
    }

    private void EnsureBusyOverlay()
    {
        if (_busyOverlay is not null) return;

        var panel = new StackPanel
        {
            Spacing = 14,
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(_busyText);

        var card = Miuix.Card(panel, padding: 24);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;

        _busyOverlay = new Grid
        {
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromArgb(102, 0, 0, 0))
        };
        _busyOverlay.Children.Add(card);
        Grid.SetRowSpan(_busyOverlay, 3);

        if (Content is Grid root) root.Children.Add(_busyOverlay);
    }

    /// <summary>淡入淡出动画（透明度）。</summary>
    private static void BeginFade(UIElement target, double to, TimeSpan duration, Action? onCompleted = null)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (onCompleted is not null) storyboard.Completed += (_, _) => onCompleted();
        storyboard.Begin();
    }
}
