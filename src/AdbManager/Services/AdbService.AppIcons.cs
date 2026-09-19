using System.Collections.Concurrent;
using AdbManager.Models;

namespace AdbManager.Services;

/// <summary>流式补全的更新阶段。</summary>
public enum AppInfoStage
{
    /// <summary>命中本地缓存，无需联网解析，直接回填。</summary>
    Cached,

    /// <summary>本批应用名已解析（图标还在打包拉回途中）。</summary>
    LabelsReady,

    /// <summary>本批图标已取回。</summary>
    IconsReady
}

/// <summary>
/// 一次增量更新的内容快照。
///
/// 刻意携带数据副本而非让界面回查 <see cref="AdbService.GetCachedLabel"/>：
/// 缓存字典正被后台解析任务写入，跨线程直接读会破坏 Dictionary 的一致性。
/// </summary>
public sealed class AppInfoUpdate
{
    public AppInfoStage Stage { get; init; }

    /// <summary>本次更新涉及的包名。</summary>
    public IReadOnlyList<string> Packages { get; init; } = Array.Empty<string>();

    /// <summary>本次新得到的 包名 → 应用名。</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次新得到的 包名 → 图标 PNG 字节。名称阶段为空。</summary>
    public IReadOnlyDictionary<string, byte[]> Icons { get; init; } =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>需要解析（即缓存未命中）的包总数。</summary>
    public int Total { get; init; }

    /// <summary>已完成的包数。</summary>
    public int Done { get; init; }
}

/// <summary>
/// 应用名 + 图标读取（app_process + dex，免 root）。
///
/// 与旧 AppLabelProbe（只取名称）的区别：改用 AppInfoProbe，一次调用同时返回
/// 应用名与图标 PNG，省掉一次 app_process 启动（每次约 2.8 秒固定开销）。
///
/// 传输策略：设备端把全部图标打进一个 tar.gz，再用 adb pull 一次取回。
/// 实测 395 个 96px 图标 = 3.0MB → tar.gz 1.15MB → pull 0.05 秒。
/// （对比 exec-out 流式 tar 需 5.6 秒——exec-out 走 shell 文本通道，
///   而 pull 走二进制 SYNC 协议，快 12 倍。）
///
/// 上报策略：命中缓存的部分先整体回填一次，随后按批（<see cref="AppInfoBatchSize"/>）
/// 解析；每批再分「名称就绪 / 图标就绪」两次上报，让界面先看到应用名、再看到图标。
/// 三个缓存字典为并发字典——后台解析任务写入的同时，界面线程可能正在读取。
/// </summary>
public sealed partial class AdbService
{
    /// <summary>设备端工作目录（dex 复用；icons 每次重建）。</summary>
    private const string AppInfoRemoteDir = "/data/local/tmp/adbmgr_appinfo";
    private const string AppInfoRemoteDex = AppInfoRemoteDir + "/appinfo.dex";
    private const string AppInfoRemoteIcons = AppInfoRemoteDir + "/icons";
    private const string AppInfoRemoteTar = AppInfoRemoteDir + "/icons.tgz";

    /// <summary>图标缩放目标边长（px）。列表按 32~40px 显示，96px 留足高 DPI 余量。</summary>
    private const int IconSizePx = 96;

    /// <summary>
    /// 单批包名上限。app_process 每次启动约 2.8 秒固定开销，批次越少越快；
    /// 仅按命令行长度设上限（395 包约 11KB，400 留足余量）。
    /// 调小可提高回填的渐进粒度，但每多一批就多付一次约 2.8 秒的 JVM 启动。
    /// </summary>
    private const int AppInfoBatchSize = 400;

    /// <summary>本进程内的图标缓存（包名 → PNG 字节）。仅在内存，重启后重新从设备读取。</summary>
    private readonly ConcurrentDictionary<string, byte[]> _iconCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已确认「确实没有图标」的包，避免反复重试。</summary>
    private readonly ConcurrentDictionary<string, bool> _knownNoIcon =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>应用名缓存（包名 → 应用名）。</summary>
    private readonly ConcurrentDictionary<string, string> _labelCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>缓存属于哪台设备：换设备时整体作废。</summary>
    private string _iconCacheSerial = "";

    /// <summary>取缓存中的图标；没有则返回 null。</summary>
    public byte[]? GetCachedIcon(string packageName) =>
        _iconCache.TryGetValue(packageName, out var bytes) ? bytes : null;

    /// <summary>取应用名（先查本次会话缓存）。</summary>
    public string? GetCachedLabel(string packageName) =>
        _labelCache.TryGetValue(packageName, out var label) ? label : null;

    /// <summary>清空图标与名称缓存（换设备或强制刷新时调用）。</summary>
    public void ClearIconCache()
    {
        _iconCache.Clear();
        _knownNoIcon.Clear();
        _labelCache.Clear();
        _iconCacheSerial = "";
    }

