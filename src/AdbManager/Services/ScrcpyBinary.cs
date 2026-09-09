using System.Reflection;

namespace AdbManager.Services;

/// <summary>
/// 把编译进 exe 的 scrcpy 组件（v4.1，Apache 2.0）在首次使用时释放到
/// %LOCALAPPDATA%\AdbManager\scrcpy\&lt;version&gt;\，之后常驻复用。
/// 不释放 scrcpy 自带的 adb——统一使用内置官方 adb。
/// </summary>
public static class ScrcpyBinary
{
    public const string BundledVersion = "4.1";
    private const string ResourcePrefix = "scrcpy-bundle.";

    public static string TargetDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdbManager", "scrcpy", BundledVersion);

    public static string ExePath => Path.Combine(TargetDirectory, "scrcpy.exe");

    /// <summary>释放内置 scrcpy 组件；返回 scrcpy.exe 完整路径。</summary>
    public static string EnsureExtracted()
    {
        Directory.CreateDirectory(TargetDirectory);
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var name in names)
        {
            using var stream = asm.GetManifestResourceStream(name)
                               ?? throw new InvalidOperationException(LocalizationService.Get("Adb_Err_Resource", name));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();

            var dest = Path.Combine(TargetDirectory, name[ResourcePrefix.Length..]);
            try
            {
                if (!File.Exists(dest) || new FileInfo(dest).Length != bytes.Length)
                    File.WriteAllBytes(dest, bytes);
            }
            catch (IOException)
            {
                // 文件被占用（例如上一次 scrcpy 尚未退出）时保留既有文件即可。
                if (!File.Exists(dest)) throw;
            }
        }

        if (!File.Exists(ExePath))
            throw new FileNotFoundException(ExePath);
        return ExePath;
    }
}
