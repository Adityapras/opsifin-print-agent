using System.Text.Json;

namespace Agent.Local;

public static class AgentConnectionStates
{
    public const string Starting = "starting";
    public const string AwaitingPairing = "awaiting_pairing";
    public const string Connecting = "connecting";
    public const string Online = "online";
    public const string Degraded = "degraded";
    public const string AuthenticationFailed = "authentication_failed";
    public const string UpdateRequired = "update_required";
    public const string Stopping = "stopping";
}

public sealed record LocalPrinterSnapshot(
    string Name,
    string? Driver,
    string? Port,
    bool IsDefault,
    string Status,
    int QueueLength);

public static class LocalPrintJobStages
{
    public const string Claimed = "claimed";
    public const string Downloading = "downloading";
    public const string SendingToPrinter = "sending_to_printer";
    public const string Printing = "printing";
    public const string VerifyingCompletion = "verifying_completion";
}

public sealed record LocalActivePrintJobSnapshot(
    Guid JobId,
    string? InvoiceNumber,
    string? ItemType,
    string WindowsPrinterName,
    string Stage,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    int Pages,
    long? SpoolerJobId = null);

public sealed record AgentHealthSnapshot(
    string AgentVersion,
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string ConnectionState,
    bool SimulationMode,
    DateTimeOffset? LastHeartbeatAttemptAt = null,
    DateTimeOffset? LastHeartbeatSuccessAt = null,
    string? LastErrorCode = null,
    string? LastErrorMessage = null,
    long ConfigVersion = 0,
    int PrinterCount = 0,
    LocalPrinterSnapshot[]? Printers = null,
    int ClaimIntervalSeconds = 5,
    int? SourcePollIntervalSeconds = null,
    LocalActivePrintJobSnapshot[]? ActivePrintJobs = null);

public sealed class AgentHealthStore
{
    public AgentHealthStore()
        : this(AgentStatePaths.HealthFile)
    {
    }

    public AgentHealthStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public void Remove()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }

    public async Task SaveAsync(AgentHealthSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("The health file path does not have a directory.");
        if (string.Equals(directory, AgentStatePaths.StateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AgentStatePaths.EnsureSecureStateDirectory();
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{Path}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    LocalJsonContext.Default.AgentHealthSnapshot,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, Path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<AgentHealthSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        await using var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
            stream,
            LocalJsonContext.Default.AgentHealthSnapshot,
            cancellationToken)
            ?? throw new InvalidDataException("The agent health file is empty.");
    }
}
