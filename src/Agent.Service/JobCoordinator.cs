using System.Collections.Concurrent;
using Agent.Hub;
using Agent.Local;
using Agent.Runtime;

namespace Agent.Service;

public sealed class JobCoordinator(
    ILogger<JobCoordinator> logger,
    AgentHubClient hubClient,
    JobProcessor jobProcessor,
    TestPagePrinter testPagePrinter,
    PrintJobJournal journal,
    AgentHealthPublisher healthPublisher,
    TimeProvider timeProvider)
{
    private readonly object configGate = new();
    private readonly SemaphoreSlim claimGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveJob> activeByPrinter = new();
    private readonly ConcurrentDictionary<Guid, ActiveJob> activeByJob = new();
    private AgentConfig? config;
    private bool claimsSuspended;

    public bool HasConfiguration
    {
        get
        {
            lock (configGate)
            {
                return config is not null;
            }
        }
    }

    public int ClaimIntervalSeconds
    {
        get
        {
            lock (configGate)
            {
                return Math.Clamp(config?.ClaimIntervalS ?? 5, 1, 60);
            }
        }
    }

    public int ActivePrintCount => activeByPrinter.Count;

    public void UpdateConfiguration(AgentConfig newConfig)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        lock (configGate)
        {
            config = newConfig;
        }
    }

    public IReadOnlySet<string> GetConfiguredPrinterNames() => SnapshotBindings()
        .Select(binding => binding.WindowsName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<int> ClaimOnceAsync(
        DeviceIdentity identity,
        IReadOnlySet<string> visiblePrinterNames,
        CancellationToken stoppingToken)
    {
        await claimGate.WaitAsync(stoppingToken);
        try
        {
            var bindings = SnapshotBindings()
                .Where(binding => visiblePrinterNames.Contains(binding.WindowsName))
                .Where(binding => !activeByPrinter.ContainsKey(binding.PrinterId))
                .ToDictionary(binding => binding.PrinterId);
            if (bindings.Count == 0)
            {
                return 0;
            }

            var jobs = await hubClient.ClaimJobsAsync(
                identity.HubBaseAddress,
                identity.BearerToken,
                AgentProduct.Version,
                bindings.Keys.ToArray(),
                stoppingToken);
            var started = 0;
            foreach (var job in jobs)
            {
                if (!bindings.TryGetValue(job.PrinterId, out var binding))
                {
                    logger.LogError(
                        "The Hub returned job {JobId} for unrequested printer {PrinterId}",
                        job.JobId,
                        job.PrinterId);
                    continue;
                }

                if (job.LeaseUntil <= timeProvider.GetUtcNow())
                {
                    logger.LogWarning("Ignoring job {JobId} because its lease has expired", job.JobId);
                    continue;
                }

                if (job.Print.NormalizationWarnings is { Count: > 0 } warnings)
                {
                    logger.LogWarning(
                        "Hub print settings for job {JobId} were normalized: {Warnings}",
                        job.JobId,
                        string.Join("; ", warnings));
                }

                var active = new ActiveJob(job.JobId, job.PrinterId, stoppingToken);
                if (!activeByPrinter.TryAdd(job.PrinterId, active))
                {
                    active.Dispose();
                    logger.LogWarning(
                        "Printer {PrinterId} already has an active job; job {JobId} was not started",
                        job.PrinterId,
                        job.JobId);
                    continue;
                }

                if (!activeByJob.TryAdd(job.JobId, active))
                {
                    activeByPrinter.TryRemove(job.PrinterId, out _);
                    active.Dispose();
                    logger.LogWarning("Job {JobId} is already active and will not be started twice", job.JobId);
                    continue;
                }

                await healthPublisher.ReportJobClaimedAsync(
                    job.JobId,
                    binding.WindowsName,
                    job.Document.Pages,
                    job.Display?.InvoiceNo,
                    job.Display?.ItemType,
                    stoppingToken);
                active.Task = Task.Run(
                    () => RunJobAsync(job, binding.WindowsName, active),
                    CancellationToken.None);
                started++;
            }

            return started;
        }
        finally
        {
            claimGate.Release();
        }
    }

    public async Task<IDisposable> SuspendClaimsAsync(CancellationToken cancellationToken)
    {
        await claimGate.WaitAsync(cancellationToken);
        Volatile.Write(ref claimsSuspended, true);
        return new ClaimSuspension(this);
    }

    public void AbortJobs(IEnumerable<Guid> jobIds)
    {
        foreach (var jobId in jobIds)
        {
            if (activeByJob.TryGetValue(jobId, out var active))
            {
                logger.LogWarning("Stopping job {JobId} at the Hub's request", jobId);
                active.Cancellation.Cancel();
            }
        }
    }

    public void AbortAll()
    {
        foreach (var active in activeByJob.Values)
        {
            active.Cancellation.Cancel();
        }
    }

    public async Task<TestPageResult> PrintTestPageAsync(
        Guid commandId,
        Guid agentId,
        Guid printerId,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref claimsSuspended))
        {
            throw new InvalidOperationException("A software update is waiting for the current print activity to finish.");
        }

        var binding = SnapshotBindings().SingleOrDefault(item => item.PrinterId == printerId)
            ?? throw new InvalidOperationException("The command printer is not present in the agent configuration.");
        var active = new ActiveJob(commandId, printerId, cancellationToken);
        if (!activeByPrinter.TryAdd(printerId, active))
        {
            active.Dispose();
            throw new InvalidOperationException("The printer is processing another job.");
        }

        try
        {
            return await testPagePrinter.PrintAsync(
                commandId,
                agentId,
                binding.WindowsName,
                active.Cancellation.Token);
        }
        finally
        {
            activeByPrinter.TryRemove(printerId, out _);
            active.Dispose();
        }
    }

    public async Task WaitForActiveJobsAsync()
    {
        var tasks = activeByJob.Values
            .Select(active => active.Task)
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        await Task.WhenAll(tasks);
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + timeout;
        while (!activeByPrinter.IsEmpty)
        {
            if (timeProvider.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, cancellationToken);
        }

        return true;
    }

    private IReadOnlyList<PrinterBinding> SnapshotBindings()
    {
        lock (configGate)
        {
            return config?.Printers?.ToArray() ?? [];
        }
    }

    private async Task RunJobAsync(ClaimedJob job, string windowsPrinterName, ActiveJob active)
    {
        try
        {
            logger.LogInformation(
                "Processing job {JobId} for printer {PrinterName}",
                job.JobId,
                windowsPrinterName);
            var previousSubmission = await journal.LoadAsync(job.JobId, active.Cancellation.Token);
            var result = previousSubmission is null
                ? await jobProcessor.ProcessAsync(
                    job,
                    windowsPrinterName,
                    ReportProgressAsync,
                    active.Cancellation.Token)
                : await jobProcessor.ResumeAsync(
                    job,
                    previousSubmission,
                    ReportProgressAsync,
                    active.Cancellation.Token);
            logger.LogInformation(
                "Job {JobId} completed locally with outcome {Outcome} and spooler JobId {SpoolerJobId}",
                job.JobId,
                result.Outcome,
                result.SpoolerJobId);
        }
        catch (Exception ex)
        {
            // JobProcessor mengubah kegagalan kerja menjadi event durable. Catch ini menjaga
            // coordinator tetap hidup bila ada bug tak terduga di luar batas processor.
            logger.LogError(ex, "Job {JobId} stopped because of an unexpected failure", job.JobId);
        }
        finally
        {
            await healthPublisher.ReportJobFinishedAsync(job.JobId);
            activeByJob.TryRemove(job.JobId, out _);
            activeByPrinter.TryRemove(job.PrinterId, out _);
            active.Dispose();
        }

        Task ReportProgressAsync(JobProgressUpdate update, CancellationToken cancellationToken) =>
            ReportProgressAndLogAsync(update, cancellationToken);

        async Task ReportProgressAndLogAsync(
            JobProgressUpdate update,
            CancellationToken cancellationToken)
        {
            // Health snapshot menggerakkan dashboard, sedangkan log stage membuat operator
            // dapat melihat bahwa pipeline tetap maju selama verifikasi spooler yang panjang.
            logger.LogInformation(
                "Job {JobId} on printer {PrinterName} moved to stage {Stage}; spooler JobId {SpoolerJobId}",
                job.JobId,
                windowsPrinterName,
                update.Stage,
                update.SpoolerJobId);
            await healthPublisher.ReportJobProgressAsync(
                job.JobId,
                update.Stage,
                update.SpoolerJobId,
                cancellationToken);
        }
    }

    private sealed class ActiveJob(
        Guid jobId,
        Guid printerId,
        CancellationToken stoppingToken) : IDisposable
    {
        public Guid JobId { get; } = jobId;

        public Guid PrinterId { get; } = printerId;

        public CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        public Task? Task { get; set; }

        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class ClaimSuspension(JobCoordinator owner) : IDisposable
    {
        private JobCoordinator? currentOwner = owner;

        public void Dispose()
        {
            var releaseOwner = Interlocked.Exchange(ref currentOwner, null);
            if (releaseOwner is null)
            {
                return;
            }

            Volatile.Write(ref releaseOwner.claimsSuspended, false);
            releaseOwner.claimGate.Release();
        }
    }
}
