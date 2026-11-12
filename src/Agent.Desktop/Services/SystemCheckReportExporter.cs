using System.IO;
using System.IO.Compression;
using System.Text;
using Agent.Local;

namespace Agent.Desktop.Services;

internal sealed class SystemCheckReportExporter
{
    public Task ExportAsync(
        string destinationPath,
        SystemCheckReport report,
        DeviceIdentity? identity,
        AgentHealthSnapshot? health,
        IReadOnlyCollection<LocalLogEntry> logs,
        CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "opsifin-system-check",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                File.WriteAllText(
                    Path.Combine(temporaryDirectory, "system-check.txt"),
                    BuildSummary(report, identity, health),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.WriteAllText(
                    Path.Combine(temporaryDirectory, "printers.csv"),
                    BuildPrinterCsv(health),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                File.WriteAllText(
                    Path.Combine(temporaryDirectory, "recent-logs.txt"),
                    BuildLogs(logs, identity),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                ZipFile.CreateFromDirectory(
                    temporaryDirectory,
                    destinationPath,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false);
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        }, cancellationToken);

    private static string BuildSummary(
        SystemCheckReport report,
        DeviceIdentity? identity,
        AgentHealthSnapshot? health)
    {
        var builder = new StringBuilder();
        builder.AppendLine("OPSIFIN PRINT AGENT - SYSTEM CHECK");
        builder.AppendLine("==================================");
        builder.AppendLine($"Generated          : {report.GeneratedAt:dd MMM yyyy HH:mm:ss} WIB");
        builder.AppendLine($"Computer           : {report.MachineName}");
        builder.AppendLine($"Operating system   : {report.OsDescription}");
        builder.AppendLine($"Architecture       : {report.Architecture}");
        builder.AppendLine($"Runtime            : {report.RuntimeDescription}");
        builder.AppendLine($"Agent version      : {AgentProduct.Version}");
        builder.AppendLine($"Agent ID           : {identity?.AgentId.ToString("D") ?? "not paired"}");
        builder.AppendLine($"Tenant ID          : {identity?.TenantId.ToString("D") ?? "-"}");
        builder.AppendLine($"Site ID            : {identity?.SiteId.ToString("D") ?? "-"}");
        builder.AppendLine($"Hub                : {identity?.HubBaseAddress.GetLeftPart(UriPartial.Authority) ?? "-"}");
        builder.AppendLine($"Config version     : {health?.ConfigVersion.ToString() ?? "-"}");
        builder.AppendLine($"Last heartbeat     : {FormatTimestamp(health?.LastHeartbeatSuccessAt)}");
        builder.AppendLine();
        builder.AppendLine($"Summary: {report.SuccessCount} passed, {report.WarningCount} warning(s), {report.DangerCount} failed");
        builder.AppendLine();

        foreach (var item in report.Items)
        {
            builder.AppendLine($"[{item.Status}] {item.Title}");
            builder.AppendLine($"Detail     : {Redact(item.Detail, identity)}");
            builder.AppendLine($"Action     : {Redact(item.Recommendation, identity)}");
            builder.AppendLine();
        }

        builder.AppendLine("SECURITY NOTE");
        builder.AppendLine("This report does not contain the device secret, pairing code, bearer token, or printed document contents.");
        return builder.ToString();
    }

    private static string BuildPrinterCsv(AgentHealthSnapshot? health)
    {
        var builder = new StringBuilder();
        builder.AppendLine("name,status,driver,port,is_default,queue_length");
        foreach (var printer in health?.Printers ?? [])
        {
            builder.Append(EscapeCsv(printer.Name)).Append(',')
                .Append(EscapeCsv(printer.Status)).Append(',')
                .Append(EscapeCsv(printer.Driver)).Append(',')
                .Append(EscapeCsv(printer.Port)).Append(',')
                .Append(printer.IsDefault ? "true" : "false").Append(',')
                .AppendLine(printer.QueueLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BuildLogs(
        IReadOnlyCollection<LocalLogEntry> logs,
        DeviceIdentity? identity)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Recent Opsifin Print Agent logs (WIB)");
        builder.AppendLine("=====================================");
        foreach (var log in logs.OrderBy(entry => entry.Timestamp))
        {
            builder.Append(log.Timestamp.ToString("dd MMM yyyy HH:mm:ss"))
                .Append(" WIB | ")
                .Append(log.Level)
                .Append(" | ")
                .Append(log.Category)
                .Append(" | ")
                .AppendLine(Redact(log.Message, identity));
        }

        return builder.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset? value) => value is { } timestamp
        ? $"{IndonesiaTime.Convert(timestamp):dd MMM yyyy HH:mm:ss} WIB"
        : "not available";

    private static string EscapeCsv(string? value)
    {
        var normalized = value ?? string.Empty;
        return $"\"{normalized.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string Redact(string value, DeviceIdentity? identity)
    {
        if (identity is null)
        {
            return value;
        }

        // Defense-in-depth: logger semestinya tidak pernah menulis credential, tetapi ZIP
        // diagnostik tetap membersihkannya sebelum file meninggalkan komputer client.
        return value
            .Replace(identity.BearerToken, "[REDACTED]", StringComparison.Ordinal)
            .Replace(identity.DeviceSecret, "[REDACTED]", StringComparison.Ordinal);
    }
}
