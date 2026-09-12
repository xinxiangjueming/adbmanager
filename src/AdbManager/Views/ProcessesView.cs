using AdbManager.Models;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AdbManager.Views;

public sealed class ProcessesView : PageBase
{
    private readonly TextBox _search = Miuix.Input("");
    private readonly ComboBox _filter = new() { MinWidth = 140 };
    /// <summary>运存占用进度条：随每次加载/刷新更新，放在刷新按钮右侧。</summary>
    private readonly ProgressBar _memoryBar = new()
    {
        Width = 170,
        Minimum = 0,
        Maximum = 100,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _memoryText = Miuix.Body("--", secondary: true);
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        MinHeight = 180,
        MaxHeight = 368
    };

    private readonly List<ProcessInfo> _apps = new();
    /// <summary>已加载列表对应的设备序列号；为空表示还没加载过。</summary>
    private string _loadedSerial = "";
    /// <summary>设备掉线后置为 true，重新连上（同序列号）时也要重新加载。</summary>
    private bool _stale = true;
    /// <summary>加载进行中标志：轮询心跳每 3 秒一次，防止上一次还没跑完就再次进入。</summary>
    private bool _loading;

    public ProcessesView()
    {
        _list.ItemContainerTransitions = new TransitionCollection { new AddDeleteThemeTransition() };
        _list.ItemTemplate = BuildItemTemplate();
        Content = Build();
        AppState.CurrentDeviceChanged += OnCurrentDeviceChanged;
        // 兜底心跳：与 AppsView 相同，覆盖 CurrentDevice 引用未变但状态变化的场景
        AppState.DevicesChanged += OnDevicesChanged;
    }

