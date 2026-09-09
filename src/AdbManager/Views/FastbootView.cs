using AdbManager.Models;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbManager.Views;

public sealed class FastbootView : PageBase
{
    private readonly StackPanel _deviceList = new() { Spacing = 8 };
    private readonly TextBlock _emptyDevices = new() { TextWrapping = TextWrapping.Wrap };

    private readonly ComboBox _flashPartition = new() { MinWidth = 180, IsEditable = true };
    private readonly TextBlock _imagePathText = new() { TextWrapping = TextWrapping.Wrap };
    private string _imagePath = "";

    private readonly ComboBox _extractPartition = new() { MinWidth = 200, MaxDropDownHeight = 320 };

    private readonly TextBox _output = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 150,
        CornerRadius = new CornerRadius(Miuix.ControlRadius)
    };

    private FastbootDevice? _device;
    private readonly DispatcherQueue? _queue;
    private bool _initialized;
    private bool _polling;

    private static readonly string[] CommonPartitions =
    {
        "boot", "boot_a", "boot_b", "init_boot", "vendor_boot", "system", "system_a", "system_b",
        "vendor", "vendor_a", "vendor_b", "product", "vbmeta", "vbmeta_a", "vbmeta_b",
        "dtbo", "recovery", "super", "cust", "modem", "abl", "xbl", "tz", "keymaster"
    };

    public FastbootView()
    {
        _queue = DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Content = Build();
    }

    public override async Task OnShownAsync()
    {
        if (_initialized) return;
        _initialized = true;

        foreach (var partition in CommonPartitions) _flashPartition.Items.Add(partition);
        _flashPartition.SelectedIndex = 0;

        _emptyDevices.Text = L("Fastboot_NoDevice");
        await RefreshDevicesAsync();
        StartPolling();
    }

    /// <summary>轮询 fastboot 设备：插拔/模式切换时自动刷新列表与分区表。</summary>
    private void StartPolling()
    {
        if (_queue is not { } queue) return;

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.Tick += async (_, _) =>
        {
            if (XamlRoot is null) { timer.Stop(); return; }  // 页面已切走
            await PollAsync();
        };
        timer.Start();
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var devices = await AppState.Adb.GetFastbootDevicesAsync();
            var serial = devices.FirstOrDefault()?.Serial ?? "";
            if (serial == (_device?.Serial ?? "")) return;

            RenderDevices(devices);
            if (_device is not null) await LoadPartitionsAsync(notify: false);
        }
        finally
        {
            _polling = false;
        }
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        // 设备
        var refresh = Miuix.PrimaryButton(L("Common_Refresh"));
        refresh.Click += async (_, _) => await RefreshDevicesAsync();

        var devicePanel = new StackPanel { Spacing = 10 };
        devicePanel.Children.Add(Miuix.SectionTitle(L("Fastboot_Devices")));
        devicePanel.Children.Add(_emptyDevices);
        devicePanel.Children.Add(_deviceList);
        root.Children.Add(Miuix.Card(devicePanel));

        // 刷入 / 擦除
        var selectImage = Miuix.SecondaryButton(L("Fastboot_SelectImg"));
        selectImage.Click += async (_, _) => await PickImageAsync();

        var flash = Miuix.PrimaryButton(L("Fastboot_Flash"));
        flash.Click += async (_, _) => await FlashAsync();

        var erase = Miuix.DangerButton(L("Fastboot_Erase"));
        erase.Click += async (_, _) => await EraseAsync();

        var getvar = Miuix.SecondaryButton(L("Fastboot_GetvarAll"));
        getvar.Click += async (_, _) => await GetvarAllAsync();

        var flashPanel = new StackPanel { Spacing = 10 };
        flashPanel.Children.Add(Miuix.SectionTitle(L("Fastboot_Title")));
        flashPanel.Children.Add(Miuix.Horizontal(_flashPartition, selectImage, flash, erase, getvar));
        flashPanel.Children.Add(_imagePathText);
        root.Children.Add(Miuix.Card(flashPanel));

        // 重启
        var rebootSystem = Miuix.SecondaryButton(L("Fastboot_RebootSystem"));
        rebootSystem.Click += async (_, _) => await FastbootRebootAsync("");
        var rebootBootloader = Miuix.SecondaryButton(L("Fastboot_RebootBootloader"));
        rebootBootloader.Click += async (_, _) => await FastbootRebootAsync("bootloader");
        var rebootEdl = Miuix.SecondaryButton(L("Fastboot_RebootEdl"));
        rebootEdl.Click += async (_, _) => await FastbootRebootAsync("edl");
        var rebootRecovery = Miuix.SecondaryButton(L("Fastboot_RebootRecovery"));
        rebootRecovery.Click += async (_, _) => await FastbootRebootAsync("recovery");

        var rebootPanel = new StackPanel { Spacing = 10 };
        rebootPanel.Children.Add(Miuix.SectionTitle(L("Tools_Reboot")));
        rebootPanel.Children.Add(Miuix.Horizontal(rebootSystem, rebootBootloader, rebootEdl, rebootRecovery));
        root.Children.Add(Miuix.Card(rebootPanel));

        // 提取镜像（走 adb）
        var readPartitions = Miuix.SecondaryButton(L("Fastboot_ReadPartitions"));
        readPartitions.Click += async (_, _) => await LoadPartitionsAsync();

        var extract = Miuix.PrimaryButton(L("Fastboot_Extract"));
        extract.Click += async (_, _) => await ExtractAsync();

        var extractPanel = new StackPanel { Spacing = 10 };
        extractPanel.Children.Add(Miuix.SectionTitle(L("Fastboot_ExtractTitle")));
        extractPanel.Children.Add(Miuix.Horizontal(_extractPartition, readPartitions, extract));
        extractPanel.Children.Add(Miuix.Body(L("Fastboot_ExtractHint"), true));
        root.Children.Add(Miuix.Card(extractPanel));

        // 输出
        var outputPanel = new StackPanel { Spacing = 10 };
        outputPanel.Children.Add(Miuix.SectionTitle(L("Tools_Output")));
        outputPanel.Children.Add(_output);
        root.Children.Add(Miuix.Card(outputPanel));

        return root;
    }

    private void AppendOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var text = line + "\n";
        if (_queue is { } queue && !queue.HasThreadAccess) queue.TryEnqueue(() => _output.Text += text);
        else _output.Text += text;
    }

    private async Task RefreshDevicesAsync()
    {
        var devices = await AppState.Adb.GetFastbootDevicesAsync().ConfigureAwait(false);
        RenderDevices(devices);
    }

    /// <summary>把设备列表渲染到顶部区域（可在任意线程调用，内部切回 UI 线程）。</summary>
    private void RenderDevices(List<FastbootDevice> devices)
    {
        void Render()
        {
            // 当前选中项已不在列表中（拔线/重启到系统/进入 9008）时改为首个设备；列表为空则清空选择
            if (_device is null || devices.All(d => d.Serial != _device.Serial))
                _device = devices.FirstOrDefault();

            _deviceList.Children.Clear();
            _emptyDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var device in devices)
            {
                var button = Miuix.SecondaryButton(device.Display);
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                var captured = device;
                button.Click += (_, _) =>
                {
                    _device = captured;
                    MainWindow.Notify(captured.Display, InfoBarSeverity.Success);
                    _ = RenderSelection();
                };

                if (_device is not null && _device.Serial == device.Serial)
                    button.BorderBrush = Miuix.Brush("MiuixAccent");

                _deviceList.Children.Add(button);
            }
        }

        if (_queue is { } queue && !queue.HasThreadAccess) queue.TryEnqueue(Render);
        else Render();
    }

    private Task RenderSelection() => RefreshDevicesAsync();

    private async Task<bool> EnsureFastbootDeviceAsync(bool warn = true)
    {
        if (_device is not null) return true;

        // 页面首次渲染时设备可能尚未被 fastboot 枚举到，操作前重新查询并同步顶部列表
        var devices = await AppState.Adb.GetFastbootDevicesAsync();
        _device = devices.FirstOrDefault();
        RenderDevices(devices);
        if (_device is not null) return true;

        if (warn) MainWindow.Notify(L("Fastboot_NoDevice"), InfoBarSeverity.Warning);
        return false;
    }

    private async Task PickImageAsync()
    {
        var file = await Pickers.PickFileAsync(".img", ".bin", ".mbn", ".elf");
        if (file is null) return;
        _imagePath = file.Path;
        _imagePathText.Text = _imagePath;
    }

    private async Task FlashAsync()
    {
        if (!await EnsureFastbootDeviceAsync()) return;

        var partition = (_flashPartition.Text ?? "").Trim();
        if (partition.Length == 0) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (_imagePath.Length == 0) { MainWindow.Notify(L("Fastboot_SelectImg"), InfoBarSeverity.Warning); return; }

        var dialog = new ContentDialog
        {
            Title = L("Fastboot_Flash"),
            Content = L("Fastboot_FlashConfirm") + $"\n{partition} <- {_imagePath}",
            PrimaryButtonText = L("Common_Confirm"),
            CloseButtonText = L("Common_Cancel"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await MainWindow.RunBusyAsync(L("Busy_Flashing"),
            () => AppState.Adb.FastbootFlashAsync(_device!.Serial, partition, _imagePath, AppendOutput));
        AppendOutput(result.Combined().Trim());
        MainWindow.Notify(result.Success ? L("Msg_Done") : result.Message,
            result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task EraseAsync()
    {
        if (!await EnsureFastbootDeviceAsync()) return;

        var partition = (_flashPartition.Text ?? "").Trim();
        if (partition.Length == 0) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }

        var dialog = new ContentDialog
        {
            Title = L("Fastboot_Erase"),
            Content = L("Fastboot_EraseConfirm") + $"\n{partition}",
            PrimaryButtonText = L("Common_Confirm"),
            CloseButtonText = L("Common_Cancel"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await MainWindow.RunBusyAsync(L("Busy_Erasing"),
            () => AppState.Adb.FastbootEraseAsync(_device!.Serial, partition, AppendOutput));
        AppendOutput(result.Combined().Trim());
        MainWindow.Notify(result.Success ? L("Msg_Done") : result.Message,
            result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task GetvarAllAsync()
    {
        if (!await EnsureFastbootDeviceAsync()) return;
        _output.Text = "";
        var result = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => AppState.Adb.FastbootGetvarAllAsync(_device!.Serial, AppendOutput));
        AppendOutput(result.Combined().Trim());
    }

    private async Task FastbootRebootAsync(string mode)
    {
        if (!await EnsureFastbootDeviceAsync()) return;
        var result = await AppState.Adb.FastbootRebootAsync(_device!.Serial, mode, AppendOutput);
        MainWindow.Notify(result.Success ? L("Msg_Done") : result.Message,
            result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task LoadPartitionsAsync(bool notify = true)
    {
        // 设备停在 bootloader/fastboot 时 adb 不在线，优先用 fastboot getvar all 解析分区
        if (_device is null) await EnsureFastbootDeviceAsync(warn: false);
        if (_device is not null)
        {
            var fastbootNames = await MainWindow.RunBusyAsync(L("Busy_LoadingPartitions"),
                () => AppState.Adb.FastbootListPartitionsAsync(_device!.Serial));
            if (fastbootNames.Count > 0)
            {
                FillPartitions(fastbootNames);
                if (notify) MainWindow.Notify(L("Msg_Done") + $" ({fastbootNames.Count})", InfoBarSeverity.Success);
                return;
            }
        }

        // 回退：adb 在线时读 /dev/block/by-name
        if (!TryGetDevice(out var adbDevice)) return;

        var partitions = await MainWindow.RunBusyAsync(L("Busy_LoadingPartitions"),
            () => AppState.Adb.ListPartitionsAsync(adbDevice!.Serial));
        FillPartitions(partitions.Select(p => p.Name));

        MainWindow.Notify(L("Msg_Done") + $" ({partitions.Count})", InfoBarSeverity.Success);
    }

    private void FillPartitions(IEnumerable<string> names)
    {
        _extractPartition.Items.Clear();
        foreach (var name in names) _extractPartition.Items.Add(name);
        if (_extractPartition.Items.Count > 0) _extractPartition.SelectedIndex = 0;
    }

    private async Task ExtractAsync()
    {
        var partition = (_extractPartition.SelectedItem as string ?? _extractPartition.Text ?? "").Trim();
        if (partition.Length == 0) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }

        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;

        _output.Text = "";

        // adb 在线（系统 / recovery）：dd + pull，兼容性最好
        if (TryGetDevice(out var adbDevice, warn: false))
        {
            var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_ExtractingImage"),
                () => AppState.Adb.ExtractPartitionAsync(adbDevice!.Serial, partition, folder.Path, AppendOutput));
            MainWindow.Notify(ok ? L("Msg_Done") + " " + message : message,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            return;
        }

        // adb 不在线：设备停在 fastboot 时用 fastboot fetch（需 fastbootd 且设备支持）
        if (_device is null) await EnsureFastbootDeviceAsync(warn: false);
        if (_device is null) { MainWindow.Notify(L("Msg_SelectDevice"), InfoBarSeverity.Warning); return; }

        var (fetched, fetchMessage) = await MainWindow.RunBusyAsync(L("Busy_ExtractingImage"),
            () => AppState.Adb.FastbootFetchPartitionAsync(_device!.Serial, partition, folder.Path, AppendOutput));

        if (!fetched && fetchMessage.Contains("does not support", StringComparison.OrdinalIgnoreCase))
        {
            MainWindow.Notify(L("Fastboot_Err_NoFetch"), InfoBarSeverity.Error);
            return;
        }

        var text = string.IsNullOrWhiteSpace(fetchMessage) ? L("Msg_Failed") : fetchMessage;
        MainWindow.Notify(fetched ? L("Msg_Done") + " " + text : text,
            fetched ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }
}
