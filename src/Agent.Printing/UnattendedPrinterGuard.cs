namespace Agent.Printing;

public sealed class UnattendedPrintingNotSupportedException(string message)
    : NotSupportedException(message);

public static class UnattendedPrinterGuard
{
    private static readonly string[] InteractivePorts =
    [
        "PORTPROMPT:",
        "FILE:",
        "NUL:",
        "SHRFAX:",
    ];

    private static readonly string[] InteractiveDriverMarkers =
    [
        "PRINT TO PDF",
        "XPS DOCUMENT WRITER",
        "FAX",
        "ONENOTE",
        "SNAGIT",
        "PDF CREATOR",
    ];

    public static string? FindUnsupportedReason(
        string? driverName,
        IEnumerable<string> portNames)
    {
        foreach (var portName in portNames)
        {
            if (InteractivePorts.Contains(portName.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return $"port '{portName}' requires an interactive desktop or output filename";
            }
        }

        if (!string.IsNullOrWhiteSpace(driverName)
            && InteractiveDriverMarkers.Any(marker =>
                driverName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return $"driver '{driverName}' is an interactive virtual printer";
        }

        return null;
    }
}
