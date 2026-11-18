using Agent.Local;

namespace Agent.Service;

internal interface IDesktopCompanionManager
{
    Task<IReadOnlyCollection<int>> StopInstalledInstancesAsync(CancellationToken cancellationToken);

    IReadOnlyCollection<int> GetInteractiveSessionIds();

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

        try
        {
            var sessions = new HashSet<int>(
                await companionManager.StopInstalledInstancesAsync(cancellationToken));
            sessions.UnionWith(companionManager.GetInteractiveSessionIds());
            foreach (var sessionId in sessions)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Windows sedang menghentikan service saat recovery berlangsung.
        }
        catch (Exception exception)
        {
            // Desktop recovery adalah fitur pendamping. Kegagalan apa pun tidak boleh
            // membatalkan startup service atau memutus print pipeline.
            logger.LogWarning(exception, "Desktop companion recovery failed safely");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
