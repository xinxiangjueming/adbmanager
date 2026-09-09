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

    public InfoView()
    {
        Content = Build();
        AppState.CurrentDeviceChanged += OnCurrentDeviceChanged;
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

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        var refresh = Miuix.PrimaryButton(L("Common_Refresh"));
        refresh.Click += async (_, _) => await LoadAsync(auto: false);

        root.Children.Add(Miuix.Card(Miuix.Horizontal(
            Miuix.SectionTitle(L("Info_Title")), refresh)));

        _basicCard = Miuix.Card(BuildGroup(L("Info_Basic")));
        _batteryCard = Miuix.Card(BuildGroup(L("Info_Battery")));
        _displayCard = Miuix.Card(BuildGroup(L("Info_Display")));
        _memoryCard = Miuix.Card(BuildGroup(L("Info_Memory")));
        _storageCard = Miuix.Card(BuildGroup(L("Info_Storage")));
        _networkCard = Miuix.Card(BuildGroup(L("Info_Network")));
        _systemCard = Miuix.Card(BuildGroup(L("Info_System")));

        // 左右两列：左=基本信息/显示/存储，右=电池/内存/网络/系统
        var columns = new Grid { ColumnSpacing = 16 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Spacing = 16 };
        var right = new StackPanel { Spacing = 16 };

        left.Children.Add(_basicCard);
        left.Children.Add(_displayCard);
        left.Children.Add(_storageCard);

        right.Children.Add(_batteryCard);
        right.Children.Add(_memoryCard);
        right.Children.Add(_networkCard);
        right.Children.Add(_systemCard);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);

        root.Children.Add(columns);

        return root;
    }

    private Microsoft.UI.Xaml.Controls.Border _basicCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _batteryCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _displayCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _memoryCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _storageCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _networkCard = Miuix.Card();
    private Microsoft.UI.Xaml.Controls.Border _systemCard = Miuix.Card();

    private static StackPanel BuildGroup(string title)
    {
        var panel = new StackPanel { Spacing = 8, Tag = title };
        panel.Children.Add(Miuix.SectionTitle(title));
        panel.Children.Add(Miuix.Body("…", true));
        return panel;
    }

    private static void Fill(Microsoft.UI.Xaml.Controls.Border card, Dictionary<string, string> entries)
    {
        var panel = (StackPanel)card.Child;
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
            panel.Children.Add(Miuix.SettingRow(key, value, Miuix.Body("", true)));
            panel.Children.Add(Miuix.Divider());
        }
        if (panel.Children.Count > 1) panel.Children.RemoveAt(panel.Children.Count - 1); // 去掉末尾分割线
    }

    private async Task LoadAsync(bool auto)
    {
        if (!TryGetDevice(out var device, warn: !auto)) return;

        if (auto && _hasLoaded && !_stale && device!.Serial == _loadedSerial) return;
        _loadedSerial = device!.Serial;
        _hasLoaded = true;
        _stale = false;

        if (!auto) MainWindow.Notify(L("Msg_Working"), InfoBarSeverity.Informational);
        var report = await AppState.Adb.GetDeviceInfoReportAsync(device.Serial);
        Fill(_basicCard, report.Basic);
        Fill(_batteryCard, report.Battery);
        Fill(_displayCard, report.Display);
        Fill(_memoryCard, report.Memory);
        Fill(_storageCard, report.Storage);
        Fill(_networkCard, report.Network);
        Fill(_systemCard, report.System);
    }
}
