using AdbManager.Models;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbManager.Views;

/// <summary>设备信息页：选中设备变化（含首次连上）时自动刷新，也可手动刷新。</summary>
public sealed class InfoView : PageBase
{
    private string _loadedSerial = "";
    private bool _hasLoaded;
    /// <summary>设备掉线后置为 true，重新连上（同序列号）时也要重新加载信息。</summary>
    private bool _stale = true;

    /// <summary>隐私卡「显示/隐藏」开关状态；默认脱敏，避免敏感信息被误截图。</summary>
    private bool _showSecrets;

    private DeviceInfoReport? _report;
    private List<WifiEntry> _wifi = new();
    private List<UsageEntry> _usage = new();
    /// <summary>加载进行中标志：轮询心跳每 3 秒一次，防止上一次还没跑完就再次进入。</summary>
    private bool _loading;

    public InfoView()
    {
        Content = Build();
        AppState.CurrentDeviceChanged += OnCurrentDeviceChanged;
        // 兜底心跳：DevicesChanged 每轮轮询都会触发，覆盖「同一设备状态变化但
        // CurrentDevice 引用未变」这类 CurrentDeviceChanged 不发的场景。
        AppState.DevicesChanged += OnDevicesChanged;
    }

    public override async Task OnShownAsync()
    {
        var device = AppState.CurrentDevice;
        if (device is not null && device.Serial != _loadedSerial) await LoadAsync(auto: true);
    }

    private void OnCurrentDeviceChanged(AdbDevice? device)
    {
        var queue = DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Apply()
        {
            // 设备消失或掉线：标记待刷新
            if (device is null || !device.IsOnline) { _stale = true; return; }
            if (!_hasLoaded || _stale || device.Serial != _loadedSerial) _ = LoadAsync(auto: true);
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
        if (!_hasLoaded || _stale || device.Serial != _loadedSerial) _ = LoadAsync(auto: true);
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        var refresh = Miuix.PrimaryButton(L("Common_Refresh"));
        refresh.Click += async (_, _) => await LoadAsync(auto: false);

        root.Children.Add(Miuix.Card(Miuix.Horizontal(
            Miuix.SectionTitle(L("Info_Title")), refresh)));

        _batteryCard = Miuix.Card(BuildGroup(L("Info_Battery")));
        _displayCard = Miuix.Card(BuildGroup(L("Info_Display")));
        _memoryRing = new UsageRing();
        _memoryCard = Miuix.Card(BuildGroupWithRing(BuildGroup(L("Info_Memory")), _memoryRing));
        _storageRing = new UsageRing();
        _storageCard = Miuix.Card(BuildGroupWithRing(BuildGroup(L("Info_Storage")), _storageRing));
        _networkCard = Miuix.Card(BuildGroup(L("Info_Network")));
        _systemCard = Miuix.Card(BuildGroup(L("Info_System")));

        _privacyCard = Miuix.Card(BuildPrivacyGroup());

        // 左右两列：左=电池/内存/存储，右=显示/网络/系统（保持两列高度均衡）
        var columns = new Grid { ColumnSpacing = 16 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Spacing = 16 };
        var right = new StackPanel { Spacing = 16 };

        left.Children.Add(_batteryCard);
        left.Children.Add(_memoryCard);
        left.Children.Add(_storageCard);

        right.Children.Add(_systemCard);
        right.Children.Add(_displayCard);
        right.Children.Add(_networkCard);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);

        root.Children.Add(columns);
        root.Children.Add(_privacyCard);   // 隐私卡独占整行：WiFi 列表与使用统计条目较长

        return root;
    }

    private Microsoft.UI.Xaml.Controls.Border _batteryCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _displayCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _memoryCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _storageCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _networkCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _systemCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _privacyCard = Miuix.Card();

    private UsageRing? _memoryRing;
    private UsageRing? _storageRing;

    private static StackPanel BuildGroup(string title)
    {
        var panel = new StackPanel { Spacing = 8, Tag = title };
        panel.Children.Add(Miuix.SectionTitle(title));
        panel.Children.Add(Miuix.Body("…", true));
        return panel;
    }

