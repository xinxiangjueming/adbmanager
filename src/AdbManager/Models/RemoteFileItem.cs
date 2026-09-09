using AdbManager.Services;

namespace AdbManager.Models;

/// <summary>设备上的一个文件/目录条目。</summary>
public sealed class RemoteFileItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsLink { get; set; }
    public long Size { get; set; }
    public string Modified { get; set; } = "";
    public string Permissions { get; set; } = "";

    public string IconGlyph => IsDirectory ? "\uE8B7" : (Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ? "\uE7B8" : "\uE8A5");
    private static string Get(string key) => LocalizationService.Get(key);

    public string KindText => IsDirectory ? Get("Model_KindDir") : Get("Model_KindFile");
    public string SizeText => IsDirectory ? "" : FormatSize(Size);

    /// <summary>目录或指向目录的符号链接都可进入。</summary>
    public bool IsNavigable => IsDirectory || IsLink;

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}
