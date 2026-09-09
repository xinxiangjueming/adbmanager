using System.Diagnostics;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbManager.Views;

public sealed class ToolsView : PageBase
{
    // 一键激活：脚本类（adb shell 直跑）
    private const string CmdShizuku = "sh /storage/emulated/0/Android/data/moe.shizuku.privileged.api/start.sh";
    private const string CmdScene = "sh /storage/emulated/0/Android/data/com.omarea.vtools/up.sh";
    private const string CmdBrevent = "sh /data/data/me.piebridge.brevent/brevent.sh || (output=$(pm path me.piebridge.brevent); export CLASSPATH=${output#*:}; app_process /system/bin me.piebridge.brevent.server.BreventServer bootstrap; /system/bin/sh /data/local/tmp/brevent.sh)";
    private const string CmdIceBox = "sh /sdcard/Android/data/com.catchingnow.icebox/files/start.sh";
    private const string CmdStopApp = "sh /storage/emulated/0/Android/data/web1n.stopapp/files/starter.sh";
    private const string CmdGreenify = "pm grant com.oasisfeng.greenify android.permission.WRITE_SECURE_SETTINGS";

    // 一键激活：dpm（Device Owner）类
    private const string PkgAirFrozen = "me.yourbay.airfrozen";
    private const string AirFrozenAdmin = "me.yourbay.airfrozen/.main.core.mgmt.MDeviceAdminReceiver";
    private const string PkgFreezeYou = "cf.playhi.freezeyou";
    private const string FreezeYouAdmin = "cf.playhi.freezeyou/.DeviceAdminReceiver";
    private const string PkgIsland = "com.oasisfeng.island";
    private const string IslandAdmin = "com.oasisfeng.island/.IslandDeviceAdminReceiver";
    private const string PkgInstaller = "com.modosa.apkinstaller";
    private const string InstallerAdmin = "com.modosa.apkinstaller/.receiver.AdminReceiver";
    private const string PkgBlackHole = "com.hld.apurikakusu";
    private const string PkgSecondSpace = "com.hld.anzenbokusu";

