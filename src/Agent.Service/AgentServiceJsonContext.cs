using System.Text.Json.Serialization;

namespace Agent.Service;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StoredDeviceIdentity))]
[JsonSerializable(typeof(AgentLogEntry))]
internal sealed partial class AgentServiceJsonContext : JsonSerializerContext;
