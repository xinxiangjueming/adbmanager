using System.IO;

namespace AdbManager.Services;

/// <summary>启动过程里程碑日志（磁盘文件），用于诊断无控制台时的崩溃点。</summary>
public static class StartupLog
{
    private static readonly object Gate = new();
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbManager", "startup.log");

    public static void Write(string step)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (directory is not null) Directory.CreateDirectory(directory);
                File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {step}\n");
            }
        }
        catch
        {
            // 诊断日志失败时静默
        }
    }
}
