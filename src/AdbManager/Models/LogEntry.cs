using AdbManager.Services;

namespace AdbManager.Models;

public enum LogLevel
{
    Info,
    Command,
    Output,
    Error,
    Success
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = "";

    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level switch
    {
        LogLevel.Command => LocalizationService.Get("Log_LevelCommand"),
        LogLevel.Output => LocalizationService.Get("Log_LevelOutput"),
        LogLevel.Error => LocalizationService.Get("Log_LevelError"),
        LogLevel.Success => LocalizationService.Get("Log_LevelSuccess"),
        _ => LocalizationService.Get("Log_LevelInfo")
    };

    public override string ToString() => $"{TimeText}  [{LevelText}]  {Message}";
}
