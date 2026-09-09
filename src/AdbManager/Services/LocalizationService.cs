using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace AdbManager.Services;

/// <summary>
/// 文案总入口。所有界面文字都通过 Key 从 Strings\&lt;语言&gt;\Resources.resw 获取，
/// 代码与界面均不出现硬编码中文。新增语言只需复制一份 Strings\&lt;语言&gt;\Resources.resw。
/// </summary>
public static class LocalizationService
{
    private const string DefaultLanguage = "zh-CN";
    private static readonly Dictionary<string, Dictionary<string, string>> _sets = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static string Language { get; private set; } = DefaultLanguage;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        AppSettings.Load();

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".resw", StringComparison.OrdinalIgnoreCase)) continue;

            var language = ParseLanguage(name);
            if (string.IsNullOrEmpty(language)) continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;

            var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var document = XDocument.Load(stream);
            foreach (var data in document.Descendants("data"))
            {
                var key = data.Attribute("name")?.Value;
                var value = data.Element("value")?.Value;
                if (!string.IsNullOrEmpty(key) && value is not null) set[key] = value;
            }

            // 同一语言可能有多个 resw（Resources + generated），必须合并而非替换
            if (_sets.TryGetValue(language, out var existing))
            {
                foreach (var pair in set) existing[pair.Key] = pair.Value;
            }
            else
            {
                _sets[language] = set;
            }
        }

        Language = ResolveLanguage();
        StartupLog.Write($"loc: languages=[{string.Join(',', _sets.Keys)}] selected={AppSettings.Language ?? "auto"} resolved={Language}");
    }

    private static string ParseLanguage(string resourceName)
    {
        // 形如 AdbManager.Strings.zh-CN.Resources.resw
        // 注意：MSBuild 会把目录名中的连字符规范化为下划线（zh-CN → zh_CN），需转回
        var parts = resourceName.Split('.');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "Strings", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1].Replace('_', '-');
        }
        return "";
    }

    /// <summary>切换语言并持久化；重启应用后生效。</summary>
    public static void SetLanguage(string? language)
    {
        AppSettings.SetLanguage(language);
        Language = ResolveLanguage();
    }

    private static string ResolveLanguage()
    {
        if (!string.IsNullOrEmpty(AppSettings.Language) && _sets.ContainsKey(AppSettings.Language))
            return AppSettings.Language;

        var culture = CultureInfo.CurrentUICulture;
        while (culture is not null && !string.IsNullOrEmpty(culture.Name))
        {
            if (_sets.ContainsKey(culture.Name)) return culture.Name;
            culture = culture.Parent;
        }
        return _sets.ContainsKey(DefaultLanguage) ? DefaultLanguage : _sets.Keys.FirstOrDefault() ?? DefaultLanguage;
    }

    public static string Get(string key)
    {
        EnsureLoaded();
        if (_sets.TryGetValue(Language, out var set) && set.TryGetValue(key, out var value))
            return value;
        if (_sets.TryGetValue(DefaultLanguage, out var fallback) && fallback.TryGetValue(key, out var fallbackValue))
            return fallbackValue;
        return key;
    }

    public static string Get(string key, params object[] args)
    {
        try
        {
            return string.Format(Get(key), args);
        }
        catch
        {
            return key;
        }
    }
}
