using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ZKeyAudioLink.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// Structured JSON-lines log. Deliberately never receives key names or typed text:
/// callers can only log fixed application events.
/// </summary>
public sealed class AppLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly ConcurrentQueue<string> _recent = new();
    private StreamWriter? _writer;
    private DateTime _writerDate;
    private long _bytesWrittenToday;
    private bool _disposed;

    private const int MaxRecentLines = 200;
    private const long MaxFileBytes = 8 * 1024 * 1024;

    public AppLog(string? directory = null)
    {
        _directory = directory ?? AppPaths.LogDirectory;
        Directory.CreateDirectory(_directory);
        OpenWriter(DateTime.Now);
    }

    public event Action<string>? LineWritten;

    public IReadOnlyList<string> RecentLines => _recent.ToArray();

    public void Debug(string message, object? data = null) => Write(LogLevel.Debug, message, data);
    public void Info(string message, object? data = null) => Write(LogLevel.Info, message, data);
    public void Warning(string message, object? data = null) => Write(LogLevel.Warning, message, data);
    public void Error(string message, object? data = null) => Write(LogLevel.Error, message, data);

    public void Error(string message, Exception exception) =>
        Write(LogLevel.Error, message, new { error = exception.GetType().Name, detail = exception.Message });

    private void Write(LogLevel level, string message, object? data)
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTime.Now;
        var line = BuildLine(now, level, message, data);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (now.Date != _writerDate)
            {
                OpenWriter(now);
            }

            _writer?.WriteLine(line);
            _writer?.Flush();
            _bytesWrittenToday += line.Length + 1;
            if (_bytesWrittenToday > MaxFileBytes)
            {
                OpenWriter(now, rotate: true);
            }
        }

        _recent.Enqueue(line);
        while (_recent.Count > MaxRecentLines)
        {
            _recent.TryDequeue(out _);
        }

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // Logging must never take down the caller.
        }
    }

    private static string BuildLine(DateTime now, LogLevel level, string message, object? data)
    {
        var sb = new StringBuilder(160);
        sb.Append('{');
        Append(sb, "t", now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(',');
        Append(sb, "level", level.ToString().ToLowerInvariant());
        sb.Append(',');
        Append(sb, "msg", message);
        if (data is not null)
        {
            sb.Append(',');
            sb.Append("\"data\":");
            try
            {
                sb.Append(JsonSerializer.Serialize(data));
            }
            catch
            {
                sb.Append("\"<unserializable>\"");
            }
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string name, string value)
    {
        sb.Append('"').Append(name).Append("\":");
        sb.Append(JsonSerializer.Serialize(value));
    }

    private void OpenWriter(DateTime now, bool rotate = false)
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // ignore
        }

        _writerDate = now.Date;
        var name = rotate
            ? $"app-{now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl"
            : $"app-{now:yyyyMMdd}.jsonl";
        var path = Path.Combine(_directory, name);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
        _bytesWrittenToday = 0;
        try
        {
            _bytesWrittenToday = new FileInfo(path).Length;
        }
        catch
        {
            _bytesWrittenToday = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
