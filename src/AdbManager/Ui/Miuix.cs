using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AdbManager.Ui;

/// <summary>
/// miuix / HyperOS 视觉体系：配色、圆角、卡片与常用控件工厂。
/// 所有颜色以「浅色 / 深色」成对定义，跟随系统主题实时切换。
/// </summary>
public static class Miuix
{
    // 圆角（miuix 风格：卡片 20、控件 16、按钮胶囊 22）
    public const double CardRadius = 20;
    public const double ControlRadius = 16;
    public const double ButtonRadius = 22;
    public const double ButtonHeight = 44;

    private static readonly (string Key, Color Light, Color Dark)[] Palette =
    {
        ("MiuixPageBackground", Color.FromArgb(255, 244, 245, 247), Color.FromArgb(255, 16, 16, 18)),
        ("MiuixCardBackground", Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 28, 28, 30)),
        ("MiuixCardBorder", Color.FromArgb(255, 232, 234, 237), Color.FromArgb(255, 48, 48, 51)),
        ("MiuixAccent", Color.FromArgb(255, 10, 132, 255), Color.FromArgb(255, 77, 163, 255)),
        ("MiuixAccentForeground", Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255)),
        ("MiuixTextPrimary", Color.FromArgb(255, 26, 26, 28), Color.FromArgb(255, 245, 245, 247)),
        ("MiuixTextSecondary", Color.FromArgb(255, 138, 143, 153), Color.FromArgb(255, 152, 152, 159)),
        ("MiuixDivider", Color.FromArgb(255, 238, 239, 242), Color.FromArgb(255, 44, 44, 47)),
        ("MiuixRowHover", Color.FromArgb(255, 248, 249, 251), Color.FromArgb(255, 36, 36, 39)),
        ("MiuixDanger", Color.FromArgb(255, 255, 59, 48), Color.FromArgb(255, 255, 89, 79)),
        ("MiuixSuccess", Color.FromArgb(255, 52, 199, 89), Color.FromArgb(255, 64, 214, 105)),
        ("MiuixSuccessDark", Color.FromArgb(255, 30, 142, 72), Color.FromArgb(255, 26, 118, 62))
    };

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    /// <summary>把 miuix 画笔注册到目标资源字典（通常是 Application.Resources）。</summary>
    public static void Register(ResourceDictionary target)
    {
        foreach (var (key, light, dark) in Palette)
        {
            var brush = new SolidColorBrush(light);
            Brushes[key] = brush;
            target[key] = brush;
        }
    }

    /// <summary>根据当前主题刷新画笔颜色（切换深浅色时调用）。</summary>
    public static void ApplyTheme(ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        foreach (var (key, light, darkColor) in Palette)
        {
            if (Brushes.TryGetValue(key, out var brush))
                brush.Color = dark ? darkColor : light;
        }
    }

    public static Brush Brush(string key) =>
        Brushes.TryGetValue(key, out var brush) ? brush : new SolidColorBrush(Colors.Transparent);

    // ---------------- 控件工厂 ----------------

    public static Border Card(UIElement? child = null, double padding = 16)
    {
        var border = new Border
        {
            Background = Brush("MiuixCardBackground"),
            BorderBrush = Brush("MiuixCardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CardRadius),
            Padding = new Thickness(padding),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (child is not null) border.Child = child;
        return border;
    }

    public static TextBlock Title(string text) => new()
    {
        Text = text,
        FontSize = 24,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush("MiuixTextPrimary"),
        Margin = new Thickness(0, 0, 0, 4)
    };

    public static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush("MiuixTextSecondary"),
        Margin = new Thickness(0, 0, 0, 8)
    };

    public static TextBlock Body(string text, bool secondary = false) => new()
    {
        Text = text,
        FontSize = 14,
        Foreground = Brush(secondary ? "MiuixTextSecondary" : "MiuixTextPrimary"),
        TextWrapping = TextWrapping.Wrap
    };

    private static Button CreateButton(string text, string backgroundKey, Color? foreground = null)
    {
        var button = new Button
        {
            Content = text,
            Height = ButtonHeight,
            CornerRadius = new CornerRadius(ButtonRadius),
            Padding = new Thickness(20, 0, 20, 0),
            Background = Brush(backgroundKey)
        };
        if (foreground is { } color) button.Foreground = new SolidColorBrush(color);
        return button;
    }

    public static Button PrimaryButton(string text) =>
        CreateButton(text, "MiuixAccent", Colors.White);

    public static Button DangerButton(string text) =>
        CreateButton(text, "MiuixDanger", Colors.White);

    /// <summary>暗绿填充按钮（「连接」「选中」等确认类操作）。</summary>
    public static Button SuccessButton(string text) =>
        CreateButton(text, "MiuixSuccessDark", Colors.White);

    public static Button SecondaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Height = ButtonHeight,
            CornerRadius = new CornerRadius(ButtonRadius),
            Padding = new Thickness(20, 0, 20, 0),
            Background = Brush("MiuixCardBackground"),
            BorderBrush = Brush("MiuixCardBorder"),
            BorderThickness = new Thickness(1),
            Foreground = Brush("MiuixTextPrimary")
        };
        return button;
    }

    public static TextBox Input(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        CornerRadius = new CornerRadius(ControlRadius),
        Height = 40,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    public static StackPanel Vertical(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12, Orientation = Orientation.Vertical };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Horizontal(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 10, Orientation = Orientation.Horizontal };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>一行「标题 + 说明 + 右侧操作」的设置行。</summary>
    public static Grid SettingRow(string title, string? description, FrameworkElement action)
    {
        var grid = new Grid
        {
            Padding = new Thickness(4, 10, 4, 10),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Body(title));
        if (!string.IsNullOrEmpty(description)) texts.Children.Add(Body(description!, true));

        Grid.SetColumn(texts, 0);
        Grid.SetColumn(action, 1);
        action.VerticalAlignment = VerticalAlignment.Center;

        grid.Children.Add(texts);
        grid.Children.Add(action);
        return grid;
    }

    public static Border Divider() => new()
    {
        Height = 1,
        Background = Brush("MiuixDivider"),
        Margin = new Thickness(4, 4, 4, 4)
    };
}
