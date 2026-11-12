using Agent.Hub;
using Agent.Local;

namespace Agent.Service;

public sealed class AgentHealthPublisher(
    AgentHealthStore store,
    AgentExecutionOptions executionOptions,
    TimeProvider timeProvider,
    ILogger<AgentHealthPublisher> logger)
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private AgentHealthSnapshot snapshot = new(
        AgentProduct.Version,
        Environment.ProcessId,
        timeProvider.GetUtcNow(),
        timeProvider.GetUtcNow(),
        AgentConnectionStates.Starting,
        executionOptions.SimulationMode);

    public Task ReportStartingAsync(CancellationToken cancellationToken) => UpdateAsync(
        current => current with
        {
            UpdatedAt = timeProvider.GetUtcNow(),
            ConnectionState = AgentConnectionStates.Starting,
            LastErrorCode = null,
            LastErrorMessage = null,
            ActivePrintJobs = [],
        },
        cancellationToken);

    public Task ReportAwaitingPairingAsync(CancellationToken cancellationToken) => UpdateAsync(
        current => current with
        {
            UpdatedAt = timeProvider.GetUtcNow(),
            ConnectionState = AgentConnectionStates.AwaitingPairing,
            LastErrorCode = null,
            LastErrorMessage = null,
        },
        cancellationToken);

    public Task ReportConnectingAsync(CancellationToken cancellationToken) => UpdateAsync(
        current => current with
        {
            UpdatedAt = timeProvider.GetUtcNow(),
            ConnectionState = AgentConnectionStates.Connecting,
            LastErrorCode = null,
            LastErrorMessage = null,
        },
        cancellationToken);

    public Task ReportHeartbeatAttemptAsync(CancellationToken cancellationToken) => UpdateAsync(
        current => current with
        {
            UpdatedAt = timeProvider.GetUtcNow(),
            LastHeartbeatAttemptAt = timeProvider.GetUtcNow(),
        },
        cancellationToken);

    public Task ReportOnlineAsync(
        long configVersion,
        int claimIntervalSeconds,
        int? sourcePollIntervalSeconds,
        IReadOnlyList<PrinterReportDto> printers,
        CancellationToken cancellationToken) => UpdateAsync(
            current => current with
            {
                UpdatedAt = timeProvider.GetUtcNow(),
                ConnectionState = AgentConnectionStates.Online,
                LastHeartbeatSuccessAt = timeProvider.GetUtcNow(),
                LastErrorCode = null,
                LastErrorMessage = null,
                ConfigVersion = configVersion,
                PrinterCount = printers.Count,
                ClaimIntervalSeconds = claimIntervalSeconds,
                SourcePollIntervalSeconds = sourcePollIntervalSeconds,
                // Snapshot ini berasal dari payload heartbeat yang baru diterima Hub agar
                // daftar pada GUI dapat dibandingkan langsung tanpa inventaris kedua.
                Printers = printers
                    .OrderByDescending(printer => printer.IsDefault)
                    .ThenBy(printer => printer.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(printer => new LocalPrinterSnapshot(
                        printer.Name,
                        printer.Driver,
                        printer.Port,
                        printer.IsDefault,
                        printer.Status,
                        printer.QueueLength))
                    .ToArray(),
            },
            cancellationToken);

    public Task ReportJobClaimedAsync(
        Guid jobId,
        string windowsPrinterName,
        int pages,
        string? invoiceNumber,
        string? itemType,
        CancellationToken cancellationToken) => UpdateAsync(
        current =>
        {
            var now = timeProvider.GetUtcNow();
            var jobs = (current.ActivePrintJobs ?? [])
                .Where(job => job.JobId != jobId)
                .Append(new LocalActivePrintJobSnapshot(
                    jobId,
                    invoiceNumber,
                    itemType,
                    windowsPrinterName,
                    LocalPrintJobStages.Claimed,
                    now,
                    now,
                    pages))
                .OrderBy(job => job.StartedAt)
                .ToArray();
            return current with
            {
                UpdatedAt = now,
                ActivePrintJobs = jobs,
            };
        },
        cancellationToken);

    public Task ReportJobProgressAsync(
        Guid jobId,
        string stage,
        long? spoolerJobId,
        CancellationToken cancellationToken) => UpdateAsync(
        current =>
        {
            var jobs = current.ActivePrintJobs ?? [];
            var existing = jobs.FirstOrDefault(job => job.JobId == jobId);
            if (existing is null
                || (existing.Stage == stage && existing.SpoolerJobId == spoolerJobId))
            {
                return current;
            }

            var now = timeProvider.GetUtcNow();
            return current with
            {
                UpdatedAt = now,
                ActivePrintJobs = jobs
                    .Select(job => job.JobId == jobId
                        ? job with
                        {
                            Stage = stage,
                            UpdatedAt = now,
                            SpoolerJobId = spoolerJobId ?? job.SpoolerJobId,
                        }
                        : job)
                    .ToArray(),
            };
        },
        cancellationToken);

    public Task ReportJobFinishedAsync(Guid jobId) => UpdateAsync(
        current =>
        {
            var jobs = current.ActivePrintJobs ?? [];
            if (!jobs.Any(job => job.JobId == jobId))
            {
                return current;
            }

            return current with
            {
                UpdatedAt = timeProvider.GetUtcNow(),
                ActivePrintJobs = jobs.Where(job => job.JobId != jobId).ToArray(),
            };
        },
        CancellationToken.None);

    public Task ReportFailureAsync(
        string connectionState,
        string? errorCode,
        string message,
        CancellationToken cancellationToken) => UpdateAsync(
            current => current with
            {
                UpdatedAt = timeProvider.GetUtcNow(),
                ConnectionState = connectionState,
                LastErrorCode = errorCode,
                LastErrorMessage = Truncate(message),
            },
            cancellationToken);

    public Task ReportStoppingAsync() => UpdateAsync(
        current => current with
        {
            UpdatedAt = timeProvider.GetUtcNow(),
            ConnectionState = AgentConnectionStates.Stopping,
        },
        CancellationToken.None);

    private async Task UpdateAsync(
        Func<AgentHealthSnapshot, AgentHealthSnapshot> update,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var updated = update(snapshot);
            if (ReferenceEquals(updated, snapshot))
            {
                return;
            }

            snapshot = updated;
            await store.SaveAsync(snapshot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Status lokal membantu diagnosis tetapi tidak boleh menghentikan pipeline cetak.
            logger.LogWarning(ex, "The local health status could not be saved");
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500];
}
