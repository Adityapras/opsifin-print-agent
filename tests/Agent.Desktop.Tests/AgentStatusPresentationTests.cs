using Agent.Desktop.Services;
using Agent.Local;

namespace Agent.Desktop.Tests;

public sealed class AgentStatusPresentationTests
{
    [Fact]
    public void Backoff_heartbeat_dengan_process_hidup_ditampilkan_sebagai_koneksi_degraded()
    {
        var presentation = AgentStatusPresentationFactory.ForStaleSnapshot(
            Snapshot(AgentConnectionStates.Degraded),
            processIsActive: true);

        Assert.Equal("warning", presentation.Kind);
        Assert.Equal("Hub connection is degraded", presentation.Title);
        Assert.Equal("Retrying", presentation.HubState);
    }

    [Fact]
    public void Process_yang_hilang_tetap_ditampilkan_sebagai_service_unresponsive()
    {
        var presentation = AgentStatusPresentationFactory.ForStaleSnapshot(
            Snapshot(AgentConnectionStates.Online),
            processIsActive: false);

        Assert.Equal("danger", presentation.Kind);
        Assert.Equal("Service is unresponsive", presentation.Title);
    }

    private static AgentHealthSnapshot Snapshot(string connectionState) => new(
        "0.3.2",
        1234,
        DateTimeOffset.UtcNow.AddMinutes(-10),
        DateTimeOffset.UtcNow.AddMinutes(-5),
        connectionState,
        SimulationMode: false);
}
