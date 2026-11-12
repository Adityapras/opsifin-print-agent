using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using Agent.Local;

namespace Agent.Desktop.Services;

internal static class SystemCheckKinds
{
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Danger = "danger";
    public const string Neutral = "neutral";
}

internal sealed record SystemCheckItem(
    string Key,
    string Title,
    string Kind,
    string Status,
    string Detail,
    string Recommendation);

internal sealed record SystemCheckReport(
    DateTimeOffset GeneratedAt,
    string MachineName,
    string OsDescription,
    string Architecture,
    string RuntimeDescription,
    IReadOnlyList<SystemCheckItem> Items)
{
    public int SuccessCount => Items.Count(item => item.Kind == SystemCheckKinds.Success);

    public int WarningCount => Items.Count(item => item.Kind == SystemCheckKinds.Warning);

    public int DangerCount => Items.Count(item => item.Kind == SystemCheckKinds.Danger);
}

internal sealed class SystemCheckService
{
    private const int NetFramework48MinimumRelease = 528040;

    public async Task<SystemCheckReport> RunAsync(
        AgentWindowsServiceStatus agentService,
        DeviceIdentity? identity,
        AgentHealthSnapshot? health,
        Uri? fallbackHubAddress,
        CancellationToken cancellationToken)
    {
        var items = new List<SystemCheckItem>();
        AddOperatingSystemCheck(items);
        AddArchitectureCheck(items);
        AddNetFrameworkCheck(items);
        AddAgentServiceCheck(items, agentService);
        AddSpoolerCheck(items);
        AddPairingCheck(items, identity);
        AddHeartbeatCheck(items, health);
        AddPrinterCheck(items, health);
        AddNativeDependencyCheck(items, agentService.Installed);
        AddStateDirectoryCheck(items);
        await AddHubChecksAsync(
            items,
            identity?.HubBaseAddress ?? fallbackHubAddress,
            cancellationToken);

        return new SystemCheckReport(
            IndonesiaTime.Now(TimeProvider.System),
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            items);
    }

    private static void AddOperatingSystemCheck(List<SystemCheckItem> items)
    {
        var version = Environment.OSVersion.Version;
        var servicePack = Environment.OSVersion.ServicePack;
        if (version.Major >= 10)
        {
            items.Add(Pass(
                "operating-system",
                "Operating system",
                $"Detected {RuntimeInformation.OSDescription}.",
                "The modern agent build can run on this version of Windows."));
            return;
        }

        if (version.Major == 6 && version.Minor == 1)
        {
            var isServicePack1 = servicePack.Contains("Service Pack 1", StringComparison.OrdinalIgnoreCase);
            items.Add(new SystemCheckItem(
                "operating-system",
                "Operating system",
                isServicePack1 ? SystemCheckKinds.Warning : SystemCheckKinds.Danger,
                isServicePack1 ? "LEGACY AGENT REQUIRED" : "SERVICE PACK REQUIRED",
                $"Detected Windows 7 {servicePack.DefaultIfEmpty("without a Service Pack")}.",
                isServicePack1
                    ? "Install the Windows 7 SP1 legacy package; the modern .NET 10 MSI is not compatible."
                    : "Install Windows 7 Service Pack 1 before installing the legacy agent."));
            return;
        }

        items.Add(Fail(
            "operating-system",
            "Operating system",
            $"Windows version {version} is outside the planned compatibility range.",
            "Use 64-bit Windows 7 SP1 or a supported modern Windows release."));
    }

    private static void AddArchitectureCheck(List<SystemCheckItem> items)
    {
        if (Environment.Is64BitOperatingSystem)
        {
            items.Add(Pass(
                "architecture",
                "Windows architecture",
                $"Detected 64-bit Windows {RuntimeInformation.OSArchitecture}.",
                "This matches the x64 agent package."));
            return;
        }

        items.Add(Fail(
            "architecture",
            "Windows architecture",
            "Detected 32-bit Windows.",
            "The current package supports x64 only. Confirm the x86 requirement before deployment."));
    }

    private static void AddNetFrameworkCheck(List<SystemCheckItem> items)
    {
        var release = ReadNetFrameworkRelease();
        if (release >= NetFramework48MinimumRelease)
        {
            items.Add(Pass(
                "net-framework",
                ".NET Framework 4.8",
                $"Release {release} is installed.",
                "This computer meets the runtime prerequisite for the Windows 7 legacy package."));
            return;
        }

        items.Add(new SystemCheckItem(
            "net-framework",
            ".NET Framework 4.8",
            SystemCheckKinds.Warning,
            "PREREQUISITE MISSING",
            release is null
                ? ".NET Framework 4.x Full was not detected."
                : $"Release {release} is older than .NET Framework 4.8.",
            "Install .NET Framework 4.8 and all required Windows updates before the legacy agent."));
    }

