using System.Diagnostics;
using System.Text;
using AdbManager.Models;

namespace AdbManager.Services;

public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>给界面展示的合并输出（优先 stdout，其次 stderr）。</summary>
    public string Message
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(StdOut) ? StdErr : StdOut;
            return text.Trim();
        }
    }
}

public sealed record MdnsService(string Name, string Address, bool IsPairing);

/// <summary>
/// 对谷歌官方 adb 的进程级封装：服务器管理、设备发现、无线配对/连接、常用工具命令。
/// </summary>
public sealed partial class AdbService
{
    private static string L(string key) => LocalizationService.Get(key);
    private static string L(string key, params object[] args) => LocalizationService.Get(key, args);
    private string? _adbPath;
    private Process? _recordingProcess;
    private string? _recordingRemoteFile;

    public string AdbPath => _adbPath ??= "adb";
    public bool UsingBundled { get; private set; }
    public string SourceText => UsingBundled ? L("Adb_SourceBundled", AdbBinary.BundledVersion) : L("Adb_SourcePath");

    /// <summary>准备 adb 可执行文件：优先使用内置官方 adb，失败时回退到 PATH。</summary>
    public void Initialize()
    {
        try
        {
            if (AdbBinary.EnsureExtracted())
            {
                _adbPath = AdbBinary.AdbPath;
                UsingBundled = true;
            }
        }
        catch (Exception ex)
        {
            AppState.Log.Error(L("Adb_Err_Extract", ex.Message));
        }

        if (!UsingBundled) _adbPath = "adb";
        AppState.Log.Info(L("Adb_SourceLog", SourceText, AdbPath));
    }

    // ---------------- 进程封装 ----------------

    private Process CreateProcess(string arguments, string? exePath = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath ?? AdbPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(exePath ?? AdbPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        return new Process { StartInfo = psi };
    }

    /// <summary>执行一条 adb / fastboot 命令，可选逐行回调用于实时日志。</summary>
    public async Task<AdbResult> RunAsync(string arguments,
        Action<string>? onLine = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        string? exePath = null)
    {
        AppState.Log.Command($"{Path.GetFileNameWithoutExtension(exePath ?? AdbPath)} {arguments}");
        if (!string.IsNullOrWhiteSpace(arguments) && onLine is null)
        {
            // 命令行本身记录即可
        }

        using var process = CreateProcess(arguments, exePath);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            var msg = L("Adb_Err_Start", ex.Message);
            AppState.Log.Error(msg);
            return new AdbResult(-1, "", msg);
        }

        var token = linked.Token;
        try
        {
            var stdoutTask = ReadAllAsync(process.StandardOutput, onLine, token);
            var stderrTask = ReadAllAsync(process.StandardError, onLine, token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);

            var result = new AdbResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
            if (!string.IsNullOrWhiteSpace(result.StdErr)) AppState.Log.Output(result.StdErr.Trim());
            return result;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var msg = L("Adb_Err_Canceled");
            AppState.Log.Error(msg);
            return new AdbResult(-1, "", msg);
        }
        catch (Exception ex)
        {
            TryKill(process);
            AppState.Log.Error(ex.Message);
            return new AdbResult(-1, "", ex.Message);
        }
    }

