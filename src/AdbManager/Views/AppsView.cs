using AdbManager.Models;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AdbManager.Views;

public sealed class AppsView : PageBase
{
    private readonly TextBox _search = Miuix.Input("");
    private readonly ComboBox _filter = new() { MinWidth = 140 };
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        // 应用数量可达数百个，靠 ListView 虚拟化承载，不改成 StackPanel。
        // 高度只做上限封顶：内容少时按内容收缩，避免把下方操作卡挤出首屏。
        // 180~368 对应「列表卡」整体 262~450（卡片 padding 32 + 搜索行 40 + 间距 10 = 82）。
        MinHeight = 180,
        MaxHeight = 368
    };

    private readonly List<PackageInfo> _packages = new();
    /// <summary>已加载列表对应的设备序列号；为空表示还没加载过。</summary>
    private string _loadedSerial = "";
    /// <summary>设备掉线后置为 true，重新连上（同序列号）时也要重新加载。</summary>
    private bool _stale = true;
    /// <summary>加载进行中标志：轮询心跳每 3 秒一次，防止上一次还没跑完就再次进入。</summary>
    private bool _loading;

    public AppsView()
    {
        // 列表增删/筛选时的进出动画
        _list.ItemContainerTransitions = new TransitionCollection { new AddDeleteThemeTransition() };
        _list.ItemTemplate = BuildItemTemplate();
        Content = Build();
        AppState.CurrentDeviceChanged += OnCurrentDeviceChanged;
        // 兜底心跳：DevicesChanged 每轮轮询都会触发，覆盖「同一设备状态变化但
        // CurrentDevice 引用未变」这类 CurrentDeviceChanged 不发的场景。
        AppState.DevicesChanged += OnDevicesChanged;
    }

    /// <summary>设备变化（含首次连上、切换设备）时自动加载应用列表，无需手动点刷新。</summary>
    private void OnCurrentDeviceChanged(AdbDevice? device)
    {
        var queue = DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Apply()
        {
            // 设备消失或掉线：标记待刷新，保留现有列表不闪空
            if (device is null || !device.IsOnline) { _stale = true; return; }
            if (_stale || device.Serial != _loadedSerial) _ = LoadAsync();
        }

        if (queue is { } q && !q.HasThreadAccess) q.TryEnqueue(Apply);
        else Apply();
    }

    /// <summary>轮询心跳：只在本页确实落后于当前设备时才真正加载，其余情况直接返回。</summary>
    private void OnDevicesChanged()
    {
        var device = AppState.CurrentDevice;
        if (device is not { IsOnline: true }) { _stale = true; return; }
        if (_loading) return;
        if (_stale || device.Serial != _loadedSerial) _ = LoadAsync();
    }

    /// <summary>
    /// 列表行模板：图标 32px + 应用名（拿不到时回退包名）+ 包名/类型/状态。
    /// 图标经 IconBytesConverter 从 PNG 字节转 ImageBitmap；无图标时显示首字母占位。
    /// 纯代码构建 UI 时 DataTemplate 只能通过 XamlReader 载入（LoadContent 是方法不可赋值）。
    /// 颜色用 ThemeResource，随深浅色主题自动切换。
    /// </summary>
    private static DataTemplate BuildItemTemplate()
    {
        const string xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid Margin="2,6,2,6">
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="Auto" />
                  <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <Border Width="32" Height="32" CornerRadius="8"
                        Background="{ThemeResource MiuixCardBackground}"
                        HorizontalAlignment="Left" VerticalAlignment="Top">
                  <Grid>
                    <TextBlock Text="{Binding IconPlaceholder}"
                               FontSize="14"
                               HorizontalAlignment="Center" VerticalAlignment="Center"
                               Foreground="{ThemeResource MiuixTextSecondary}" />
                    <Image Source="{Binding IconImage}" Width="32" Height="32"
                           HorizontalAlignment="Center" VerticalAlignment="Center" />
                  </Grid>
                </Border>
                <StackPanel Grid.Column="1" Spacing="2" Margin="10,0,0,0" VerticalAlignment="Top">
                  <TextBlock Text="{Binding DisplayName}"
                             FontSize="14"
                             TextWrapping="NoWrap"
                             TextTrimming="CharacterEllipsis"
                             Foreground="{ThemeResource MiuixTextPrimary}" />
                  <TextBlock Text="{Binding SubtitleText}"
                             FontSize="12"
                             TextWrapping="NoWrap"
                             TextTrimming="CharacterEllipsis"
                             Foreground="{ThemeResource MiuixTextSecondary}" />
                </StackPanel>
              </Grid>
            </DataTemplate>
            """;

        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    public override async Task OnShownAsync()
    {
        _search.PlaceholderText = L("Apps_Search");
        if (_filter.Items.Count == 0)
        {
            _filter.Items.Add(L("Apps_ThirdParty"));
            _filter.Items.Add(L("Apps_System"));
            _filter.Items.Add(L("Apps_Disabled"));
            _filter.SelectedIndex = 0;
            _filter.SelectionChanged += (_, _) => ApplyFilter();
            _search.TextChanged += (_, _) => ApplyFilter();
        }

        // 每次进入页面都校验设备是否换了/曾掉线，避免显示上一台设备的应用
        var device = AppState.CurrentDevice;
        if (device is { IsOnline: true } && (_stale || device.Serial != _loadedSerial)) await LoadAsync();
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        var installApk = Miuix.PrimaryButton(L("Apps_InstallApk"));
        installApk.Click += async (_, _) => await InstallApkAsync();
        var installBundle = Miuix.SecondaryButton(L("Apps_InstallBundle"));
        installBundle.Click += async (_, _) => await InstallBundleAsync();
        var reload = Miuix.SecondaryButton(L("Common_Refresh"));
        reload.Click += async (_, _) => await LoadAsync();

        var topCard = Miuix.Card(new StackPanel { Spacing = 10 });
        ((StackPanel)topCard.Child).Children.Add(Miuix.SectionTitle(L("Apps_Title")));
        ((StackPanel)topCard.Child).Children.Add(Miuix.Horizontal(installApk, installBundle, reload));
        root.Children.Add(topCard);

        var uninstall = Miuix.DangerButton(L("Apps_Uninstall"));
        uninstall.Click += async (_, _) => await UninstallAsync(keepData: false);
        var uninstallKeep = Miuix.SecondaryButton(L("Apps_UninstallKeepData"));
        uninstallKeep.Click += async (_, _) => await UninstallAsync(keepData: true);
        var freeze = Miuix.SecondaryButton(L("Apps_Freeze"));
        freeze.Click += async (_, _) => await FreezeAsync(true);
        var unfreeze = Miuix.SecondaryButton(L("Apps_Unfreeze"));
        unfreeze.Click += async (_, _) => await FreezeAsync(false);
        var forceStop = Miuix.SecondaryButton(L("Apps_ForceStop"));
        forceStop.Click += async (_, _) => await RunOnSelectedAsync((s, p) => AppState.Adb.ForceStopAsync(s, p), L("Msg_Done"));
        var clearData = Miuix.SecondaryButton(L("Apps_ClearData"));
        clearData.Click += async (_, _) => await RunOnSelectedAsync((s, p) => AppState.Adb.ClearDataAsync(s, p), L("Msg_Done"));
        var extractApk = Miuix.PrimaryButton(L("Apps_ExtractApk"));
        extractApk.Click += async (_, _) => await ExtractApkAsync();

        // 操作卡放在列表卡之前：按钮先于列表出现，列表再长也不会把操作区顶出首屏
        var actionCard = Miuix.Card(new StackPanel { Spacing = 10 });
        var actionPanel = (StackPanel)actionCard.Child;
        actionPanel.Children.Add(Miuix.Horizontal(uninstall, freeze, unfreeze, forceStop, clearData));
        // 第二行：保留数据卸载与提取 APK 放一起，避免第一行按钮过多导致横向溢出
        actionPanel.Children.Add(Miuix.Horizontal(uninstallKeep, extractApk));
        root.Children.Add(actionCard);

        var listPanel = new StackPanel { Spacing = 10 };
        listPanel.Children.Add(Miuix.Horizontal(_search, _filter));
        listPanel.Children.Add(_list);
        root.Children.Add(Miuix.Card(listPanel));

        return root;
    }

    /// <summary>提取选中应用的 APK（含拆分包）到电脑。</summary>
    private async Task ExtractApkAsync()
    {
        if (Selected() is not { } package) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;

        var targetDir = System.IO.Path.Combine(folder.Path, package.PackageName);
        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_ExtractingApk"),
            () => AppState.Adb.ExtractApkAsync(device!.Serial, package.PackageName, targetDir));
        MainWindow.Notify(ok ? L("Msg_ExtractOk") + " " + message : message,
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private PackageInfo? Selected() => _list.SelectedItem as PackageInfo;

    private async Task LoadAsync()
    {
        if (_loading) return;
        if (!TryGetDevice(out var device)) return;

        _loading = true;
        try
        {
            _loadedSerial = device!.Serial;
            _stale = false;

            AppState.Log.Info(L("Apps_Loading"));
            _packages.Clear();
            _packages.AddRange(await MainWindow.RunBusyAsync(L("Apps_InfoLoading"),
                () => AppState.Adb.ListPackagesWithIconsAsync(device.Serial)));
            ApplyFilter();
            AppState.Log.Success(L("Apps_Loaded", _packages.Count));
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<PackageInfo> query = _filter.SelectedIndex switch
        {
            1 => _packages.Where(p => p.IsSystem),
            2 => _packages.Where(p => p.IsDisabled),
            _ => _packages.Where(p => !p.IsSystem)
        };

        var keyword = _search.Text.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            // 应用名与包名都可命中：用户既可能搜「微信」，也可能搜「com.tencent.mm」
            query = query.Where(p =>
                p.PackageName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                p.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        _list.ItemsSource = query.ToList();
    }

    private async Task InstallApkAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var file = await Pickers.PickFileAsync(".apk");
        if (file is null) return;

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Installing"),
            () => AppState.Adb.InstallApkAsync(device!.Serial, file.Path));
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }

    private async Task InstallBundleAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var file = await Pickers.PickFileAsync(".apks", ".zip", ".xapk");
        if (file is null) return;

        string directory;
        try
        {
            directory = AdbService.ExtractApks(file.Path);
        }
        catch (Exception ex)
        {
            MainWindow.Notify(L("Msg_Failed") + " " + ex.Message, InfoBarSeverity.Error);
            return;
        }

        var apks = Directory.GetFiles(directory, "*.apk", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetFileName(f).Contains("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        if (apks.Count == 0)
        {
            MainWindow.Notify(L("Msg_Failed") + " " + L("Apps_Err_NoApkInBundle"), InfoBarSeverity.Error);
            return;
        }

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Installing"),
            () => AppState.Adb.InstallMultipleAsync(device!.Serial, apks));
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }

    /// <summary>
    /// 卸载选中应用。<paramref name="keepData"/> 为 true 时走 `pm uninstall -k`，
    /// 保留 /data/data 与 /sdcard/Android/data 下的数据，重装同包名后可恢复。
    /// </summary>
    private async Task UninstallAsync(bool keepData)
    {
        if (Selected() is not { } package) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var dialog = new ContentDialog
        {
            Title = L(keepData ? "Apps_UninstallKeepData" : "Apps_Uninstall"),
            Content = L(keepData ? "Msg_ConfirmUninstallKeep" : "Msg_ConfirmUninstall") + "\n" + package.PackageName,
            PrimaryButtonText = L("Common_Confirm"),
            CloseButtonText = L("Common_Cancel"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => AppState.Adb.UninstallAsync(device!.Serial, package.PackageName, keepData));
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }

    private async Task FreezeAsync(bool frozen)
    {
        if (Selected() is not { } package) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        if (frozen)
        {
            var dialog = new ContentDialog
            {
                Title = L("Apps_Freeze"),
                Content = L("Msg_ConfirmFreeze") + "\n" + package.PackageName,
                PrimaryButtonText = L("Common_Confirm"),
                CloseButtonText = L("Common_Cancel"),
                XamlRoot = App.MainWindow.Content.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => AppState.Adb.SetPackageFrozenAsync(device!.Serial, package.PackageName, frozen));
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }

    private async Task RunOnSelectedAsync(Func<string, string, Task<(bool, string)>> action, string successText)
    {
        if (Selected() is not { } package) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => action(device!.Serial, package.PackageName));
        MainWindow.Notify(ok ? successText : message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }
}
