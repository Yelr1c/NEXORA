using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Logging;

/// <summary>Removes personal information (user names, profile paths, e-mail addresses) from log text.</summary>
public static partial class LogSanitizer
{
    private static readonly string? UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string UserName = Environment.UserName;

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        if (!string.IsNullOrEmpty(UserProfile) && UserProfile.Length > 3)
        {
            result = result.Replace(UserProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        result = UsersPathRegex().Replace(result, @"$1<user>");
        result = EmailRegex().Replace(result, "<email>");
        if (!string.IsNullOrEmpty(UserName) && UserName.Length >= 3)
        {
            result = Regex.Replace(result, @"\b" + Regex.Escape(UserName) + @"\b", "<user>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }

        return result;
    }

    [GeneratedRegex(@"([A-Za-z]:\\Users\\)[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex UsersPathRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();
}

/// <summary>
/// Structured JSON-lines file logger with daily files, size cap and retention. Writes on a background
/// thread so logging never blocks the UI.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly Thread _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, int retentionDays = 14)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        CleanupOldLogs(retentionDays);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "NEXORA log writer", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal bool IsEnabled(LogLevel level) => level >= _minimumLevel && level != LogLevel.None;

    internal void Enqueue(string line)
    {
        if (Volatile.Read(ref _disposed) == 0 && !_queue.IsAddingCompleted)
        {
            try
            {
                _queue.TryAdd(line);
            }
            catch (InvalidOperationException)
            {
                // Logging after shutdown is silently ignored.
            }
        }
    }

    private void WriteLoop()
    {
        StreamWriter? writer = null;
        string? currentPath = null;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var path = Path.Combine(_directory, $"nexora-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
                    if (path != currentPath || writer is null || writer.BaseStream.Length > MaxFileBytes)
                    {
                        writer?.Dispose();
                        if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                        {
                            path = Path.Combine(_directory, $"nexora-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
                        }

                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = false };
                        currentPath = path;
                    }

                    writer.WriteLine(line);
                    if (_queue.Count == 0)
                    {
                        writer.Flush();
                    }
                }
                catch (IOException)
                {
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private void CleanupOldLogs(int retentionDays)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "nexora-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-retentionDays))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention is best effort.
        }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("ts", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                json.WriteString("level", logLevel.ToString());
                json.WriteString("category", category);
                json.WriteString("message", LogSanitizer.Sanitize(formatter(state, exception)));
                if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                {
                    json.WriteStartObject("props");
                    foreach (var (key, value) in properties)
                    {
                        if (key == "{OriginalFormat}")
                        {
                            continue;
                        }

                        json.WriteString(key, LogSanitizer.Sanitize(Convert.ToString(value, CultureInfo.InvariantCulture)));
                    }

                    json.WriteEndObject();
                }

                if (exception is not null)
                {
                    json.WriteString("exception", exception.GetType().FullName);
                    json.WriteString("error", LogSanitizer.Sanitize(exception.Message));
                    json.WriteString("stack", LogSanitizer.Sanitize(exception.StackTrace));
                }

                json.WriteEndObject();
            }

            provider.Enqueue(Encoding.UTF8.GetString(buffer.ToArray()));
        }
    }
}
