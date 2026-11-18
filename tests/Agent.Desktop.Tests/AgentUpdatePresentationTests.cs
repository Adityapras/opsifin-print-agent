using Agent.Desktop.Services;
using Agent.Local;

namespace Agent.Desktop.Tests;

public sealed class AgentUpdatePresentationTests
{
    [Fact]
    public void Tidak_ada_update_tidak_ditampilkan()
    {
        Assert.False(AgentUpdatePresentation.ShouldDisplay(null));
    }

    [Fact]
    public void Update_yang_sudah_berhasil_tidak_ditampilkan()
    {
        Assert.False(AgentUpdatePresentation.ShouldDisplay(CreateUpdate(AgentUpdateStatuses.Succeeded)));
    }

    [Theory]
    [InlineData(AgentUpdateStatuses.PendingDownload)]
    [InlineData(AgentUpdateStatuses.Downloading)]
    [InlineData(AgentUpdateStatuses.Verifying)]
    [InlineData(AgentUpdateStatuses.Ready)]
    [InlineData(AgentUpdateStatuses.WaitingForIdle)]
    [InlineData(AgentUpdateStatuses.Installing)]
    [InlineData(AgentUpdateStatuses.Failed)]
    public void Update_aktif_atau_gagal_ditampilkan(string status)
    {
        Assert.True(AgentUpdatePresentation.ShouldDisplay(CreateUpdate(status)));
    }

    private static AgentUpdateState CreateUpdate(string status)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentUpdateState(
            Guid.CreateVersion7(),
            "1.0.8",
            "1.0.9",
            status,
            AgentUpdatePolicies.Manual,
            "https://hub.example/agent-update.msi",
            new string('a', 64),
            now,
            now);
    }
}
