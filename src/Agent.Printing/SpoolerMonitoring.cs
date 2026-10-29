namespace Agent.Printing;

public sealed record SpoolerObservation(
    bool JobExists,
    uint JobStatus,
    uint PrinterStatus,
    uint PrinterAttributes,
    uint PagesPrinted,
    uint TotalPages,
    string? StatusText = null);

public interface ISpoolerStatusSource
{
    ValueTask<SpoolerObservation> ReadAsync(
        string printerName,
        uint spoolerJobId,
        CancellationToken cancellationToken = default);
}

public enum SpoolerMonitorOutcome
{
    Completed,
    PrintedUnverified,
    Failed,
    NotObserved,
}

public sealed record SpoolerMonitorResult(
    SpoolerMonitorOutcome Outcome,
    uint PagesPrinted,
    uint LastJobStatus,
    uint LastPrinterStatus,
    string? Detail);

public sealed class SpoolerCompletionTracker
{
    private const uint JobStatusError = 0x00000002;
    private const uint JobStatusDeleting = 0x00000004;
    private const uint JobStatusDeleted = 0x00000100;

    private const uint PrinterStatusError = 0x00000002;
    private const uint PrinterStatusPendingDeletion = 0x00000004;
    private const uint PrinterStatusPaperJam = 0x00000008;
    private const uint PrinterStatusPaperOut = 0x00000010;
    private const uint PrinterStatusPaperProblem = 0x00000040;
    private const uint PrinterStatusOffline = 0x00000080;
    private const uint PrinterStatusOutputBinFull = 0x00000800;
    private const uint PrinterStatusNotAvailable = 0x00001000;
    private const uint PrinterStatusNoToner = 0x00040000;
    private const uint PrinterStatusPagePunt = 0x00080000;
    private const uint PrinterStatusUserIntervention = 0x00100000;
    private const uint PrinterStatusOutOfMemory = 0x00200000;
    private const uint PrinterStatusDoorOpen = 0x00400000;
    private const uint PrinterStatusServerUnknown = 0x00800000;
    private const uint PrinterStatusServerOffline = 0x02000000;
    private const uint PrinterAttributeWorkOffline = 0x00000400;

    private const uint FatalJobMask = JobStatusError | JobStatusDeleting | JobStatusDeleted;
    private const uint PrinterErrorMask = PrinterStatusError | PrinterStatusPendingDeletion
        | PrinterStatusPaperJam | PrinterStatusPaperOut | PrinterStatusPaperProblem
        | PrinterStatusOffline | PrinterStatusOutputBinFull | PrinterStatusNotAvailable
        | PrinterStatusNoToner | PrinterStatusPagePunt | PrinterStatusUserIntervention
        | PrinterStatusOutOfMemory | PrinterStatusDoorOpen | PrinterStatusServerUnknown
        | PrinterStatusServerOffline;

    private bool jobWasObserved;
    private bool fatalJobStatusWasObserved;
    private DateTimeOffset? disappearedAt;
    private SpoolerObservation? lastJobObservation;
    private SpoolerObservation? lastObservation;
    private readonly TimeSpan verificationWindow;

    public SpoolerCompletionTracker(TimeSpan verificationWindow, bool jobWasPreviouslyObserved = false)
    {
        if (verificationWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(verificationWindow));
        }

        this.verificationWindow = verificationWindow;
        jobWasObserved = jobWasPreviouslyObserved;
    }

    public SpoolerMonitorResult? Observe(SpoolerObservation observation, DateTimeOffset observedAt)
    {
        lastObservation = observation;
        if (observation.JobExists)
        {
            jobWasObserved = true;
            fatalJobStatusWasObserved |= (observation.JobStatus & FatalJobMask) != 0;
            lastJobObservation = observation;
            disappearedAt = null;
            return null;
        }

        if (!jobWasObserved)
        {
            return Result(
                SpoolerMonitorOutcome.NotObserved,
                "JobId tidak pernah terlihat di spooler; completion tidak boleh diasumsikan.");
        }

        if (fatalJobStatusWasObserved)
        {
            return Result(
                SpoolerMonitorOutcome.Failed,
                "Job hilang setelah spooler pernah melaporkan ERROR/DELETING/DELETED.");
        }

        disappearedAt ??= observedAt;
        if (HasPrinterError(observation))
        {
            return Result(
                SpoolerMonitorOutcome.PrintedUnverified,
                "Printer melaporkan error dalam jendela verifikasi setelah job hilang.");
        }

        if (observedAt - disappearedAt.Value >= verificationWindow)
        {
            return Result(
                SpoolerMonitorOutcome.Completed,
                "Job hilang tanpa error dan printer sehat selama jendela verifikasi.");
        }

        return null;
    }

    private static bool HasPrinterError(SpoolerObservation observation) =>
        (observation.PrinterStatus & PrinterErrorMask) != 0
        || (observation.PrinterAttributes & PrinterAttributeWorkOffline) != 0;

    private SpoolerMonitorResult Result(SpoolerMonitorOutcome outcome, string detail)
    {
        var observation = lastObservation
            ?? throw new InvalidOperationException("Belum ada observasi spooler.");
        var jobObservation = lastJobObservation ?? observation;
        return new SpoolerMonitorResult(
            outcome,
            jobObservation.PagesPrinted,
            jobObservation.JobStatus,
            observation.PrinterStatus,
            detail);
    }
}

public sealed class SpoolerJobMonitor(
    ISpoolerStatusSource statusSource,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<SpoolerMonitorResult> MonitorAsync(
        string printerName,
        uint spoolerJobId,
        Func<SpoolerObservation, CancellationToken, ValueTask>? observationChanged = null,
        TimeSpan? pollInterval = null,
        TimeSpan? verificationWindow = null,
        TimeSpan? observationRefreshInterval = null,
        bool jobWasPreviouslyObserved = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        if (spoolerJobId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spoolerJobId));
        }

        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        var window = verificationWindow ?? TimeSpan.FromSeconds(60);
        var refreshInterval = observationRefreshInterval ?? TimeSpan.FromSeconds(30);
        if (refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(observationRefreshInterval));
        }

        var tracker = new SpoolerCompletionTracker(window, jobWasPreviouslyObserved);
        SpoolerObservation? previous = null;
        DateTimeOffset? lastPublishedAt = null;

        while (true)
        {
            var current = await statusSource.ReadAsync(printerName, spoolerJobId, cancellationToken);
            var now = clock.GetUtcNow();
            var shouldPublish = current != previous
                || lastPublishedAt is null
                || now - lastPublishedAt.Value >= refreshInterval;
            if (shouldPublish && observationChanged is not null)
            {
                await observationChanged(current, cancellationToken);
                lastPublishedAt = now;
            }

            previous = current;
            var result = tracker.Observe(current, now);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(interval, clock, cancellationToken);
        }
    }
}
