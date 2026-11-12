using System.IO.Compression;
using Agent.Desktop.Services;
using Agent.Local;

namespace Agent.Desktop.Tests;

public sealed class SystemCheckReportExporterTests
{
    [Fact]
    public async Task Export_tidak_memasukkan_device_secret_atau_bearer_token()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, "system-check.zip");
        Directory.CreateDirectory(directory);
        try
        {
            const string secret = "secret-yang-tidak-boleh-keluar";
            var identity = new DeviceIdentity(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                new Uri("https://print.example.test/agent/v1/"),
                secret);
            var report = new SystemCheckReport(
                new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(7)),
                "CLIENT-01",
                "Windows test",
                "X64",
                ".NET test",
                [
                    new SystemCheckItem(
                        "security",
                        "Redaction",
                        SystemCheckKinds.Success,
                        "PASSED",
                        $"Nilai uji {identity.BearerToken}",
                        $"Rahasia uji {secret}"),
                ]);
            var logs = new[]
            {
                new LocalLogEntry(
                    report.GeneratedAt,
                    "Warning",
                    "Test",
                    $"Header Bearer {identity.BearerToken}; secret={secret}"),
            };

            await new SystemCheckReportExporter().ExportAsync(
                destination,
                report,
                identity,
                health: null,
                logs,
                CancellationToken.None);

            using var archive = ZipFile.OpenRead(destination);
            var contents = new List<string>();
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                contents.Add(await reader.ReadToEndAsync());
            }

            var combined = string.Join('\n', contents);
            Assert.DoesNotContain(secret, combined, StringComparison.Ordinal);
            Assert.DoesNotContain(identity.BearerToken, combined, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", combined, StringComparison.Ordinal);
            Assert.Contains(identity.AgentId.ToString("D"), combined, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
