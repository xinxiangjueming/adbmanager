using System.IO.Compression;
using AdbManager.Models;

namespace AdbManager.Services;

public sealed partial class AdbService
{
    /// <summary>列出设备已安装包（第三方 + 系统 + 冻结状态）。</summary>
    public async Task<List<PackageInfo>> ListPackagesAsync(string serial)
    {
        var thirdParty = await QueryPackagesAsync(serial, "-3").ConfigureAwait(false);
        var system = await QueryPackagesAsync(serial, "-s").ConfigureAwait(false);
        var disabled = await QueryPackagesAsync(serial, "-d").ConfigureAwait(false);

        var disabledSet = disabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<PackageInfo>();

        foreach (var name in thirdParty) result.Add(new PackageInfo { PackageName = name, IsDisabled = disabledSet.Contains(name) });
        foreach (var name in system)
            result.Add(new PackageInfo { PackageName = name, IsSystem = true, IsDisabled = disabledSet.Contains(name) });

        return result.OrderBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase).ToList();
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
