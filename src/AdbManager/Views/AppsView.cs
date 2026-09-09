using AdbManager.Models;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AdbManager.Views;

public sealed class AppsView : PageBase
{
    private readonly TextBox _search = Miuix.Input("");
    private readonly ComboBox _filter = new() { MinWidth = 140 };
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        MinHeight = 320,
        MaxHeight = 520
    };

    private readonly List<PackageInfo> _packages = new();
    private bool _loaded;

    public AppsView()
    {
        // 列表增删/筛选时的进出动画
        _list.ItemContainerTransitions = new TransitionCollection { new AddDeleteThemeTransition() };
        Content = Build();
    }

    public override async Task OnShownAsync()
    {
        if (_loaded) return;
        _loaded = true;

        _search.PlaceholderText = L("Apps_Search");
        _filter.Items.Add(L("Apps_ThirdParty"));
        _filter.Items.Add(L("Apps_System"));
        _filter.Items.Add(L("Apps_Disabled"));
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => ApplyFilter();
        _search.TextChanged += (_, _) => ApplyFilter();

        await LoadAsync();
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        var installApk = Miuix.PrimaryButton(L("Apps_InstallApk"));
        installApk.Click += async (_, _) => await InstallApkAsync();
        var installBundle = Miuix.SecondaryButton(L("Apps_InstallBundle"));
        installBundle.Click += async (_, _) => await InstallBundleAsync();
        var reload = Miuix.SecondaryButton(L("Common_Refresh"));
        reload.Click += async (_, _) => await LoadAsync();

        var topCard = Miuix.Card(new StackPanel { Spacing = 10 });
        ((StackPanel)topCard.Child).Children.Add(Miuix.SectionTitle(L("Apps_Title")));
        ((StackPanel)topCard.Child).Children.Add(Miuix.Horizontal(installApk, installBundle, reload));
        root.Children.Add(topCard);

        var listPanel = new StackPanel { Spacing = 10 };
        listPanel.Children.Add(Miuix.Horizontal(_search, _filter));
        listPanel.Children.Add(_list);
        root.Children.Add(Miuix.Card(listPanel));

        var uninstall = Miuix.DangerButton(L("Apps_Uninstall"));
        uninstall.Click += async (_, _) => await UninstallAsync();
        var freeze = Miuix.SecondaryButton(L("Apps_Freeze"));
        freeze.Click += async (_, _) => await FreezeAsync(true);
        var unfreeze = Miuix.SecondaryButton(L("Apps_Unfreeze"));
        unfreeze.Click += async (_, _) => await FreezeAsync(false);
        var forceStop = Miuix.SecondaryButton(L("Apps_ForceStop"));
        forceStop.Click += async (_, _) => await RunOnSelectedAsync((s, p) => AppState.Adb.ForceStopAsync(s, p), L("Msg_Done"));
        var clearData = Miuix.SecondaryButton(L("Apps_ClearData"));
        clearData.Click += async (_, _) => await RunOnSelectedAsync((s, p) => AppState.Adb.ClearDataAsync(s, p), L("Msg_Done"));
        var extractApk = Miuix.PrimaryButton(L("Apps_ExtractApk"));
        extractApk.Click += async (_, _) => await ExtractApkAsync();

        var actionCard = Miuix.Card(new StackPanel { Spacing = 10 });
        ((StackPanel)actionCard.Child).Children.Add(Miuix.Horizontal(uninstall, freeze, unfreeze, forceStop, clearData));
        ((StackPanel)actionCard.Child).Children.Add(Miuix.Horizontal(extractApk));
        root.Children.Add(actionCard);

        return root;
    }

    /// <summary>提取选中应用的 APK（含拆分包）到电脑。</summary>
    private async Task ExtractApkAsync()
    {
        if (Selected() is not { } package) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;

        var targetDir = System.IO.Path.Combine(folder.Path, package.PackageName);
        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_ExtractingApk"),
            () => AppState.Adb.ExtractApkAsync(device!.Serial, package.PackageName, targetDir));
        MainWindow.Notify(ok ? L("Msg_ExtractOk") + " " + message : message,
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private PackageInfo? Selected() => _list.SelectedItem as PackageInfo;

    private async Task LoadAsync()
    {
        if (!TryGetDevice(out var device)) return;

        AppState.Log.Info(L("Apps_Loading"));
        _packages.Clear();
        _packages.AddRange(await MainWindow.RunBusyAsync(L("Apps_Loading"),
            () => AppState.Adb.ListPackagesAsync(device!.Serial)));
        ApplyFilter();
        AppState.Log.Success(L("Apps_Loaded", _packages.Count));
    }

    private void ApplyFilter()
    {
        IEnumerable<PackageInfo> query = _filter.SelectedIndex switch
        {
            1 => _packages.Where(p => p.IsSystem),
            2 => _packages.Where(p => p.IsDisabled),
            _ => _packages.Where(p => !p.IsSystem)
        };

        var keyword = _search.Text.Trim();
        if (!string.IsNullOrEmpty(keyword))
            query = query.Where(p => p.PackageName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        _list.ItemsSource = query.ToList();
    }

    private async Task InstallApkAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var file = await Pickers.PickFileAsync(".apk");
        if (file is null) return;

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Installing"),
            () => AppState.Adb.InstallApkAsync(device!.Serial, file.Path));
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }

    private async Task InstallBundleAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var file = await Pickers.PickFileAsync(".apks", ".zip", ".xapk");
        if (file is null) return;

        string directory;
        try
        {
            directory = AdbService.ExtractApks(file.Path);
        }
        catch (Exception ex)
        {
            MainWindow.Notify(L("Msg_Failed") + " " + ex.Message, InfoBarSeverity.Error);
            return;
        }

        var apks = Directory.GetFiles(directory, "*.apk", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetFileName(f).Contains("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        if (apks.Count == 0)
        {
            MainWindow.Notify(L("Msg_Failed") + " " + L("Apps_Err_NoApkInBundle"), InfoBarSeverity.Error);
            return;
        }

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Installing"),
            () => AppState.Adb.InstallMultipleAsync(device!.Serial, apks));
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }

    private async Task UninstallAsync()
    {
        if (Selected() is not { } package) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var dialog = new ContentDialog
        {
            Title = L("Apps_Uninstall"),
            Content = L("Msg_ConfirmUninstall") + "\n" + package.PackageName,
            PrimaryButtonText = L("Common_Confirm"),
            CloseButtonText = L("Common_Cancel"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => AppState.Adb.UninstallAsync(device!.Serial, package.PackageName, false));
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }

    private async Task FreezeAsync(bool frozen)
    {
        if (Selected() is not { } package) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        if (frozen)
        {
            var dialog = new ContentDialog
            {
                Title = L("Apps_Freeze"),
                Content = L("Msg_ConfirmFreeze") + "\n" + package.PackageName,
                PrimaryButtonText = L("Common_Confirm"),
                CloseButtonText = L("Common_Cancel"),
                XamlRoot = App.MainWindow.Content.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => AppState.Adb.SetPackageFrozenAsync(device!.Serial, package.PackageName, frozen));
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }

    private async Task RunOnSelectedAsync(Func<string, string, Task<(bool, string)>> action, string successText)
    {
        if (Selected() is not { } package) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Working"),
            () => action(device!.Serial, package.PackageName));
        MainWindow.Notify(ok ? successText : message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok) await LoadAsync();
    }
}