    private static void AddAgentServiceCheck(
        List<SystemCheckItem> items,
        AgentWindowsServiceStatus service)
    {
        if (service.IsRunning)
        {
            items.Add(Pass(
                "agent-service",
                "Opsifin Print Agent service",
                $"Windows Service {service.State.ToLowerInvariant()}.",
                "The service is ready to receive heartbeats and jobs from the Hub."));
            return;
        }

        items.Add(service.Installed
            ? Fail(
                "agent-service",
                "Opsifin Print Agent service",
                $"The service is installed but currently {service.State.ToLowerInvariant()}.",
                "Start or restart the service, then run System Check again.")
            : Fail(
                "agent-service",
                "Opsifin Print Agent service",
                "The Windows Service is not installed.",
                "Install the agent MSI as an Administrator."));
    }

    private static void AddSpoolerCheck(List<SystemCheckItem> items)
    {
        try
        {
            using var spooler = new ServiceController("Spooler");
            items.Add(spooler.Status == ServiceControllerStatus.Running
                ? Pass(
                    "print-spooler",
                    "Windows Print Spooler",
                    "Print Spooler is running.",
                    "Windows can accept print jobs from the agent.")
                : Fail(
                    "print-spooler",
                    "Windows Print Spooler",
                    $"Print Spooler status is {spooler.Status}.",
                    "Start the Print Spooler service before test printing."));
        }
        catch (InvalidOperationException ex)
        {
            items.Add(Fail(
                "print-spooler",
                "Windows Print Spooler",
                $"Print Spooler status could not be read: {ex.Message}",
                "Review the Spooler service and Windows Administrator permissions."));
        }
    }

    private static void AddPairingCheck(List<SystemCheckItem> items, DeviceIdentity? identity)
    {
        items.Add(identity is not null
            ? Pass(
                "pairing",
                "Identity and pairing",
                $"Agent {identity.AgentId:D} is paired with {identity.HubBaseAddress.GetLeftPart(UriPartial.Authority)}.",
                "The device secret is protected locally with DPAPI and excluded from diagnostic reports.")
            : new SystemCheckItem(
                "pairing",
                "Identity and pairing",
                SystemCheckKinds.Warning,
                "NOT PAIRED",
                "No agent identity is available on this computer.",
                "Create a pairing code in the Hub, then connect through Pairing & setup."));
    }

    private static void AddHeartbeatCheck(List<SystemCheckItem> items, AgentHealthSnapshot? health)
    {
        if (health?.LastHeartbeatSuccessAt is not { } heartbeat)
        {
            items.Add(Fail(
                "heartbeat",
                "Hub heartbeat",
                "No successful heartbeat has been recorded.",
                "Review the pairing, Hub connection, and agent logs."));
            return;
        }

        var age = DateTimeOffset.UtcNow - heartbeat;
        var detail = $"Last heartbeat: {IndonesiaTime.Convert(heartbeat):dd MMM yyyy HH:mm:ss} WIB ({FormatAge(age)} ago).";
        items.Add(age <= TimeSpan.FromSeconds(90) && health.ConnectionState == AgentConnectionStates.Online
            ? Pass(
                "heartbeat",
                "Hub heartbeat",
                detail,
                "The Hub is receiving agent status normally.")
            : Fail(
                "heartbeat",
                "Hub heartbeat",
                detail,
                "Restart the service if it does not return online after two heartbeat intervals."));
    }

    private static void AddPrinterCheck(List<SystemCheckItem> items, AgentHealthSnapshot? health)
    {
        var printers = health?.Printers;
        if (printers is { Length: > 0 })
        {
            var online = printers.Count(printer => printer.Status == "ONLINE");
            var problematic = printers.Length - online;
            items.Add(new SystemCheckItem(
                "printer-inventory",
                "Printer inventory",
                problematic == 0 ? SystemCheckKinds.Success : SystemCheckKinds.Warning,
                problematic == 0 ? "READY" : "REVIEW REQUIRED",
                $"The heartbeat reported {printers.Length} printer(s): {online} online and {problematic} requiring attention.",
                problematic == 0
                    ? "Printer names in this report can be compared directly with the Hub inventory."
                    : "Review printers that are not ONLINE in the Dashboard table and report ZIP."));
            return;
        }

        items.Add(Fail(
            "printer-inventory",
            "Printer inventory",
            "No printers were included in the latest heartbeat snapshot.",
            "Make the printer available to the service account, restart the service, and wait for a heartbeat."));
    }

