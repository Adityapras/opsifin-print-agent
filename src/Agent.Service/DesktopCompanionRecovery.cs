using Agent.Local;

namespace Agent.Service;

internal interface IDesktopCompanionManager
{
    Task<IReadOnlyCollection<int>> StopInstalledInstancesAsync(CancellationToken cancellationToken);

    bool TryStartTray(int sessionId, out string? error);
}

internal sealed class DesktopCompanionRecovery(
    AgentUpdateStore updateStore,
    IDesktopCompanionManager companionManager,
    ILogger<DesktopCompanionRecovery> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        AgentUpdateState? update;
        try
        {
            update = await updateStore.LoadAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Recovery companion tidak boleh mencegah service utama mulai bekerja.
            logger.LogWarning(exception, "Desktop companion recovery could not read the update state");
            return;
        }

        if (update?.Status != AgentUpdateStatuses.Installing)
        {
            return;
        }

        var sessions = await companionManager.StopInstalledInstancesAsync(cancellationToken);
        foreach (var sessionId in sessions.Distinct())
        {
            if (companionManager.TryStartTray(sessionId, out var error))
            {
                logger.LogInformation(
                    "Agent Control was restarted in Windows session {SessionId} after update {TargetVersion}",
                    sessionId,
                    update.TargetVersion);
            }
            else
            {
                // Service dan printing tetap sehat bila desktop session menolak relaunch.
                // Shortcut dan HKLM Run masih menyediakan jalur recovery manual/login berikutnya.
                logger.LogWarning(
                    "Agent Control could not be restarted in Windows session {SessionId}: {RecoveryError}",
                    sessionId,
                    error);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
