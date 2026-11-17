using Agent.Local;

namespace Agent.Service;

public sealed class AgentStorageMaintenance(
    AgentStorageCleaner cleaner,
    ILogger<AgentStorageMaintenance> logger,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await cleaner.CleanAsync(stoppingToken);
                if (result.DeletedFiles > 0 || result.DeletedDirectories > 0)
                {
                    logger.LogInformation(
                        "Storage cleanup removed {DeletedFiles} file(s) and {DeletedDirectories} directory(s), reclaiming {ReclaimedBytes} bytes",
                        result.DeletedFiles,
                        result.DeletedDirectories,
                        result.ReclaimedBytes);
                }

                foreach (var error in result.Errors)
                {
                    logger.LogWarning("Storage cleanup skipped an item: {CleanupError}", error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Maintenance tidak boleh menghentikan print pipeline. Error tetap terlihat
                // di diagnostics dan cleanup akan dicoba kembali pada jadwal berikutnya.
                logger.LogWarning(exception, "Agent storage cleanup failed");
            }

            await Task.Delay(TimeSpan.FromDays(1), timeProvider, stoppingToken);
        }
    }
}
