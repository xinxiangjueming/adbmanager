using AdbManager.Models;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AdbManager.Views;

public sealed class FilesView : PageBase
{
    private string _path = "/sdcard";
    private readonly TextBlock _pathText = Miuix.Body("/sdcard");
    private readonly StackPanel _list = new() { Spacing = 6 };
    private RemoteFileItem? _selected;
    private RemoteFileItem? _clipboardItem;
    private bool _clipboardIsCut;

    public FilesView()
    {
        // 目录条目入场动画（淡入 + 上滑）
        _list.Transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromVerticalOffset = 14, IsStaggeringEnabled = false }
        };
        Content = Build();
    }

    public override async Task OnShownAsync()
    {
        if (_list.Children.Count == 0) await BrowseAsync(_path);
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        var up = Miuix.SecondaryButton(L("Files_Up"));
        up.Click += async (_, _) =>
        {
            var parent = Path.GetDirectoryName(_path.TrimEnd('/'))?.Replace('\\', '/') ?? "/";
            await BrowseAsync(string.IsNullOrEmpty(parent) ? "/" : parent);
        };

        var home = Miuix.SecondaryButton(L("Files_Home"));
        home.Click += async (_, _) => await BrowseAsync("/");

        var refresh = Miuix.PrimaryButton(L("Common_Refresh"));
        refresh.Click += async (_, _) => await BrowseAsync(_path);

        var topCard = Miuix.Card(new StackPanel { Spacing = 10 });
        var top = (StackPanel)topCard.Child;
        top.Children.Add(Miuix.SectionTitle(L("Files_Title")));
        top.Children.Add(Miuix.Horizontal(up, home, refresh));
        top.Children.Add(_pathText);
        root.Children.Add(topCard);

        var listCard = Miuix.Card(new StackPanel { Spacing = 8 });
        ((StackPanel)listCard.Child).Children.Add(new ScrollViewer
        {
            Content = _list,
            MaxHeight = 430,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0)
        });
        root.Children.Add(listCard);

        var upload = Miuix.PrimaryButton(L("Files_Upload"));
        upload.Click += async (_, _) => await UploadAsync();
        var download = Miuix.SecondaryButton(L("Files_Download"));
        download.Click += async (_, _) => await DownloadAsync();
        var rename = Miuix.SecondaryButton(L("Common_Rename"));
        rename.Click += async (_, _) => await RenameAsync();
        var newFolder = Miuix.SecondaryButton(L("Files_NewFolder"));
        newFolder.Click += async (_, _) => await NewFolderAsync();
        var copy = Miuix.SecondaryButton(L("Common_Copy"));
        copy.Click += (_, _) => { _clipboardItem = _selected; _clipboardIsCut = false; };
        var cut = Miuix.SecondaryButton(L("Common_Cut"));
        cut.Click += (_, _) => { _clipboardItem = _selected; _clipboardIsCut = true; };
        var paste = Miuix.SecondaryButton(L("Common_Paste"));
        paste.Click += async (_, _) => await PasteAsync();
        var delete = Miuix.DangerButton(L("Common_Delete"));
        delete.Click += async (_, _) => await DeleteAsync();

        var actionCard = Miuix.Card(new StackPanel { Spacing = 10 });
        ((StackPanel)actionCard.Child).Children.Add(Miuix.Horizontal(upload, download, rename, newFolder));
        ((StackPanel)actionCard.Child).Children.Add(Miuix.Horizontal(copy, cut, paste, delete));
        root.Children.Add(actionCard);

        return root;
    }

    private async Task BrowseAsync(string path)
    {
        if (!TryGetDevice(out var device)) return;

        _path = path;
        _pathText.Text = _path;
        _selected = null;

        // 先显示内联 loading，再拉取目录
        ShowListLoading();
        var items = await AppState.Adb.ListDirectoryAsync(device!.Serial, _path);
        _list.Children.Clear();

        if (items.Count == 0)
        {
            _list.Children.Add(Miuix.Body(L("Files_Empty"), true));
            return;
        }

        foreach (var item in items)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(Miuix.ControlRadius),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                BorderBrush = Miuix.Brush("MiuixCardBorder"),
                Padding = new Thickness(12, 8, 12, 8)
            };

            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(Miuix.Body(item.Name));
            panel.Children.Add(Miuix.Body($"{item.KindText} · {item.SizeText} · {item.Modified}", true));
            button.Content = panel;

            var captured = item;
            button.Click += async (_, _) =>
            {
                // 目录与符号链接（如 /sdcard、/storage/emulated/0）都允许进入
                if (captured.IsNavigable) await BrowseAsync(captured.FullPath);
                else
                {
                    _selected = captured;
                    MainWindow.Notify(captured.Name, InfoBarSeverity.Informational);
                    await BrowseAsync(_path);
                }
            };

            if (_selected is not null && _selected.FullPath == item.FullPath)
                button.BorderBrush = Miuix.Brush("MiuixAccent");

            _list.Children.Add(button);
        }
    }

    /// <summary>目录拉取期间的行内加载指示。</summary>
    private void ShowListLoading()
    {
        _list.Children.Clear();
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 14, 0, 14)
        };
        row.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(Miuix.Body(L("Busy_LoadingFiles"), true));
        _list.Children.Add(row);
    }

    private async Task UploadAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var file = await Pickers.PickFileAsync("*");
        if (file is null) return;

        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Uploading"),
            () => AppState.Adb.PushAsync(device!.Serial, file.Path, _path));
        MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        await BrowseAsync(_path);
    }

    private async Task DownloadAsync()
    {
        if (_selected is null) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;

        var target = Path.Combine(folder.Path, _selected.Name);
        var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Downloading"),
            () => AppState.Adb.PullAsync(device!.Serial, _selected.FullPath, target));
        MainWindow.Notify(ok ? L("Msg_Done") + " " + target : message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task RenameAsync()
    {
        if (_selected is null) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var box = new TextBox { Text = _selected.Name };
        var dialog = new ContentDialog
        {
            Title = L("Common_Rename"),
            Content = box,
            PrimaryButtonText = L("Common_Confirm"),
            CloseButtonText = L("Common_Cancel"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = box.Text.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        var target = (_path.TrimEnd('/') + "/" + newName);
        var (ok, message) = await AppState.Adb.MoveRemoteAsync(device!.Serial, _selected.FullPath, target);
        MainWindow.Notify(ok ? L("Msg_Done") : message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        await BrowseAsync(_path);
    }

    private async Task NewFolderAsync()
    {
        if (!TryGetDevice(out var device)) return;

        var box = new TextBox();
        var dialog = new ContentDialog
        {
            Title = L("Files_NewFolder"),
            Content = box,
            PrimaryButtonText = L("Common_Confirm"),
            CloseButtonText = L("Common_Cancel"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var (ok, message) = await AppState.Adb.MakeDirectoryAsync(device!.Serial, _path.TrimEnd('/') + "/" + name);
        MainWindow.Notify(ok ? L("Msg_Done") : message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        await BrowseAsync(_path);
    }

    private async Task PasteAsync()
    {
        if (_clipboardItem is null) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var clipboardItem = _clipboardItem!;
        var isCut = _clipboardIsCut;
        var target = _path.TrimEnd('/') + "/" + clipboardItem.Name;
        var result = await MainWindow.RunBusyAsync(L("Busy_Transferring"), () => isCut
            ? AppState.Adb.MoveRemoteAsync(device!.Serial, clipboardItem.FullPath, target)
            : AppState.Adb.CopyRemoteAsync(device!.Serial, clipboardItem.FullPath, target));

        MainWindow.Notify(result.Success ? L("Msg_Done") : result.Message,
            result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        _clipboardItem = null;
        await BrowseAsync(_path);
    }

    private async Task DeleteAsync()
    {
        if (_selected is null) { MainWindow.Notify(L("Msg_NoSelection"), InfoBarSeverity.Warning); return; }
        if (!TryGetDevice(out var device)) return;

        var dialog = new ContentDialog
        {
            Title = L("Common_Delete"),
            Content = L("Msg_ConfirmDelete") + "\n" + _selected.Name,
            PrimaryButtonText = L("Common_Confirm"),
            CloseButtonText = L("Common_Cancel"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var (ok, message) = await AppState.Adb.DeleteRemoteAsync(device!.Serial, _selected.FullPath);
        MainWindow.Notify(ok ? L("Msg_Done") : message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        await BrowseAsync(_path);
    }
}
