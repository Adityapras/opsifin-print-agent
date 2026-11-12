using System.Net;
using Agent.Hub;
using Agent.Local;
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
    AgentUpdateStore updateStore,
    AgentHealthPublisher healthPublisher,
    AgentExecutionOptions executionOptions) : BackgroundService
{
    private const int AuthenticationRetrySeconds = 300;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await healthPublisher.ReportStartingAsync(stoppingToken);
        try
        {
            AgentStatePaths.EnsureSecureStateDirectory();
            await eventOutbox.InitializeAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "The SQLite outbox could not be initialized; the agent cannot run without durable events");
            await healthPublisher.ReportFailureAsync(
                AgentConnectionStates.Degraded,
                "outbox_initialization_failed",
                "The SQLite outbox could not be initialized.",
                stoppingToken);
            return;
        }

        var identity = await WaitForIdentity(stoppingToken);
        if (identity is null)
        {
            return;
        }

        logger.LogInformation(
            "Opsifin Print Agent {Version} is running as {AgentId}",
            AgentProduct.Version,
            identity.AgentId);
        if (executionOptions.SimulationMode)
        {
            logger.LogWarning(
                "Simulation mode is enabled; Hub jobs will complete without sending output to a physical printer");
        }
        await healthPublisher.ReportConnectingAsync(stoppingToken);

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
            await healthPublisher.ReportStoppingAsync();
        }
    }

    private async Task RunHeartbeatLoopAsync(DeviceIdentity identity, CancellationToken stoppingToken)
    {
        var configVersion = 0L;
        var heartbeatIntervalSeconds = AgentRuntimeDefaults.HeartbeatIntervalSeconds;
        int? sourcePollIntervalSeconds = null;
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = heartbeatIntervalSeconds;
            try
            {
                var now = DateTimeOffset.UtcNow;
                var printers = BuildPrinterReports();
                var update = await BuildUpdateHeartbeatAsync(stoppingToken);
                await healthPublisher.ReportHeartbeatAttemptAsync(stoppingToken);
                var response = await hubClient.HeartbeatAsync(
                    identity.HubBaseAddress,
                    identity.BearerToken,
                    AgentProduct.Version,
                    new HeartbeatRequest(
                        AgentProduct.Version,
                        Environment.TickCount64 / 1000,
                        now,
                        configVersion,
                        executionOptions.SimulationMode ? ["simulation"] : ["pdfium"],
                        printers,
                        update),
                    stoppingToken);

                configVersion = response.ConfigVersion;
                if (response.Config is not null)
                {
                    LogNormalizedPrintSettings(response.Config);
                    jobCoordinator.UpdateConfiguration(response.Config);
                    heartbeatIntervalSeconds = NormalizeHeartbeatInterval(response.Config.HeartbeatIntervalS);
                    sourcePollIntervalSeconds = response.Config.SourcePollIntervalS;
                }

                consecutiveFailures = 0;
                var clockDrift = Math.Abs((response.ServerTime - now).TotalSeconds);
                if (clockDrift > 60)
                {
                    logger.LogWarning("The agent and Hub clocks differ by {ClockDrift:F0} seconds", clockDrift);
                }

                if (response.Commands is { Count: > 0 })
                {
                    await commandHandler.HandleAsync(identity, response.Commands, stoppingToken);
                }

                await healthPublisher.ReportOnlineAsync(
                    configVersion,
                    jobCoordinator.ClaimIntervalSeconds,
                    sourcePollIntervalSeconds,
                    printers,
                    stoppingToken);

                logger.LogDebug(
                    "Heartbeat accepted; config version {ConfigVersion}, {PrinterCount} printer(s)",
                    configVersion,
                    printers.Count);
            }
            catch (AgentApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized)
            {
                logger.LogError("The Hub rejected the agent identity ({ErrorCode})", ex.ErrorCode);
                await healthPublisher.ReportFailureAsync(
                    AgentConnectionStates.AuthenticationFailed,
                    ex.ErrorCode,
                    "The Hub rejected the agent identity.",
                    stoppingToken);
                delaySeconds = AuthenticationRetrySeconds;
            }
            catch (AgentApiException ex) when ((int?)ex.StatusCode == 426)
            {
                logger.LogError("The Hub rejected the agent version ({ErrorCode}); waiting for an update command", ex.ErrorCode);
                await healthPublisher.ReportFailureAsync(
                    AgentConnectionStates.UpdateRequired,
                    ex.ErrorCode,
                    "The agent version must be updated.",
                    stoppingToken);
                delaySeconds = AuthenticationRetrySeconds;
            }
            catch (AgentApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                delaySeconds = RetryAfterSeconds(ex);
                logger.LogWarning("The Hub rate-limited the heartbeat; retrying in {DelaySeconds} seconds", delaySeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                delaySeconds = CalculateBackoffSeconds(consecutiveFailures);
                logger.LogWarning(ex, "Heartbeat failed; retrying in {DelaySeconds} seconds", delaySeconds);
                await healthPublisher.ReportFailureAsync(
                    AgentConnectionStates.Degraded,
                    ex is AgentApiException apiException ? apiException.ErrorCode : ex.GetType().Name,
                    ex.Message,
                    stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }

    private async Task<AgentUpdateHeartbeat?> BuildUpdateHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await updateStore.LoadAsync(cancellationToken);
            if (state is null || state.Status == AgentUpdateStatuses.Succeeded)
            {
                return null;
            }

            // Hub memakai lifecycle yang lebih ringkas daripada state internal Agent.
            // Pending download dan verifikasi tetap dilaporkan sebagai proses download.
            var status = state.Status switch
            {
                AgentUpdateStatuses.PendingDownload => AgentUpdateStatuses.Downloading,
                AgentUpdateStatuses.Verifying => AgentUpdateStatuses.Downloading,
                AgentUpdateStatuses.Downloading => AgentUpdateStatuses.Downloading,
                AgentUpdateStatuses.Ready => AgentUpdateStatuses.Ready,
                AgentUpdateStatuses.WaitingForIdle => AgentUpdateStatuses.WaitingForIdle,
                AgentUpdateStatuses.Installing => AgentUpdateStatuses.Installing,
                AgentUpdateStatuses.Failed => AgentUpdateStatuses.Failed,
                _ => null,
            };

            return status is null
                ? null
                : new AgentUpdateHeartbeat(
                    state.CommandId,
                    state.TargetVersion,
                    status,
                    Math.Clamp(state.ProgressPercent, 0, 100),
                    state.Message,
                    state.UpdatedAt);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            logger.LogWarning(exception, "The local update state could not be added to heartbeat");
            return null;
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
                        logger.LogInformation("Starting {JobCount} claimed job(s)", started);
                    }
                }

                consecutiveFailures = 0;
            }
            catch (AgentApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized
                                               || (int?)ex.StatusCode == 426)
            {
                delaySeconds = AuthenticationRetrySeconds;
                logger.LogError("The Hub rejected the claim ({ErrorCode})", ex.ErrorCode);
            }
            catch (AgentApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                delaySeconds = RetryAfterSeconds(ex);
                logger.LogWarning("The Hub rate-limited the claim; retrying in {DelaySeconds} seconds", delaySeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                delaySeconds = CalculateBackoffSeconds(consecutiveFailures);
                logger.LogWarning(ex, "Claim failed; retrying in {DelaySeconds} seconds", delaySeconds);
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
                    AgentProduct.Version,
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
                logger.LogWarning(ex, "Outbox dispatch failed; retrying in 2 seconds");
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

                await healthPublisher.ReportAwaitingPairingAsync(stoppingToken);

                logger.LogWarning(
                    "The agent is not enrolled. Run Agent.Service.exe enroll --hub-url <URL> --pairing-code <CODE>");
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
                logger.LogCritical(ex, "The agent identity could not be read");
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
            logger.LogError(ex, "Failed to read the printer inventory");
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
            logger.LogError(ex, "Failed to read printers; claim was skipped");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static int NormalizeHeartbeatInterval(int? seconds) =>
        seconds is > 0 ? Math.Clamp(seconds.Value, 10, 300) : AgentRuntimeDefaults.HeartbeatIntervalSeconds;

    private void LogNormalizedPrintSettings(AgentConfig config)
    {
        foreach (var binding in config.Printers ?? [])
        {
            if (binding.Print?.NormalizationWarnings is not { Count: > 0 } warnings)
            {
                continue;
            }

            // Config cacat tidak boleh memutus heartbeat; warning tetap dicatat agar
            // operator dapat memperbaiki sumber datanya di Hub.
            logger.LogWarning(
                "Hub print settings for printer {PrinterName} were normalized: {Warnings}",
                binding.WindowsName,
                string.Join("; ", warnings));
        }
    }

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
