using System.Diagnostics;
using System.Security.Cryptography;
using Agent.Hub;
using Agent.Local;

namespace Agent.Service;

public sealed record UpdateScheduleResult(bool Accepted, string Message);

public sealed class AgentUpdateCoordinator(
    ILogger<AgentUpdateCoordinator> logger,
    AuthenticatedUpdateDownloader updateDownloader,
    DeviceIdentityStore identityStore,
    AgentUpdateStore updateStore,
    JobCoordinator jobCoordinator,
    EventOutbox outbox,
    IHostApplicationLifetime applicationLifetime,
    TimeProvider timeProvider) : BackgroundService
{
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan OutboxTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim scheduleGate = new(1, 1);

    public async Task<UpdateScheduleResult> ScheduleAsync(
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryParseCommand(command, out var request, out var error))
        {
            return new UpdateScheduleResult(false, error);
        }

        if (!Version.TryParse(AgentProduct.Version, out var currentVersion)
            || !Version.TryParse(request.TargetVersion, out var targetVersion))
        {
            return new UpdateScheduleResult(false, "The current or target agent version is invalid.");
        }

        if (targetVersion <= currentVersion)
        {
            return new UpdateScheduleResult(
                false,
                $"Agent {request.TargetVersion} is not newer than the installed version {AgentProduct.Version}.");
        }

        await scheduleGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await updateStore.LoadAsync(cancellationToken);
            if (existing is not null
                && !AgentUpdateStatuses.IsTerminal(existing.Status)
                && existing.CommandId != command.Id)
            {
                return new UpdateScheduleResult(
                    false,
                    $"Update {existing.TargetVersion} is already {existing.Status.Replace('_', ' ')}.");
            }

            var now = timeProvider.GetUtcNow();
            await updateStore.SaveAsync(
                new AgentUpdateState(
                    command.Id,
                    AgentProduct.Version,
                    request.TargetVersion,
                    AgentUpdateStatuses.PendingDownload,
                    request.InstallPolicy,
                    request.PackageUrl.AbsoluteUri,
                    request.Sha256,
                    now,
                    now,
                    request.ReleaseNotes,
                    Message: "The update was accepted and will download in the background."),
                cancellationToken);
            updateStore.ClearInstallRequest();
            return new UpdateScheduleResult(
                true,
                request.InstallPolicy == AgentUpdatePolicies.Automatic
                    ? $"Update {request.TargetVersion} was scheduled for automatic installation when the Agent is idle."
                    : $"Update {request.TargetVersion} was accepted; the operator will be notified when it is ready.");
        }
        finally
        {
            scheduleGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var state = await updateStore.LoadAsync(stoppingToken);
                state = await RecoverInterruptedStateAsync(state, stoppingToken);
                if (state?.Status == AgentUpdateStatuses.PendingDownload
                    && DownloadRetryIsDue(state))
                {
                    await DownloadAndVerifyAsync(state, stoppingToken);
                }
                else if (state?.Status == AgentUpdateStatuses.Ready
                    && await ShouldInstallAsync(state, stoppingToken))
                {
                    await InstallWhenIdleAsync(state, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The update coordinator encountered an unexpected error");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken);
        }
    }

    private async Task<AgentUpdateState?> RecoverInterruptedStateAsync(
        AgentUpdateState? state,
        CancellationToken cancellationToken)
    {
        if (state is null)
        {
            return null;
        }

        AgentUpdateState? recovered = state.Status switch
        {
            AgentUpdateStatuses.Downloading or AgentUpdateStatuses.Verifying => state with
            {
                Status = AgentUpdateStatuses.PendingDownload,
                UpdatedAt = timeProvider.GetUtcNow(),
                Attempts = state.Attempts + 1,
                Message = "The previous download was interrupted and will resume with a clean retry.",
            },
            AgentUpdateStatuses.WaitingForIdle => state with
            {
                Status = AgentUpdateStatuses.Ready,
                UpdatedAt = timeProvider.GetUtcNow(),
                Attempts = state.Attempts + 1,
                Message = "The previous safe-drain attempt was interrupted. The verified package remains ready.",
            },
            AgentUpdateStatuses.Installing
                when string.Equals(AgentProduct.Version, state.TargetVersion, StringComparison.OrdinalIgnoreCase) => state with
                {
                    Status = AgentUpdateStatuses.Succeeded,
                    UpdatedAt = timeProvider.GetUtcNow(),
                    ProgressPercent = 100,
                    Message = $"Agent {state.TargetVersion} is installed and running.",
                },
            AgentUpdateStatuses.Installing
                when timeProvider.GetUtcNow() - state.UpdatedAt > TimeSpan.FromMinutes(3) => state with
                {
                    Status = AgentUpdateStatuses.Ready,
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Attempts = state.Attempts + 1,
                    Message = "The previous installer did not complete. Printing resumed and the verified package can be retried.",
                },
            _ => null,
        };

        if (recovered is null)
        {
            return state;
        }

        updateStore.ClearInstallRequest();
        await updateStore.SaveAsync(recovered, cancellationToken);
        logger.LogWarning(
            "Recovered interrupted update {TargetVersion} from state {OldStatus} to {NewStatus}",
            state.TargetVersion,
            state.Status,
            recovered.Status);
        return recovered;
    }

    private async Task DownloadAndVerifyAsync(
        AgentUpdateState state,
        CancellationToken cancellationToken)
    {
        var updateDirectory = Path.Combine(AgentStatePaths.UpdateDirectory, state.CommandId.ToString("N"));
        Directory.CreateDirectory(updateDirectory);
        var packagePath = Path.Combine(updateDirectory, $"OpsifinPrintAgent-{state.TargetVersion}.msi");
        var temporaryPath = $"{packagePath}.download";
        await SaveProgressAsync(
            state,
            AgentUpdateStatuses.Downloading,
            0,
            "Downloading the update package…",
            cancellationToken);

        try
        {
            var identity = await identityStore.LoadAsync(cancellationToken)
                ?? throw new InvalidOperationException("The Agent identity is unavailable for the authenticated update download.");
            using var response = await updateDownloader.DownloadAsync(
                identity.HubBaseAddress,
                new Uri(state.PackageUrl, UriKind.Absolute),
                identity.BearerToken,
                AgentProduct.Version,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
            {
                throw new InvalidDataException("The update package exceeds the 512 MB safety limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long received = 0;
            var lastProgress = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    break;
                }

                received += count;
                if (received > MaximumPackageBytes)
                {
                    throw new InvalidDataException("The update package exceeds the 512 MB safety limit.");
                }

                hasher.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                var contentLength = response.Content.Headers.ContentLength;
                var progress = contentLength is > 0
                    ? (int)Math.Clamp(received * 90 / contentLength.Value, 0, 90)
                    : 0;
                if (progress >= lastProgress + 5)
                {
                    lastProgress = progress;
                    await SaveProgressAsync(
                        state,
                        AgentUpdateStatuses.Downloading,
                        progress,
                        $"Downloaded {received / 1024 / 1024} MB…",
                        cancellationToken);
                }
            }

            await destination.FlushAsync(cancellationToken);
            var actualHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(state.Sha256)))
            {
                throw new InvalidDataException("The update package SHA-256 does not match the value supplied by Print Hub.");
            }

            AtomicFileCommit.CloseAndMove(destination, temporaryPath, packagePath);
            await SaveProgressAsync(
                state,
                AgentUpdateStatuses.Verifying,
                95,
                "Checking update package integrity…",
                cancellationToken);

            var ready = state with
            {
                Status = AgentUpdateStatuses.Ready,
                UpdatedAt = timeProvider.GetUtcNow(),
                PackagePath = packagePath,
                ProgressPercent = 100,
                Attempts = 0,
                Message = state.InstallPolicy == AgentUpdatePolicies.Automatic
                    ? "The update is ready and will install when printing is idle."
                    : "The update is ready. Select Install update when convenient.",
            };
            await updateStore.SaveAsync(ready, cancellationToken);
            logger.LogInformation("Update {TargetVersion} was downloaded and verified", state.TargetVersion);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await RecordDownloadFailureAsync(state, temporaryPath, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordDownloadFailureAsync(state, temporaryPath, ex);
        }
    }

    private async Task<bool> ShouldInstallAsync(
        AgentUpdateState state,
        CancellationToken cancellationToken)
    {
        if (await updateStore.IsInstallRequestedAsync(state.CommandId, cancellationToken))
        {
            return true;
        }

        return state.InstallPolicy == AgentUpdatePolicies.Automatic
            && (state.Attempts == 0 || timeProvider.GetUtcNow() - state.UpdatedAt >= RetryDelay);
    }

    private bool DownloadRetryIsDue(AgentUpdateState state)
    {
        if (state.Attempts == 0)
        {
            return true;
        }

        var delaySeconds = Math.Min(300, 15 * (1 << Math.Min(state.Attempts - 1, 4)));
        return timeProvider.GetUtcNow() - state.UpdatedAt >= TimeSpan.FromSeconds(delaySeconds);
    }

    private static bool IsTransientDownloadFailure(Exception exception) =>
        exception is HttpRequestException or IOException or TimeoutException or OperationCanceledException;

    private async Task RecordDownloadFailureAsync(
        AgentUpdateState state,
        string temporaryPath,
        Exception exception)
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var attempts = state.Attempts + 1;
        var retry = IsTransientDownloadFailure(exception) && attempts < 3;
        await updateStore.SaveAsync(
            state with
            {
                Status = retry
                    ? AgentUpdateStatuses.PendingDownload
                    : AgentUpdateStatuses.Failed,
                UpdatedAt = timeProvider.GetUtcNow(),
                Attempts = attempts,
                Message = retry
                    ? $"Download attempt {attempts} failed and will retry automatically: {exception.Message}"
                    : $"The update was not installed: {exception.Message}",
            },
            CancellationToken.None);
        logger.LogError(exception, "Update {TargetVersion} could not be staged", state.TargetVersion);
    }

    private async Task InstallWhenIdleAsync(
        AgentUpdateState state,
        CancellationToken cancellationToken)
    {
        using var claimSuspension = await jobCoordinator.SuspendClaimsAsync(cancellationToken);
        await updateStore.SaveAsync(
            state with
            {
                Status = AgentUpdateStatuses.WaitingForIdle,
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = jobCoordinator.ActivePrintCount == 0
                    ? "No active printing. Flushing final events to Print Hub…"
                    : $"Waiting for {jobCoordinator.ActivePrintCount} active print operation(s) to finish…",
            },
            cancellationToken);

        if (!await jobCoordinator.WaitForIdleAsync(DrainTimeout, cancellationToken))
        {
            await PostponeAsync(
                state,
                "The update was postponed because print activity did not finish within 15 minutes.",
                cancellationToken);
            return;
        }

        if (!await WaitForOutboxAsync(cancellationToken))
        {
            await PostponeAsync(
                state,
                "The update was postponed because pending print events could not be confirmed by Print Hub.",
                cancellationToken);
            return;
        }

        var packagePath = state.PackagePath;
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            await updateStore.SaveAsync(
                state with
                {
                    Status = AgentUpdateStatuses.Failed,
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Message = "The downloaded update package is no longer available.",
                },
                cancellationToken);
            return;
        }

        var installedUpdater = Path.Combine(AppContext.BaseDirectory, "Opsifin.Agent.Updater.exe");
        if (!File.Exists(installedUpdater))
        {
            await updateStore.SaveAsync(
                state with
                {
                    Status = AgentUpdateStatuses.Failed,
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Message = "The installed updater helper is missing.",
                },
                cancellationToken);
            return;
        }

        var updateDirectory = Path.GetDirectoryName(packagePath)
            ?? throw new InvalidOperationException("The staged package directory is unavailable.");
        var stagedUpdater = Path.Combine(updateDirectory, "Opsifin.Agent.Updater.exe");
        File.Copy(installedUpdater, stagedUpdater, true);
        var logPath = Path.Combine(updateDirectory, "install.log");
        var installing = state with
        {
            Status = AgentUpdateStatuses.Installing,
            UpdatedAt = timeProvider.GetUtcNow(),
            Message = "Printing is idle. Installing the update and restarting the Agent…",
        };
        await updateStore.SaveAsync(installing, cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = stagedUpdater,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("apply");
        AddArgument(startInfo, "package", packagePath);
        AddArgument(startInfo, "target-version", state.TargetVersion);
        AddArgument(startInfo, "service-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(startInfo, "state", updateStore.StatePath);
        AddArgument(startInfo, "request", updateStore.InstallRequestPath);
        AddArgument(startInfo, "health", AgentStatePaths.HealthFile);
        AddArgument(startInfo, "log", logPath);
        AddArgument(startInfo, "service", AgentProduct.ServiceName);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The updater helper could not be started.");

        logger.LogInformation("Update {TargetVersion} is taking over; stopping the Agent service", state.TargetVersion);
        applicationLifetime.StopApplication();
    }

    private async Task<bool> WaitForOutboxAsync(CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + OutboxTimeout;
        while (await outbox.CountAsync(cancellationToken) > 0)
        {
            if (timeProvider.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
        }

        return true;
    }

    private async Task PostponeAsync(
        AgentUpdateState state,
        string message,
        CancellationToken cancellationToken)
    {
        updateStore.ClearInstallRequest();
        await updateStore.SaveAsync(
            state with
            {
                Status = AgentUpdateStatuses.Ready,
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = message,
                Attempts = state.Attempts + 1,
            },
            cancellationToken);
        logger.LogWarning("Update {TargetVersion} postponed: {Reason}", state.TargetVersion, message);
    }

    private Task SaveProgressAsync(
        AgentUpdateState state,
        string status,
        int progress,
        string message,
        CancellationToken cancellationToken) => updateStore.SaveAsync(
            state with
            {
                Status = status,
                UpdatedAt = timeProvider.GetUtcNow(),
                ProgressPercent = progress,
                Message = message,
            },
            cancellationToken);

    private static bool TryParseCommand(
        AgentCommand command,
        out ParsedUpdateRequest request,
        out string error)
    {
        request = default!;
        error = string.Empty;
        if (command.Args is not { ValueKind: System.Text.Json.JsonValueKind.Object } args
            || !TryString(args, "version", out var version)
            || !TryString(args, "url", out var urlValue)
            || !TryString(args, "sha256", out var sha256))
        {
            error = "The update command requires version, url, and sha256.";
            return false;
        }

        if (!Uri.TryCreate(urlValue, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps
                && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)))
        {
            error = "The update URL must use HTTPS; HTTP is allowed only for localhost development.";
            return false;
        }

        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            error = "The update SHA-256 must contain exactly 64 hexadecimal characters.";
            return false;
        }

        var installPolicy = TryString(args, "install_policy", out var policy)
            ? policy.ToLowerInvariant()
            : AgentUpdatePolicies.Manual;
        if (installPolicy is not AgentUpdatePolicies.Manual and not AgentUpdatePolicies.Automatic)
        {
            error = "install_policy must be 'manual' or 'automatic'.";
            return false;
        }

        _ = TryString(args, "release_notes", out var releaseNotes);
        request = new ParsedUpdateRequest(
            version,
            url,
            sha256.ToLowerInvariant(),
            installPolicy,
            string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes[..Math.Min(2000, releaseNotes.Length)]);
        return true;
    }

    private static bool TryString(
        System.Text.Json.JsonElement args,
        string name,
        out string value)
    {
        value = string.Empty;
        return args.TryGetProperty(name, out var property)
            && property.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add($"--{name}");
        startInfo.ArgumentList.Add(value);
    }

    private sealed record ParsedUpdateRequest(
        string TargetVersion,
        Uri PackageUrl,
        string Sha256,
        string InstallPolicy,
        string? ReleaseNotes);
}
