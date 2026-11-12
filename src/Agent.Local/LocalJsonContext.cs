using System.Text.Json.Serialization;

namespace Agent.Local;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StoredDeviceIdentity))]
[JsonSerializable(typeof(AgentRuntimeSettings))]
[JsonSerializable(typeof(AgentUpdateState))]
[JsonSerializable(typeof(AgentUpdateInstallRequest))]
[JsonSerializable(typeof(LocalPrinterSnapshot))]
[JsonSerializable(typeof(LocalActivePrintJobSnapshot))]
[JsonSerializable(typeof(AgentHealthSnapshot))]
internal sealed partial class LocalJsonContext : JsonSerializerContext;
