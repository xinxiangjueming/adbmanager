using System.Diagnostics;
using System.Globalization;
using AdbManager.Services;
using AdbManager.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbManager.Views;

public sealed class SettingsView : PageBase
{
    public SettingsView()
    {
        Content = Build();
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 16 };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Miuix.SectionTitle(L("Settings_Title")));
        panel.Children.Add(Miuix.SettingRow(L("Settings_AdbSource"), AppState.Adb.SourceText, Miuix.Body(AppState.Adb.UsingBundled ? AdbBinary.BundledVersion : "PATH", true)));
        panel.Children.Add(Miuix.Divider());
        panel.Children.Add(Miuix.SettingRow(L("Settings_AdbPath"), AppState.Adb.AdbPath, Miuix.Body("", true)));
        panel.Children.Add(Miuix.Divider());

        // 语言（语言名以各自原生语言显示，属业界惯例）
        var languageBox = new ComboBox { MinWidth = 160 };
        languageBox.Items.Add(L("Settings_FollowSystem"));   // 0
        languageBox.Items.Add("简体中文");                    // 1 zh-CN
        languageBox.Items.Add("English");                    // 2 en-US
        languageBox.Items.Add("日本語");                      // 3 ja
        languageBox.Items.Add("한국어");                      // 4 ko
        languageBox.Items.Add("Deutsch");                    // 5 de
        languageBox.Items.Add("Français");                   // 6 fr
        languageBox.Items.Add("Español");                    // 7 es
        languageBox.SelectedIndex = AppSettings.Language switch
        {
            "zh-CN" => 1,
            "en-US" => 2,
            "ja" => 3,
            "ko" => 4,
            "de" => 5,
            "fr" => 6,
            "es" => 7,
            _ => 0
        };
        languageBox.SelectionChanged += (_, _) =>
        {
            LocalizationService.SetLanguage(languageBox.SelectedIndex switch
            {
                1 => "zh-CN",
                2 => "en-US",
                3 => "ja",
                4 => "ko",
                5 => "de",
                6 => "fr",
                7 => "es",
                _ => null
            });
            MainWindow.Notify(L("Msg_LanguageRestart"), InfoBarSeverity.Informational);
        };
        panel.Children.Add(Miuix.SettingRow(L("Settings_Language"), CultureInfo.CurrentUICulture.Name, languageBox));
        panel.Children.Add(Miuix.Divider());

        // 主题：跟随系统 / 浅色 / 深色（实时生效）
        var themeBox = new ComboBox { MinWidth = 160 };
        themeBox.Items.Add(L("Settings_ThemeFollow"));   // 0
        themeBox.Items.Add(L("Settings_ThemeLight"));    // 1
        themeBox.Items.Add(L("Settings_ThemeDark"));     // 2
        themeBox.SelectedIndex = AppSettings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        themeBox.SelectionChanged += (_, _) =>
        {
            AppSettings.SetTheme(themeBox.SelectedIndex switch
            {
                1 => "light",
                2 => "dark",
                _ => null
            });
            App.MainWindow.ApplyThemeSetting();
        };
        panel.Children.Add(Miuix.SettingRow(L("Settings_Theme"), L("Settings_ThemeFollow"), themeBox));

        var openFolder = Miuix.PrimaryButton(L("Settings_OpenAdbFolder"));
        openFolder.Click += (_, _) =>
        {
            var directory = Path.GetDirectoryName(AppState.Adb.AdbPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        };
        panel.Children.Add(openFolder);

        root.Children.Add(Miuix.Card(panel));
        return root;
    }
}
