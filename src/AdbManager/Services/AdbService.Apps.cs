using System.IO.Compression;
using AdbManager.Models;

namespace AdbManager.Services;

public sealed partial class AdbService
{
    /// <summary>列出设备已安装包（第三方 + 系统 + 冻结状态 + 应用显示名）。</summary>
    public async Task<List<PackageInfo>> ListPackagesAsync(string serial)
    {
        var result = await ListPackagesCoreAsync(serial).ConfigureAwait(false);

        // 应用名读取失败不阻断列表：拿不到 label 的包回退显示包名
        try
        {
            var labels = await QueryLabelsAsync(serial, result.Select(p => p.PackageName).ToList())
                .ConfigureAwait(false);
            foreach (var package in result)
            {
                if (labels.TryGetValue(package.PackageName, out var label)) package.Label = label;
            }
        }
        catch (Exception ex)
        {
            AppState.Log.Error(L("Apps_Err_LabelRead", ex.Message));
        }

        return result.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 只列出包名与类型/冻结状态，不读应用名。
    /// 供 ListPackagesWithIconsAsync 使用——应用名与图标在同一次 app_process
    /// 调用里一并取回，若在此处先查一次名称，会多付一次约 2.8 秒的 JVM 启动开销。
    /// </summary>
    private async Task<List<PackageInfo>> ListPackagesCoreAsync(string serial)
    {
        var thirdParty = await QueryPackagesAsync(serial, "-3").ConfigureAwait(false);
        var system = await QueryPackagesAsync(serial, "-s").ConfigureAwait(false);
        var disabled = await QueryPackagesAsync(serial, "-d").ConfigureAwait(false);

        var disabledSet = disabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<PackageInfo>();

        foreach (var name in thirdParty) result.Add(new PackageInfo { PackageName = name, IsDisabled = disabledSet.Contains(name) });
        foreach (var name in system)
            result.Add(new PackageInfo { PackageName = name, IsSystem = true, IsDisabled = disabledSet.Contains(name) });

        return result;
    }

    private async Task<List<string>> QueryPackagesAsync(string serial, string flag)
    {
        var result = await RunAsync($"-s \"{serial}\" shell pm list packages {flag}", timeout: TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        var names = new List<string>();
        foreach (var line in result.StdOut.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith("package:", StringComparison.OrdinalIgnoreCase)) continue;
            var name = text["package:".Length..].Trim();
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    // ---------------- 应用名读取（app_process + dex） ----------------

    /// <summary>
    /// 批量读取应用显示名（application-label）。返回「包名 → 应用名」字典；
    /// 取不到的包不会出现在结果中。失败时返回空字典（调用方回退显示包名）。
    ///
    /// 走 AppInfoProbe 的 label-only 模式（outDir 传 "-"），与图标读取共用同一个 dex，
    /// 只是跳过 PNG 编码与磁盘写入。
    /// </summary>
    public async Task<Dictionary<string, string>> QueryLabelsAsync(string serial, IReadOnlyList<string> packages)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (packages.Count == 0) return labels;

        if (!await EnsureAppInfoDexAsync(serial).ConfigureAwait(false))
            return labels;

        for (var offset = 0; offset < packages.Count; offset += AppInfoBatchSize)
        {
            var batch = packages.Skip(offset).Take(AppInfoBatchSize).ToList();
            var arguments = string.Join(' ', batch.Select(QuoteIfNeeded));
            var command =
                $"CLASSPATH={AppInfoRemoteDex} app_process /system/bin AppInfoProbe - {arguments}";

            var result = await RunAsync($"-s \"{serial}\" shell \"{EscapeForShell(command)}\"",
                    timeout: TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);

            // 复用 AppInfoProbe 的解析：label-only 模式下第三列（图标路径）恒为空，忽略即可
            ParseAppInfoOutput(result.StdOut, out _, out var parsed);
            foreach (var (package, label) in parsed) labels[package] = label;
        }

        return labels;
    }

    private static string QuoteIfNeeded(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>把整条设备端命令包进双引号前，先转义其中的特殊字符。</summary>
    private static string EscapeForShell(string command) =>
        command.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");


    /// <summary>安装单个 APK。</summary>
    public async Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath)
    {
        var result = await RunAsync($"-s \"{serial}\" install -r \"{apkPath}\"", null, TimeSpan.FromMinutes(15))
            .ConfigureAwait(false);
        var ok = result.Success && result.Message.Contains("Success", StringComparison.OrdinalIgnoreCase);
        return (ok, result.Message);
    }

    /// <summary>把 .apks（拆分/分包 bundle）解压后用 install-multiple 安装。</summary>
    public static string ExtractApks(string apksPath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AdbManager", "apks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ZipFile.ExtractToDirectory(apksPath, directory);
        return directory;
    }

    /// <summary>安装拆分包集合（base + splits）。</summary>
    public async Task<(bool Success, string Message)> InstallMultipleAsync(string serial, IEnumerable<string> apkPaths)
    {
        var files = apkPaths.ToList();
        if (files.Count == 0) return (false, L("Apps_Err_NoApks"));

        var arguments = $"-s \"{serial}\" install-multiple -r " +
                        string.Join(' ', files.Select(f => $"\"{f}\""));

        var result = await RunAsync(arguments, null, TimeSpan.FromMinutes(20)).ConfigureAwait(false);
        var ok = result.Success && result.Message.Contains("Success", StringComparison.OrdinalIgnoreCase);
        return (ok, result.Message);
    }

    public async Task<(bool Success, string Message)> UninstallAsync(string serial, string package, bool keepData)
    {
        var argument = keepData ? $"pm uninstall -k --user 0 {package}" : $"pm uninstall --user 0 {package}";
        var result = await RunAsync($"-s \"{serial}\" shell {argument}", null, TimeSpan.FromMinutes(5))
            .ConfigureAwait(false);
        var ok = result.Success && result.Message.Contains("Success", StringComparison.OrdinalIgnoreCase);
        return (ok, result.Message);
    }

    /// <summary>冻结 / 解冻应用（pm disable-user 无需 root）。</summary>
    public async Task<(bool Success, string Message)> SetPackageFrozenAsync(string serial, string package, bool frozen)
    {
        var argument = frozen
            ? $"pm disable-user --user 0 {package}"
            : $"pm enable {package}";

        var result = await RunAsync($"-s \"{serial}\" shell {argument}", null, TimeSpan.FromMinutes(2))
            .ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public async Task<(bool Success, string Message)> ForceStopAsync(string serial, string package)
    {
        var result = await RunAsync($"-s \"{serial}\" shell am force-stop {package}", null, TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public async Task<(bool Success, string Message)> ClearDataAsync(string serial, string package)
    {
        var result = await RunAsync($"-s \"{serial}\" shell pm clear {package}", null, TimeSpan.FromMinutes(2))
            .ConfigureAwait(false);
        var ok = result.Success && result.Message.Contains("Success", StringComparison.OrdinalIgnoreCase);
        return (ok, result.Message);
    }

    /// <summary>提取已安装应用的 APK（含拆分包）到电脑目录。</summary>
    public async Task<(bool Success, string Message)> ExtractApkAsync(string serial, string package, string localDir)
    {
        var result = await RunAsync($"-s \"{serial}\" shell pm path {package}", null, TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        var remotePaths = result.StdOut.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["package:".Length..].Trim())
            .Where(path => path.Length > 0)
            .Distinct()
            .ToList();

        if (remotePaths.Count == 0) return (false, L("Apps_Err_NoApkPath"));

        try
        {
            Directory.CreateDirectory(localDir);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        foreach (var remote in remotePaths)
        {
            var fileName = Path.GetFileName(remote.Replace('\\', '/'));
            var target = Path.Combine(localDir, fileName);
            if (File.Exists(target)) target = Path.Combine(localDir, $"extract_{fileName}");

            var pull = await RunAsync($"-s \"{serial}\" pull \"{remote}\" \"{target}\"", null, TimeSpan.FromMinutes(15))
                .ConfigureAwait(false);
            if (!pull.Success) return (false, pull.Message);
        }

        return (true, localDir);
    }
}
