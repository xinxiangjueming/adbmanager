using System.Reflection;

namespace AdbManager.Services;

/// <summary>
/// 把编译进 exe 的谷歌官方 adb（platform-tools）在首次运行时释放到
/// %LOCALAPPDATA%\AdbManager\adb\&lt;version&gt;\，之后常驻复用。
/// </summary>
public static class AdbBinary
{
    /// <summary>内置 adb 版本，与 tools\adb 中文件保持一致。</summary>
    public const string BundledVersion = "37.0.1";

    private static readonly (string FileName, string ResourceName)[] Files =
    {
        ("adb.exe", "adb-bundle.adb.exe"),
        ("AdbWinApi.dll", "adb-bundle.AdbWinApi.dll"),
        ("AdbWinUsbApi.dll", "adb-bundle.AdbWinUsbApi.dll"),
        ("fastboot.exe", "adb-bundle.fastboot.exe"),
        ("libwinpthread-1.dll", "adb-bundle.libwinpthread-1.dll"),
    };

    public static string TargetDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdbManager", "adb", BundledVersion);

    public static string AdbPath => Path.Combine(TargetDirectory, "adb.exe");

    public static string FastbootPath => Path.Combine(TargetDirectory, "fastboot.exe");

    /// <summary>释放内置 adb；返回 true 表示使用的是内置官方 adb。</summary>
    public static bool EnsureExtracted()
    {
        Directory.CreateDirectory(TargetDirectory);
        var asm = Assembly.GetExecutingAssembly();

        foreach (var (fileName, resourceName) in Files)
        {
            using var stream = asm.GetManifestResourceStream(resourceName)
                               ?? throw new InvalidOperationException(LocalizationService.Get("Adb_Err_Resource", resourceName));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();

            var dest = Path.Combine(TargetDirectory, fileName);
            try
            {
                if (!File.Exists(dest) || new FileInfo(dest).Length != bytes.Length)
                    File.WriteAllBytes(dest, bytes);
            }
            catch (IOException)
            {
                // 文件被占用（例如上一次的 adb server 仍在运行）时保留既有文件即可。
                if (!File.Exists(dest)) throw;
            }
        }

        return File.Exists(AdbPath);
    }
}