    private static void AddNativeDependencyCheck(List<SystemCheckItem> items, bool serviceInstalled)
    {
        var requiredFiles = new[] { "Agent.Service.exe", "pdfium.dll", "e_sqlite3.dll" };
        var missing = requiredFiles
            .Where(file => !File.Exists(Path.Combine(AppContext.BaseDirectory, file)))
            .ToArray();
        if (missing.Length == 0)
        {
            items.Add(Pass(
                "native-dependencies",
                "Local print dependencies",
                "The service, PDFium, and native SQLite files are available.",
                "The main print-pipeline components were found in the installation folder."));
            return;
        }

        items.Add(new SystemCheckItem(
            "native-dependencies",
            "Local print dependencies",
            serviceInstalled ? SystemCheckKinds.Danger : SystemCheckKinds.Warning,
            serviceInstalled ? "FILES MISSING" : "NOT INSTALLED",
            $"Missing files: {string.Join(", ", missing)}.",
            serviceInstalled
                ? "Repair or reinstall the agent MSI."
                : "These dependencies will be available after the MSI is installed."));
    }

    private static void AddStateDirectoryCheck(List<SystemCheckItem> items)
    {
        var probePath = Path.Combine(AgentStatePaths.StateDirectory, $"system-check-{Guid.NewGuid():N}.tmp");
        try
        {
            if (!Directory.Exists(AgentStatePaths.StateDirectory))
            {
                items.Add(Fail(
                    "state-directory",
                    "Secure local storage",
                    $"The folder {AgentStatePaths.StateDirectory} does not exist yet.",
                    "Start or reinstall the service so the agent can create its secured state folder."));
                return;
            }

            File.WriteAllText(probePath, "opsifin-system-check");
            File.Delete(probePath);
            items.Add(Pass(
                "state-directory",
                "Secure local storage",
                $"The folder {AgentStatePaths.StateDirectory} is writable.",
                "Administrators and SYSTEM can store the identity, health snapshot, outbox, and logs."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            items.Add(Fail(
                "state-directory",
                "Secure local storage",
                $"The state folder is not writable: {ex.Message}",
                "Repair the ProgramData permissions or reinstall the MSI as an Administrator."));
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private static async Task AddHubChecksAsync(
        List<SystemCheckItem> items,
        Uri? hubAddress,
        CancellationToken cancellationToken)
    {
        if (hubAddress is null)
        {
            items.Add(new SystemCheckItem(
                "hub-connectivity",
                "Hub network connection",
                SystemCheckKinds.Warning,
                "ADDRESS REQUIRED",
                "No Hub address is available from the identity or pairing form.",
                "Enter the Hub address in Pairing & setup, then run System Check again."));
            return;
        }

        var origin = new Uri(hubAddress.GetLeftPart(UriPartial.Authority));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, origin);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            items.Add(Pass(
                "hub-connectivity",
                "Hub network connection",
                $"{origin} responded with HTTP {(int)response.StatusCode}.",
                "DNS, TCP, TLS, and certificate validation succeeded without sending device credentials."));

            if (response.Headers.Date is { } serverDate)
            {
                var drift = (serverDate - DateTimeOffset.UtcNow).Duration();
                items.Add(new SystemCheckItem(
                    "clock-sync",
                    "Windows clock synchronization",
                    drift <= TimeSpan.FromSeconds(60) ? SystemCheckKinds.Success : SystemCheckKinds.Warning,
                    drift <= TimeSpan.FromSeconds(60) ? "SYNCHRONIZED" : "TIME DRIFT",
                    $"The local clock differs from the Hub HTTP clock by approximately {drift.TotalSeconds:F0} seconds.",
                    drift <= TimeSpan.FromSeconds(60)
                        ? "A synchronized clock keeps leases and event ordering accurate."
                        : "Enable Windows time synchronization before using the agent."));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            items.Add(Fail(
                "hub-connectivity",
                "Hub network connection",
                $"{origin} could not be reached: {ex.Message}",
                "Review DNS, firewall, proxy, TLS 1.2, certificates, and the client network connection."));
        }
    }

    private static int? ReadNetFrameworkRelease()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var full = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
                if (full?.GetValue("Release") is int release)
                {
                    return release;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // View registry berikutnya mungkin tetap dapat dibaca pada instalasi lama.
            }
        }

        return null;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            return "the local clock is ahead of the timestamp";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(0, (int)age.TotalSeconds)} seconds";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} minutes";
        }

        return $"{(int)age.TotalHours} hours";
    }

    private static SystemCheckItem Pass(
        string key,
        string title,
        string detail,
        string recommendation) => new(
            key,
            title,
            SystemCheckKinds.Success,
            "PASSED",
            detail,
            recommendation);

    private static SystemCheckItem Fail(
        string key,
        string title,
        string detail,
        string recommendation) => new(
            key,
            title,
            SystemCheckKinds.Danger,
            "FAILED",
            detail,
            recommendation);
}

internal static class StringExtensions
{
    public static string DefaultIfEmpty(this string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
