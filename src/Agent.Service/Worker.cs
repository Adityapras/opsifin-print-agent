using System.Net;
using Agent.Hub;
using Agent.Printing;

namespace Agent.Service;

public sealed class Worker(
    ILogger<Worker> logger,
    AgentHubClient hubClient,
    DeviceIdentityStore identityStore,
    EventOutbox eventOutbox,
    EventOutboxDispatcher outboxDispatcher,
    JobCoordinator jobCoordinator,
    AgentCommandHandler commandHandler,
    AgentExecutionOptions executionOptions) : BackgroundService
{
    private const int AuthenticationRetrySeconds = 300;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            AgentStatePaths.EnsureSecureStateDirectory();
            await eventOutbox.InitializeAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Outbox SQLite tidak dapat diinisialisasi; agent tidak boleh berjalan tanpa durability event");
            return;
        }

        var identity = await WaitForIdentity(stoppingToken);
        if (identity is null)
        {
            return;
        }

        logger.LogInformation(
            "Opsifin Print Agent {Version} berjalan sebagai {AgentId}",
            AgentMetadata.Version,
            identity.AgentId);

        try
        {
            await Task.WhenAll(
                RunHeartbeatLoopAsync(identity, stoppingToken),
                RunClaimLoopAsync(identity, stoppingToken),
                RunOutboxLoopAsync(identity, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Penghentian service adalah jalur normal.
        }
        finally
        {
            jobCoordinator.AbortAll();
            await jobCoordinator.WaitForActiveJobsAsync();
        }
    }

    private async Task RunHeartbeatLoopAsync(DeviceIdentity identity, CancellationToken stoppingToken)
    {
        var configVersion = 0L;
        var heartbeatIntervalSeconds = AgentRuntimeDefaults.HeartbeatIntervalSeconds;
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = heartbeatIntervalSeconds;
            try
            {
                var now = DateTimeOffset.UtcNow;
                var printers = BuildPrinterReports();
                var response = await hubClient.HeartbeatAsync(
                    identity.HubBaseAddress,
                    identity.BearerToken,
                    AgentMetadata.Version,
                    new HeartbeatRequest(
                        AgentMetadata.Version,
                        Environment.TickCount64 / 1000,
                        now,
                        configVersion,
                        executionOptions.SimulationMode ? ["simulation"] : ["pdfium"],
                        printers),
                    stoppingToken);

                configVersion = response.ConfigVersion;
                if (response.Config is not null)
                {
                    jobCoordinator.UpdateConfiguration(response.Config);
                    heartbeatIntervalSeconds = NormalizeHeartbeatInterval(response.Config.HeartbeatIntervalS);
                }

                consecutiveFailures = 0;
                var clockDrift = Math.Abs((response.ServerTime - now).TotalSeconds);
                if (clockDrift > 60)
                {
                    logger.LogWarning("Selisih jam agent dan Hub {ClockDrift:F0} detik", clockDrift);
                }

                if (response.Commands is { Count: > 0 })
                {
                    await commandHandler.HandleAsync(identity, response.Commands, stoppingToken);
                }

                logger.LogDebug(
                    "Heartbeat diterima; config version {ConfigVersion}, {PrinterCount} printer",
                    configVersion,
                    printers.Count);
            }
            catch (AgentApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized)
            {
                logger.LogError("Hub menolak identitas agent ({ErrorCode})", ex.ErrorCode);
                delaySeconds = AuthenticationRetrySeconds;
            }
            catch (AgentApiException ex) when ((int?)ex.StatusCode == 426)
            {
                logger.LogError("Versi agent ditolak Hub ({ErrorCode}); menunggu perintah update", ex.ErrorCode);
                delaySeconds = AuthenticationRetrySeconds;
            }
            catch (AgentApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                delaySeconds = RetryAfterSeconds(ex);
                logger.LogWarning("Hub membatasi heartbeat; mencoba lagi dalam {DelaySeconds} detik", delaySeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                delaySeconds = CalculateBackoffSeconds(consecutiveFailures);
                logger.LogWarning(ex, "Heartbeat gagal; mencoba lagi dalam {DelaySeconds} detik", delaySeconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }

    private async Task RunClaimLoopAsync(DeviceIdentity identity, CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = jobCoordinator.ClaimIntervalSeconds;
            try
            {
                if (jobCoordinator.HasConfiguration)
                {
                    var started = await jobCoordinator.ClaimOnceAsync(
                        identity,
                        executionOptions.SimulationMode
                            ? jobCoordinator.GetConfiguredPrinterNames()
                            : GetVisiblePrinterNames(),
                        stoppingToken);
                    if (started > 0)
                    {
                        logger.LogInformation("Memulai {JobCount} job hasil claim", started);
                    }
                }

                consecutiveFailures = 0;
            }
            catch (AgentApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized
                                               || (int?)ex.StatusCode == 426)
            {
                delaySeconds = AuthenticationRetrySeconds;
                logger.LogError("Claim ditolak Hub ({ErrorCode})", ex.ErrorCode);
            }
            catch (AgentApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                delaySeconds = RetryAfterSeconds(ex);
                logger.LogWarning("Hub membatasi claim; mencoba lagi dalam {DelaySeconds} detik", delaySeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                delaySeconds = CalculateBackoffSeconds(consecutiveFailures);
                logger.LogWarning(ex, "Claim gagal; mencoba lagi dalam {DelaySeconds} detik", delaySeconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }

    private async Task RunOutboxLoopAsync(DeviceIdentity identity, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatch = await outboxDispatcher.DispatchDueAsync(
                    identity.HubBaseAddress,
                    identity.BearerToken,
                    AgentMetadata.Version,
                    DateTimeOffset.UtcNow,
                    stoppingToken);
                if (dispatch.Delivered > 0 || dispatch.Rescheduled > 0)
                {
                    logger.LogInformation(
                        "Outbox: {Delivered} event terkirim, {Rescheduled} dijadwalkan ulang",
                        dispatch.Delivered,
                        dispatch.Rescheduled);
                }

                jobCoordinator.AbortJobs(dispatch.AbortedJobIds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dispatch outbox gagal; mencoba lagi dalam 2 detik");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task<DeviceIdentity?> WaitForIdentity(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await identityStore.LoadAsync(stoppingToken);
                if (identity is not null)
                {
                    return identity;
                }

                logger.LogWarning(
                    "Agent belum di-enroll. Jalankan Agent.Service.exe enroll --hub-url <URL> --pairing-code <KODE>");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                // File ada tetapi tidak dapat dibuka/dekripsi bukan keadaan yang akan pulih lewat
                // retry jaringan. Tetap hidup agar operator bisa memperbaiki atau enroll ulang,
                // tetapi jangan pernah mengirim request dengan identity setengah terbaca.
                logger.LogCritical(ex, "Identitas agent tidak dapat dibaca");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        return null;
    }

    private IReadOnlyList<PrinterReportDto> BuildPrinterReports()
    {
        try
        {
            return PrinterInventory.List()
                .Select(printer => new PrinterReportDto(
                    printer.Name,
                    printer.DriverName,
                    string.Join(',', printer.Ports.Select(port => port.Name)),
                    printer.IsDefault,
                    PrinterStatusMapper.Map(printer.Status, printer.Attributes),
                    printer.Status,
                    printer.QueueLength))
                .ToArray();
        }
        catch (Exception ex)
        {
            // Heartbeat tetap harus sampai ke Hub agar kegagalan inventaris terlihat sebagai
            // agent online tanpa printer, bukan salah didiagnosis sebagai agent mati.
            logger.LogError(ex, "Gagal membaca inventaris printer");
            return [];
        }
    }

    private IReadOnlySet<string> GetVisiblePrinterNames()
    {
        try
        {
            return PrinterInventory.List()
                .Select(printer => printer.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Tanpa inventaris yang dapat dipercaya agent tidak boleh mengklaim job karena
            // PRINTER_NOT_FOUND setelah claim akan menghabiskan attempt secara sia-sia.
            logger.LogError(ex, "Gagal membaca printer; claim dilewati");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static int NormalizeHeartbeatInterval(int? seconds) =>
        seconds is > 0 ? Math.Clamp(seconds.Value, 10, 300) : AgentRuntimeDefaults.HeartbeatIntervalSeconds;

    private static int CalculateBackoffSeconds(int failureCount)
    {
        var exponent = Math.Min(failureCount - 1, 7);
        var baseDelay = Math.Min(300, 2 << exponent);
        var jitter = Random.Shared.Next(0, Math.Max(1, baseDelay / 4));
        return baseDelay + jitter;
    }

    private static int RetryAfterSeconds(AgentApiException exception) =>
        Math.Clamp(
            (int)Math.Ceiling((exception.RetryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds),
            1,
            3600);
}
