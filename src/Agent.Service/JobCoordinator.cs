using System.Collections.Concurrent;
using Agent.Hub;
using Agent.Runtime;

namespace Agent.Service;

public sealed class JobCoordinator(
    ILogger<JobCoordinator> logger,
    AgentHubClient hubClient,
    JobProcessor jobProcessor,
    TestPagePrinter testPagePrinter,
    PrintJobJournal journal,
    TimeProvider timeProvider)
{
    private readonly object configGate = new();
    private readonly ConcurrentDictionary<Guid, ActiveJob> activeByPrinter = new();
    private readonly ConcurrentDictionary<Guid, ActiveJob> activeByJob = new();
    private AgentConfig? config;

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
            AgentMetadata.Version,
            bindings.Keys.ToArray(),
            stoppingToken);
        var started = 0;
        foreach (var job in jobs)
        {
            if (!bindings.TryGetValue(job.PrinterId, out var binding))
            {
                logger.LogError(
                    "Hub mengembalikan job {JobId} untuk printer {PrinterId} yang tidak diminta",
                    job.JobId,
                    job.PrinterId);
                continue;
            }

            if (job.LeaseUntil <= timeProvider.GetUtcNow())
            {
                logger.LogWarning("Mengabaikan job {JobId} karena lease sudah kedaluwarsa", job.JobId);
                continue;
            }

            var active = new ActiveJob(job.JobId, job.PrinterId, stoppingToken);
            if (!activeByPrinter.TryAdd(job.PrinterId, active))
            {
                active.Dispose();
                logger.LogWarning(
                    "Printer {PrinterId} sudah memiliki job aktif; job {JobId} tidak dimulai",
                    job.PrinterId,
                    job.JobId);
                continue;
            }

            if (!activeByJob.TryAdd(job.JobId, active))
            {
                activeByPrinter.TryRemove(job.PrinterId, out _);
                active.Dispose();
                logger.LogWarning("Job {JobId} sudah aktif dan tidak dijalankan dua kali", job.JobId);
                continue;
            }

            active.Task = Task.Run(
                () => RunJobAsync(job, binding.WindowsName, active),
                CancellationToken.None);
            started++;
        }

        return started;
    }

    public void AbortJobs(IEnumerable<Guid> jobIds)
    {
        foreach (var jobId in jobIds)
        {
            if (activeByJob.TryGetValue(jobId, out var active))
            {
                logger.LogWarning("Menghentikan job {JobId} atas perintah Hub", jobId);
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
        var binding = SnapshotBindings().SingleOrDefault(item => item.PrinterId == printerId)
            ?? throw new InvalidOperationException("Printer command tidak ada di konfigurasi agent.");
        var active = new ActiveJob(commandId, printerId, cancellationToken);
        if (!activeByPrinter.TryAdd(printerId, active))
        {
            active.Dispose();
            throw new InvalidOperationException("Printer sedang memproses job lain.");
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

    private IReadOnlyList<PrinterBinding> SnapshotBindings()
    {
        lock (configGate)
        {
            return config?.Printers.ToArray() ?? [];
        }
    }

    private async Task RunJobAsync(ClaimedJob job, string windowsPrinterName, ActiveJob active)
    {
        try
        {
            logger.LogInformation(
                "Memproses job {JobId} untuk printer {PrinterName}",
                job.JobId,
                windowsPrinterName);
            var previousSubmission = await journal.LoadAsync(job.JobId, active.Cancellation.Token);
            var result = previousSubmission is null
                ? await jobProcessor.ProcessAsync(job, windowsPrinterName, active.Cancellation.Token)
                : await jobProcessor.ResumeAsync(job, previousSubmission, active.Cancellation.Token);
            logger.LogInformation(
                "Job {JobId} selesai lokal dengan hasil {Outcome} dan JobId spooler {SpoolerJobId}",
                job.JobId,
                result.Outcome,
                result.SpoolerJobId);
        }
        catch (Exception ex)
        {
            // JobProcessor mengubah kegagalan kerja menjadi event durable. Catch ini menjaga
            // coordinator tetap hidup bila ada bug tak terduga di luar batas processor.
            logger.LogError(ex, "Job {JobId} berhenti karena kegagalan tak terduga", job.JobId);
        }
        finally
        {
            activeByJob.TryRemove(job.JobId, out _);
            activeByPrinter.TryRemove(job.PrinterId, out _);
            active.Dispose();
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
}
