using System.Text.Json;

namespace Agent.Hub;

public sealed record EnrollRequest(
    string PairingCode,
    string Hostname,
    string Os,
    string AgentVersion,
    string MachineId)
{
    public override string ToString() =>
        $"EnrollRequest {{ Hostname = {Hostname}, Os = {Os}, AgentVersion = {AgentVersion} }}";
}

public sealed record EnrollResponse(
    Guid AgentId,
    string DeviceSecret,
    Guid TenantId,
    Guid SiteId,
    AgentConfig Config)
{
    public override string ToString() =>
        $"EnrollResponse {{ AgentId = {AgentId}, TenantId = {TenantId}, SiteId = {SiteId} }}";
}

public sealed record PrinterReportDto(
    string Name,
    string? Driver,
    string? Port,
    bool IsDefault,
    string Status,
    long RawStatus,
    int QueueLength);

public sealed record HeartbeatRequest(
    string AgentVersion,
    long UptimeS,
    DateTimeOffset AgentTime,
    long ConfigVersion,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<PrinterReportDto> Printers);

public sealed record HeartbeatResponse(
    DateTimeOffset ServerTime,
    long ConfigVersion,
    AgentConfig? Config,
    IReadOnlyList<AgentCommand>? Commands);

public sealed class AgentConfig
{
    public int ClaimIntervalS { get; init; } = 5;

    public int HeartbeatIntervalS { get; init; } = 30;

    public string LogLevel { get; init; } = "info";

    public IReadOnlyList<PrinterBinding> Printers { get; init; } = [];
}

public sealed record PrinterBinding(Guid PrinterId, string WindowsName, PrintSettings? Print);

public sealed class PrintSettings
{
    public string Engine { get; init; } = "pdfium";

    public int Copies { get; init; } = 1;

    public bool Duplex { get; init; }

    public string? Tray { get; init; }

    public string Scale { get; init; } = "noscale";
}

public sealed record AgentCommand(Guid Id, string Type, JsonElement? Args);

public sealed record CommandResultAttachment(Stream Content, string FileName, string MediaType);

public sealed record ClaimJobsRequest(IReadOnlyList<Guid> PrinterIds);

public sealed record ClaimJobsResponse(IReadOnlyList<ClaimedJob> Jobs);

public sealed record ClaimedJob(
    Guid JobId,
    Guid AttemptId,
    Guid PrinterId,
    DateTimeOffset LeaseUntil,
    ClaimedDocument Document,
    PrintSettings Print);

public sealed record ClaimedDocument(
    Uri Url,
    string Sha256,
    int Pages,
    long SizeBytes);

public sealed record JobEvent(
    Guid EventId,
    Guid AttemptId,
    string Type,
    DateTimeOffset OccurredAt,
    JsonElement? Data);

public sealed record JobEventsRequest(IReadOnlyList<JobEvent> Events);

public sealed record JobEventsResponse(
    IReadOnlyList<Guid> Accepted,
    IReadOnlyList<Guid> Duplicates,
    string JobState,
    DateTimeOffset? LeaseUntil,
    bool Abort);

internal sealed record ErrorEnvelope(ErrorDetail Error);

internal sealed record ErrorDetail(string Code, string Message);
