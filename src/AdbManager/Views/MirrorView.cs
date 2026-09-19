using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbManager.Views;

/// <summary>
/// 投屏与控制：通过内置 scrcpy 实时镜像设备屏幕并用鼠标键盘控制。
/// 手机与 Wear OS 手表均支持；手表预设走低分辨率 + 低码率。
/// </summary>
public sealed class MirrorView : PageBase
{
    private readonly ComboBox _preset = new() { MinWidth = 200, SelectedIndex = 0 };
    private readonly TextBox _maxSize = Miuix.Input("0");
    private readonly TextBox _maxFps = Miuix.Input("0");
    private readonly TextBox _bitRate = Miuix.Input("8");
    private readonly CheckBox _noAudio = new();
    private readonly CheckBox _readOnly = new();
    private readonly CheckBox _screenOff = new();
    private readonly CheckBox _stayAwake = new();
    private readonly Button _startButton = Miuix.SuccessButton("");
    private readonly Button _stopButton = Miuix.DangerButton("");
    private readonly TextBlock _status = Miuix.Body("", true);
    private bool _loaded;

    // 运行时选项状态（仅投屏运行中有效）
    private string? _mirrorSerial;   // 投屏目标设备；运行中切换选项发给它，而不是当前选中的设备
    private string? _savedStayAwake; // 设备原有 stay_on_while_plugged_in 值，投屏结束时恢复
    private bool _stayAwakeApplied;  // 保持唤醒已由本程序开启
    private bool _screenOffApplied;  // 屏幕已熄灭（启动选项或运行中切换）

    // stay_on_while_plugged_in 位掩码：1=AC 2=USB 4=无线，7=任一供电即唤醒
    private const string StayAwakeSetting = "stay_on_while_plugged_in";
    private const string StayAwakeOn = "7";
    private const int KeyEventPower = 26;
    private const int KeyEventWakeup = 224; // 只唤醒不休眠，避免电源键把亮屏设备再按灭

    public MirrorView()
    {
        _status.VerticalAlignment = VerticalAlignment.Center;
        Content = Build();
        AppState.Scrcpy.Exited += OnScrcpyExited;
    }

    private void OnScrcpyExited(int? code)
    {
        var queue = DispatcherQueue;
        if (queue is { } q && !q.HasThreadAccess) { q.TryEnqueue(() => OnScrcpyExited(code)); return; }
        UpdateButtons();
        MainWindow.Notify(L("Mirror_Exit"), InfoBarSeverity.Informational);
        _ = RestoreRuntimeOptionsAsync();
    }

    public override Task OnShownAsync()
    {
        if (_loaded) { UpdateButtons(); return Task.CompletedTask; }
        _loaded = true;

        _preset.Items.Add(L("Mirror_PresetAuto"));
        _preset.Items.Add(L("Mirror_PresetPhone"));
        _preset.Items.Add(L("Mirror_PresetWatch"));
        _preset.SelectedIndex = 0;

        _maxSize.PlaceholderText = L("Mirror_MaxSize");
        _maxFps.PlaceholderText = L("Mirror_MaxFps");
        _bitRate.PlaceholderText = L("Mirror_BitRate");
        _noAudio.Content = L("Mirror_NoAudio");
        _readOnly.Content = L("Mirror_ReadOnly");
        _screenOff.Content = L("Mirror_ScreenOff");
        _stayAwake.Content = L("Mirror_StayAwake");
        _startButton.Content = L("Mirror_Start");
        _stopButton.Content = L("Mirror_Stop");
        _status.Text = L("Mirror_NotRunning");

        _startButton.Click += async (_, _) => await StartAsync();
        _stopButton.Click += (_, _) => AppState.Scrcpy.Stop();

        // 熄屏 / 保持唤醒支持投屏运行中实时切换；未投屏时仅记录勾选，启动时生效
        _screenOff.Checked += async (_, _) => await ApplyScreenOffAsync();
        _screenOff.Unchecked += async (_, _) => await ApplyScreenOffAsync();
        _stayAwake.Checked += async (_, _) => await ApplyStayAwakeAsync();
        _stayAwake.Unchecked += async (_, _) => await ApplyStayAwakeAsync();

        UpdateButtons();
        return Task.CompletedTask;
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        var mirrorPanel = new StackPanel { Spacing = 10 };
        mirrorPanel.Children.Add(Miuix.SectionTitle(L("Mirror_Title")));
        mirrorPanel.Children.Add(Miuix.Body(L("Mirror_Desc"), true));
        mirrorPanel.Children.Add(Miuix.SettingRow(L("Mirror_Preset"), null, _preset));

        _maxSize.MinWidth = 120;
        _maxFps.MinWidth = 120;
        _bitRate.MinWidth = 120;
        mirrorPanel.Children.Add(Miuix.Horizontal(_maxSize, _maxFps, _bitRate));
        mirrorPanel.Children.Add(Miuix.Horizontal(_noAudio, _readOnly));
        mirrorPanel.Children.Add(Miuix.Horizontal(_screenOff, _stayAwake));
        mirrorPanel.Children.Add(Miuix.Horizontal(_startButton, _stopButton, _status));
        root.Children.Add(Miuix.Card(mirrorPanel));

        var keysPanel = new StackPanel { Spacing = 10 };
        keysPanel.Children.Add(Miuix.SectionTitle(L("Mirror_Keys")));
        keysPanel.Children.Add(Miuix.Horizontal(
            KeyButton(L("Key_Home"), 3),
            KeyButton(L("Key_Back"), 4),
            KeyButton(L("Key_Recents"), 187)));
        keysPanel.Children.Add(Miuix.Horizontal(
            KeyButton(L("Key_Power"), 26),
            KeyButton(L("Key_VolUp"), 24),
            KeyButton(L("Key_VolDown"), 25)));
        root.Children.Add(Miuix.Card(keysPanel));

        return root;
    }

