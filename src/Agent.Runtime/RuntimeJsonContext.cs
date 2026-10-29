using System.Text.Json.Serialization;

namespace Agent.Runtime;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DownloadStartedData))]
[JsonSerializable(typeof(DownloadCompletedData))]
[JsonSerializable(typeof(SpooledData))]
[JsonSerializable(typeof(SpoolerStatusData))]
[JsonSerializable(typeof(PrintCompletedData))]
[JsonSerializable(typeof(PrintErrorData))]
[JsonSerializable(typeof(ActivePrintRecord))]
[JsonSerializable(typeof(CommandExecutionRecord))]
internal sealed partial class RuntimeJsonContext : JsonSerializerContext;