    /// <summary>
    /// 流式解析应用名与图标：先回填本地缓存命中的部分，再按批解析其余包，
    /// 每批分「名称就绪 / 图标就绪」两次回调。
    ///
    /// 调用方在回调里更新界面即可获得与手机端一致的渐进体验——
    /// 列表骨架早已可见，名称与图标随后填入，全程不阻塞 UI。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="packages">需要补全的包名（通常是当前列表的全部包）。</param>
    /// <param name="onUpdate">增量更新回调，可能在后台线程触发，调用方需自行切回 UI 线程。</param>
    public async Task LoadAppInfoStreamingAsync(
        string serial,
        IReadOnlyList<string> packages,
        Action<AppInfoUpdate> onUpdate,
        CancellationToken cancellationToken = default)
    {
        if (packages.Count == 0) return;

        // 换设备时作废旧缓存，避免把上一台设备的信息显示到新设备上
        if (!string.Equals(_iconCacheSerial, serial, StringComparison.Ordinal))
        {
            _iconCache.Clear();
            _knownNoIcon.Clear();
            _labelCache.Clear();
            _iconCacheSerial = serial;
        }

        // 第一段：本地缓存命中的内容立即回填，界面无需等待 app_process（重启后二次进入即走此路径）
        var cachedLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cachedIcons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();

        foreach (var package in packages)
        {
            if (_labelCache.TryGetValue(package, out var label) && label.Length > 0)
                cachedLabels[package] = label;

            if (GetCachedIcon(package) is { Length: > 0 } icon)
                cachedIcons[package] = icon;

            // 判定条件用「或」而非「与」：一次调用同时产出名称与图标，但若某包确实
            // 没有图标（记入 _knownNoIcon）而名称也还没拿到，仍需再查一次，
            // 否则该包会永远退回显示包名。
            if (!_labelCache.ContainsKey(package) ||
                (!_iconCache.ContainsKey(package) && !_knownNoIcon.ContainsKey(package)))
            {
                pending.Add(package);
            }
        }

        onUpdate(new AppInfoUpdate
        {
            Stage = AppInfoStage.Cached,
            Packages = packages,
            Labels = cachedLabels,
            Icons = cachedIcons,
            Total = pending.Count
        });

        if (pending.Count == 0) return;

        if (!await EnsureAppInfoDexAsync(serial).ConfigureAwait(false))
        {
            // dex 不可用：名称也拿不到，调用方自行用包名兜底
            return;
        }

        // 设备端图标目录每次重建，清掉上一次异常中断可能残留的 PNG
        await RunAsync($"-s \"{serial}\" shell rm -rf {AppInfoRemoteIcons}",
                timeout: TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        var done = 0;
        for (var offset = 0; offset < pending.Count; offset += AppInfoBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = pending.Skip(offset).Take(AppInfoBatchSize).ToList();
            await QueryAppInfoBatchAsync(serial, batch, done, pending.Count, onUpdate)
                .ConfigureAwait(false);
            done += batch.Count;
        }
    }

    /// <summary>执行一批：取名称 + 生成图标 + 打包拉回，分两次回调上报。</summary>
    private async Task QueryAppInfoBatchAsync(
        string serial,
        IReadOnlyList<string> batch,
        int doneBefore,
        int total,
        Action<AppInfoUpdate> onUpdate)
    {
        var arguments = string.Join(' ', batch.Select(QuoteIfNeeded));
        var command =
            $"CLASSPATH={AppInfoRemoteDex} app_process /system/bin AppInfoProbe " +
            $"{AppInfoRemoteIcons} -s {IconSizePx} {arguments}";

        var result = await RunAsync($"-s \"{serial}\" shell \"{EscapeForShell(command)}\"",
                timeout: TimeSpan.FromSeconds(120))
            .ConfigureAwait(false);

        ParseAppInfoOutput(result.StdOut, out var iconsProduced, out var labels);

        foreach (var (package, label) in labels)
            _labelCache[package] = label;

        // 没产出图标的包记入黑名单，避免下轮反复重试
        var producedSet = iconsProduced.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var package in batch)
        {
            if (!producedSet.Contains(package)) _knownNoIcon.TryAdd(package, true);
        }

        var done = doneBefore + batch.Count;

        // 名称先上报：图标还要经过设备端打包、pull、解包三步，先让界面能显示应用名
        onUpdate(new AppInfoUpdate
        {
            Stage = AppInfoStage.LabelsReady,
            Packages = batch,
            Labels = labels,
            Total = total,
            Done = done
        });

        var icons = await FetchIconsAsync(serial, iconsProduced).ConfigureAwait(false);

        onUpdate(new AppInfoUpdate
        {
            Stage = AppInfoStage.IconsReady,
            Packages = batch,
            Icons = icons,
            Total = total,
            Done = done
        });

        // 清理设备端图标目录（dex 保留复用）
        await RunAsync($"-s \"{serial}\" shell rm -rf {AppInfoRemoteIcons}",
                timeout: TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 设备端打 tar 包并 pull 回本地，解包后填充图标缓存。
    /// 返回本次取回的 包名 → PNG 字节（供流式回调携带，避免界面跨线程读缓存）。
    /// </summary>
    private async Task<Dictionary<string, byte[]>> FetchIconsAsync(string serial, List<string> produced)
    {
        var icons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (produced.Count == 0) return icons;

        // 1. 设备端打成一个 tar.gz（单个文件传输，比逐个 pull 快得多）。
        //    用 tar -C 指定目录，避免 `cd a && tar` 的 && 在多层转义下被破坏（实测报 "cd: too many arguments"）。
        var pack = await RunAsync(
                $"-s \"{serial}\" shell tar -czf {AppInfoRemoteTar} -C {AppInfoRemoteDir} icons",
                timeout: TimeSpan.FromMinutes(2))
            .ConfigureAwait(false);

        if (!pack.Success)
        {
            AppState.Log.Error(L("Apps_Err_IconPack", pack.Message));
            return icons;
        }

        // 2. 拉到本地临时目录
        var tempDir = Path.Combine(Path.GetTempPath(), "AdbManager", "icons_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tarLocal = Path.Combine(tempDir, "icons.tgz");

        try
        {
            var pull = await PullAsync(serial, AppInfoRemoteTar, tarLocal).ConfigureAwait(false);
            if (!pull.Success)
            {
                AppState.Log.Error(L("Apps_Err_IconPull", pull.Message));
                return icons;
            }

            // 3. 解包（System.Formats.Tar 或回退 tar.exe）
            var extractDir = Path.Combine(tempDir, "x");
            Directory.CreateDirectory(extractDir);
            if (!TryExtractTarGz(tarLocal, extractDir)) return icons;

            // 4. 读进内存缓存
            var iconsDir = Path.Combine(extractDir, "icons");
            if (!Directory.Exists(iconsDir)) iconsDir = extractDir;

            var loaded = 0;
            foreach (var file in Directory.EnumerateFiles(iconsDir, "*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    _iconCache[name] = bytes;
                    icons[name] = bytes;
                    loaded++;
                }
                catch
                {
                    // 单个图标读失败忽略
                }
            }

            AppState.Log.Info(L("Apps_IconLoaded", loaded.ToString()));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
        }

        return icons;
    }

    /// <summary>解压 tar.gz。优先用系统 tar.exe（Windows 10 1803+ 自带），失败则放弃。</summary>
    private static bool TryExtractTarGz(string tarPath, string destDir)
    {
        try
        {
            var tarExe = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (!File.Exists(tarExe)) tarExe = "tar";

            var psi = new System.Diagnostics.ProcessStartInfo(tarExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // -x 解包 / -z gzip / -f 指定文件 / -C 目标目录
            psi.ArgumentList.Add("-xzf");
            psi.ArgumentList.Add(tarPath);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(destDir);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(60_000);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppState.Log.Error(L("Apps_Err_IconExtract", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// 解析 AppInfoProbe 输出：每行「包名\t应用名\t图标路径」。
    /// 名称失败为 &lt;ERR&gt;，图标失败路径为空。
    /// </summary>
    private static void ParseAppInfoOutput(
        string output,
        out List<string> iconsProduced,
        out Dictionary<string, string> labels)
    {
        iconsProduced = new List<string>();
        labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("FATAL", StringComparison.Ordinal) ||
                line.StartsWith("USAGE", StringComparison.Ordinal)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            var package = parts[0].Trim();
            var label = parts[1].Trim();
            var iconPath = parts[2].Trim();

            if (package.Length == 0) continue;

            if (label.Length > 0 && !label.StartsWith("<ERR>", StringComparison.Ordinal))
                labels[package] = label;

            if (iconPath.Length > 0) iconsProduced.Add(package);
        }
    }

    /// <summary>把内置 dex push 到设备。返回是否可用。</summary>
    private async Task<bool> EnsureAppInfoDexAsync(string serial)
    {
        string local;
        try
        {
            local = LabelDexBinary.EnsureExtracted();
        }
        catch (Exception ex)
        {
            AppState.Log.Error(L("Apps_Err_LabelDexMissing") + " " + ex.Message);
            return false;
        }

        await RunAsync($"-s \"{serial}\" shell mkdir -p {AppInfoRemoteDir}",
                timeout: TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        // adb push 自身比对大小，内容一致时回报 "0 skipped" 不实际传输
        var push = await RunAsync($"-s \"{serial}\" push \"{local}\" {AppInfoRemoteDex}",
                timeout: TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        if (!push.Success && !push.Combined().Contains("0 skipped", StringComparison.OrdinalIgnoreCase))
        {
            AppState.Log.Error(L("Apps_Err_LabelDexPush", push.Message));
            return false;
        }

        return true;
    }
}
