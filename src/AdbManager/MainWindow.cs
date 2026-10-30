using AdbManager.Services;
using AdbManager.Ui;
using AdbManager.Views;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
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
    private ScrollViewer _pageScroll = null!;   // BuildShell 中创建；按页面切换纵向滚动模式
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
    private readonly DispatcherQueueTimer _notifyTimer;   // 通知自动收起的延时器
    private Storyboard? _notifyFade;                      // 通知淡出动画，新通知到达时需停掉
    private AcrylicBackdrop? _acrylic;                    // 微信式实时模糊背景，窗口关闭时释放
    private Grid? _shellHost;                             // 外壳宿主：[实时界面] + [旧画面覆盖层（过渡期）] + [输入拦截层]
    private Grid? _shellRoot;                             // 实时界面根（标题栏 + 导航 + 内容 + 状态栏）
    private Image? _themeSnapshot;                        // 过渡期间盖在最上层的旧画面（抓屏所得，含系统亚克力）
    private Grid? _themeInputBlocker;                     // 过渡期间拦截触摸（裁剪不影响命中测试）
    private DispatcherQueueTimer? _themeTimer;             // 圆形揭示的帧驱动器
    private bool _themeTransitioning;                     // 过渡进行中标记（防重入 / 快速连点取消）
    private static MainWindow? _current;

    /// <summary>成功 / 提示类通知的自动收起延时；错误与警告不自动关闭。</summary>
    private static readonly TimeSpan NotifyAutoCloseDelay = TimeSpan.FromSeconds(4);

    private static readonly (string Key, string Glyph)[] NavItems =
    {
        ("Nav_Devices", "\uE8EA"),
        ("Nav_Mirror", "\uE7F4"),
        ("Nav_Tools", "\uE90F"),
        ("Nav_Info", "\uE9D9"),
        ("Nav_Fastboot", "\uE945"),
        ("Nav_Apps", "\uE7B8"),
        ("Nav_Processes", "\uE768"),
        ("Nav_Files", "\uE8B7"),
        ("Nav_Logs", "\uE8A5"),
        ("Nav_Settings", "\uE713")
    };

    public MainWindow()
    {
        _current = this;
        Title = LocalizationService.Get("App_Title");
        // 亚克力背景：实时模糊窗口后面的桌面（微信侧栏那种效果）；
        // 侧栏与顶栏全透明、内容区半透明层色的搭配保持不变
        _acrylic = new AcrylicBackdrop();
        _acrylic.Attach(this);
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

        _notifyTimer = dispatcher.CreateTimer();
        _notifyTimer.Interval = NotifyAutoCloseDelay;
        _notifyTimer.IsRepeating = false;
        _notifyTimer.Tick += (_, _) => CloseNotify();

        Closed += OnClosed;

        _ = RefreshDevicesQuietlyAsync();
    }

    /// <summary>窗口关闭时清理后台进程：停止 adb server 守护进程并回收遗留录屏进程，
    /// 使关闭软件后设备连接随之中断，不再在后台驻留。</summary>
    private void OnClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        _acrylic?.Dispose();
        _acrylic = null;
        try
        {
            AppState.Adb.ShutdownAsync().Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // 退出阶段静默：即使清理失败也不应阻塞窗口关闭
        }
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

        // Win11 云母风格：根容器与侧栏全透明透出 Mica，内容区叠半透明层色保证正文可读。
        // 侧栏展开/收起动画不同步露底的问题（microsoft-ui-xaml#9370）随之消失——
        // 露出的正是底层云母，与两侧视觉一致，无需再靠同色填充遮盖。
        root.Background = Miuix.Brush("MiuixPageBackground");
        _navigation.Resources["NavigationViewExpandedPaneBackground"] = Miuix.Brush("MiuixPageBackground");
        _navigation.Resources["NavigationViewContentBackground"] = Miuix.Brush("MiuixLayerBackground");

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
        // 填满型页面（FillsViewport）会把纵向滚动切到 Disabled：该方向按视口高度做
        // 有界测量，页面内的 * 行才能吃掉剩余空间；普通页面保持 Auto 原样滚动。
        _pageScroll = new ScrollViewer { Content = _pageHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var contentGrid = new Grid();
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_pageScroll, 0);
        Grid.SetRow(_infoBar, 1);
        _infoBar.Margin = new Thickness(24, 0, 24, 12);
        contentGrid.Children.Add(_pageScroll);
        contentGrid.Children.Add(_infoBar);

        _navigation.Content = contentGrid;
        _navigation.SelectionChanged += OnNavigationSelectionChanged;
        Grid.SetRow(_navigation, 1);
        root.Children.Add(_navigation);

        // 状态栏
        var statusBar = new Border
        {
            Background = Ui.Miuix.Brush("MiuixLayerBackground"),
            BorderBrush = Ui.Miuix.Brush("MiuixCardBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 6, 16, 6)
        };
        statusBar.Child = _statusText;
        Grid.SetRow(statusBar, 2);
        root.Children.Add(statusBar);

        // 外壳宿主：实时界面 root 放进来，过渡期间再在其上临时盖一张「旧画面覆盖层」。
        // 覆盖层是抓屏所得（含系统亚克力），圆形裁剪只让它留在圆内、半径从满帧收缩到 0——
        // 视觉上就是从右下角把旧画面擦掉、露出新主题，全程没有哪块区域抢先变色。
        _shellRoot = root;
        _shellHost = new Grid();
        _shellHost.Children.Add(root);
        Content = _shellHost;

        root.ActualThemeChanged += (_, _) => OnActualThemeChanged(root);
        Miuix.ApplyTheme(root.ActualTheme);
        // 亚克力淋色初值：下面 ApplyThemeSetting(animate:false) 在「跟随系统」下会因
        // RequestedTheme 未变化而提前返回，不初始化的话侧栏会停在控制器默认淋色上。
        // ActualTheme 此刻还没解析出来（Default）就跳过，保持原行为，等首次真实切换再设。
        if (root.ActualTheme != ElementTheme.Default) _acrylic?.SetTheme(root.ActualTheme);
        UpdateTitleBarColors(root.ActualTheme);

        _navigation.SelectedItem = _navigation.MenuItems[0];
        UpdateStatus();
        ApplyThemeSetting(animate: false);
        Services.StartupLog.Write("shell:done");
    }

    /// <summary>按用户设置应用主题（跟随系统 / 浅色 / 深色）。
    /// animate=true（设置页手动切换默认）时播放从右下角圆形揭示的过渡——
    /// 移植自 SportLink 的 ThemeTransition：先抓一张旧画面盖在最上层，再把这张覆盖层
    /// 裁成圆、半径从满帧收缩到 0，旧画面被擦掉的地方就是新主题。启动初值等场景传 animate:false 直切。</summary>
    public void ApplyThemeSetting(bool animate = true)
    {
        if (_shellRoot is not { } root) return;

        var target = AppSettings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        // 上一轮过渡未结束（快速连点）：先干净收尾——撤裁剪、移走旧快照，
        // 否则接下来的切换会作用在被裁掉的画面上。收尾必须在「主题是否变化」判断之前，
        // 否则连点回原主题会提前 return，把未完成的过渡留在后面生效。
        if (_themeTransitioning) EndThemeTransition();

        // 主题未变化（启动初值与设置一致等）直接跳过
        if (root.RequestedTheme == target) return;

        if (!animate)
        {
            ApplyThemeCore(root, target);
            return;
        }

        _ = TransitionThemeAsync(root, target);
    }

    /// <summary>真正生效主题：设置 RequestedTheme 并刷新 miuix 画笔 / 亚克力 / 标题栏。
    /// 过渡期间亚克力可以放心瞬切——此刻整屏仍被旧画面覆盖层盖住，这次变化用户根本看不到。</summary>
    private void ApplyThemeCore(Grid root, ElementTheme target)
    {
        root.RequestedTheme = target;
        Miuix.ApplyTheme(root.ActualTheme);
        _acrylic?.SetTheme(root.ActualTheme);
        UpdateTitleBarColors(root.ActualTheme);
    }

    /// <summary>主题实际变化回调（ActualThemeChanged）。只做画笔刷新。
    /// 「跟随系统」下 ActualTheme 是系统直接翻新的，此刻 ThemeResources 已经换过、旧主题像素
    /// 不复存在，截不到「旧主题快照」⇒ 圆形揭露无从做起，只能即时换装（该事件本身也是变化
    /// 之后才到的）。手动切换由 ApplyThemeSetting 主动驱动，不依赖这里。</summary>
    private void OnActualThemeChanged(Grid root)
    {
        Miuix.ApplyTheme(root.ActualTheme);
        _acrylic?.SetTheme(root.ActualTheme);
        UpdateTitleBarColors(root.ActualTheme);
    }

    /// <summary>圆形揭示过渡（移植自 SportLink 的 ThemeTransition）。
    ///
    /// 覆盖层必须是「用户此刻看到的整屏像素」，并且盖在最上层、只让圆内可见。
    /// 早先的版本是「截 XAML 快照垫在底下 + 裁实时界面」，但本应用页面底色全透明，侧栏 / 页边 /
    /// 顶栏的观感来自窗口背后的系统亚克力——RenderTargetBitmap 截不到系统背景，快照里这些区域
    /// 是空的（alpha = 0），圆外会透出已经切成新主题的实时淋色，圆形还没扫到侧栏就先变色了。
    /// 现在改用 ScreenGrab 抓屏幕 DC（DWM 合成后的最终画面，含亚克力），覆盖层才真正等于旧画面；
    /// 裁剪施加在覆盖层自身（圆内可见），半径从满帧收缩到 0，视觉上就是从右下角擦掉旧画面。
    /// 对齐 SportLink：400ms 线性。差异：WinUI 的几何裁剪是硬边，无 BlurMaskFilter 羽化。</summary>
    private async Task TransitionThemeAsync(Grid root, ElementTheme target)
    {
        var host = _shellHost;
        var w = root.ActualWidth;
        var h = root.ActualHeight;
        // 窗口尚未布局（尺寸 0）→ 直接切换，无动画降级
        if (host is null || w <= 0 || h <= 0)
        {
            ApplyThemeCore(root, target);
            return;
        }

        // 1) 抓「用户此刻看到的画面」，必须含系统亚克力（原因见 ScreenGrab 注释）。
        //    抓不到（窗口被完全遮挡 / 远程会话等）就直切，不做半成品动画。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var pixels = ScreenGrab.CaptureClient(hwnd, out var pxW, out var pxH);
        if (pixels is null)
        {
            ApplyThemeCore(root, target);
            return;
        }

        _themeTransitioning = true;
        try
        {
            var bitmap = new WriteableBitmap(pxW, pxH);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(pixels, 0, pixels.Length);
            }
            bitmap.Invalidate();

            // 2) 盖在最上层。此刻覆盖层与屏幕完全一致，用户看不出任何变化
            var snapshot = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            host.Children.Add(snapshot);
            _themeSnapshot = snapshot;
            host.UpdateLayout();

            // 3) 等它真正上屏。否则下面切主题那一帧覆盖层还没画出来，会闪一下新主题
            await NextRenderTickAsync();
            // 等待期间被取消（快速连点）：后续步骤交给新一轮过渡，这里直接退出
            if (!_themeTransitioning || !ReferenceEquals(_themeSnapshot, snapshot)) return;

            // 4) 生效新主题。亚克力淋色在这里瞬切完全没问题——整屏仍被覆盖层盖住，看不见这次变化
            ApplyThemeCore(root, target);

            // 5) 裁剪只作用在覆盖层自身：圆内保留旧画面。圆心钉在右下角顶点，
            //    半径从 max → 0 线性收缩，旧画面就被从右下角开始擦掉，露出下面的新主题。
            var maxRadius = (float)(Math.Sqrt(w * w + h * h) * 1.05);
            var visual = ElementCompositionPreview.GetElementVisual(snapshot);
            var compositor = visual.Compositor;
            var hole = compositor.CreateEllipseGeometry();
            hole.Center = new System.Numerics.Vector2((float)w, (float)h);
            hole.Radius = new System.Numerics.Vector2(maxRadius, maxRadius);
            visual.Clip = compositor.CreateGeometricClip(hole);

            // 6) 拦截输入：Composition 裁剪不影响 XAML 命中测试，圆外那块旧画面视觉上已消失，
            //    但仍会响应点击，故压一层透明挡板
            var blocker = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            host.Children.Add(blocker);
            _themeInputBlocker = blocker;

            // 7) 60fps 线性收缩
            const double durationMs = 400;
            var sw = Stopwatch.StartNew();
            var timer = DispatcherQueue.CreateTimer();
            _themeTimer = timer;
            timer.IsRepeating = true;
            timer.Interval = TimeSpan.FromMilliseconds(1000.0 / 60);
            timer.Tick += (_, _) =>
            {
                var t = Math.Min(1.0, sw.ElapsedMilliseconds / durationMs);
                var r = maxRadius * (float)(1.0 - t);
                hole.Radius = new System.Numerics.Vector2(r, r);
                if (t >= 1.0) EndThemeTransition();
            };
            timer.Start();
        }
        catch
        {
            // 抓屏 / 渲染异常：降级为直接切换，并清掉可能已插入的覆盖层，不阻塞主题生效
            ApplyThemeCore(root, target);
            EndThemeTransition();
        }
    }

    /// <summary>结束过渡（正常擦完或被下一次切换取消）。先摘掉覆盖层上的裁剪再移除元素，
    /// 避免移除瞬间留下被裁掉的一帧；此刻半径已为 0，画面与实时界面一致，移除无跳变。</summary>
    private void EndThemeTransition()
    {
        _themeTimer?.Stop();
        _themeTimer = null;

        if (_themeSnapshot is { } snapshot)
        {
            try { ElementCompositionPreview.GetElementVisual(snapshot).Clip = null; }
            catch { /* 窗口已关闭时元素视觉可能已失效，忽略 */ }
            _shellHost?.Children.Remove(snapshot);
        }
        _themeSnapshot = null;

        if (_themeInputBlocker is { } blocker) _shellHost?.Children.Remove(blocker);
        _themeInputBlocker = null;

        _themeTransitioning = false;
    }

    /// <summary>等一帧真正渲染（合成器提交过一次）。窗口最小化 / 被遮挡时拿不到渲染回调，
    /// 100ms 兜底放行，避免过渡卡住后一直挂着裁剪。</summary>
    private static async Task NextRenderTickAsync()
    {
        var tcs = new TaskCompletionSource<bool>();

        void OnRendering(object? sender, object e)
        {
            CompositionTarget.Rendering -= OnRendering;
            tcs.TrySetResult(true);
        }

        CompositionTarget.Rendering += OnRendering;
        await Task.WhenAny(tcs.Task, Task.Delay(100));
        CompositionTarget.Rendering -= OnRendering;
        tcs.TrySetResult(false);
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
                "Nav_Processes" => new ProcessesView(),
                "Nav_Files" => new FilesView(),
                "Nav_Logs" => new LogsView(),
                "Nav_Settings" => new SettingsView(),
                _ => new DevicesView()
            };
            _pages[key] = page;
        }

        _pageScroll.VerticalScrollBarVisibility = page.FillsViewport
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
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
        // 后台线程调用时 GetForCurrentThread() 返回 null，回退到窗口所属队列
        var queue = DispatcherQueue.GetForCurrentThread() ?? _current.DispatcherQueue;
        if (queue is { } q && !q.HasThreadAccess)
        {
            q.TryEnqueue(() => Notify(message, severity));
            return;
        }

        _current.ShowNotify(message, severity);
    }

    /// <summary>在 UI 线程上真正渲染一条通知：成功/提示类延时自动收起，错误/警告保持常驻。</summary>
    private void ShowNotify(string message, InfoBarSeverity severity)
    {
        // 新通知到达：取消上一次的收起计划，并撤销可能正在进行的淡出
        _notifyTimer.Stop();
        if (_notifyFade is { } fade)
        {
            fade.Stop();
            _notifyFade = null;
        }
        _infoBar.Opacity = 1;

        _infoBar.Severity = severity;
        _infoBar.Title = severity == InfoBarSeverity.Error
            ? LocalizationService.Get("Msg_Failed")
            : LocalizationService.Get("Msg_Done");
        _infoBar.Message = message;
        _infoBar.IsOpen = true;

        if (severity is InfoBarSeverity.Success or InfoBarSeverity.Informational)
            _notifyTimer.Start();
    }

    /// <summary>淡出并收起当前通知。InfoBar 的 IsOpen=false 自带收起动画，这里先做一层透明度过渡。</summary>
    private void CloseNotify()
    {
        if (!_infoBar.IsOpen) return;

        var fade = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, _infoBar);
        Storyboard.SetTargetProperty(anim, "Opacity");
        fade.Children.Add(anim);
        fade.Completed += (_, _) =>
        {
            _notifyFade = null;
            _infoBar.IsOpen = false;
            _infoBar.Opacity = 1;   // 复位，供下次通知淡入
        };
        _notifyFade = fade;
        fade.Begin();
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

        _shellRoot?.Children.Add(_busyOverlay);
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
