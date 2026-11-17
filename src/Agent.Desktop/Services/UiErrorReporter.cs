using System.IO;
using System.Text;
using System.Text.Json;
using Agent.Local;

namespace Agent.Desktop.Services;

internal sealed record UiErrorLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    int EventId,
    string Message,
    string Exception);

internal sealed class UiErrorReporter
{
    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Report(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            AgentStatePaths.EnsureSecureStateDirectory();
            Directory.CreateDirectory(AgentStatePaths.LogDirectory);
            var now = IndonesiaTime.Now(TimeProvider.System);
            var entry = new UiErrorLogEntry(
                now,
                "Error",
                "Agent.Desktop",
                0,
                "Agent Control encountered an unexpected error.",
                exception.ToString());
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            var path = Path.Combine(AgentStatePaths.LogDirectory, $"agent-control-{now:yyyyMMdd}.jsonl");
            lock (WriteGate)
            {
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception writeException)
        {
            // Kegagalan menulis diagnostics tidak boleh menutupi error UI yang asli.
            System.Diagnostics.Trace.TraceError(writeException.ToString());
        }
    }
}
