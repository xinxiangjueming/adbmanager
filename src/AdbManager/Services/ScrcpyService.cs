using System.Diagnostics;
using System.Text;

namespace AdbManager.Services;

public sealed class ScrcpyOptions
{
    public required string Serial { get; init; }
    public int MaxSize { get; init; }
    public int MaxFps { get; init; }
    public double BitRateMbps { get; init; }
    public bool NoAudio { get; init; }
    public bool ReadOnly { get; init; }
    public bool ScreenOff { get; init; }
    public bool StayAwake { get; init; }
}

/// <summary>
/// scrcpy 进程托管：启动/停止镜像窗口并转发退出事件。
/// 设备端 server 由 scrcpy 自动 push、自动清理，无需手动干预。
/// </summary>
public sealed class ScrcpyService
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>投屏进程退出（携带退出码）。在非 UI 线程触发，订阅方需自行切换线程。</summary>
    public event Action<int?>? Exited;

    public (bool Success, string Message) Start(ScrcpyOptions options, string adbPath)
    {
        if (IsRunning) return (false, LocalizationService.Get("Mirror_Running"));

        try
        {
            ScrcpyBinary.EnsureExtracted();
        }
        catch (Exception ex)
        {
            return (false, LocalizationService.Get("Mirror_Err_NotFound", ex.Message));
        }

        var args = new StringBuilder();
        args.Append("--serial=").Append(Quote(options.Serial));
        if (options.MaxSize > 0) args.Append(" --max-size=").Append(options.MaxSize);
        if (options.MaxFps > 0) args.Append(" --max-fps=").Append(options.MaxFps);
        if (options.BitRateMbps > 0)
            args.Append(" --video-bit-rate=").Append(options.BitRateMbps.ToString("0.##")).Append('M');
        if (options.NoAudio) args.Append(" --no-audio");
        if (options.ReadOnly) args.Append(" --read-only");
        if (options.ScreenOff) args.Append(" --turn-screen-off");
        if (options.StayAwake) args.Append(" --stay-awake");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ScrcpyBinary.ExePath,
                Arguments = args.ToString(),
                WorkingDirectory = ScrcpyBinary.TargetDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        // scrcpy 没有 --adb 选项：它按「scrcpy.exe 同目录 → PATH」查找 adb。
        // 双保险：
        // ① 把内置 adb 复制到 scrcpy 目录（scrcpy 优先使用同目录 adb），
        //    杜绝系统 PATH 上其他版本 adb（如老版本 C:\adb\adb.exe 1.0.32）劫持导致的 server 版本冲突；
        // ② 再把内置 adb 目录注入子进程 PATH 作为兜底。
        var adbDirectory = Path.GetDirectoryName(adbPath);
        try
        {
            var localAdb = Path.Combine(ScrcpyBinary.TargetDirectory, "adb.exe");
            if (!File.Exists(localAdb) || new FileInfo(localAdb).Length != new FileInfo(adbPath).Length)
                File.Copy(adbPath, localAdb, overwrite: true);

            foreach (var name in new[] { "AdbWinApi.dll", "AdbWinUsbApi.dll" })
            {
                var src = Path.Combine(adbDirectory ?? "", name);
                var dst = Path.Combine(ScrcpyBinary.TargetDirectory, name);
                if (File.Exists(src) && (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length))
                    File.Copy(src, dst, overwrite: true);
            }
        }
        catch (IOException) { /* 文件被占用时保留既有副本 */ }

        if (!string.IsNullOrEmpty(adbDirectory))
        {
            var process0 = process.StartInfo;
            var existingPath = process0.EnvironmentVariables["PATH"];
            process0.EnvironmentVariables["PATH"] = adbDirectory + Path.PathSeparator + existingPath;
        }

        process.Exited += (_, _) =>
        {
            int? code = null;
            try { if (process.HasExited) code = process.ExitCode; } catch { /* 已退出但拿不到码 */ }
            if (ReferenceEquals(_process, process)) _process = null;
            AppState.Log.Info($"scrcpy exited (code {code})");
            Exited?.Invoke(code);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            StartupLog.Write("scrcpy start failed: " + ex.Message);
            return (false, LocalizationService.Get("Mirror_Err_Start", ex.Message));
        }

        _process = process;
        PumpOutput(process);
        return (true, LocalizationService.Get("Mirror_Started"));
    }

    /// <summary>异步转发 scrcpy 的 stdout/stderr 到日志页与启动日志，便于诊断启动失败。</summary>
    private static void PumpOutput(Process process)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                foreach (var line in stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    LogLine(line);
                foreach (var line in stderr.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    LogLine(line);
            }
            catch { /* 进程被杀时读取可能失败，忽略 */ }
        });
    }

    private static void LogLine(string line)
    {
        var text = line.TrimEnd();
        if (text.Length == 0) return;
        AppState.Log.Output("[scrcpy] " + text);
        StartupLog.Write("[scrcpy] " + text);
    }

    /// <summary>停止投屏（强制结束 scrcpy 进程；设备端 server 会自行清理）。</summary>
    public void Stop()
    {
        if (_process is not { HasExited: false } process) return;
        try { process.Kill(true); } catch { /* 已退出 */ }
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;
}
