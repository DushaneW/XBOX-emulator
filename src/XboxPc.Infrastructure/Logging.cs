using System.Collections.Concurrent;
using System.Text.Json;
using XboxPc.Application;

namespace XboxPc.Infrastructure;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XboxPc");
    public static string DatabaseFile => Path.Combine(DataDirectory, "xboxpc.db");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}

/// <summary>
/// Structured JSON-lines log with redaction, daily files, size cap, and an in-memory ring buffer for the UI.
/// Logging never throws into callers.
/// </summary>
public sealed class JsonLinesLog : IAppLog, IDisposable
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int RingCapacity = 500;

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<AppLogEntry> _recent = new();
    private readonly TimeProvider _time;
    private StreamWriter? _writer;
    private string? _currentFile;

    public JsonLinesLog(string directory, TimeProvider? time = null)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<AppLogEntry>? EntryWritten;

    public IReadOnlyList<AppLogEntry> Recent => _recent.ToArray();

    public string? CurrentFile => _currentFile;

    public void Write(AppLogLevel level, string category, string message, Exception? exception = null)
    {
        var entry = new AppLogEntry(_time.GetUtcNow(), level, category, LogRedactor.Redact(message),
            exception is null ? null : LogRedactor.Redact($"{exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}"));
        _recent.Enqueue(entry);
        while (_recent.Count > RingCapacity && _recent.TryDequeue(out _)) { }

        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = entry.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                level = entry.Level.ToString(),
                category = entry.Category,
                message = entry.Message,
                exception = entry.Exception,
            });
            lock (_gate)
            {
                EnsureWriter(entry.Timestamp);
                _writer!.WriteLine(line);
                _writer.Flush();
            }
        }
        catch (IOException) { /* Disk full / locked: keep the in-memory copy, never crash the app. */ }
        catch (UnauthorizedAccessException) { }

        EntryWritten?.Invoke(this, entry);
    }

    private void EnsureWriter(DateTimeOffset now)
    {
        var baseName = Path.Combine(_directory, $"xboxpc-{now:yyyyMMdd}");
        if (_writer is not null && _currentFile is not null && _currentFile.StartsWith(baseName, StringComparison.Ordinal) && new FileInfo(_currentFile).Length < MaxFileBytes)
            return;

        _writer?.Dispose();
        Directory.CreateDirectory(_directory);
        var index = 0;
        string file;
        do { file = index == 0 ? baseName + ".jsonl" : $"{baseName}-{index}.jsonl"; index++; }
        while (File.Exists(file) && new FileInfo(file).Length >= MaxFileBytes);
        _writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        _currentFile = file;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
