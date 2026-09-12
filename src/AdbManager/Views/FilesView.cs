using AdbManager.Models;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;

namespace AdbManager.Views;

public sealed class FilesView : PageBase
{
    private readonly string _home = "/storage/emulated/0";
    private string _path = "/storage/emulated/0";
    private readonly TextBlock _pathText = Miuix.Body("/storage/emulated/0");
    private readonly StackPanel _list = new() { Spacing = 6 };
    private RemoteFileItem? _selected;
    private RemoteFileItem? _clipboardItem;
    private bool _clipboardIsCut;
    private bool _showHidden = true;
    /// <summary>已浏览目录所属的设备序列号；为空表示还没浏览过。</summary>
    private string _loadedSerial = "";
    /// <summary>设备掉线后置为 true，重新连上（同序列号）时也要重新浏览。</summary>
    private bool _stale = true;

    public FilesView()
    {
        // 目录条目入场动画（淡入 + 上滑）
        _list.Transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromVerticalOffset = 14, IsStaggeringEnabled = false }
        };
        Content = Build();
        AppState.CurrentDeviceChanged += OnCurrentDeviceChanged;
    }

    /// <summary>设备变化（含首次连上、切换设备）时自动回到默认目录重新浏览，避免显示上一台设备的内容。</summary>
    private void OnCurrentDeviceChanged(AdbDevice? device)
    {
        var queue = DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Apply()
        {
            // 设备消失或掉线：标记待刷新，保留当前目录不闪空
            if (device is null || !device.IsOnline) { _stale = true; return; }
            if (_stale || device.Serial != _loadedSerial) _ = BrowseAsync(_home);
        }

        if (queue is { } q && !q.HasThreadAccess) q.TryEnqueue(Apply);
        else Apply();
    }

    public override async Task OnShownAsync()
    {
        // 换过设备或曾掉线则回到默认目录重新浏览；否则沿用当前目录
        var device = AppState.CurrentDevice;
        var deviceChanged = device is { IsOnline: true } && device.Serial != _loadedSerial;
        if (deviceChanged && _stale) _path = _home;

        if (_list.Children.Count == 0 || _stale || deviceChanged) await BrowseAsync(_path);
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
        // 拖放上传：从资源管理器拖文件/文件夹到列表区 → 推送到当前目录
        listCard.AllowDrop = true;
        listCard.DragOver += ListCard_DragOver;
        listCard.Drop += ListCard_Drop;
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

        var hiddenButton = Miuix.SecondaryButton(L("Files_HideHidden"));
        hiddenButton.Click += async (_, _) =>
        {
            _showHidden = !_showHidden;
            hiddenButton.Content = _showHidden ? L("Files_HideHidden") : L("Files_ShowHidden");
            await BrowseAsync(_path);
        };

        var actionCard = Miuix.Card(new StackPanel { Spacing = 10 });
        ((StackPanel)actionCard.Child).Children.Add(Miuix.Horizontal(upload, download, rename, newFolder));
        ((StackPanel)actionCard.Child).Children.Add(Miuix.Horizontal(copy, cut, paste, delete, hiddenButton));
        root.Children.Add(actionCard);

        return root;
    }

    private async Task BrowseAsync(string path)
    {
        if (!TryGetDevice(out var device)) return;

        _path = path;
        _pathText.Text = _path;
        _selected = null;
        _loadedSerial = device!.Serial;
        _stale = false;

        // 先显示内联 loading，再拉取目录
        ShowListLoading();
        var items = await AppState.Adb.ListDirectoryAsync(device!.Serial, _path);
        if (!_showHidden)
            items = items.Where(i => !i.Name.StartsWith(".", StringComparison.Ordinal)).ToList();
        _list.Children.Clear();

        if (items.Count == 0)
        {
            _list.Children.Add(Miuix.Body(L("Files_Empty"), true));
        }
        else
        {
            foreach (var item in items)
            {
            // 行结构：左=条目按钮（单击选中，目录与文件均可；目录不再点击即进入），右=目录的「打开」按钮
            var row = new Grid
            {
                ColumnSpacing = 8,
                Tag = item.FullPath,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };

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
            button.Click += (_, _) =>
            {
                // 单击 = 选中（目录与文件均可），删除/重命名/剪切/复制/下载均作用于选中项
                _selected = captured;
                MainWindow.Notify(captured.Name, InfoBarSeverity.Informational);
                HighlightSelection();
            };

            Grid.SetColumn(button, 0);
            row.Children.Add(button);

            if (captured.IsNavigable)
            {
                var open = Miuix.SecondaryButton(L("Common_Open"));
                open.Height = double.NaN;
                open.VerticalAlignment = VerticalAlignment.Stretch;
                open.MinWidth = 76;
                open.Click += async (_, _) => await BrowseAsync(captured.FullPath);
                Grid.SetColumn(open, 1);
                row.Children.Add(open);
            }

            if (_selected is not null && _selected.FullPath == item.FullPath)
                button.BorderBrush = Miuix.Brush("MiuixAccent");

            _list.Children.Add(row);
            }
        }
    }

    /// <summary>重新应用选中高亮（不刷新列表，保留滚动位置）。</summary>
    private void HighlightSelection()
    {
        foreach (var child in _list.Children)
        {
            if (child is not Grid row || row.Tag is not string fullPath) continue;
            if (row.Children[0] is Button button)
                button.BorderBrush = _selected?.FullPath == fullPath
                    ? Miuix.Brush("MiuixAccent")
                    : Miuix.Brush("MiuixCardBorder");
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

    private void ListCard_DragOver(object sender, DragEventArgs e)
    {
        // 必须同步设置接受操作（DragOver 高频触发，不能异步）
        if (!TryGetDevice(out _, warn: false))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = L("Files_DropHint");
        e.Handled = true;
    }

    private async void ListCard_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!TryGetDevice(out var device)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Where(i => !string.IsNullOrEmpty(i.Path)).Select(i => (i.Path, i.Name)).ToList();
        if (paths.Count == 0) return;

        // Drop handler 内做异步工作需要 deferral，否则数据视图可能在返回后失效
        var deferral = e.GetDeferral();
        try
        {
            var (ok, message) = await MainWindow.RunBusyAsync(L("Busy_Uploading"), async () =>
            {
                var failed = new List<string>();
                foreach (var (path, name) in paths)
                {
                    var r = await AppState.Adb.PushAsync(device!.Serial, path, _path);
                    if (!r.Success) failed.Add(name);
                }

                return failed.Count == 0
                    ? (true, string.Format(L("Files_DropDone"), paths.Count))
                    : (false, string.Format(L("Files_DropPartial"), failed.Count) + " " + string.Join(", ", failed));
            });
            MainWindow.Notify(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            await BrowseAsync(_path);
        }
        finally
        {
            deferral.Complete();
        }
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
