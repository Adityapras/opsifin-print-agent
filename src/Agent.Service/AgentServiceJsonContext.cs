using System.Text.Json.Serialization;

namespace Agent.Service;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AgentLogEntry))]
internal sealed partial class AgentServiceJsonContext : JsonSerializerContext;
