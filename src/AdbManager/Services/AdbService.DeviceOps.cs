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
}

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

    public async Task<DeviceInfoReport> GetDeviceInfoReportAsync(string serial)
    {
        var report = new DeviceInfoReport();
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var command = "echo ==PROPS==; getprop; " +
                      "echo ==BATTERY==; dumpsys battery; " +
                      "cat /sys/class/power_supply/battery/uevent 2>/dev/null; " +
                      "cat /sys/class/power_supply/Battery/uevent 2>/dev/null; " +
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
        if (health.Length > 0) report.Battery[L("Bt_Health")] = health == "2" ? L("Bt_HealthGood") : health;
        var temperature = Battery("temperature");
        if (temperature.Length > 0 && int.TryParse(temperature, out var temp)) report.Battery[L("Bt_Temp")] = temp / 10.0 + " °C";
        var voltage = Battery("voltage");
        if (voltage.Length > 0) report.Battery[L("Bt_Voltage")] = voltage + " mV";
        var technology = Battery("technology");
        if (technology.Length > 0) report.Battery[L("Bt_Type")] = technology;
        string Uevent(string key) => Battery(key);
        var chargeFull = Uevent("POWER_SUPPLY_CHARGE_FULL");
        if (chargeFull.Length > 0 && long.TryParse(chargeFull, out var cf)) report.Battery[L("Bt_Capacity")] = cf / 1000 + " mAh";
        var chargeDesign = Uevent("POWER_SUPPLY_CHARGE_FULL_DESIGN");
        if (chargeDesign.Length > 0 && long.TryParse(chargeDesign, out var cd)) report.Battery[L("Bt_Design")] = cd / 1000 + " mAh";

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

        return report;
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
