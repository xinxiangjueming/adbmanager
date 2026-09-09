namespace AdbManager.Services;

/// <summary>应用配置（%LOCALAPPDATA%\AdbManager\config.ini）：语言与主题。</summary>
public static class AppSettings
{
    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbManager", "config.ini");

    /// <summary>用户手动选择的语言（null = 跟随系统）。</summary>
    public static string? Language { get; private set; }

    /// <summary>主题：null = 跟随系统；"light" = 浅色；"dark" = 深色。</summary>
    public static string? Theme { get; private set; }

    private static bool _loaded;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(ConfigPath)) return;
            foreach (var line in File.ReadAllLines(ConfigPath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;
                var key = parts[0].Trim();
                var value = parts[1].Trim();

                if (key.Equals("language", StringComparison.OrdinalIgnoreCase))
                    Language = value.Length > 0 ? value : null;
                else if (key.Equals("theme", StringComparison.OrdinalIgnoreCase))
                    Theme = value is "light" or "dark" ? value : null;
            }
        }
        catch
        {
            // 配置读取失败时使用默认值
        }
    }

    public static void SetLanguage(string? language)
    {
        Language = language;
        Save();
    }

    public static void SetTheme(string? theme)
    {
        Theme = theme is "light" or "dark" ? theme : null;
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath,
                $"language={Language ?? ""}\ntheme={Theme ?? ""}\n");
        }
        catch
        {
            // 配置写入失败时仅本次会话生效
        }
    }
}
