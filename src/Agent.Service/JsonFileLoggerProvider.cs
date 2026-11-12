using System.Text;
using System.Text.Json;
using Agent.Local;
using Microsoft.Extensions.Logging;

namespace Agent.Service;

internal sealed record AgentLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    int EventId,
    string Message,
    string? Exception);

/// <summary>Menulis JSON Lines supaya log bisa diarsipkan tanpa parser khusus di Hub.</summary>
internal sealed class JsonFileLoggerProvider(string directory, TimeProvider timeProvider) : ILoggerProvider
{
    private readonly object writeGate = new();
    private bool directoryReady;

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (level == LogLevel.None)
        {
            return;
        }

        var now = IndonesiaTime.Now(timeProvider);
        var entry = new AgentLogEntry(
            now,
            level.ToString(),
            category,
            eventId.Id,
            formatter(state, exception),
            exception?.ToString());
        var json = JsonSerializer.Serialize(entry, AgentServiceJsonContext.Default.AgentLogEntry);

        lock (writeGate)
        {
            try
            {
                if (!directoryReady)
                {
                    // Log tetap berada di bawah ACL state yang sama dengan identity DPAPI dan outbox.
                    AgentStatePaths.EnsureSecureStateDirectory();
                    Directory.CreateDirectory(directory);
                    directoryReady = true;
                }

                var path = Path.Combine(directory, $"agent-{now:yyyyMMdd}.jsonl");
                using var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(0, SeekOrigin.End);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(json);
            }
            catch (IOException)
            {
                // Kegagalan sink log tidak boleh mematikan service atau memicu logging rekursif.
            }
            catch (UnauthorizedAccessException)
            {
                // Event Log/console masih tersedia bila ACL lokal tidak dapat disiapkan.
            }
        }
    }

    private sealed class JsonFileLogger(JsonFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            provider.Write(category, logLevel, eventId, state, exception, formatter);
    }
}
