using System.Text.RegularExpressions;
using AdbManager.Models;

namespace AdbManager.Services;

/// <summary>
/// 运行中进程：`ps -A` 取进程行，与 `pm list packages` 交叉匹配后只保留属于
/// 已安装应用（系统 / 第三方）的进程。
///
/// 匹配分两层：
/// 1. 进程名（去掉 :子进程 后缀）直接命中包名——覆盖绝大多数应用；
/// 2. 未命中的行若以应用 UID（u0_aXXX）运行，用 `pm list packages -U` 的
///    uid → 包名映射反查——覆盖 android.process.acore、
///    com.google.android.gms.unstable、android:process 自定义全名等别名进程。
/// 原生守护进程（root / system / radio 等 UID，如 vendor.*-service）两层都
/// 不命中，天然被过滤，不会误当成可强停的应用。
/// </summary>
public sealed partial class AdbService
{
    private static readonly Regex AppUidRegex = new(@"^u(\d+)_a(\d+)$", RegexOptions.Compiled);

    /// <summary>Android uid 计算常量：uid = userId × 100000 + 10000 + 应用序号。</summary>
    private const long PerUserRange = 100_000;
    private const long FirstApplicationUid = 10_000;

    /// <summary>列出当前运行中的应用（按包名聚合，含应用名与图标）。失败时抛出异常由调用方处理。</summary>
    public async Task<List<ProcessInfo>> ListRunningAppsAsync(string serial)
    {
        // ps 输出较慢的设备上可能超过默认超时，放宽到 30s 已足够
        List<(string Name, int Pid, long RssKb, string User)> processes;
        try
        {
            processes = await QueryProcessesAsync(serial, "ps -A").ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // 老版本 Android（toolbox ps）不支持 -A，回退无参 ps；两次都失败时由第二次抛出
            processes = await QueryProcessesAsync(serial, "ps").ConfigureAwait(false);
        }

        if (processes.Count == 0) return new List<ProcessInfo>();

        // 系统 / 第三方包集合与 uid 映射相互独立，并行查询
        var systemTask = QueryPackagesAsync(serial, "-s");
        var thirdPartyTask = QueryPackagesAsync(serial, "-3");
        var uidMapTask = QueryPackageUidsAsync(serial);
        await Task.WhenAll(systemTask, thirdPartyTask, uidMapTask).ConfigureAwait(false);

        var systemSet = systemTask.Result.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var thirdPartySet = thirdPartyTask.Result.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uidMap = uidMapTask.Result;

        // 按包名聚合：子进程（com.a.b:push）并入主包，PID 全保留、RSS 求和
        var grouped = new Dictionary<string, ProcessInfo>(StringComparer.OrdinalIgnoreCase);

        ProcessInfo GetOrCreate(string package, bool isSystem)
        {
            if (!grouped.TryGetValue(package, out var info))
            {
                info = new ProcessInfo { PackageName = package, IsSystem = isSystem };
                grouped[package] = info;
            }
            return info;
        }

        foreach (var (name, pid, rssKb, user) in processes)
        {
            var package = name;
            var colon = package.IndexOf(':');
            if (colon > 0) package = package[..colon];

            if (systemSet.Contains(package)) { GetOrCreate(package, true).Add(pid, rssKb); continue; }
            if (thirdPartySet.Contains(package)) { GetOrCreate(package, false).Add(pid, rssKb); continue; }

            var resolved = ResolveByAppUid(user, package, uidMap, systemSet, thirdPartySet);
            if (resolved is null) continue;
            GetOrCreate(resolved.Value.Package, resolved.Value.IsSystem).Add(pid, rssKb);
        }

        var apps = grouped.Values.ToList();

        // 应用名 + 图标：复用应用管理页的 app_process 通道与会话缓存，
        // 应用管理页已加载过时这里几乎零开销
        await LoadAppInfoForAppsAsync(serial, apps).ConfigureAwait(false);

        return apps.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>执行 ps 并解析出（进程名, PID, RSS KB, USER）。命令失败时抛异常，由调用方提示用户。</summary>
    private async Task<List<(string Name, int Pid, long RssKb, string User)>> QueryProcessesAsync(
        string serial, string command)
    {
        var result = await RunAsync($"-s \"{serial}\" shell {command}", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        var rows = ParsePsOutput(result.StdOut);

        // 成功的 ps 至少会输出表头 + 常驻进程；完全无输出且有报错视为命令失败
        if (rows.Count == 0 && result.StdOut.Trim().Length == 0 && result.StdErr.Trim().Length > 0)
            throw new InvalidOperationException(result.StdErr.Trim());

        return rows;
    }

    /// <summary>解析 ps 输出行，只保留进程名含「.」的行（应用进程必有包名点号，内核线程与 zygote 均无）。</summary>
    private static List<(string Name, int Pid, long RssKb, string User)> ParsePsOutput(string output)
    {
        var rows = new List<(string, int, long, string)>();
        foreach (var rawLine in output.Split('\n'))
        {
            var columns = rawLine.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // 新 toybox 格式：USER PID PPID VSZ RSS WCHAN ADDR S NAME（9 列）
            // 旧 toolbox 格式：USER PID PPID VSIZE RSS WCHAN PC NAME（8 列）——两者 RSS 都在第 5 列
            if (columns.Length < 8) continue;
            if (!int.TryParse(columns[1], out var pid) || pid <= 0) continue;
            if (!long.TryParse(columns[4], out var rssKb)) rssKb = 0;

            // NAME 列理论上可含空格：从固定列起把剩余列拼回去
            var nameStart = columns.Length >= 9 ? 8 : 7;
            var name = string.Join(' ', columns, nameStart, columns.Length - nameStart).Trim();
            if (name.Length == 0 || !name.Contains('.')) continue;

            rows.Add((name, pid, rssKb, columns[0]));
        }

        return rows;
    }

    /// <summary>
    /// 读取设备运存占用（/proc/meminfo 快照）。老内核没有 MemAvailable 时
    /// 按 MemFree + Cached + Buffers - Shmem 估算。读取失败返回 null。
    /// </summary>
    public async Task<DeviceMemoryInfo?> GetDeviceMemoryAsync(string serial)
    {
        var result = await RunAsync($"-s \"{serial}\" shell cat /proc/meminfo", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        long total = 0, available = 0, free = 0, buffers = 0, cached = 0, shmem = 0;
        foreach (var rawLine in result.StdOut.Split('\n'))
        {
            var colon = rawLine.IndexOf(':');
            if (colon <= 0) continue;
            var key = rawLine[..colon].Trim();
            var valueText = rawLine[(colon + 1)..].Trim();
            var space = valueText.IndexOf(' ');
            if (space > 0) valueText = valueText[..space];
            if (!long.TryParse(valueText, out var value)) continue;

            switch (key)
            {
                case "MemTotal": total = value; break;
                case "MemAvailable": available = value; break;
                case "MemFree": free = value; break;
                case "Buffers": buffers = value; break;
                case "Cached": cached = value; break;
                case "Shmem": shmem = value; break;
            }
        }

        if (total <= 0) return null;
        if (available <= 0) available = Math.Max(0, free + cached + buffers - shmem);
        return new DeviceMemoryInfo(total, available);
    }

    /// <summary>
    /// 取当前前台应用包名（topResumedActivity / mFocusedActivity）。
    /// 先用设备端 grep 只回传 resumed 行；个别 ROM grep 异常时回退全量 dumpsys 在本机解析。
    /// 取不到（锁屏、奇异 ROM）返回 null。
    /// </summary>
    public async Task<string?> GetForegroundPackageAsync(string serial)
    {
        var result = await RunAsync(
                $"-s \"{serial}\" shell \"dumpsys activity activities | grep -iE 'resumedactivity|focusedactivity'\"",
                null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        var package = ParseForegroundPackage(result.StdOut);
        if (package is not null) return package;

        var full = await RunAsync($"-s \"{serial}\" shell dumpsys activity activities",
                null, TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        return ParseForegroundPackage(full.StdOut);
    }

    /// <summary>从 dumpsys 行提取包名：ActivityRecord{… u0 com.example/.MainActivity …}。</summary>
    private static readonly Regex ForegroundPackageRegex =
        new(@"u\d+\s+([a-zA-Z][\w]*(?:\.\w+)+)/", RegexOptions.Compiled);

    private static string? ParseForegroundPackage(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.Contains("ActivityRecord", StringComparison.Ordinal)) continue;
            var match = ForegroundPackageRegex.Match(line);
            if (match.Success) return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// `pm list packages -U` 输出解析成 uid → 包名列表（共享 uid 的多包都在列表里；
    /// 多用户包如 gms 会输出「uid:10128,99910128」逗号分隔的多个 uid）。
    /// 老版本 pm 不支持 -U 时返回空字典，匹配退化为仅按进程名。
    /// </summary>
    private async Task<Dictionary<long, List<string>>> QueryPackageUidsAsync(string serial)
    {
        var result = await RunAsync($"-s \"{serial}\" shell pm list packages -U", null, TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        var map = new Dictionary<long, List<string>>();
        foreach (var rawLine in result.StdOut.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("package:", StringComparison.OrdinalIgnoreCase)) continue;

            var uidIndex = line.IndexOf(" uid:", StringComparison.OrdinalIgnoreCase);
            if (uidIndex < 0) continue;
            var name = line["package:".Length..uidIndex].Trim();
            if (name.Length == 0) continue;

            foreach (var token in line[(uidIndex + " uid:".Length)..].Split(','))
            {
                if (!long.TryParse(token.Trim(), out var uid)) continue;
                if (!map.TryGetValue(uid, out var list)) map[uid] = list = new List<string>();
                list.Add(name);
            }
        }

        return map;
    }

    /// <summary>
    /// 用应用 UID 反查进程所属包。仅接受 uX_aY 形式的用户名——system / radio 等共享
    /// UID 对应大量包，无法消歧，一律放弃（这类原生进程本就不该出现在应用列表里）。
    /// </summary>
    private static (string Package, bool IsSystem)? ResolveByAppUid(
        string user,
        string processName,
        Dictionary<long, List<string>> uidMap,
        HashSet<string> systemSet,
        HashSet<string> thirdPartySet)
    {
        if (uidMap.Count == 0) return null;

        var match = AppUidRegex.Match(user);
        if (!match.Success) return null;
        var uid = long.Parse(match.Groups[1].Value) * PerUserRange
                  + FirstApplicationUid
                  + long.Parse(match.Groups[2].Value);
        if (!uidMap.TryGetValue(uid, out var candidates)) return null;

        // 共享 uid 的多个包：进程名一般以其中一个包名为前缀，否则无法消歧
        var package = candidates.Count == 1
            ? candidates[0]
            : candidates.FirstOrDefault(c => processName.StartsWith(c, StringComparison.OrdinalIgnoreCase));
        if (package is null) return null;

        if (systemSet.Contains(package)) return (package, true);
        if (thirdPartySet.Contains(package)) return (package, false);
        return null;
    }

    /// <summary>批量补齐应用名与图标（走应用管理页的 dex 通道），结果写入会话缓存。</summary>
    private async Task LoadAppInfoForAppsAsync(string serial, List<ProcessInfo> apps)
    {
        // 换设备时整体作废图标缓存，语义与应用管理页一致
        if (!string.Equals(_iconCacheSerial, serial, StringComparison.Ordinal))
        {
            _iconCache.Clear();
            _knownNoIcon.Clear();
            _iconCacheSerial = serial;
        }

        var pending = apps
            .Where(p => !_labelCache.ContainsKey(p.PackageName) ||
                        (!_iconCache.ContainsKey(p.PackageName) && !_knownNoIcon.Contains(p.PackageName)))
            .Select(p => p.PackageName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pending.Count == 0)
        {
            ApplyCachedAppInfo(apps);
            return;
        }

        try
        {
            if (!await EnsureAppInfoDexAsync(serial).ConfigureAwait(false)) return;

            for (var offset = 0; offset < pending.Count; offset += AppInfoBatchSize)
            {
                var batch = pending.Skip(offset).Take(AppInfoBatchSize).ToList();
                await QueryAppInfoBatchAsync(serial, batch).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppState.Log.Error(L("Apps_Err_LabelRead", ex.Message));
        }
        finally
        {
            // 成败都回填一次缓存里已有的值：拿不到的项界面回退显示包名 / 占位图标
            ApplyCachedAppInfo(apps);
        }
    }

    /// <summary>把会话缓存里已有的应用名与图标写回到运行列表项。</summary>
    private void ApplyCachedAppInfo(List<ProcessInfo> apps)
    {
        foreach (var app in apps)
        {
            if (_labelCache.TryGetValue(app.PackageName, out var label) && label.Length > 0)
                app.Label = label;
            app.IconBytes = GetCachedIcon(app.PackageName);
        }
    }
}
