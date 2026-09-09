using AdbManager.Models;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AdbManager.Views;

public sealed class DevicesView : PageBase
{
    private readonly StackPanel _deviceList = new() { Spacing = 10 };
    private readonly TextBlock _emptyText = new() { TextWrapping = TextWrapping.Wrap };

    private readonly TextBox _connectIp = Miuix.Input("");
    private readonly TextBox _connectPort = Miuix.Input("5555");

    private readonly TextBox _pairIp = Miuix.Input("");
    private readonly TextBox _pairPort = Miuix.Input("");
    private readonly TextBox _pairCode = Miuix.Input("");

    private readonly StackPanel _scanResults = new() { Spacing = 6 };
    private bool _loaded;

    public DevicesView()
    {
        Content = Build();
        AppState.DevicesChanged += () => DispatcherQueue?.TryEnqueue(RenderDevices);
    }

    public override async Task OnShownAsync()
    {
        if (_loaded) { RenderDevices(); return; }
        _loaded = true;

        _connectIp.PlaceholderText = L("Wireless_Ip");
        _connectPort.PlaceholderText = L("Wireless_ConnectPort");
        _pairIp.PlaceholderText = L("Wireless_Ip");
        _pairPort.PlaceholderText = L("Wireless_PairPort");
        _pairCode.PlaceholderText = L("Wireless_PairCode");
        _emptyText.Text = L("Devices_Empty");

        await AppState.RefreshDevicesAsync();
        RenderDevices();
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        // 顶部操作
        var refreshButton = Miuix.PrimaryButton(L("Common_Refresh"));
        refreshButton.Click += async (_, _) =>
        {
            await AppState.RefreshDevicesAsync();
            MainWindow.Notify(L("Msg_RefreshDone"), InfoBarSeverity.Success);
        };

        var restartButton = Miuix.SecondaryButton(L("Devices_RestartServer"));
        restartButton.Click += async (_, _) =>
        {
            var result = await AppState.Adb.RestartServerAsync();
            await AppState.RefreshDevicesAsync();
            MainWindow.Notify(result.Success ? L("Msg_Done") : result.Message,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        };

        var killButton = Miuix.DangerButton(L("Devices_KillServer"));
        killButton.Click += async (_, _) =>
        {
            var result = await AppState.Adb.KillServerAsync();
            await AppState.RefreshDevicesAsync();
            MainWindow.Notify(result.Success ? L("Msg_Done") : result.Message,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        };

        root.Children.Add(Miuix.Horizontal(refreshButton, restartButton, killButton));

        // 设备列表（卡片内容限高，避免遮挡页面底部）
        var devicePanel = new StackPanel { Spacing = 10 };
        devicePanel.Children.Add(Miuix.SectionTitle(L("Devices_Title")));
        devicePanel.Children.Add(_emptyText);
        devicePanel.Children.Add(new ScrollViewer
        {
            Content = _deviceList,
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0)
        });
        root.Children.Add(Miuix.Card(devicePanel));

        // 无线连接：IP 与端口分列，输入框加长
        _connectIp.MinWidth = 300;
        _connectPort.MinWidth = 150;
        _pairIp.MinWidth = 300;
        _pairPort.MinWidth = 130;
        _pairCode.MinWidth = 130;

        var connectButton = Miuix.SuccessButton(L("Wireless_Connect"));
        connectButton.Click += async (_, _) => await ConnectAsync();

        var scanButton = Miuix.SecondaryButton(L("Wireless_Scan"));
        scanButton.Click += async (_, _) => await ScanAsync();

        var tcpipButton = Miuix.SecondaryButton(L("Wireless_Tcpip"));
        tcpipButton.Click += async (_, _) => await TcpipAsync();

        var wirelessPanel = new StackPanel { Spacing = 10 };
        wirelessPanel.Children.Add(Miuix.SectionTitle(L("Wireless_Title")));
        wirelessPanel.Children.Add(Miuix.Horizontal(_connectIp, _connectPort, connectButton, scanButton));

        wirelessPanel.Children.Add(Miuix.Divider());
        wirelessPanel.Children.Add(Miuix.SectionTitle(L("Wireless_PairTitle")));

        var pairButton = Miuix.PrimaryButton(L("Wireless_Pair"));
        pairButton.Click += async (_, _) => await PairAsync();

        wirelessPanel.Children.Add(Miuix.Horizontal(_pairIp, _pairPort, _pairCode));
        wirelessPanel.Children.Add(Miuix.Horizontal(pairButton));
        wirelessPanel.Children.Add(Miuix.Horizontal(tcpipButton));
        wirelessPanel.Children.Add(_scanResults);
        wirelessPanel.Children.Add(Miuix.Body(L("Wireless_Hint"), true));

        root.Children.Add(Miuix.Card(wirelessPanel));

        return root;
    }

    /// <summary>Android 10 及以下：USB 先连接后切 TCP 模式。</summary>
    private async Task TcpipAsync()
    {
        if (!TryGetDevice(out var device)) return;
        var (ok, message) = await AppState.Adb.TcpipAsync(device!.Serial, 5555);
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void RenderDevices()
    {
        _deviceList.Children.Clear();
        _emptyText.Visibility = AppState.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var device in AppState.Devices)
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Padding = new Thickness(4)
            };

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

            var header = Miuix.Body(device.DisplayName);
            header.FontSize = 16;
            header.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            info.Children.Add(header);

            // 状态文字配色：已连接=绿色，未授权/离线=红色
            var stateLine = Miuix.Body(device.StateText);
            stateLine.Foreground = device.IsOnline
                ? Miuix.Brush("MiuixSuccess")
                : Miuix.Brush("MiuixDanger");
            info.Children.Add(stateLine);

            info.Children.Add(Miuix.Body($"{device.Serial} · {device.ConnectionKind}", true));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            // 已连上并自动成为当前设备时不再显示「选中」
            if (!ReferenceEquals(AppState.CurrentDevice, device))
            {
                var selectButton = Miuix.SuccessButton(L("Devices_Select"));
                selectButton.Height = 36;
                selectButton.Click += (_, _) => FadeOutThen(selectButton, () =>
                {
                    AppState.CurrentDevice = device;
                    MainWindow.Notify(device.DisplayName, InfoBarSeverity.Success);
                    RenderDevices();
                });
                actions.Children.Add(selectButton);
            }

            if (string.IsNullOrEmpty(device.UsbPort))
            {
                var disconnect = Miuix.DangerButton(L("Devices_Disconnect"));
                disconnect.Height = 36;
                disconnect.Click += async (_, _) =>
                {
                    var (ok, message) = await AppState.Adb.DisconnectAsync(device.Serial);
                    await AppState.RefreshDevicesAsync();
                    MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
                };
                actions.Children.Add(disconnect);
            }

            Grid.SetColumn(info, 0);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(info);
            grid.Children.Add(actions);

            var row = Miuix.Card(grid, 12);
            if (ReferenceEquals(AppState.CurrentDevice, device))
                row.BorderBrush = Miuix.Brush("MiuixAccent");

            _deviceList.Children.Add(row);
        }
    }

    /// <summary>淡出动画结束后再执行动作，避免「选中」按钮硬切消失。</summary>
    private static void FadeOutThen(FrameworkElement element, Action completed)
    {
        if (element.XamlRoot is null) { completed(); return; }

        var animation = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    private async Task ConnectAsync()
    {
        var ip = _connectIp.Text.Trim();
        if (ip.Length == 0) { MainWindow.Notify(L("Wireless_Ip"), InfoBarSeverity.Warning); return; }

        var port = _connectPort.Text.Trim();
        var address = port.Length > 0 ? $"{ip}:{port}" : $"{ip}:5555";

        var (ok, message) = await AppState.Adb.ConnectAsync(address);
        await AppState.RefreshDevicesAsync();
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task PairAsync()
    {
        var ip = _pairIp.Text.Trim();
        var port = _pairPort.Text.Trim();
        var code = _pairCode.Text.Trim();

        if (ip.Length == 0 || port.Length == 0)
        {
            MainWindow.Notify(L("Wireless_PairAddress"), InfoBarSeverity.Warning);
            return;
        }
        if (code.Length == 0)
        {
            MainWindow.Notify(L("Wireless_PairCode"), InfoBarSeverity.Warning);
            return;
        }

        var (paired, pairMessage) = await AppState.Adb.PairAsync($"{ip}:{port}", code);
        if (!paired)
        {
            MainWindow.Notify(pairMessage, InfoBarSeverity.Error);
            return;
        }

        AppState.Log.Success(L("Msg_PairOk"));
        MainWindow.Notify(L("Msg_PairOk"), InfoBarSeverity.Success);

        // 配对端口 ≠ 连接端口：优先用户填写的连接端口，再尝试常见端口
        var connectPort = _connectPort.Text.Trim();
        var candidates = new List<int>();
        if (connectPort.Length > 0 && int.TryParse(connectPort, out var preferred)) candidates.Add(preferred);
        candidates.AddRange(new[] { 5555, 37265, 37819, 38941, 42113 });

        foreach (var portCandidate in candidates.Distinct())
        {
            var (ok, message) = await AppState.Adb.ConnectAsync($"{ip}:{portCandidate}");
            if (ok)
            {
                await AppState.RefreshDevicesAsync();
                MainWindow.Notify(message, InfoBarSeverity.Success);
                return;
            }
        }

        await AppState.RefreshDevicesAsync();
        MainWindow.Notify(L("Msg_PairOk") + " " + L("Wireless_Hint"), InfoBarSeverity.Informational);
    }

    private async Task ScanAsync()
    {
        _scanResults.Children.Clear();

        var (services, diagnostics) = await AppState.Adb.ScanMdnsFullAsync();

        if (services.Count == 0)
        {
            // mDNS 扫不到（常见：手机没开「无线调试」开关 / 防火拦组播），但有 USB 在线设备 → 提供一键转无线
            var usbDevice = AppState.Devices.FirstOrDefault(d => d.IsOnline && !string.IsNullOrEmpty(d.UsbPort));
            if (usbDevice is not null)
            {
                var usbIp = await AppState.Adb.GetWlanIpAsync(usbDevice.Serial);
                if (usbIp.Length > 0)
                {
                    var usbButton = Miuix.PrimaryButton(string.Format(L("Wireless_UsbToWireless"), $"{usbIp}:5555"));
                    usbButton.Height = 40;
                    var usbSerial = usbDevice.Serial;
                    usbButton.Click += async (_, _) => await UsbToWirelessAsync(usbSerial);
                    _scanResults.Children.Add(usbButton);
                }
            }

            _scanResults.Children.Add(Miuix.Body(L("Wireless_ScanEmpty"), true));
            foreach (var diagnostic in diagnostics)
                _scanResults.Children.Add(Miuix.Body("· " + diagnostic, true));
            _scanResults.Children.Add(Miuix.Body(L("Wireless_ScanTrouble"), true));
            return;
        }

        _scanResults.Children.Add(Miuix.Body(L("Msg_RefreshDone"), true));
        foreach (var service in services)
        {
            var kind = service.IsPairing ? L("Wireless_KindPair") : L("Wireless_KindConnect");
            var button = Miuix.SecondaryButton($"{service.Address} ({kind})");
            button.Height = 36;
            var address = service.Address;
            button.Click += async (_, _) =>
            {
                var host = address.Split(':')[0];
                var port = address.Contains(':') ? address.Split(':')[1] : "5555";

                if (service.IsPairing)
                {
                    _pairIp.Text = host;
                    _pairPort.Text = port;
                }
                else
                {
                    _connectIp.Text = host;
                    _connectPort.Text = port;
                    var (ok, message) = await AppState.Adb.ConnectAsync(address);
                    await AppState.RefreshDevicesAsync();
                    MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
                }
            };
            _scanResults.Children.Add(button);
        }
    }

    /// <summary>USB 一键转无线：tcpip 5555 + 查询 WLAN IP + 自动 connect。</summary>
    private async Task UsbToWirelessAsync(string serial)
    {
        var (ok, message) = await AppState.Adb.UsbToWirelessAsync(serial);
        await AppState.RefreshDevicesAsync();
        MainWindow.Notify(ok ? L("Msg_Connected") + " " + message : message,
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }
}
