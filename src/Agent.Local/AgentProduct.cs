namespace Agent.Local;

public static class AgentProduct
{
    // Versi diambil dari metadata build supaya service, desktop, MSI, dan heartbeat tidak
    // menyimpang ketika nomor rilis dinaikkan di Directory.Build.props.
    public static string Version { get; } = ReadAssemblyVersion();

    public const string ServiceName = "OpsifinPrintAgent";

    public const string ServiceDisplayName = "Opsifin Print Agent";

    private static string ReadAssemblyVersion()
    {
        var version = typeof(AgentProduct).Assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
