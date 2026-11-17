using Agent.Local;
using Agent.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Service.Tests;

public sealed class DesktopCompanionRecoveryTests
{
    [Fact]
    public async Task Installing_update_merestart_tray_pada_setiap_session_lama()
    {
        var directory = CreateTestDirectory();
        try
        {
            var store = CreateStore(directory);
            await store.SaveAsync(CreateState(AgentUpdateStatuses.Installing), CancellationToken.None);
            var manager = new FakeCompanionManager([2, 2, 5]);
            var recovery = new DesktopCompanionRecovery(
                store,
                manager,
                NullLogger<DesktopCompanionRecovery>.Instance);

            await recovery.StartAsync(CancellationToken.None);

            Assert.Equal(1, manager.StopCalls);
            Assert.Equal([2, 5], manager.StartedSessions);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Theory]
    [InlineData(AgentUpdateStatuses.Ready)]
    [InlineData(AgentUpdateStatuses.Succeeded)]
    [InlineData(AgentUpdateStatuses.Failed)]
    public async Task Status_selain_installing_tidak_mengganggu_companion(string status)
    {
        var directory = CreateTestDirectory();
        try
        {
            var store = CreateStore(directory);
            await store.SaveAsync(CreateState(status), CancellationToken.None);
            var manager = new FakeCompanionManager([2]);
            var recovery = new DesktopCompanionRecovery(
                store,
                manager,
                NullLogger<DesktopCompanionRecovery>.Instance);

            await recovery.StartAsync(CancellationToken.None);

            Assert.Equal(0, manager.StopCalls);
            Assert.Empty(manager.StartedSessions);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static AgentUpdateStore CreateStore(string directory) => new(
        Path.Combine(directory, "update-state.json"),
        Path.Combine(directory, "update-request.json"));

    private static AgentUpdateState CreateState(string status) => new(
        Guid.NewGuid(),
        "1.0.5",
        "1.0.6",
        status,
        AgentUpdatePolicies.Manual,
        "https://example.test/agent.msi",
        new string('a', 64),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeCompanionManager(IReadOnlyCollection<int> sessions) : IDesktopCompanionManager
    {
        public int StopCalls { get; private set; }

        public List<int> StartedSessions { get; } = [];

        public Task<IReadOnlyCollection<int>> StopInstalledInstancesAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            return Task.FromResult(sessions);
        }

        public bool TryStartTray(int sessionId, out string? error)
        {
            error = null;
            StartedSessions.Add(sessionId);
            return true;
        }
    }
}