    /// <summary>设备变化（含首次连上、切换设备）时自动加载运行列表，无需手动点刷新。</summary>
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
    /// 列表行模板：与应用管理页一致（图标 32px + 应用名 + 包名/类型/PID/内存）。
    /// 纯代码构建 UI 时 DataTemplate 只能通过 XamlReader 载入。
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
                  <Grid>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="Auto" />
                      <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="{Binding ForegroundText}"
                               Visibility="{Binding ForegroundVisible}"
                               FontSize="12"
                               FontWeight="SemiBold"
                               Margin="0,0,6,0"
                               Foreground="{ThemeResource MiuixDanger}" />
                    <TextBlock Grid.Column="1"
                               Text="{Binding SubtitleText}"
                               FontSize="12"
                               TextWrapping="NoWrap"
                               TextTrimming="CharacterEllipsis"
                               Foreground="{ThemeResource MiuixTextSecondary}" />
                  </Grid>
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
            _filter.Items.Add(L("Proc_All"));
            _filter.Items.Add(L("Apps_ThirdParty"));
            _filter.Items.Add(L("Apps_System"));
            _filter.SelectedIndex = 0;
            _filter.SelectionChanged += (_, _) => ApplyFilter();
            _search.TextChanged += (_, _) => ApplyFilter();
        }

        // 每次进入页面都校验设备是否换了/曾掉线，避免显示上一台设备的进程
        var device = AppState.CurrentDevice;
        if (device is { IsOnline: true } && (_stale || device.Serial != _loadedSerial)) await LoadAsync();
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        var reload = Miuix.SecondaryButton(L("Common_Refresh"));
        reload.Click += async (_, _) => await LoadAsync();

        var topCard = Miuix.Card(new StackPanel { Spacing = 10 });
        ((StackPanel)topCard.Child).Children.Add(Miuix.SectionTitle(L("Proc_Title")));

        // 按钮 / 进度条 / 文字三者高度不同，用单行 Grid 统一垂直居中；
        // 垂直 StackPanel 会让子项按各自高度顶对齐，导致文字与进度条错位
        var topRow = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < 3; i++) topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(reload, 0);
        Grid.SetColumn(_memoryBar, 1);
        Grid.SetColumn(_memoryText, 2);
        foreach (FrameworkElement cell in new FrameworkElement[] { reload, _memoryBar, _memoryText })
            cell.VerticalAlignment = VerticalAlignment.Center;
        topRow.Children.Add(reload);
        topRow.Children.Add(_memoryBar);
        topRow.Children.Add(_memoryText);
        ((StackPanel)topCard.Child).Children.Add(topRow);
        root.Children.Add(topCard);

        var forceStop = Miuix.SecondaryButton(L("Apps_ForceStop"));
        forceStop.Click += async (_, _) => await ForceStopAsync();

        var actionCard = Miuix.Card(new StackPanel { Spacing = 10 });
        ((StackPanel)actionCard.Child).Children.Add(Miuix.Horizontal(forceStop));
        root.Children.Add(actionCard);

        var listPanel = new StackPanel { Spacing = 10 };
        listPanel.Children.Add(Miuix.Horizontal(_search, _filter));
        listPanel.Children.Add(_list);
        root.Children.Add(Miuix.Card(listPanel));

        return root;
    }

    private ProcessInfo? Selected() => _list.SelectedItem as ProcessInfo;

    private async Task LoadAsync()
    {
        if (_loading) return;
        if (!TryGetDevice(out var device)) return;

        _loading = true;
        try
        {
            _loadedSerial = device!.Serial;
            _stale = false;

            AppState.Log.Info(L("Proc_Loading"));
            // 进程列表（含名称/图标，最慢）与运存、前台应用三个查询相互独立，并行执行
            var (apps, memory, foreground) = await MainWindow.RunBusyAsync(L("Proc_Loading"), async () =>
            {
                var appsTask = AppState.Adb.ListRunningAppsAsync(device.Serial);
                var memoryTask = AppState.Adb.GetDeviceMemoryAsync(device.Serial);
                var foregroundTask = AppState.Adb.GetForegroundPackageAsync(device.Serial);
                await Task.WhenAll(appsTask, memoryTask, foregroundTask).ConfigureAwait(false);
                return (appsTask.Result, memoryTask.Result, foregroundTask.Result);
            });

            foreach (var app in apps)
                app.IsForeground = foreground is not null &&
                                   string.Equals(app.PackageName, foreground, StringComparison.OrdinalIgnoreCase);

            _apps.Clear();
            _apps.AddRange(apps);
            UpdateMemory(memory);
            ApplyFilter();
            SelectForeground();
            AppState.Log.Success(L("Proc_Loaded", _apps.Count));
        }
        catch (Exception ex)
        {
            MainWindow.Notify(L("Proc_Err_Ps", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>刷新运存进度条与数据文本。</summary>
    private void UpdateMemory(DeviceMemoryInfo? memory)
    {
        if (memory is null)
        {
            _memoryBar.Value = 0;
            _memoryText.Text = "--";
            return;
        }

        _memoryBar.Value = memory.UsedPercent;
        _memoryText.Text = $"{memory.UsedGb:0.#} / {memory.TotalGb:0.#} GB · {memory.UsedPercent:0}%";
    }

    /// <summary>自动选中并滚动到前台应用，让用户一眼看到「哪个正在运行」。</summary>
    private void SelectForeground()
    {
        if (_list.ItemsSource is not List<ProcessInfo> items) return;
        var foreground = items.FirstOrDefault(p => p.IsForeground);
        if (foreground is null) return;

        _list.SelectedItem = foreground;
        _list.ScrollIntoView(foreground);
    }

    private void ApplyFilter()
    {
        IEnumerable<ProcessInfo> query = _filter.SelectedIndex switch
        {
            1 => _apps.Where(p => !p.IsSystem),
            2 => _apps.Where(p => p.IsSystem),
            _ => _apps
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

    private async Task ForceStopAsync()
    {
        if (Selected() is not { } app) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => AppState.Adb.ForceStopAsync(device!.Serial, app.PackageName));
        MainWindow.Notify(ok ? L("Msg_Done") : message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }
}
