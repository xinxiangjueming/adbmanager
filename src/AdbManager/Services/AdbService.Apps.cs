using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// 只列出包名与类型/冻结状态，不读应用名（约 1 秒）。
    ///
    /// 供界面先落地首屏使用：应用名与图标在同一次 app_process 调用里一并回填
    /// （见 <see cref="LoadAppInfoStreamingAsync"/>），若在此处先查一次名称，
    /// 会多付一次约 2.8 秒的 JVM 启动开销。
    ///
    /// 排序固定按包名：名称到手后不重排，避免用户正在滚动时列表跳动
    /// （手机端 AppsScreen 同样只做筛选、不按应用名重排）。
    /// </summary>
    public async Task<List<PackageInfo>> ListPackageNamesAsync(string serial)
    {
        var result = await ListPackagesCoreAsync(serial).ConfigureAwait(false);
        return result.OrderBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>只列出包名与类型/冻结状态，不读应用名。供界面先落地首屏使用。</summary>
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

    /// <summary>
    /// 安装 XAPK（APKPure 格式）：解压后安装 base.apk + 拆分包，若存在 obb/ 目录则额外把
    /// OBB 游戏数据包推送至 /sdcard/Android/obb/&lt;包名&gt;/。
    ///
    /// XAPK 与普通 APKS 的关键差异：它携带 manifest.json（含包名）与可选 obb 资源。
    /// 若仅按「解压全部 apk → install-multiple」处理，会漏装 OBB，导致游戏 / 大型应用缺资源。
    /// </summary>
    public async Task<(bool Success, string Message)> InstallXapkAsync(string serial, string xapkPath)
    {
        string directory;
        try
        {
            directory = Path.Combine(Path.GetTempPath(), "AdbManager", "xapk", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            ZipFile.ExtractToDirectory(xapkPath, directory);
        }
        catch (Exception ex)
        {
            return (false, L("Apps_Err_XapkExtract", ex.Message));
        }

        try
        {
            var apks = Directory.GetFiles(directory, "*.apk", SearchOption.AllDirectories)
                .OrderBy(f => Path.GetFileName(f).Contains("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
            if (apks.Count == 0) return (false, L("Apps_Err_NoApkInBundle"));

            var install = await InstallMultipleAsync(serial, apks).ConfigureAwait(false);
            if (!install.Success) return install;

            // XAPK 可选 OBB 数据包：应用安装完成后推送，供游戏 / 大型应用读取
            var obbDir = Path.Combine(directory, "obb");
            var obbFiles = Directory.Exists(obbDir)
                ? Directory.GetFiles(obbDir, "*.obb", SearchOption.AllDirectories).ToList()
                : new List<string>();
            if (obbFiles.Count == 0) return install;

            var package = ReadXapkPackageName(directory);
            if (string.IsNullOrEmpty(package))
                return (true, install.Message + "\n" + L("Apps_Warn_XapkNoPkg"));

            var remoteObb = $"/sdcard/Android/obb/{package}";
            await RunAsync($"-s \"{serial}\" shell mkdir -p \"{remoteObb}\"", null, TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);

            var report = new StringBuilder(install.Message);
            foreach (var obb in obbFiles)
            {
                var push = await RunAsync($"-s \"{serial}\" push \"{obb}\" \"{remoteObb}/\"", null, TimeSpan.FromMinutes(5))
                    .ConfigureAwait(false);
                if (!push.Success) report.Append('\n').Append(L("Apps_Warn_ObkPushFail", Path.GetFileName(obb)));
            }
            report.Append('\n').Append(L("Apps_XapkObkPushed", obbFiles.Count));
            return (true, report.ToString());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* 临时目录清理失败不影响安装结果 */ }
        }
    }

    /// <summary>从 XAPK 的 manifest.json 读取包名（APKPure 字段为 package_name，兼容 package）。</summary>
    private static string? ReadXapkPackageName(string directory)
    {
        var manifest = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (var key in new[] { "package_name", "package" })
            {
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
        }
        catch { /* 解析失败回退 null，由调用方跳过 OBB 推送 */ }
        return null;
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

    /// <summary>
    /// 提取已安装应用到电脑目录，按设备上的真实安装形态保存为可直接再安装的格式：
    /// 单 APK 存为「名称.apk」；拆分包打包为「名称.apks」（zip 容器，base + splits）；
    /// 设备上存在 OBB 数据包时打包为「名称.xapk」（zip 容器，含 manifest.json 与 obb/）。
    /// 返回生成的文件完整路径。
    /// </summary>
    public async Task<(bool Success, string Message)> ExtractApkAsync(string serial, string package, string displayName, string localDir)
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

        // 先拉到临时目录、全部成功后再打包落盘：半途失败不会在目标目录留下残缺文件
        var tempDir = Path.Combine(Path.GetTempPath(), "AdbManager", "extract", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            var apkFiles = new List<string>();
            foreach (var remote in remotePaths)
            {
                var fileName = Path.GetFileName(remote.Replace('\\', '/'));
                var target = Path.Combine(tempDir, fileName);

                var pull = await RunAsync($"-s \"{serial}\" pull \"{remote}\" \"{target}\"", null, TimeSpan.FromMinutes(15))
                    .ConfigureAwait(false);
                if (!pull.Success) return (false, pull.Message);
                apkFiles.Add(target);
            }

            var obbFiles = await PullObbFilesAsync(serial, package, tempDir).ConfigureAwait(false);

            var baseName = SanitizeFileName(displayName);
            if (baseName.Length == 0) baseName = SanitizeFileName(package);

            string targetFile;
            if (obbFiles.Count > 0)
            {
                // 有 OBB 的应用（多为游戏）缺数据包无法运行，打成 .xapk 才能随安装器一并恢复
                var version = await QueryVersionAsync(serial, package).ConfigureAwait(false);
                var manifest = BuildXapkManifest(package, baseName, version, apkFiles, obbFiles);
                targetFile = BuildZipBundle(UniqueTarget(localDir, baseName + ".xapk"), apkFiles, obbFiles, manifest);
            }
            else if (apkFiles.Count == 1)
            {
                targetFile = UniqueTarget(localDir, baseName + ".apk");
                File.Copy(apkFiles[0], targetFile);
            }
            else
            {
                targetFile = BuildZipBundle(UniqueTarget(localDir, baseName + ".apks"), apkFiles, obbFiles: null, manifestJson: null);
            }

            return (true, targetFile);
        }
        catch (Exception ex)
        {
            return (false, L("Apps_Err_Pack", ex.Message));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* 临时目录清理失败不影响提取结果 */ }
        }
    }

    /// <summary>
    /// 拉取设备的 OBB 数据包到 tempDir/obb/。目录不存在是常态（多数应用没有 OBB），静默返回空列表；
    /// 个别文件拉取失败跳过而不中断提取（.xapk 缺单个 OBB 仍可安装，只是缺该份资源）。
    /// </summary>
    private async Task<List<string>> PullObbFilesAsync(string serial, string package, string tempDir)
    {
        var remoteDir = $"/sdcard/Android/obb/{package}";
        var list = await RunAsync($"-s \"{serial}\" shell ls {remoteDir}", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        if (!list.Success) return new List<string>();

        var names = list.StdOut.Split('\n')
            .Select(line => line.Trim())
            .Select(line => line.Replace('\\', '/').Split('/').Last())
            .Where(name => name.EndsWith(".obb", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
        if (names.Count == 0) return new List<string>();

        var obbDir = Path.Combine(tempDir, "obb");
        Directory.CreateDirectory(obbDir);

        var pulled = new List<string>();
        foreach (var name in names)
        {
            var target = Path.Combine(obbDir, name);
            var pull = await RunAsync($"-s \"{serial}\" pull \"{remoteDir}/{name}\" \"{target}\"", null, TimeSpan.FromMinutes(15))
                .ConfigureAwait(false);
            if (pull.Success) pulled.Add(target);
        }
        return pulled;
    }

    /// <summary>dumpsys 读取版本号 / 版本名供 manifest.json 使用，失败返回空值（缺字段可容忍）。</summary>
    private async Task<(string Code, string Name)> QueryVersionAsync(string serial, string package)
    {
        try
        {
            var result = await RunAsync($"-s \"{serial}\" shell dumpsys package {package}", null, TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);
            var code = Regex.Match(result.StdOut, @"versionCode=(\d+)").Groups[1].Value;
            var name = Regex.Match(result.StdOut, @"versionName=([^\s]+)").Groups[1].Value;
            return (code, name);
        }
        catch
        {
            return ("0", "");
        }
    }

    /// <summary>生成 XAPK manifest.json（v2 必要字段；SAI / MT 等安装器按需读取，缺省字段可容忍）。</summary>
    private static string BuildXapkManifest(string package, string displayName, (string Code, string Name) version,
        List<string> apkFiles, List<string> obbFiles)
    {
        var manifest = new
        {
            xapk_version = 2,
            package_name = package,
            name = displayName,
            version_code = version.Code,
            version_name = version.Name,
            split_apks = apkFiles.Select(f => new
            {
                file = Path.GetFileName(f),
                id = Path.GetFileNameWithoutExtension(f),
            }).ToArray(),
            expansions = obbFiles.Select(f => new
            {
                file = "obb/" + Path.GetFileName(f),
                install_location = $"Android/obb/{package}",
                install_path = $"Android/obb/{package}/{Path.GetFileName(f)}",
            }).ToArray(),
        };
        return JsonSerializer.Serialize(manifest);
    }

    /// <summary>把拉取到的 apk（与可选 OBB / manifest.json）打包成 zip 容器（.apks / .xapk 均为该结构）。</summary>
    private static string BuildZipBundle(string targetFile, List<string> apkFiles, List<string>? obbFiles, string? manifestJson)
    {
        using var archive = ZipFile.Open(targetFile, ZipArchiveMode.Create);
        foreach (var apk in apkFiles)
        {
            using var entry = archive.CreateEntry(Path.GetFileName(apk), CompressionLevel.Optimal).Open();
            using var file = File.OpenRead(apk);
            file.CopyTo(entry);
        }
        if (obbFiles is not null)
        {
            foreach (var obb in obbFiles)
            {
                using var entry = archive.CreateEntry("obb/" + Path.GetFileName(obb), CompressionLevel.Optimal).Open();
                using var file = File.OpenRead(obb);
                file.CopyTo(entry);
            }
        }
        if (manifestJson is not null)
        {
            using var entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
            using var writer = new StreamWriter(entry);
            writer.Write(manifestJson);
        }
        return targetFile;
    }

    /// <summary>目标目录内重名时追加序号（应用重名常见，覆盖别人提取过的文件不合适）。</summary>
    private static string UniqueTarget(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 1; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{baseName} ({i}){extension}");
        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
