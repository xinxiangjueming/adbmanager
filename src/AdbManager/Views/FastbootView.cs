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
        var rebootFastbootd = Miuix.SecondaryButton(L("Fastboot_RebootFastbootd"));
        rebootFastbootd.Click += async (_, _) => await FastbootRebootAsync("fastboot");
        var rebootRecovery = Miuix.SecondaryButton(L("Fastboot_RebootRecovery"));
        rebootRecovery.Click += async (_, _) => await FastbootRebootAsync("recovery");

        var rebootPanel = new StackPanel { Spacing = 10 };
        rebootPanel.Children.Add(Miuix.SectionTitle(L("Tools_Reboot")));
        rebootPanel.Children.Add(Miuix.Horizontal(rebootSystem, rebootBootloader, rebootFastbootd, rebootRecovery));
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

        void Render()
        {
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

            _device ??= devices.FirstOrDefault();
        }

        if (_queue is { } queue && !queue.HasThreadAccess) queue.TryEnqueue(Render);
        else Render();
    }

    private Task RenderSelection() => RefreshDevicesAsync();

    private async Task<bool> EnsureFastbootDeviceAsync()
    {
        if (_device is not null) return true;
        var devices = await AppState.Adb.GetFastbootDevicesAsync();
        _device = devices.FirstOrDefault();
        if (_device is not null) return true;

        MainWindow.Notify(L("Fastboot_NoDevice"), InfoBarSeverity.Warning);
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

    private async Task LoadPartitionsAsync()
    {
        if (!TryGetDevice(out var adbDevice)) return;

        var partitions = await MainWindow.RunBusyAsync(L("Busy_LoadingPartitions"),
            () => AppState.Adb.ListPartitionsAsync(adbDevice!.Serial));
        _extractPartition.Items.Clear();
        foreach (var partition in partitions) _extractPartition.Items.Add(partition.Name);
        if (_extractPartition.Items.Count > 0) _extractPartition.SelectedIndex = 0;

        MainWindow.Notify(L("Msg_Done") + $" ({partitions.Count})", InfoBarSeverity.Success);
    }

    private async Task ExtractAsync()
    {
        if (!TryGetDevice(out var adbDevice)) return;

        var partition = (_extractPartition.SelectedItem as string ?? _extractPartition.Text ?? "").Trim();
        if (partition.Length == 0) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }

        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;

        _output.Text = "";
        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_ExtractingImage"),
            () => AppState.Adb.ExtractPartitionAsync(adbDevice!.Serial, partition, folder.Path, AppendOutput));
        MainWindow.Notify(ok ? L("Msg_Done") + " " + message : message,
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }
}