    /// <summary>
    /// 信息卡布局：左侧键值行，右侧占比环（内存 / 存储卡专用）。
    /// 卡片 Child 变成 Grid，Fill 取行面板时需经 GroupPanel 还原。
    /// </summary>
    private static Grid BuildGroupWithRing(StackPanel rows, UsageRing ring)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(rows);
        Grid.SetColumn(ring, 1);
        ring.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(ring);
        return grid;
    }

    /// <summary>卡片 Child 是普通 StackPanel 或「行 + 环」Grid，取其中承载键值行的面板。</summary>
    private static StackPanel GroupPanel(Microsoft.UI.Xaml.Controls.Border card) =>
        card.Child is Grid grid ? (StackPanel)grid.Children[0] : (StackPanel)card.Child;

    /// <summary>刷新占比环；没有百分比数据（读取失败等）时隐藏。</summary>
    private static void UpdateRing(UsageRing? ring, double? percent)
    {
        if (ring is null) return;
        if (percent is { } value)
        {
            ring.Visibility = Visibility.Visible;
            ring.Set(value);
        }
        else
        {
            ring.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>隐私卡：标题行带「显示/隐藏」开关 + 说明 + WiFi 与使用统计两块内容。</summary>
    private StackPanel BuildPrivacyGroup()
    {
        var panel = new StackPanel { Spacing = 8, Tag = L("Info_PrivacyTitle") };

        _secretToggle = Miuix.SecondaryButton(L("Info_ShowSecrets"));
        _secretToggle.Click += (_, _) =>
        {
            _showSecrets = !_showSecrets;
            _secretToggle!.Content = _showSecrets ? L("Info_HideSecrets") : L("Info_ShowSecrets");
            // 系统卡含 IMEI（敏感字段），需随开关同步重绘。
            if (_report is not null) Fill(_systemCard, _report.System, secretKeys: new[] { L("Sys_Imei") });
            RenderPrivacy();
        };

        panel.Children.Add(Miuix.Horizontal(Miuix.SectionTitle(L("Info_PrivacyTitle")), _secretToggle));
        panel.Children.Add(Miuix.Body(L("Info_PrivacyHint"), true));

        _wifiHost = new StackPanel { Spacing = 4 };
        _usageHost = new StackPanel { Spacing = 4 };
        panel.Children.Add(_wifiHost);
        panel.Children.Add(_usageHost);
        return panel;
    }

    private Button? _secretToggle;
    private StackPanel _wifiHost = new();
    private StackPanel _usageHost = new();

    /// <summary>
    /// 填充分组卡片。secretKeys 中的键（如 IMEI）视为敏感字段：默认脱敏，
    /// 由隐私卡的「显示/隐藏」开关统一控制。
    /// </summary>
    private void Fill(Microsoft.UI.Xaml.Controls.Border card, Dictionary<string, string> entries,
        string[]? secretKeys = null)
    {
        var panel = GroupPanel(card);
        panel.Children.Clear();
        panel.Children.Add(Miuix.SectionTitle((string)panel.Tag!));

        if (entries.Count == 0)
        {
            panel.Children.Add(Miuix.Body(L("Files_Empty"), true));
            return;
        }

        foreach (var (key, value) in entries)
        {
            if (value.Length == 0) continue;
            var isSecret = secretKeys is not null && secretKeys.Contains(key);
            var shown = isSecret && !_showSecrets ? Mask(value) : value;
            panel.Children.Add(Miuix.SettingRow(key, shown, Miuix.Body("", true)));
            panel.Children.Add(Miuix.Divider());
        }
        if (panel.Children.Count > 1) panel.Children.RemoveAt(panel.Children.Count - 1); // 去掉末尾分割线
    }

    private async Task LoadAsync(bool auto)
    {
        if (_loading) return;
        if (!TryGetDevice(out var device, warn: !auto)) return;

        if (auto && _hasLoaded && !_stale && device!.Serial == _loadedSerial) return;

        _loading = true;
        try
        {
            _loadedSerial = device!.Serial;
            _hasLoaded = true;
            _stale = false;

            if (!auto) MainWindow.Notify(L("Msg_Working"), InfoBarSeverity.Informational);

            var serial = device.Serial;
            var report = await AppState.Adb.GetDeviceInfoReportAsync(serial);

            // 隐私数据单独取（拉文件 + dex，耗时更长），失败不阻断主信息展示。
            _wifi = new List<WifiEntry>();
            _usage = new List<UsageEntry>();
            try
            {
                if (report.HasRoot) _wifi = await AppState.Adb.GetWifiPasswordsAsync(serial);
                _usage = await AppState.Adb.GetUsageStatsAsync(serial);
                _usage = await AppState.Adb.FillUsageLabelsAsync(serial, _usage);
            }
            catch (Exception ex)
            {
                AppState.Log.Error($"privacy data: {ex.Message}");
            }

            _report = report;
            Fill(_batteryCard, report.Battery);
            Fill(_displayCard, report.Display);
            Fill(_memoryCard, report.Memory);
            Fill(_storageCard, report.Storage);
            UpdateRing(_memoryRing, report.MemoryUsedPercent);
            UpdateRing(_storageRing, report.StorageUsedPercent);
            Fill(_networkCard, report.Network);
            Fill(_systemCard, report.System, secretKeys: new[] { L("Sys_Imei") });
            RenderPrivacy();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>隐私卡内容渲染；受 _showSecrets 控制是否脱敏。</summary>
    private void RenderPrivacy()
    {
        _wifiHost.Children.Clear();
        _usageHost.Children.Clear();

        var hasRoot = _report?.HasRoot == true;

        // ---- WiFi 密码 ----
        _wifiHost.Children.Add(Miuix.SectionTitle(L("Info_Wifi")));
        if (!hasRoot)
        {
            _wifiHost.Children.Add(Miuix.Body(L("Info_NeedRoot"), true));
        }
        else if (_wifi.Count == 0)
        {
            _wifiHost.Children.Add(Miuix.Body(L("Info_WifiEmpty"), true));
        }
        else
        {
            foreach (var entry in _wifi)
            {
                var value = entry.HasPassword
                    ? (_showSecrets ? entry.Password! : Mask(entry.Password!))
                    : L("Info_OpenNetwork");
                _wifiHost.Children.Add(Miuix.SettingRow(entry.Ssid, value, Miuix.Body("", true)));
                _wifiHost.Children.Add(Miuix.Divider());
            }
            if (_wifiHost.Children.Count > 1) _wifiHost.Children.RemoveAt(_wifiHost.Children.Count - 1);
        }

        // ---- 应用使用 ----
        _usageHost.Children.Add(Miuix.SectionTitle(L("Info_Usage")));
        if (_usage.Count == 0)
        {
            _usageHost.Children.Add(Miuix.Body(L("Files_Empty"), true));
            return;
        }

        foreach (var entry in _usage)
        {
            var count = L("Info_UsageCount", entry.Count);
            var title = entry.Label.Length > 0 ? entry.Label : entry.Package;
            // 应用名与包名不同时，把包名作为副标题保留可追溯性。
            var description = title == entry.Package ? count : $"{entry.Package} · {count}";
            _usageHost.Children.Add(Miuix.SettingRow(title, description, Miuix.Body("", true)));
            _usageHost.Children.Add(Miuix.Divider());
        }
        if (_usageHost.Children.Count > 1) _usageHost.Children.RemoveAt(_usageHost.Children.Count - 1);
    }

    /// <summary>把敏感串脱敏为「首字符 + 圆点 + 长度」，既提示存在又不泄露内容。</summary>
    private static string Mask(string secret)
    {
        if (secret.Length == 0) return "";
        if (secret.Length <= 2) return new string('•', secret.Length);
        return secret[0] + new string('•', Math.Min(secret.Length - 1, 10));
    }
}
