namespace Agent.Runtime;

public sealed record JobProcessorOptions(
    string DocumentsDirectory,
    TimeSpan CompletionVerificationWindow,
    TimeSpan SpoolerPollInterval);

public enum JobProcessOutcome
{
    Completed,
    PrintedUnverified,
    Failed,
    Aborted,
}

public sealed record JobProcessResult(
    JobProcessOutcome Outcome,
    uint? SpoolerJobId,
    string? ErrorCode,
    string? Detail);

public sealed record ActivePrintRecord(
    Guid JobId,
    string WindowsPrinterName,
    uint SpoolerJobId,
    int PageCount,
    DateTimeOffset SubmittedAt);

internal sealed record DownloadStartedData(long SizeBytes, int Pages);

internal sealed record DownloadCompletedData(long SizeBytes, string Sha256, long DurationMs);

internal sealed record SpooledData(uint SpoolerJobId, int Pages);

internal sealed record SpoolerStatusData(
    uint SpoolerJobId,
    bool JobExists,
    uint RawStatus,
    uint PrinterRawStatus,
    uint PagesPrinted,
    uint TotalPages,
    string? StatusText);

internal sealed record PrintCompletedData(uint SpoolerJobId, uint PagesPrinted);

internal sealed record PrintErrorData(string ErrorCode, string Message);