    /// <summary>执行 adb exec-out 类命令并返回原始字节（截图等二进制数据）。</summary>
    public async Task<byte[]> RunBinaryAsync(string arguments, TimeSpan? timeout = null)
    {
        AppState.Log.Command("adb " + arguments);
        using var process = CreateProcess(arguments);
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            process.Start();
            using var buffer = new MemoryStream();
            var readTask = process.StandardOutput.BaseStream.CopyToAsync(buffer, 81920, cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(readTask, errorTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var error = (await errorTask).Trim();
            if (!string.IsNullOrWhiteSpace(error)) AppState.Log.Output(error);

            var bytes = buffer.ToArray();
            if (bytes.Length == 0) AppState.Log.Error(L("Adb_Err_NoData"));
            return bytes;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            AppState.Log.Error(L("Adb_Err_Timeout"));
            return Array.Empty<byte>();
        }
    }

    private static async Task<string> ReadAllAsync(StreamReader reader, Action<string>? onLine, CancellationToken token)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) break;
            builder.AppendLine(line);
            if (onLine is not null)
            {
                var text = line.TrimEnd();
                if (!string.IsNullOrWhiteSpace(text)) onLine(text);
            }
        }
        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { /* 已退出 */ }
    }

    // ---------------- 服务器 ----------------

    public Task<AdbResult> StartServerAsync() => RunAsync("start-server", timeout: TimeSpan.FromSeconds(30));

    public Task<AdbResult> KillServerAsync() => RunAsync("kill-server", timeout: TimeSpan.FromSeconds(30));

    /// <summary>应用退出时清理：结束可能遗留的录屏进程并停止 adb server 守护进程，
    /// 避免关闭软件后 adb server 与设备连接仍在后台驻留。</summary>
    public async Task ShutdownAsync()
    {
        if (_recordingProcess is not null)
        {
            TryKill(_recordingProcess);
            try { _recordingProcess.Dispose(); } catch { /* 已退出 */ }
            _recordingProcess = null;
            _recordingRemoteFile = null;
        }

        await KillServerAsync().ConfigureAwait(false);
    }

    /// <summary>重启 adb 服务；遇到版本冲突会强制清理残留 server 进程。</summary>
    public async Task<AdbResult> RestartServerAsync()
    {
        var kill = await KillServerAsync().ConfigureAwait(false);
        var text = kill.Combined();
        if (text.Contains("mismatch", StringComparison.OrdinalIgnoreCase) || !kill.Success)
        {
            AppState.Log.Info(L("Adb_RestartDetect"));
            foreach (var p in Process.GetProcessesByName("adb"))
            {
                try { if (p.Id != Environment.ProcessId) p.Kill(true); } catch { /* 权限不足则跳过 */ }
                finally { p.Dispose(); }
            }
            await Task.Delay(500).ConfigureAwait(false);
        }

        var start = await StartServerAsync().ConfigureAwait(false);
        AppState.Log.Info(start.Success ? L("Adb_ServerStarted") : L("Adb_ServerStartError", start.Message));
        return start;
    }

    // ---------------- 设备 ----------------

    /// <summary>adb devices -l 解析。</summary>
    public async Task<List<AdbDevice>> GetDevicesAsync()
    {
        var result = await RunAsync("devices -l", timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var devices = new List<AdbDevice>();

        foreach (var rawLine in result.StdOut.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("*", StringComparison.Ordinal)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var device = new AdbDevice { Serial = parts[0], State = parts[1] };
            foreach (var field in parts.Skip(2))
            {
                var index = field.IndexOf(':');
                if (index <= 0) continue;
                var key = field[..index];
                var value = field[(index + 1)..];
                switch (key)
                {
                    case "model": device.Model = value; break;
                    case "product": device.Product = value; break;
                    case "device": device.DeviceCodename = value; break;
                    case "transport_id": device.TransportId = value; break;
                    case "usb": device.UsbPort = value; break;
                }
            }

            if (string.IsNullOrEmpty(device.Model) && device.Serial.Contains(':') && device.IsOnline)
            {
                // 无线设备：devices -l 有时拿不到 model，按需补查
                device.Model = await QueryPropAsync(device.Serial, "ro.product.model").ConfigureAwait(false);
            }

            devices.Add(device);
        }

        return devices;
    }

    private async Task<string> QueryPropAsync(string serial, string property)
    {
        var result = await RunAsync($"-s \"{serial}\" shell getprop {property}", timeout: TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        return result.StdOut.Trim();
    }

    /// <summary>读取设备主要属性。</summary>
    public async Task<Dictionary<string, string>> GetPropsAsync(string serial)
    {
        var wanted = new[]
        {
            "ro.product.manufacturer", "ro.product.model", "ro.product.device", "ro.product.name",
            "ro.build.version.release", "ro.build.version.sdk", "ro.build.display.id",
            "ro.serialno", "persist.sys.device_name", "ro.product.cpu.abi"
        };

        var props = new Dictionary<string, string>();
        var all = await RunAsync($"-s \"{serial}\" shell getprop", timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        foreach (var line in all.StdOut.Split('\n'))
        {
            var text = line.Trim().Trim('[', ']');
            if (!text.Contains("]: [")) continue;
            var split = text.Split("]: [", 2);
            if (split.Length != 2) continue;
            var key = split[0].Trim('[', ' ', ']');
            var value = split[1].Trim('[', ' ', ']');
            if (wanted.Contains(key)) props[key] = value;
        }
        return props;
    }

    // ---------------- 无线 ----------------

    /// <summary>Android 11+：使用配对码配对（配对端口 ≠ 连接端口）。</summary>
    public async Task<(bool Success, string Message)> PairAsync(string hostPort, string pairingCode)
    {
        if (string.IsNullOrWhiteSpace(hostPort)) return (false, L("Adb_Err_PairAddrEmpty"));
        if (string.IsNullOrWhiteSpace(pairingCode)) return (false, L("Adb_Err_PairCodeEmpty"));

        var result = await RunAsync($"pair \"{hostPort.Trim()}\" \"{pairingCode.Trim()}\"",
            timeout: TimeSpan.FromSeconds(45)).ConfigureAwait(false);

        var text = result.Message;
        var ok = result.Success && text.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase);
        return (ok, text);
    }

    public async Task<(bool Success, string Message)> ConnectAsync(string hostPort)
    {
        if (string.IsNullOrWhiteSpace(hostPort)) return (false, L("Adb_Err_ConnectAddrEmpty"));
        if (!hostPort.Contains(':')) hostPort += ":5555";

        var result = await RunAsync($"connect \"{hostPort.Trim()}\"", timeout: TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        var text = result.Message;
        var ok = result.Success &&
                 (text.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("already connected", StringComparison.OrdinalIgnoreCase));
        return (ok, text);
    }

    public async Task<(bool Success, string Message)> DisconnectAsync(string? hostPort)
    {
        var argument = string.IsNullOrWhiteSpace(hostPort) ? "disconnect" : $"disconnect \"{hostPort.Trim()}\"";
        var result = await RunAsync(argument, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    /// <summary>Android 10 及以下：先 USB 连接，切到 TCP 模式。</summary>
    public async Task<(bool Success, string Message)> TcpipAsync(string serial, int port)
    {
        var result = await RunAsync($"-s \"{serial}\" tcpip {port}", timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    /// <summary>扫描局域网内开启无线调试的设备（mDNS）。</summary>
    public async Task<List<MdnsService>> ScanMdnsAsync()
    {
        var result = await RunAsync("mdns services", timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var list = new List<MdnsService>();
        foreach (var rawLine in result.StdOut.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("List of", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var name = parts[0].Trim();
            var address = parts[1].Trim();
            list.Add(new MdnsService(name, address, name.Contains("_adb-tls-pairing")));
        }
        return list;
    }

    // ---------------- 常用工具 ----------------

    public async Task<(bool Success, string Message)> ScreenshotAsync(string serial, string savePath)
    {
        var bytes = await RunBinaryAsync($"-s \"{serial}\" exec-out screencap -p", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        if (bytes.Length == 0) return (false, L("Adb_Err_ScreenshotEmpty"));

        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        await File.WriteAllBytesAsync(savePath, bytes).ConfigureAwait(false);
        return (true, savePath);
    }

    public Task<AdbResult> RebootAsync(string serial, string mode) =>
        RunAsync($"-s \"{serial}\" reboot {mode}".TrimEnd(), timeout: TimeSpan.FromSeconds(60));

    public Task<AdbResult> ShellAsync(string serial, string command, Action<string>? onLine = null, TimeSpan? timeout = null) =>
        RunAsync($"-s \"{serial}\" shell {command}", onLine, timeout ?? TimeSpan.FromSeconds(30));

    /// <summary>
    /// 从 dumpsys package 输出中查找应用的 DeviceAdminReceiver 组件（形如 包名/.XxxReceiver）。
    /// 用于 dpm set-device-owner 激活；找不到返回 null。
    /// </summary>
    public async Task<string?> FindDeviceAdminReceiverAsync(string serial, string package)
    {
        var result = await RunAsync($"-s \"{serial}\" shell dumpsys package {package}",
            null, TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        var lines = result.Combined().Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("android.app.action.DEVICE_ADMIN_ENABLED", StringComparison.Ordinal)) continue;

            for (var j = i + 1; j < Math.Min(i + 8, lines.Length); j++)
            {
                var start = lines[j].IndexOf(package + "/", StringComparison.Ordinal);
                if (start < 0) continue;

                var token = lines[j][start..].Split(' ', '\t', '\r', '\n')[0];
                if (token.Length > package.Length + 1) return token;
            }
        }

        return null;
    }

    /// <summary>启动录屏（后台持续运行，需调用 StopRecordingAsync 结束）。</summary>
    public async Task<(bool Success, string Message)> StartRecordingAsync(string serial, string remotePath, int seconds)
    {
        var process = CreateProcess($"-s \"{serial}\" shell screenrecord --time-limit {seconds} \"{remotePath}\"");
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        _recordingProcess = process;
        _recordingRemoteFile = remotePath;
        _ = Task.Run(() => process.WaitForExit());
        return (true, L("Msg_RecordStarted"));
    }

    /// <summary>结束录屏并把视频拉回本地。</summary>
    public async Task<(bool Success, string Message)> StopRecordingAsync(string serial, string localDir)
    {
        if (_recordingProcess is null || _recordingRemoteFile is null) return (false, L("Adb_Err_NoRecording"));

        TryKill(_recordingProcess);
        _recordingProcess.Dispose();
        _recordingProcess = null;

        await Task.Delay(1500).ConfigureAwait(false); // 等待设备写完 mp4 头
        var remote = _recordingRemoteFile;
        _recordingRemoteFile = null;

        Directory.CreateDirectory(localDir);
        var pull = await RunAsync($"-s \"{serial}\" pull \"{remote}\" \"{localDir}\"", null, TimeSpan.FromMinutes(5))
            .ConfigureAwait(false);
        if (!pull.Success) return (false, L("Adb_Err_RecordPull", pull.Message));

        var localFile = Path.Combine(localDir, Path.GetFileName(remote));
        _ = RunAsync($"-s \"{serial}\" shell rm \"{remote}\"", timeout: TimeSpan.FromSeconds(15));
        return (true, localFile);
    }

    public bool IsRecording => _recordingProcess is not null;
}

internal static class AdbResultExtensions
{
    public static string Combined(this AdbResult result) => result.StdOut + "\n" + result.StdErr;
}