    private readonly TextBox _command = Miuix.Input("");
    private readonly TextBox _output = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 160,
        MaxHeight = 320,
        CornerRadius = new CornerRadius(Miuix.ControlRadius)
    };

    private readonly Button _recordButton = Miuix.PrimaryButton("");

    private readonly TextBox _tetherPort = Miuix.Input("8888");
    private readonly TextBlock _tetherStatus = Miuix.Body("", true);
    private readonly Button _tetherButton = Miuix.PrimaryButton("");
    private int _tetherPortActive;
    private bool _tetherRunning;

    public ToolsView()
    {
        Content = Build();
    }

    public override Task OnShownAsync()
    {
        _command.PlaceholderText = L("Tools_ShellPlaceholder");
        _recordButton.Content = AppState.Adb.IsRecording ? L("Tools_StopRecord") : L("Tools_Record");
        _tetherButton.Content = _tetherRunning ? L("Tether_Stop") : L("Tether_Start");
        return Task.CompletedTask;
    }

    /// <summary>开启/停止反向共享电脑网络（HTTP 代理 + adb reverse + 设备系统代理）。</summary>
    private async Task ToggleTetherAsync()
    {
        if (!TryGetDevice(out var device)) return;

        if (_tetherRunning)
        {
            var stopped = await MainWindow.RunBusyAsync(L("Busy_Working"),
                () => AppState.Adb.StopReverseTetherAsync(device!.Serial, _tetherPortActive));
            _tetherRunning = false;
            _tetherButton.Content = L("Tether_Start");
            _tetherStatus.Text = stopped.Success ? L("Msg_Done") : stopped.Message;
            MainWindow.Notify(stopped.Success ? L("Msg_Done") : stopped.Message,
                stopped.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            return;
        }

        var portText = _tetherPort.Text.Trim();
        if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535)
        {
            MainWindow.Notify(L("Tether_Port"), InfoBarSeverity.Warning);
            return;
        }

        var started = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => AppState.Adb.StartReverseTetherAsync(device!.Serial, port));
        if (!started.Success)
        {
            _tetherStatus.Text = started.Message;
            MainWindow.Notify(started.Message, InfoBarSeverity.Error);
            return;
        }

        _tetherPortActive = port;
        _tetherRunning = true;
        _tetherButton.Content = L("Tether_Stop");
        _tetherStatus.Text = started.Message;
        MainWindow.Notify(started.Message, InfoBarSeverity.Success);
    }

    /// <summary>清除锁屏密码（需 TWRP/恢复模式）。</summary>
    private async Task ClearLockAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var dialog = new ContentDialog
        {
            Title = L("Recovery_ClearLock"),
            Content = L("Recovery_ClearLockConfirm"),
            PrimaryButtonText = L("Common_Confirm"),
            CloseButtonText = L("Common_Cancel"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => AppState.Adb.ClearScreenLockAsync(device!.Serial));
        MainWindow.Notify(result.Success ? L("Msg_Done") : result.Message,
            result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    /// <summary>跳过谷歌验证（需 TWRP/恢复模式）。</summary>
    private async Task FrpBypassAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var dialog = new ContentDialog
        {
            Title = L("Recovery_FrpSkip"),
            Content = L("Recovery_FrpConfirm"),
            PrimaryButtonText = L("Common_Confirm"),
            CloseButtonText = L("Common_Cancel"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => AppState.Adb.FrpBypassAsync(device!.Serial));
        MainWindow.Notify(result.Success ? L("Msg_Done") : result.Message,
            result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        // 截图 / 录屏
        var screenshot = Miuix.PrimaryButton(L("Tools_Screenshot"));
        screenshot.Click += async (_, _) => await ScreenshotAsync();

        _recordButton.Click += async (_, _) => await ToggleRecordingAsync();

        var mediaCard = Miuix.Card(new StackPanel { Spacing = 10 });
        ((StackPanel)mediaCard.Child).Children.Add(Miuix.SectionTitle(L("Tools_Title")));
        ((StackPanel)mediaCard.Child).Children.Add(Miuix.Horizontal(screenshot, _recordButton));
        root.Children.Add(mediaCard);

        // 重启
        var rebootSystem = Miuix.SecondaryButton(L("Tools_RebootNormal"));
        rebootSystem.Click += async (_, _) => await RebootAsync("");
        var rebootRecovery = Miuix.SecondaryButton(L("Tools_RebootRecovery"));
        rebootRecovery.Click += async (_, _) => await RebootAsync("recovery");
        var rebootBootloader = Miuix.SecondaryButton(L("Tools_RebootBootloader"));
        rebootBootloader.Click += async (_, _) => await RebootAsync("bootloader");
        var rebootEdl = Miuix.SecondaryButton(L("Tools_RebootEdl"));
        rebootEdl.Click += async (_, _) => await RebootAsync("edl");

        var rebootCard = Miuix.Card(new StackPanel { Spacing = 10 });
        ((StackPanel)rebootCard.Child).Children.Add(Miuix.SectionTitle(L("Tools_Reboot")));
        ((StackPanel)rebootCard.Child).Children.Add(Miuix.Horizontal(rebootSystem, rebootRecovery, rebootBootloader, rebootEdl));
        root.Children.Add(rebootCard);

        // 一键激活（常用玩机工具）
        var activatePanel = new StackPanel { Spacing = 10 };
        activatePanel.Children.Add(Miuix.SectionTitle(L("Tools_ActivateTitle")));
        activatePanel.Children.Add(Miuix.Horizontal(
            ActivateShellButton("Shizuku", CmdShizuku),
            ActivateShellButton("Scene", CmdScene),
            ActivateShellButton("黑阈", CmdBrevent),
            ActivateShellButton("冰箱", CmdIceBox)));
        activatePanel.Children.Add(Miuix.Horizontal(
            ActivateShellButton("小黑屋", CmdStopApp),
            ActivateDpmButton("空调狗", PkgAirFrozen, AirFrozenAdmin),
            ActivateDpmButton("自冻", PkgFreezeYou, FreezeYouAdmin),
            ActivateDpmButton("炼妖壶", PkgIsland, IslandAdmin)));
        activatePanel.Children.Add(Miuix.Horizontal(
            ActivateDpmButton("安装狮", PkgInstaller, InstallerAdmin),
            ActivateShellButton("绿色守护", CmdGreenify),
            ActivateDpmButton("黑洞", PkgBlackHole),
            ActivateDpmButton("第二空间", PkgSecondSpace)));
        activatePanel.Children.Add(Miuix.Body(L("Tools_ActivateHint"), true));
        root.Children.Add(Miuix.Card(activatePanel));

        // Shell
        var run = Miuix.PrimaryButton(L("Tools_Run"));
        run.Click += async (_, _) => await RunCommandAsync();

        var wifiFix = Miuix.SecondaryButton(L("Tools_WifiFix"));
        wifiFix.Click += async (_, _) => await WifiFixAsync();

        var shellPanel = new StackPanel { Spacing = 10 };
        shellPanel.Children.Add(Miuix.SectionTitle(L("Tools_Shell")));
        shellPanel.Children.Add(Miuix.Horizontal(_command, run, wifiFix));
        shellPanel.Children.Add(Miuix.SectionTitle(L("Tools_Output")));
        shellPanel.Children.Add(_output);
        root.Children.Add(Miuix.Card(shellPanel));

        // 反向共享电脑网络
        var tetherPanel = new StackPanel { Spacing = 10 };
        _tetherPort.MinWidth = 130;
        _tetherButton.MinWidth = 140;
        _tetherButton.Click += async (_, _) => await ToggleTetherAsync();

        tetherPanel.Children.Add(Miuix.SectionTitle(L("Tether_Title")));
        tetherPanel.Children.Add(Miuix.Horizontal(_tetherPort, _tetherButton));
        tetherPanel.Children.Add(_tetherStatus);
        tetherPanel.Children.Add(Miuix.Body(L("Tether_Hint"), true));
        root.Children.Add(Miuix.Card(tetherPanel));

        // Recovery（TWRP）高级操作
        var clearLock = Miuix.DangerButton(L("Recovery_ClearLock"));
        clearLock.Click += async (_, _) => await ClearLockAsync();

        var frpSkip = Miuix.DangerButton(L("Recovery_FrpSkip"));
        frpSkip.Click += async (_, _) => await FrpBypassAsync();

        var recoveryPanel = new StackPanel { Spacing = 10 };
        recoveryPanel.Children.Add(Miuix.SectionTitle(L("Recovery_Title")));
        recoveryPanel.Children.Add(Miuix.Horizontal(clearLock, frpSkip));
        recoveryPanel.Children.Add(Miuix.Body(L("Recovery_NeedTwrp"), true));
        root.Children.Add(Miuix.Card(recoveryPanel));

        return root;
    }

    private async Task ScreenshotAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var file = await Pickers.PickSaveFileAsync($"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}", ".png", L("Pick_Png"));
        if (file is null) return;

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Screenshot"),
            () => AppState.Adb.ScreenshotAsync(device!.Serial, file.Path));
        if (ok)
        {
            AppState.Log.Success(L("Msg_ScreenshotSaved") + " " + message);
            MainWindow.Notify(L("Msg_ScreenshotSaved") + " " + message, InfoBarSeverity.Success);
        }
        else
        {
            MainWindow.Notify(message, InfoBarSeverity.Error);
        }
    }

    private async Task ToggleRecordingAsync()
    {
        if (!TryGetDevice(out var device)) return;

        if (AppState.Adb.IsRecording)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AdbManager");
            var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_SavingRecording"),
                () => AppState.Adb.StopRecordingAsync(device!.Serial, directory));
            _recordButton.Content = L("Tools_Record");
            MainWindow.Notify(ok ? L("Msg_RecordSaved") + " " + message : message,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        else
        {
            var remote = "/sdcard/Movies/adbmanager_record.mp4";
            var (ok, message) = await AppState.Adb.StartRecordingAsync(device!.Serial, remote, 180);
            _recordButton.Content = L("Tools_StopRecord");
            MainWindow.Notify(ok ? L("Msg_RecordStarted") : message,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
    }

    private async Task RebootAsync(string mode)
    {
        if (!TryGetDevice(out var device)) return;
        await AppState.Adb.RebootAsync(device!.Serial, mode);
        MainWindow.Notify(L("Msg_Done"), InfoBarSeverity.Success);
    }

    private void AppendText(Microsoft.UI.Dispatching.DispatcherQueue? queue, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var payload = text + "\n";
        if (queue is { } q && !q.HasThreadAccess) q.TryEnqueue(() => _output.Text += payload);
        else _output.Text += payload;
    }

    private async Task RunCommandAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var command = _command.Text.Trim();
        if (string.IsNullOrEmpty(command)) return;

        _output.Text = "";
        // adb 输出在后台线程，需调度回 UI 线程
        var queue = DispatcherQueue;
        var streamed = false;
        var result = await AppState.Adb.ShellAsync(device!.Serial, command, line =>
        {
            streamed = true;
            queue?.TryEnqueue(() => _output.Text += line + "\n");
        }, TimeSpan.FromMinutes(2));
        if (!streamed && !string.IsNullOrWhiteSpace(result.Message)) AppendText(queue, result.Message);
    }

    private Button ActivateShellButton(string name, string command)
    {
        var button = Miuix.SecondaryButton(name);
        ToolTipService.SetToolTip(button, "adb shell " + command);
        button.Click += async (_, _) => await ActivateToolAsync(command);
        return button;
    }

    /// <summary>Device Owner 激活按钮：receiver 为空时先从 dumpsys 探测管理员组件。</summary>
    private Button ActivateDpmButton(string name, string package, string? receiver = null)
    {
        var admin = string.IsNullOrEmpty(receiver) ? package + "/<自动探测>" : receiver;
        var button = Miuix.SecondaryButton(name);
        ToolTipService.SetToolTip(button, $"adb shell dpm set-device-owner {admin}");
        button.Click += async (_, _) => await ActivateDpmAsync(package, receiver);
        return button;
    }

    private async Task ActivateDpmAsync(string package, string? receiver)
    {
        if (!TryGetDevice(out var device)) return;
        var serial = device!.Serial;

        var admin = receiver;
        if (string.IsNullOrEmpty(admin))
        {
            admin = await MainWindow.RunBusyAsync(L("Busy_Working"),
                () => AppState.Adb.FindDeviceAdminReceiverAsync(serial, package));
            if (string.IsNullOrEmpty(admin))
            {
                MainWindow.Notify(L("Tools_Act_Err_NoAdmin"), InfoBarSeverity.Error);
                return;
            }
        }

        await ActivateToolAsync($"dpm set-device-owner {admin}");
    }

    /// <summary>执行常用工具激活脚本（Shizuku / Scene 等），输出实时回显。</summary>
    private async Task ActivateToolAsync(string command)
    {
        if (!TryGetDevice(out var device)) return;

        _output.Text = "";
        var queue = DispatcherQueue;
        var result = await AppState.Adb.ShellAsync(device!.Serial, command,
            line => queue?.TryEnqueue(() => _output.Text += line + "\n"), TimeSpan.FromSeconds(60));
        AppendText(queue, result.Message);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Message))
            MainWindow.Notify(result.Success ? L("Msg_Done") : result.Message,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    /// <summary>WiFi 检测修复（重置 captive portal 检测源 + 重启 WiFi），参考手机端实现。</summary>
    private async Task WifiFixAsync()
    {
        if (!TryGetDevice(out var device)) return;

        _output.Text = "";
        var queue = DispatcherQueue;
        void Append(string text) => queue?.TryEnqueue(() => _output.Text += text + "\n");

        var commands = new[]
        {
            "settings put global captive_portal_mode 1",
            "settings put global captive_portal_http_url http://connect.rom.miui.com/generate_204",
            "settings put global captive_portal_https_url https://connect.rom.miui.com/generate_204",
            "settings put global captive_portal_fallback_url http://cp.cloudflare.com/generate_204",
            "svc wifi disable",
            "svc wifi enable"
        };

        foreach (var command in commands)
        {
            Append("adb shell " + command);
            var result = await AppState.Adb.ShellAsync(device!.Serial, command, null, TimeSpan.FromSeconds(30));
            if (!string.IsNullOrWhiteSpace(result.Message)) Append(result.Message);
        }

        MainWindow.Notify(L("Msg_Done"), InfoBarSeverity.Success);
    }
}
