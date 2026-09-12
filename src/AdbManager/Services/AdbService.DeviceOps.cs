using AdbManager.Models;

namespace AdbManager.Services;

/// <summary>设备信息报表（分组键值对）。</summary>
public sealed class DeviceInfoReport
{
    public Dictionary<string, string> Basic = new();
    public Dictionary<string, string> Battery = new();
    public Dictionary<string, string> Display = new();
    public Dictionary<string, string> Memory = new();
    public Dictionary<string, string> Storage = new();
    public Dictionary<string, string> Network = new();
    public Dictionary<string, string> System = new();

    /// <summary>内存 / 存储占用百分比（0~100），供信息卡占比环使用；取不到时为 null。</summary>
    public double? MemoryUsedPercent;
    public double? StorageUsedPercent;

    /// <summary>设备是否具有可用的 root（su）；为 false 时 Battery/System 中的 root 专属字段不会填充。</summary>
    public bool HasRoot;
}

/// <summary>一条已保存的 WiFi 配置。Password 为 null 表示开放网络（无密码）。</summary>
public sealed record WifiEntry(string Ssid, string? Password)
{
    public bool HasPassword => !string.IsNullOrEmpty(Password);
}

/// <summary>一条应用使用统计。</summary>
public sealed record UsageEntry(string Package, string Label, int Count);

public sealed partial class AdbService
{
    // ---------------- Recovery（TWRP）高级操作 ----------------

    /// <summary>清除锁屏密码（TWRP/恢复模式；加密设备可能无效）。</summary>
    public async Task<(bool Success, string Message)> ClearScreenLockAsync(string serial)
    {
        var command = "mount /data 2>/dev/null; rm -f /data/system/gesture.key " +
                      "/data/system/locksettings.db /data/system/locksettings.db-wal /data/system/locksettings.db-shm " +
                      "/data/system/password.key /data/system/pattern.key; echo done";
        var result = await RunAsync($"-s \"{serial}\" shell \"{command}\"", null, TimeSpan.FromMinutes(3))
            .ConfigureAwait(false);
        return (result.Success && result.Combined().Contains("done"), result.Message);
    }

    /// <summary>删除 Google 账号数据以跳过谷歌验证（TWRP/恢复模式；效果因机型而异）。</summary>
    public async Task<(bool Success, string Message)> FrpBypassAsync(string serial)
    {
        var command = "mount /data 2>/dev/null; rm -f /data/system/users/0/accounts.db " +
                      "/data/system/users/0/accounts.db-journal /data/system/users/0/accounts.db-wal " +
                      "/data/system/users/0/accounts.db-shm; echo done";
        var result = await RunAsync($"-s \"{serial}\" shell \"{command}\"", null, TimeSpan.FromMinutes(3))
            .ConfigureAwait(false);
        return (result.Success && result.Combined().Contains("done"), result.Message);
    }

    // ---------------- 反向共享电脑网络（HTTP 代理 + adb reverse） ----------------

