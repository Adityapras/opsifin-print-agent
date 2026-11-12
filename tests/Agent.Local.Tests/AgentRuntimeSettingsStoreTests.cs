using System.Text.Json;
using Agent.Local;

namespace Agent.Local.Tests;

public sealed class AgentRuntimeSettingsStoreTests
{
    [Fact]
    public async Task File_yang_belum_ada_tidak_mengubah_default_runtime()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "opsifin-agent-tests",
            Guid.NewGuid().ToString("N"),
            "runtime-settings.json");

        Assert.Null(await new AgentRuntimeSettingsStore(path).LoadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mode_simulasi_disimpan_atomik_dan_dapat_dibaca_ulang(bool simulationMode)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "opsifin-agent-tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "runtime-settings.json");
        try
        {
            var store = new AgentRuntimeSettingsStore(path);
            var settings = new AgentRuntimeSettings(simulationMode);

            await store.SaveAsync(settings, CancellationToken.None);
            var loaded = await new AgentRuntimeSettingsStore(path).LoadAsync(CancellationToken.None);

            Assert.Equal(settings, loaded);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(
                simulationMode,
                document.RootElement.GetProperty("simulation_mode").GetBoolean());
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
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
    public async Task Penyimpanan_baru_mengganti_mode_sebelumnya()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "opsifin-agent-tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "runtime-settings.json");
        try
        {
            var store = new AgentRuntimeSettingsStore(path);
            await store.SaveAsync(new AgentRuntimeSettings(true), CancellationToken.None);
            await store.SaveAsync(new AgentRuntimeSettings(false), CancellationToken.None);

            Assert.Equal(
                new AgentRuntimeSettings(false),
                await store.LoadAsync(CancellationToken.None));
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