    private async Task StartAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var preset = _preset.SelectedIndex;
        if (preset == 0)
        {
            // 自动检测：Wear OS 手表应用低分辨率预设（官方不支持 screenrecord，scrcpy 是唯一可行路径）
            var characteristics = await AppState.Adb.RunAsync(
                $"-s \"{device!.Serial}\" shell getprop ro.build.characteristics",
                null, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            if (characteristics.StdOut.Contains("watch", StringComparison.OrdinalIgnoreCase))
            {
                preset = 2;
                MainWindow.Notify(L("Mirror_WatchDetected"), InfoBarSeverity.Informational);
            }
        }

        int.TryParse(_maxSize.Text.Trim(), out var maxSize);
        int.TryParse(_maxFps.Text.Trim(), out var maxFps);
        double.TryParse(_bitRate.Text.Trim(), out var bitRate);

        if (preset == 2)
        {
            maxSize = 400;
            maxFps = 0;
            bitRate = 2;
            _noAudio.IsChecked = true;
        }

        var options = new ScrcpyOptions
        {
            Serial = device!.Serial,
            MaxSize = maxSize,
            MaxFps = maxFps,
            BitRateMbps = bitRate,
            NoAudio = _noAudio.IsChecked == true,
            ReadOnly = _readOnly.IsChecked == true,
            ScreenOff = _screenOff.IsChecked == true
        };

        var (ok, message) = AppState.Scrcpy.Start(options, AppState.Adb.AdbPath);
        _status.Text = message;
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok)
        {
            _mirrorSerial = device.Serial;
            _savedStayAwake = null;
            _stayAwakeApplied = false;
            _screenOffApplied = _screenOff.IsChecked == true; // scrcpy 原生熄屏：不弹锁屏，退出时自动恢复
            await ApplyStayAwakeAsync(); // 保持唤醒统一由本程序设置，退出时恢复设备原值
        }
        UpdateButtons();
    }

    // ---------------- 运行时选项：熄灭屏幕 / 保持唤醒 ----------------
    // scrcpy 的选项只在进程启动时生效，这里让两个勾选框在投屏运行中实时切换：
    //   保持唤醒 → settings put system stay_on_while_plugged_in，投屏结束恢复设备原值；
    //   熄灭屏幕 → 启动时仍走 scrcpy 原生 --turn-screen-off（不弹锁屏），
    //              运行中切换用电源键/唤醒键模拟（会触发系统锁屏，重新亮屏需解锁）。

    private async Task ApplyStayAwakeAsync()
    {
        if (!AppState.Scrcpy.IsRunning || _mirrorSerial is null) return;
        var enable = _stayAwake.IsChecked == true;
        if (enable == _stayAwakeApplied) return;
        var serial = _mirrorSerial;

        if (enable)
        {
            var current = await AppState.Adb.ShellAsync(serial, $"settings get system {StayAwakeSetting}");
            _savedStayAwake = NormalizeStayAwake(current.StdOut);
            await AppState.Adb.ShellAsync(serial, $"settings put system {StayAwakeSetting} {StayAwakeOn}");
        }
        else
        {
            await AppState.Adb.ShellAsync(serial, $"settings put system {StayAwakeSetting} {_savedStayAwake ?? "0"}");
        }
        _stayAwakeApplied = enable;
    }

    private async Task ApplyScreenOffAsync()
    {
        if (!AppState.Scrcpy.IsRunning || _mirrorSerial is null) return;
        var off = _screenOff.IsChecked == true;
        if (off == _screenOffApplied) return;
        var serial = _mirrorSerial;

        if (off)
        {
            // 已熄屏时再按电源键会把屏幕点亮，先探测当前状态
            if (await DetectScreenOnAsync(serial) == true)
                await AppState.Adb.ShellAsync(serial, $"input keyevent {KeyEventPower}");
        }
        else
        {
            // WAKEUP 对已亮屏设备是无害操作；dismiss-keyguard 只清除非安全锁屏，不绕过密码
            await AppState.Adb.ShellAsync(serial, $"input keyevent {KeyEventWakeup}");
            await AppState.Adb.ShellAsync(serial, "wm dismiss-keyguard");
        }
        _screenOffApplied = off;
    }

    /// <summary>投屏结束：恢复保持唤醒原值；若屏幕处于熄灭状态则唤醒，避免手机停留在黑屏锁定。</summary>
    private async Task RestoreRuntimeOptionsAsync()
    {
        var serial = _mirrorSerial;
        _mirrorSerial = null;
        if (serial is null) return;

        if (_stayAwakeApplied)
        {
            _stayAwakeApplied = false;
            await AppState.Adb.ShellAsync(serial, $"settings put system {StayAwakeSetting} {_savedStayAwake ?? "0"}");
        }
        _savedStayAwake = null;

        if (_screenOffApplied)
        {
            _screenOffApplied = false;
            await AppState.Adb.ShellAsync(serial, $"input keyevent {KeyEventWakeup}");
            await AppState.Adb.ShellAsync(serial, "wm dismiss-keyguard");
        }
    }

    /// <summary>探测设备屏幕是否点亮；null 表示无法判断（此时按亮屏处理）。</summary>
    private static async Task<bool?> DetectScreenOnAsync(string serial)
    {
        var power = await AppState.Adb.ShellAsync(serial, "dumpsys power", null, TimeSpan.FromSeconds(10));
        if (power.Success)
        {
            var on = ParseScreenState(power.StdOut);
            if (on is not null) return on;
        }
        // scrcpy 原生熄屏直接改显示状态，power 里可能仍报 Awake，再看 dumpsys display
        var display = await AppState.Adb.ShellAsync(serial, "dumpsys display", null, TimeSpan.FromSeconds(10));
        return display.Success ? ParseScreenState(display.StdOut) : null;
    }

    private static bool? ParseScreenState(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            string? value =
                line.StartsWith("Display Power: state=", StringComparison.Ordinal) ? line["Display Power: state=".Length..] :
                line.StartsWith("mScreenState=", StringComparison.Ordinal) ? line["mScreenState=".Length..] :
                line.StartsWith("mWakefulness=", StringComparison.Ordinal) ? line["mWakefulness=".Length..] :
                null;
            if (value is null) continue;
            if (value.StartsWith("OFF", StringComparison.Ordinal) || value.StartsWith("DOZE", StringComparison.Ordinal) ||
                value.StartsWith("Asleep", StringComparison.Ordinal) || value.StartsWith("Dozing", StringComparison.Ordinal))
                return false;
            if (value.StartsWith("ON", StringComparison.Ordinal) || value.StartsWith("Awake", StringComparison.Ordinal) ||
                value.StartsWith("VR", StringComparison.Ordinal))
                return true;
        }
        return null;
    }

    /// <summary>settings get 返回非数字（如未设置时的 "null"/空）视为 0。</summary>
    private static string NormalizeStayAwake(string output)
    {
        var value = output.Trim();
        return value.Length > 0 && value.Length <= 2 && value.All(char.IsDigit) ? value : "0";
    }

    private void UpdateButtons()
    {
        var running = AppState.Scrcpy.IsRunning;
        _status.Text = running ? L("Mirror_Running") : L("Mirror_NotRunning");
        _startButton.IsEnabled = !running;
        _stopButton.IsEnabled = running;
    }

    private Button KeyButton(string text, int keyCode)
    {
        var button = Miuix.SecondaryButton(text);
        button.Height = 40;
        button.Click += async (_, _) =>
        {
            if (!TryGetDevice(out var device)) return;
            await AppState.Adb.ShellAsync(device!.Serial, $"input keyevent {keyCode}");
        };
        return button;
    }
}
