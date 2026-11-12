using System.Text.Json.Serialization;

namespace Agent.Hub;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(AgentUpdateHeartbeat))]
[JsonSerializable(typeof(HeartbeatResponse))]
[JsonSerializable(typeof(ClaimJobsRequest))]
[JsonSerializable(typeof(ClaimJobsResponse))]
[JsonSerializable(typeof(JobEvent))]
[JsonSerializable(typeof(JobEventsRequest))]
[JsonSerializable(typeof(JobEventsResponse))]
[JsonSerializable(typeof(ErrorEnvelope))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
