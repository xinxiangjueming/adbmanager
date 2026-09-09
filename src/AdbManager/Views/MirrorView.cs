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
            ScreenOff = _screenOff.IsChecked == true,
            StayAwake = _stayAwake.IsChecked == true
        };

        var (ok, message) = AppState.Scrcpy.Start(options, AppState.Adb.AdbPath);
        _status.Text = message;
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        UpdateButtons();
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
