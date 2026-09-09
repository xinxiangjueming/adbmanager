using System.Collections.ObjectModel;
using AdbManager.Models;
using Microsoft.UI.Dispatching;

namespace AdbManager.Services;

/// <summary>统一的日志中心，线程安全，自动切换到 UI 线程更新集合。</summary>
public sealed class Logger
{
    private const int MaxEntries = 2000;
    private readonly ObservableCollection<LogEntry> _entries = new();
    private DispatcherQueue? _queue;

    public ObservableCollection<LogEntry> Entries => _entries;

    public void Attach(DispatcherQueue queue) => _queue = queue;

    public void Info(string message) => Add(LogLevel.Info, message);
    public void Command(string message) => Add(LogLevel.Command, message);
    public void Output(string message) => Add(LogLevel.Output, message);
    public void Error(string message) => Add(LogLevel.Error, message);
    public void Success(string message) => Add(LogLevel.Success, message);

    private void Add(LogLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        if (_queue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => AddCore(level, message));
            return;
        }

        AddCore(level, message);
    }

    private void AddCore(LogLevel level, string message)
    {
        foreach (var line in message.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(text)) continue;
            _entries.Add(new LogEntry { Level = level, Message = text });
        }

        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
    }

    public void Clear()
    {
        if (_queue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(_entries.Clear);
            return;
        }
        _entries.Clear();
    }
}
