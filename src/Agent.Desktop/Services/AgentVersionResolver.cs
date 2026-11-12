namespace Agent.Desktop.Services;

internal static class AgentVersionResolver
{
    public static string Resolve(string controlVersion, string? serviceVersion) =>
        string.IsNullOrWhiteSpace(serviceVersion) ? controlVersion : serviceVersion;
}
