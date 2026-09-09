using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AdbManager.Views;

public sealed class LogsView : PageBase
{
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        MinHeight = 420,
        MaxHeight = 720
    };

    public LogsView()
    {
        Content = Build();
    }

    public override Task OnShownAsync()
    {
        if (_list.ItemsSource is null) _list.ItemsSource = AppState.Log.Entries;
        return Task.CompletedTask;
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        var clear = Miuix.SecondaryButton(L("Logs_Clear"));
        clear.Click += (_, _) => AppState.Log.Clear();

        var copy = Miuix.PrimaryButton(L("Logs_Copy"));
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(string.Join('\n', AppState.Log.Entries.Select(e => e.ToString())));
            Clipboard.SetContent(package);
            MainWindow.Notify(L("Msg_Done"), InfoBarSeverity.Success);
        };

        var card = Miuix.Card(new StackPanel { Spacing = 10 });
        var panel = (StackPanel)card.Child;
        panel.Children.Add(Miuix.SectionTitle(L("Logs_Title")));
        panel.Children.Add(Miuix.Horizontal(clear, copy));
        panel.Children.Add(_list);
        root.Children.Add(card);

        return root;
    }
}
