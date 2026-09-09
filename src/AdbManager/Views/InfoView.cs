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
            // 仅在设备在线且序列号变化时自动加载，避免循环刷新
            if (device is null || !device.IsOnline || device.Serial == _loadedSerial) return;
            _ = LoadAsync(auto: true);
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

        root.Children.Add(_basicCard);
        root.Children.Add(_batteryCard);
        root.Children.Add(_displayCard);
        root.Children.Add(_memoryCard);
        root.Children.Add(_storageCard);
        root.Children.Add(_networkCard);
        root.Children.Add(_systemCard);

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

        if (auto && device!.Serial == _loadedSerial && _hasLoaded) return;
        _loadedSerial = device!.Serial;
        _hasLoaded = true;

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
