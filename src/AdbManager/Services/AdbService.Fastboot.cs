using AdbManager.Models;
using Zeroconf;

namespace AdbManager.Services;

public sealed partial class AdbService
{
    /// <summary>fastboot 可执行文件路径（随 adb 一起释放）。</summary>
    public string FastbootPath => AdbBinary.FastbootPath;

    /// <summary>
    /// 扫描无线调试设备（mDNS）：与手机端 NsdManager 相同，直接向网络发送
    /// _adb-tls-connect/_adb-tls-pairing 查询，不依赖 adb server 的 mDNS 后端。
    /// 同时保留 adb mdns services 作为补充来源，并输出诊断信息。
    /// </summary>
    public async Task<(List<MdnsService> Services, List<string> Diagnostics)> ScanMdnsFullAsync()
    {
        var list = new List<MdnsService>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<string>();

        // 1) 原生 mDNS（Zeroconf，等同手机端 NsdManager）
        try
        {
            var responses = await ZeroconfResolver.ResolveAsync(
                new[] { "_adb-tls-connect._tcp.local.", "_adb-tls-pairing._tcp.local." },
                scanTime: TimeSpan.FromSeconds(6)).ConfigureAwait(false);

            var count = 0;
            foreach (var host in responses)
            {
                foreach (var service in host.Services.Values)
                {
                    var name = service.Name;
                    var address = $"{host.IPAddress}:{service.Port}";
                    var isPairing = name.Contains("_adb-tls-pairing", StringComparison.OrdinalIgnoreCase);
                    if (seen.Add(address))
                    {
                        list.Add(new MdnsService(name, address, isPairing));
                        count++;
                    }
                }
            }
            diagnostics.Add(L("Scan_ZeroconfFound", count));
        }
        catch (Exception ex)
        {
            diagnostics.Add(L("Scan_ZeroconfFail", ex.Message));
        }

        // 2) adb server 自带 mDNS（作为补充）
        var mdnsCheck = await RunAsync("mdns check", timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        diagnostics.Add("adb mdns check：" + (string.IsNullOrWhiteSpace(mdnsCheck.Combined()) ? L("Scan_NoOutput") : mdnsCheck.Combined().Trim()));

        var mdnsServices = await RunAsync("mdns services", timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var adbCount = 0;
        foreach (var rawLine in mdnsServices.StdOut.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("List of", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var name = parts[0].Trim();
            var address = parts[1].Trim();
            if (seen.Add(address))
            {
                list.Add(new MdnsService(name, address, name.Contains("_adb-tls-pairing")));
                adbCount++;
            }
        }
        diagnostics.Add(L("Scan_MdnsNew", adbCount));

        return (list, diagnostics);
    }

    /// <summary>获取设备的 WLAN IP（需要 adb 在线；USB/无线均可查询）。</summary>
    public async Task<string> GetWlanIpAsync(string serial)
    {
        var result = await RunAsync($"-s \"{serial}\" shell ip -f inet addr show wlan0", timeout: TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);

        var ip = ParseInetAddress(result.StdOut);
        if (ip.Length == 0)
        {
            var fallback = await RunAsync($"-s \"{serial}\" shell ip addr show wlan0", timeout: TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
            ip = ParseInetAddress(fallback.StdOut);
        }
        if (ip.Length == 0)
        {
            // 双卡/无 wlan0 时兜底：取路由表里的 wlan/eth0 网段地址
            var route = await RunAsync($"-s \"{serial}\" shell ip route", timeout: TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
            foreach (var rawLine in route.StdOut.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("192.168.", StringComparison.Ordinal) &&
                    !line.StartsWith("10.", StringComparison.Ordinal) &&
                    !line.StartsWith("172.", StringComparison.Ordinal)) continue;
                var via = line.IndexOf("src ", StringComparison.Ordinal);
                if (via < 0) continue;
                var rest = line[(via + 4)..].Trim();
                var ipCandidate = rest.Split(' ')[0];
                if (ipCandidate.Length > 0) return ipCandidate;
            }
        }
        return ip;
    }

    private static string ParseInetAddress(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith("inet ", StringComparison.Ordinal)) continue;
            var token = text.Split(' ')[1];
            var slash = token.IndexOf('/');
            return slash > 0 ? token[..slash] : token;
        }
        return "";
    }

    /// <summary>USB 一键转无线：tcpip 5555 → 查 WLAN IP → 自动 connect。</summary>
    public async Task<(bool Success, string Message)> UsbToWirelessAsync(string serial)
    {
        var tcpip = await TcpipAsync(serial, 5555).ConfigureAwait(false);
        if (!tcpip.Success) return (false, tcpip.Message);

        await Task.Delay(1000).ConfigureAwait(false); // 等待 adbd 重启到 TCP 模式

        var ip = await GetWlanIpAsync(serial).ConfigureAwait(false);
        if (ip.Length == 0) return (false, L("Adb_Err_NoWlanIp"));

        var connect = await ConnectAsync($"{ip}:5555").ConfigureAwait(false);
        return connect.Success
            ? (true, $"{ip}:5555")
            : (false, connect.Message);
    }

    /// <summary>列出 Fastboot（Bootloader）模式下的设备。</summary>
    public async Task<List<FastbootDevice>> GetFastbootDevicesAsync()
    {
        var result = await RunAsync("devices", timeout: TimeSpan.FromSeconds(30), exePath: FastbootPath)
            .ConfigureAwait(false);

        var devices = new List<FastbootDevice>();
        foreach (var rawLine in result.StdOut.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("*", StringComparison.Ordinal)) continue;
            if (line.StartsWith("List of", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            devices.Add(new FastbootDevice { Serial = parts[0], State = parts[1] });
        }

        if (devices.Count == 0 && !string.IsNullOrWhiteSpace(result.Combined()))
        {
            AppState.Log.Output(result.Combined().Trim());
        }
        return devices;
    }

    /// <summary>刷入镜像到指定分区（fastboot flash &lt;partition&gt; &lt;img&gt;）。</summary>
    public Task<AdbResult> FastbootFlashAsync(string serial, string partition, string imagePath, Action<string>? onLine = null) =>
        RunAsync($"-s {serial} flash {partition} \"{imagePath}\"", onLine, TimeSpan.FromMinutes(20), exePath: FastbootPath);

    /// <summary>擦除分区（fastboot erase，不可恢复）。</summary>
    public Task<AdbResult> FastbootEraseAsync(string serial, string partition, Action<string>? onLine = null) =>
        RunAsync($"-s {serial} erase {partition}", onLine, TimeSpan.FromMinutes(10), exePath: FastbootPath);

    /// <summary>读取设备信息（fastboot getvar all）。</summary>
    public Task<AdbResult> FastbootGetvarAllAsync(string serial, Action<string>? onLine = null) =>
        RunAsync($"-s {serial} getvar all", onLine, TimeSpan.FromMinutes(5), exePath: FastbootPath);

    /// <summary>从 Fastboot 模式重启：mode = 空(系统) / bootloader / recovery / fastboot。</summary>
    public Task<AdbResult> FastbootRebootAsync(string serial, string mode = "", Action<string>? onLine = null)
    {
        var target = string.IsNullOrWhiteSpace(mode) ? "reboot" : $"reboot {mode}";
        return RunAsync($"-s {serial} {target}".TrimEnd(), onLine, TimeSpan.FromMinutes(2), exePath: FastbootPath);
    }

    /// <summary>
    /// 提取分区镜像到电脑：dd 分区到 /sdcard/AdbManager/，再 adb pull 回本地。
    /// 部分只读分区需要 root 权限才能读取。
    /// </summary>
    public async Task<(bool Success, string Message)> ExtractPartitionAsync(
        string serial, string partition, string localDir, Action<string>? onLine = null)
    {
        var remote = $"/sdcard/AdbManager/{partition}.img";

        await RunAsync($"-s \"{serial}\" shell mkdir -p /sdcard/AdbManager", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        var dd = await RunAsync(
            $"-s \"{serial}\" shell dd if=/dev/block/by-name/{partition} of={remote} bs=1048576",
            onLine, TimeSpan.FromMinutes(30)).ConfigureAwait(false);
        if (!dd.Success) return (false, L("Fastboot_Err_Dd", dd.Message));

        var pull = await RunAsync($"-s \"{serial}\" pull \"{remote}\" \"{localDir}\"", onLine, TimeSpan.FromMinutes(30))
            .ConfigureAwait(false);
        if (!pull.Success) return (false, L("Adb_Err_Pull", pull.Message));

        _ = RunAsync($"-s \"{serial}\" shell rm \"{remote}\"", timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        var localPath = Path.Combine(localDir, $"{partition}.img");
        return (File.Exists(localPath), localPath);
    }

    /// <summary>读取分区表（/dev/block/by-name），非 root 下不可读时尝试 su。</summary>
    public async Task<List<RemoteFileItem>> ListPartitionsAsync(string serial)
    {
        var items = await ListDirectoryAsync(serial, "/dev/block/by-name").ConfigureAwait(false);
        if (items.Count > 0) return CleanPartitionNames(items);

        // 部分 ROM 需 root 才能列出分区
        var su = await RunAsync($"-s \"{serial}\" shell su -c \"ls -la /dev/block/by-name\"", timeout: TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        foreach (var rawLine in su.StdOut.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("total", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7) continue;

            var name = string.Join(' ', parts.Skip(parts.Length >= 8 ? 7 : parts.Length - 1));
            var arrow = name.IndexOf("->", StringComparison.Ordinal);
            if (arrow > 0) name = name[..arrow].Trim();
            if (name is "." or ".." or "") continue;

            items.Add(new RemoteFileItem
            {
                Name = name,
                FullPath = $"/dev/block/by-name/{name}",
                IsDirectory = false,
                IsLink = line[0] == 'l',
                Size = long.TryParse(parts[4], out var size) ? size : 0
            });
        }

        return CleanPartitionNames(items);
    }

    private static List<RemoteFileItem> CleanPartitionNames(List<RemoteFileItem> items)
    {
        foreach (var item in items)
        {
            // by-name 下多为符号链接："boot_a -> /dev/block/sde44"
            var arrow = item.Name.IndexOf("->", StringComparison.Ordinal);
            if (arrow > 0) item.Name = item.Name[..arrow].Trim();
        }
        return items
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
