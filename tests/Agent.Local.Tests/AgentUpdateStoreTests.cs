using Agent.Local;

namespace Agent.Local.Tests;

public sealed class AgentUpdateStoreTests
{
    [Fact]
    public async Task State_dan_permintaan_install_disimpan_terpisah_agar_tidak_saling_menimpa()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "opsifin-agent-tests",
            Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(directory, "update-state.json");
        var requestPath = Path.Combine(directory, "update-request.json");
        try
        {
            var commandId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var state = new AgentUpdateState(
                commandId,
                "0.3.6",
                "0.3.7",
                AgentUpdateStatuses.Ready,
                AgentUpdatePolicies.Manual,
                "https://hub.example/agent-update.msi",
                new string('a', 64),
                now,
                now,
                "Safe update",
                "C:\\ProgramData\\Opsifin\\PrintAgent\\state\\updates\\agent.msi",
                100,
                "Ready");
            var store = new AgentUpdateStore(statePath, requestPath);

            await store.SaveAsync(state, CancellationToken.None);
            await store.RequestInstallAsync(commandId, CancellationToken.None);

            Assert.Equal(state, await store.LoadAsync(CancellationToken.None));
            Assert.True(await store.IsInstallRequestedAsync(commandId, CancellationToken.None));
            Assert.False(await store.IsInstallRequestedAsync(Guid.NewGuid(), CancellationToken.None));

            store.ClearInstallRequest();
            Assert.False(await store.IsInstallRequestedAsync(commandId, CancellationToken.None));
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
    public async Task State_yang_belum_ada_dibaca_sebagai_null()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "opsifin-agent-tests",
            Guid.NewGuid().ToString("N"));
        var store = new AgentUpdateStore(
            Path.Combine(directory, "update-state.json"),
            Path.Combine(directory, "update-request.json"));

        Assert.Null(await store.LoadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(AgentUpdateStatuses.Succeeded, true)]
    [InlineData(AgentUpdateStatuses.Failed, true)]
    [InlineData(AgentUpdateStatuses.Ready, false)]
    [InlineData(AgentUpdateStatuses.Installing, false)]
    public void Terminal_status_dikenali_dengan_benar(string status, bool expected)
    {
        Assert.Equal(expected, AgentUpdateStatuses.IsTerminal(status));
    }
}
