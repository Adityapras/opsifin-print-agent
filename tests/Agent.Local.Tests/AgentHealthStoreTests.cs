using System.Text.Json;
using Agent.Local;

namespace Agent.Local.Tests;

public sealed class AgentHealthStoreTests
{
    [Fact]
    public async Task Snapshot_bisa_dibaca_setelah_store_dibuka_ulang()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "health.json");
        try
        {
            var snapshot = new AgentHealthSnapshot(
            "0.3.0",
                1234,
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow,
                AgentConnectionStates.Online,
                false,
                LastHeartbeatAttemptAt: DateTimeOffset.UtcNow,
                LastHeartbeatSuccessAt: DateTimeOffset.UtcNow,
                ConfigVersion: 8,
                PrinterCount: 2,
                ClaimIntervalSeconds: 5,
                Printers:
                [
                    new LocalPrinterSnapshot(
                        "EPSON L3210 Series",
                        "EPSON L3210 Series",
                        "USB001",
                        true,
                        "ONLINE",
                        0),
                    new LocalPrinterSnapshot(
                        "Microsoft Print to PDF",
                        "Microsoft Print To PDF",
                        "PORTPROMPT:",
                        false,
                        "ONLINE",
                        1),
                ],
                ActivePrintJobs:
                [
                    new LocalActivePrintJobSnapshot(
                        Guid.Parse("019a4d3d-19af-7c61-b4cf-f949fd97a22d"),
                        null,
                        null,
                        "EPSON L3210 Series",
                        LocalPrintJobStages.Printing,
                        DateTimeOffset.UtcNow.AddSeconds(-12),
                        DateTimeOffset.UtcNow,
                        2,
                        41),
                ]);
            await new AgentHealthStore(path).SaveAsync(snapshot, CancellationToken.None);

            var loaded = await new AgentHealthStore(path).LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(
                snapshot with { Printers = null, ActivePrintJobs = null },
                loaded with { Printers = null, ActivePrintJobs = null });
            var expectedPrinters = Assert.IsType<LocalPrinterSnapshot[]>(snapshot.Printers);
            var actualPrinters = Assert.IsType<LocalPrinterSnapshot[]>(loaded.Printers);
            Assert.Equal<LocalPrinterSnapshot>(expectedPrinters, actualPrinters);
            var expectedJobs = Assert.IsType<LocalActivePrintJobSnapshot[]>(snapshot.ActivePrintJobs);
            var actualJobs = Assert.IsType<LocalActivePrintJobSnapshot[]>(loaded.ActivePrintJobs);
            Assert.Equal<LocalActivePrintJobSnapshot>(expectedJobs, actualJobs);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("online", document.RootElement.GetProperty("connection_state").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("printer_count").GetInt32());
            Assert.Equal(
                "EPSON L3210 Series",
                document.RootElement.GetProperty("printers")[0].GetProperty("name").GetString());
            Assert.Equal(5, document.RootElement.GetProperty("claim_interval_seconds").GetInt32());
            Assert.Equal(
                "printing",
                document.RootElement.GetProperty("active_print_jobs")[0].GetProperty("stage").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task File_yang_belum_ada_dibaca_sebagai_null()
    {
        var path = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"), "health.json");

        Assert.Null(await new AgentHealthStore(path).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Remove_menghapus_snapshot_health_lama()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "health.json");
        try
        {
            var store = new AgentHealthStore(path);
            await store.SaveAsync(
                new AgentHealthSnapshot(
                    "0.3.0",
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    AgentConnectionStates.AwaitingPairing,
                    false),
                CancellationToken.None);

            store.Remove();

            Assert.Null(await store.LoadAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