    public async Task<(bool Success, string Message)> StartReverseTetherAsync(string serial, int port)
    {
        try
        {
            HttpProxyServer.Start(port);
        }
        catch (Exception ex)
        {
            return (false, L("Tether_Err_Start", ex.Message));
        }

        var reverse = await RunAsync($"-s \"{serial}\" reverse tcp:{port} tcp:{port}", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        if (!reverse.Success) { HttpProxyServer.Stop(); return (false, reverse.Message); }

        var setProxy = await RunAsync($"-s \"{serial}\" shell settings put global http_proxy 127.0.0.1:{port}",
            null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!setProxy.Success) { HttpProxyServer.Stop(); return (false, setProxy.Message); }

        var check = await RunAsync($"-s \"{serial}\" shell settings get global http_proxy", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        return (true, L("Tether_ProxySet", check.StdOut.Trim(), port));
    }

    public async Task<(bool Success, string Message)> StopReverseTetherAsync(string serial, int port)
    {
        var clear = await RunAsync($"-s \"{serial}\" shell settings put global http_proxy :0", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        var remove = await RunAsync($"-s \"{serial}\" reverse --remove tcp:{port}", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        HttpProxyServer.Stop();
        return (clear.Success, clear.Success ? L("Tether_Stopped") : clear.Message);
    }

    public async Task<(bool Success, string Message)> GetProxyStatusAsync(string serial)
    {
        var check = await RunAsync($"-s \"{serial}\" shell settings get global http_proxy", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        return (true, check.StdOut.Trim());
    }

    // ---------------- 设备信息（参考 wearadb 的分段采集） ----------------

    /// <summary>
    /// 检测设备是否具备可用的 root（su）。结果按序列号缓存，避免每次刷新都执行一次 su。
    /// 判据：`su -c id` 成功且输出包含 uid=0。
    /// </summary>
    private readonly Dictionary<string, bool> _rootCache = new();

    public async Task<bool> HasRootAsync(string serial, bool refresh = false)
    {
        if (!refresh && _rootCache.TryGetValue(serial, out var cached)) return cached;

        var result = await RunAsync($"-s \"{serial}\" shell su -c id", null, TimeSpan.FromSeconds(20))
            .ConfigureAwait(false);

        var ok = result.Combined().Contains("uid=0", StringComparison.Ordinal);
        _rootCache[serial] = ok;
        AppState.Log.Info($"root check ({serial}): {(ok ? "granted" : "unavailable")}");
        return ok;
    }

    /// <summary>
    /// 以 root 执行一条 shell 命令并返回原始 stdout。
    /// 注意：必须用单引号包裹（su -c '...'）——双引号包裹时 $() 命令替换会被
    /// 外层 adb/Windows 命令行提前消费，导致设备端拿到空值。
    /// </summary>
    private async Task<string> RunRootAsync(string serial, string command, TimeSpan? timeout = null)
    {
        var result = await RunAsync($"-s \"{serial}\" shell su -c '{command}'", null,
            timeout ?? TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        return result.StdOut;
    }

    public async Task<DeviceInfoReport> GetDeviceInfoReportAsync(string serial)
    {
        var report = new DeviceInfoReport();
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var command = "echo ==PROPS==; getprop; " +
                      "echo ==BATTERY==; dumpsys battery; " +
                      "echo ==DISPLAY==; wm size; wm density; " +
                      "echo ==MEM==; head -3 /proc/meminfo; " +
                      "echo ==UPTIME==; uptime; " +
                      "echo ==KERNEL==; cat /proc/version; " +
                      "echo ==NET==; ip addr show wlan0 2>/dev/null; settings get secure android_id; " +
                      "echo ==STORAGE==; df -k /data 2>/dev/null | head -5";

        var result = await RunAsync($"-s \"{serial}\" shell \"{command}\"", null, TimeSpan.FromMinutes(2))
            .ConfigureAwait(false);

        var current = "PROPS";
        foreach (var rawLine in result.StdOut.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith("=="))
            {
                current = trimmed.Trim('=', ' ').Trim();
                if (!sections.ContainsKey(current)) sections[current] = new List<string>();
                continue;
            }
            if (line.Length == 0) continue;
            if (trimmed.StartsWith("$ ") || trimmed.EndsWith("#") || trimmed.StartsWith("adb:")) continue; // shell 提示符
            if (!sections.ContainsKey(current)) sections[current] = new List<string>();
            sections[current].Add(line);
        }

        // 基本信息（getprop）
        var props = ParseProps(sections.GetValueOrDefault("PROPS") ?? new List<string>());
        string Prop(string key) => props.GetValueOrDefault(key, "");

        // 诊断：报告各分段行数与解析出的属性数（日志页可见，用于定位信息采集问题）
        AppState.Log.Info("device report: " + string.Join(", ",
            sections.Select(s => $"{s.Key}={s.Value.Count}")) + $", props={props.Count}");

        report.Basic[L("Info_Brand")] = Prop("ro.product.manufacturer");
        report.Basic[L("Info_Model")] = Prop("ro.product.model") + (Prop("ro.product.marketname").Length > 0 ? " (" + Prop("ro.product.marketname") + ")" : "");
        report.Basic[L("Info_Codename")] = Prop("ro.product.device");
        report.Basic[L("Info_AndroidVersion")] = Prop("ro.build.version.release");
        report.Basic[L("Info_Sdk")] = Prop("ro.build.version.sdk");
        report.Basic[L("Info_Patch")] = Prop("ro.build.version.security_patch");
        report.Basic[L("Info_BuildId")] = Prop("ro.build.display.id");
        report.Basic[L("Info_Fingerprint")] = Prop("ro.build.fingerprint");
        report.Basic[L("Info_Abi")] = Prop("ro.product.cpu.abi");

        // 电池
        var batteryLines = sections.GetValueOrDefault("BATTERY") ?? new List<string>();
        string Battery(string key)
        {
            foreach (var line in batteryLines)
            {
                if (line.Trim().StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    return line[(line.IndexOf(':') + 1)..].Trim();
            }
            foreach (var line in batteryLines)
            {
                if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    return line[(key.Length + 1)..].Trim();
            }
            return "";
        }

        var level = Battery("level");
        var status = Battery("status");
        var statusText = status switch
        {
            "2" => L("Bt_Charging"),
            "3" => L("Bt_Discharging"),
            "4" => L("Bt_NotCharging"),
            "5" => L("Bt_Full"),
            _ => status
        };
        if (level.Length > 0) report.Battery[L("Bt_Level")] = level + " %";
        if (status.Length > 0) report.Battery[L("Bt_Status")] = statusText;
        var health = Battery("health");
        var temperature = Battery("temperature");
        if (temperature.Length > 0 && int.TryParse(temperature, out var temp)) report.Battery[L("Bt_Temp")] = temp / 10.0 + " °C";
        var voltage = Battery("voltage");
        if (voltage.Length > 0) report.Battery[L("Bt_Voltage")] = voltage + " mV";
        var technology = Battery("technology");
        if (technology.Length > 0) report.Battery[L("Bt_Type")] = technology;

        // 电池容量 / 健康度 / 循环次数：/sys/class/power_supply/battery/ 下的
        // charge_full、charge_full_design、cycle_count、uevent 对 shell 身份全部 Permission denied，
        // 只有 root 可读。因此这三项统一走 root 通道；dumpsys battery 的 health 只是
        // 一个状态枚举（2=Good），并非容量健康度，故仅在无法计算百分比时作为兜底。
        report.HasRoot = await HasRootAsync(serial).ConfigureAwait(false);
        if (report.HasRoot)
        {
            var full = (await RunRootAsync(serial,
                "cat /sys/class/power_supply/battery/charge_full 2>/dev/null").ConfigureAwait(false)).Trim();
            var design = (await RunRootAsync(serial,
                "cat /sys/class/power_supply/battery/charge_full_design 2>/dev/null").ConfigureAwait(false)).Trim();

            if (long.TryParse(full, out var fullUah) && fullUah > 0)
                report.Battery[L("Bt_Capacity")] = fullUah / 1000 + " mAh";
            if (long.TryParse(design, out var designUah) && designUah > 0)
                report.Battery[L("Bt_Design")] = designUah / 1000 + " mAh";

            if (long.TryParse(full, out var hFull) && long.TryParse(design, out var hDesign) &&
                hFull > 0 && hDesign > 0)
            {
                var healthPct = hFull * 100.0 / hDesign;
                report.Battery[L("Bt_HealthPct")] = healthPct.ToString("0.0") + " %";
            }
            else if (health.Length > 0)
            {
                // 容量不可读时退回状态枚举，至少不丢失该项。
                report.Battery[L("Bt_HealthPct")] = health == "2" ? L("Bt_HealthGood") : health;
            }

            var cycles = await RunRootAsync(serial,
                "cat /sys/class/power_supply/battery/cycle_count 2>/dev/null").ConfigureAwait(false);
            var value = cycles.Trim();
            if (long.TryParse(value, out var cycleValue) && cycleValue >= 0)
                report.Battery[L("Bt_CycleCount")] = cycleValue.ToString();
        }
        else if (health.Length > 0)
        {
            report.Battery[L("Bt_HealthPct")] = health == "2" ? L("Bt_HealthGood") : health;
        }

        // 显示
        foreach (var line in sections.GetValueOrDefault("DISPLAY") ?? new List<string>())
        {
            var text = line.Trim();
            if (text.StartsWith("Physical size", StringComparison.OrdinalIgnoreCase))
                report.Display[L("Disp_Physical")] = text["Physical size:".Length..].Trim();
            if (text.StartsWith("Override size", StringComparison.OrdinalIgnoreCase))
                report.Display[L("Disp_Current")] = text["Override size:".Length..].Trim();
            if (text.StartsWith("Physical density", StringComparison.OrdinalIgnoreCase))
                report.Display[L("Disp_Density")] = text["Physical density:".Length..].Trim();
        }

        // 内存（运行内存）：总量 / 可用 / 已用（含占用率）
        // /proc/meminfo 行格式为 "MemTotal:  11785844 kB"，数值需先剥离 kB 后缀再解析
        long totalKb = 0, availKb = 0;
        foreach (var line in sections.GetValueOrDefault("MEM") ?? new List<string>())
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (!long.TryParse(line[(colon + 1)..].Replace("kB", "").Trim(), out var kb)) continue;

            if (name.Equals("MemTotal", StringComparison.OrdinalIgnoreCase))
            {
                totalKb = kb;
                report.Memory[L("Mem_Total")] = FormatKb(kb);
            }
            else if (name.Equals("MemAvailable", StringComparison.OrdinalIgnoreCase))
            {
                availKb = kb;
                report.Memory[L("Mem_Avail")] = FormatKb(kb);
            }
        }

        if (totalKb > 0 && availKb > 0)
        {
            var usedKb = Math.Max(0, totalKb - availKb);
            var percent = (int)Math.Round(usedKb * 100.0 / totalKb);
            report.Memory[L("Mem_Used")] = $"{FormatKb(usedKb)} ({percent} %)";
            report.MemoryUsedPercent = percent;
        }

        // 存储：df -k /data → 总容量 / 已用（含占用率）/ 可用，与内存卡同款显示形式
        foreach (var line in sections.GetValueOrDefault("STORAGE") ?? new List<string>())
        {
            var text = line.Trim();
            if (!text.Contains("/data")) continue;

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            if (!long.TryParse(parts[1], out var sizeKb)) continue;
            if (!long.TryParse(parts[2], out var usedKb)) continue;
            if (!long.TryParse(parts[3], out var freeKb)) continue;

            var percent = sizeKb > 0 ? (int)Math.Round(usedKb * 100.0 / sizeKb) : 0;
            report.Storage[L("Sto_Total")] = FormatKb(sizeKb);
            report.Storage[L("Sto_Free")] = FormatKb(freeKb);
            report.Storage[L("Sto_Used")] = $"{FormatKb(usedKb)} ({percent} %)";
            report.StorageUsedPercent = percent;
            break;
        }
        if (report.Storage.Count == 0)
            report.Storage[L("Sto_Data")] = string.Join(" | ", sections.GetValueOrDefault("STORAGE") ?? new List<string>());

        // 网络
        var netLines = sections.GetValueOrDefault("NET") ?? new List<string>();
        foreach (var line in netLines)
        {
            var text = line.Trim();
            if (text.StartsWith("inet "))
            {
                var ip = text.Split(' ')[1];
                report.Network[L("Net_WlanIp")] = ip;
            }
        }
        var androidId = netLines.FirstOrDefault(l => !l.Contains("inet") && l.Trim().Length >= 8 && !l.Contains(":"));
        if (androidId is not null && androidId.Trim().Length >= 8) report.Network["Android ID"] = androidId.Trim();

        // 系统
        var kernel = (sections.GetValueOrDefault("KERNEL") ?? new List<string>()).FirstOrDefault();
        if (kernel is not null) report.System[L("Sys_Kernel")] = kernel.Trim();
        var uptime = (sections.GetValueOrDefault("UPTIME") ?? new List<string>()).FirstOrDefault();
        if (uptime is not null) report.System[L("Sys_Uptime")] = uptime.Trim();
        report.System[L("Devices_Serial")] = serial;

        // ---- root 专属：芯片平台 / IMEI / UFS 闪存寿命 ----
        if (report.HasRoot)
        {
            var platform = (await RunRootAsync(serial, "getprop ro.board.platform")
                .ConfigureAwait(false)).Trim();
            if (platform.Length > 0) report.System[L("Sys_Platform")] = platform;

            var imei = await ReadImeiAsync(serial).ConfigureAwait(false);
            if (imei.Length > 0) report.System[L("Sys_Imei")] = imei;

            var ufs = await ReadUfsHealthAsync(serial).ConfigureAwait(false);
            if (ufs.Length > 0) report.System[L("Sys_UfsLife")] = ufs;
        }

        return report;
    }

    /// <summary>
    /// 读取 IMEI（双卡时以「卡1 / 卡2」形式返回）。多路径兜底：不同 ROM 暴露的属性名不一致。
    /// </summary>
    private async Task<string> ReadImeiAsync(string serial)
    {
        var output = await RunRootAsync(serial,
            "getprop persist.radio.imei; getprop persist.radio.imei2; " +
            "getprop ro.ril.oem.imei; getprop persist.vendor.radio.imei",
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        var seen = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var value = raw.Trim();
            if (value.Length < 10) continue;                  // IMEI 15 位；过滤空行与错误文本
            if (value.Contains(' ') || value.Contains('[')) continue;
            if (!value.All(char.IsDigit)) continue;            // 纯数字校验
            if (seen.Contains(value)) continue;                // 去重（多个属性可能返回同一值）
            seen.Add(value);
        }

        if (seen.Count == 0) return "";
        if (seen.Count == 1) return seen[0];
        return string.Join(" / ", seen.Select((v, i) => $"SIM{i + 1}: {v}"));
    }

    /// <summary>
    /// 读取 UFS 闪存剩余寿命。路径含 SoC 地址（因机型而异），故先用 find 定位目录再读取。
    /// 分两步执行（不嵌套 $() 命令替换——多层 shell 转义会吃掉子表达式）。
    /// 换算：health_descriptor 的 life_time_estimation 为 0x01~0x0B，值越小寿命越充足。
    /// </summary>
    private async Task<string> ReadUfsHealthAsync(string serial)
    {
        // 第 1 步：定位 health_descriptor 目录（不同机型 SoC 地址不同，无法写死）。
        var find = await RunAsync($"-s \"{serial}\" shell su -c " +
            "'find /sys/devices -type d -name health_descriptor 2>/dev/null | head -1'",
            null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        var dir = find.StdOut.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("/sys/", StringComparison.Ordinal));
        if (string.IsNullOrEmpty(dir)) return "";

        // 第 2 步：按已知目录读取两个寿命字段。
        var read = await RunAsync($"-s \"{serial}\" shell su -c " +
            $"'cat {dir}/life_time_estimation_a 2>/dev/null; cat {dir}/life_time_estimation_b 2>/dev/null'",
            null, TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        var values = read.StdOut.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (values.Count == 0) return "";

        var parts = new List<string>();
        for (var i = 0; i < values.Count && i < 2; i++)
        {
            if (!TryParseHexByte(values[i], out var level)) continue;
            // 0x01 表示寿命充足，0x0B 表示已接近寿命终点；换算为剩余百分比。
            var remaining = Math.Clamp((0x0B - level) * 10, 0, 100);
            parts.Add($"{(i == 0 ? L("Sys_UfsA") : L("Sys_UfsB"))} {remaining} %");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>解析 "0x01" / "1" 形式的十六进制或十进制字节。</summary>
    private static bool TryParseHexByte(string text, out int value)
    {
        value = 0;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return int.TryParse(trimmed, out value);
    }

    // ---------------- WiFi 密码（root 专属） ----------------

    /// <summary>
    /// 读取已保存的 WiFi 网络与密码。文件受 system 属主保护（-rw-------），仅 root 可读；
    /// 老版本 Android 路径为 /data/misc/wifi/WifiConfigStore.xml，一并兜底。
    /// 复制到 /data/local/tmp 后 pull 回本地解析 XML（比在设备端 grep 更可靠）。
    /// </summary>
    public async Task<List<WifiEntry>> GetWifiPasswordsAsync(string serial)
    {
        var list = new List<WifiEntry>();
        if (!await HasRootAsync(serial).ConfigureAwait(false)) return list;

        const string tmp = "/data/local/tmp/adbmgr_wifi.xml";
        var candidates = new[]
        {
            "/data/misc/apexdata/com.android.wifi/WifiConfigStore.xml",
            "/data/misc/wifi/WifiConfigStore.xml"
        };

        string? pulled = null;
        foreach (var source in candidates)
        {
            // 单引号包裹命令体（避免 $()/重定向被外层命令行消费）。
            var copy = await RunRootAsync(serial,
                $"if [ -f {source} ]; then cat {source} > {tmp}; chmod 644 {tmp}; echo OK; fi",
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!copy.Contains("OK")) continue;

            var local = Path.Combine(Path.GetTempPath(), $"adbmgr_wifi_{Guid.NewGuid():N}.xml");
            var pull = await RunAsync($"-s \"{serial}\" pull {tmp} \"{local}\"", null, TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);
            _ = RunAsync($"-s \"{serial}\" shell rm -f {tmp}", timeout: TimeSpan.FromSeconds(15));

            if (pull.Success && File.Exists(local)) { pulled = local; break; }
        }

        if (pulled is null) return list;

        try
        {
            list = ParseWifiConfig(pulled);
        }
        catch (Exception ex)
        {
            AppState.Log.Error($"wifi config parse: {ex.Message}");
        }
        finally
        {
            try { File.Delete(pulled); } catch { /* 临时文件清理失败可忽略 */ }
        }

        return list;
    }

    /// <summary>
    /// 解析 WifiConfigStore.xml：结构为 NetworkList/Network/WifiConfiguration/string[@name]。
    /// 逐个 Network 配对 SSID 与 PreSharedKey（密码字段可能不存在，表示开放网络）。
    /// </summary>
    private static List<WifiEntry> ParseWifiConfig(string path)
    {
        var list = new List<WifiEntry>();
        var document = System.Xml.Linq.XDocument.Load(path);
        var root = document.Root;
        if (root is null) return list;

        var networkList = root.Element("NetworkList");
        if (networkList is null) return list;

        foreach (var network in networkList.Elements("Network"))
        {
            var config = network.Element("WifiConfiguration");
            if (config is null) continue;

            string? ssid = null, psk = null;
            foreach (var node in config.Elements("string"))
            {
                var name = node.Attribute("name")?.Value;
                var value = node.Value;
                if (name == "SSID") ssid = UnwrapQuoted(value);
                else if (name == "PreSharedKey") psk = UnwrapQuoted(value);
            }

            if (string.IsNullOrEmpty(ssid)) continue;

            // SSID 为 "..." 形式表示普通名称；十六进制形式（"0x..."）表示含特殊字符的名称。
            var resolved = DecodeSsid(ssid!);
            var hasPassword = !string.IsNullOrEmpty(psk) && psk != "*";
            list.Add(new WifiEntry(resolved, hasPassword ? psk : null));
        }

        return list;
    }

    /// <summary>去掉 XML 值外围的引号（WifiConfigStore 对字符串统一加 " " 包裹）。</summary>
    private static string UnwrapQuoted(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"')) text = text[1..^1];
        return text;
    }

    /// <summary>SSID 若为 0x 开头的十六进制串，按 UTF-8 还原；否则原样返回。</summary>
    private static string DecodeSsid(string ssid)
    {
        if (!ssid.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || ssid.Length <= 2) return ssid;

        try
        {
            var hex = ssid[2..];
            if (hex.Length % 2 != 0) return ssid;
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            return decoded.All(c => !char.IsControl(c)) ? decoded : ssid;
        }
        catch
        {
            return ssid;
        }
    }

    // ---------------- 应用使用统计 ----------------

    /// <summary>
    /// 读取应用使用统计。优先解析 /data/system/usagestats（root，含时长），
    /// 失败则回退 dumpsys usagestats 的事件流聚合（无需 root，但只有次数没有时长）。
    /// </summary>
    public async Task<List<UsageEntry>> GetUsageStatsAsync(string serial, int top = 12)
    {
        var result = await RunAsync($"-s \"{serial}\" shell dumpsys usagestats",
            null, TimeSpan.FromMinutes(1)).ConfigureAwait(false);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in result.StdOut.Split('\n'))
        {
            // 形如：time="..." type=ACTIVITY_RESUMED package=com.xxx flags=0x0
            if (!raw.Contains("type=ACTIVITY_RESUMED", StringComparison.Ordinal)) continue;

            var marker = raw.IndexOf("package=", StringComparison.Ordinal);
            if (marker < 0) continue;
            var rest = raw[(marker + "package=".Length)..];
            var end = rest.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            var package = end < 0 ? rest.Trim() : rest[..end].Trim();
            if (package.Length == 0) continue;

            counts[package] = counts.GetValueOrDefault(package) + 1;
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .Take(top)
            .Select(pair => new UsageEntry(pair.Key, pair.Key, pair.Value))
            .ToList();
    }

    /// <summary>把使用统计里的包名补成应用名（复用应用页的 label dex 方案）；失败则保留包名。</summary>
    public async Task<List<UsageEntry>> FillUsageLabelsAsync(string serial, List<UsageEntry> usage)
    {
        if (usage.Count == 0) return usage;

        try
        {
            var labels = await QueryLabelsAsync(serial, usage.Select(u => u.Package).ToList())
                .ConfigureAwait(false);
            if (labels.Count == 0) return usage;

            return usage
                .Select(u => labels.TryGetValue(u.Package, out var label) && label.Length > 0
                    ? u with { Label = label }
                    : u)
                .ToList();
        }
        catch (Exception ex)
        {
            AppState.Log.Error($"usage labels: {ex.Message}");
            return usage;
        }
    }

private static string FormatKb(long kb)
{
    var mb = kb / 1024.0;
    return mb >= 1024 ? (mb / 1024).ToString("0.#") + " GB" : ((int)mb) + " MB";
}

private static Dictionary<string, string> ParseProps(List<string> lines)
{
    var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in lines)
    {
        var text = line.Trim();
        if (!text.StartsWith('[')) continue;
        var closeKey = text.IndexOf(']');
        if (closeKey <= 0) continue;
        var key = text[1..closeKey];
        // getprop 行格式为 [key]: [value]，key 的 ] 之后是 ": " 分隔符，必须先跳过
        var rest = text[(closeKey + 1)..].Trim().TrimStart(':', ' ');
        if (!rest.StartsWith('[')) continue;
        var closeValue = rest.LastIndexOf(']');
        if (closeValue < 0) continue;
        props[key] = rest[1..closeValue];
    }
    return props;
}
}
