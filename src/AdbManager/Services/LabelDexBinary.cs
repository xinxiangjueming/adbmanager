using System.Reflection;

namespace AdbManager.Services;

/// <summary>
/// 把编译进 exe 的 AppInfoProbe dex（约 5KB，原创实现）在首次运行时释放到
/// %LOCALAPPDATA%\AdbManager\labeldex\，再 push 到设备 /data/local/tmp/ 执行。
///
/// 用途：一次调用同时读取应用显示名（application-label）与图标。
/// Android 不通过 adb 直接暴露应用名，dumpsys 只返回 labelRes 资源 ID
/// （部分 ROM 上甚至统一返回占位值，不可用），APK 内的 label 只有系统
/// PackageManager 能正确解析。此 dex 借 app_process 拿到系统 Context，
/// 调 PackageManager.getApplicationLabel() 得到与桌面一致的应用名（含中文），
/// 再用 createPackageContext + getDrawableForDensity 导出图标 PNG。
///
/// 调用形式：
///   CLASSPATH=&lt;dex&gt; app_process /system/bin AppInfoProbe &lt;outDir|'-'&gt; [-s 尺寸] &lt;pkg...&gt;
/// outDir 传 '-' 时为 label-only 模式，只取名称不生成图标。
///
/// 源码见 tools\labeldex\src\AppInfoProbe.java，重新编译用 tools\labeldex\build-dex.ps1。
/// 该 dex 同时被 WearAdb 复用（副本：wearadb/app/src/main/assets/appinfo.dex），
/// 内容变更时必须同步更新那份副本。
/// </summary>
public static class LabelDexBinary
{
    private const string ResourceName = "labeldex.classes.dex";

    /// <summary>dex 释放到本地后的路径。</summary>
    public static string LocalPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdbManager", "labeldex", "classes.dex");

    /// <summary>把内置 dex 释放到本地磁盘，返回路径。资源缺失时抛异常。</summary>
    public static string EnsureExtracted()
    {
        var path = LocalPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException(
                               LocalizationService.Get("Apps_Err_LabelDexMissing"));

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();

        try
        {
            // 内容由内置资源决定，长度一致即视为已释放，避免每次启动重写
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                File.WriteAllBytes(path, bytes);
        }
        catch (IOException)
        {
            // 被占用时保留既有文件即可
            if (!File.Exists(path)) throw;
        }

        return path;
    }
}
