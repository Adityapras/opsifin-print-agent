using System.Collections.Concurrent;
using Agent.Printing;

namespace Agent.Runtime;

/// <summary>Simulator deterministik untuk menguji pipeline Hub tanpa menyentuh printer Windows.</summary>
public sealed class SimulatedPrintSystem : IPrintEngine, ISpoolerStatusSource, ISpoolerJobController
{
    private readonly ConcurrentDictionary<uint, SimulatedJob> jobs = new();
    private int nextJobId = 1000;

    public ValueTask<PrintSubmission> SubmitAsync(
        PrintRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.DocumentPath))
        {
            throw new FileNotFoundException("The simulated document was not found.", request.DocumentPath);
        }

        var pages = checked((request.ExpectedPageCount ?? 1) * request.Copies);
        var jobId = (uint)Interlocked.Increment(ref nextJobId);
        jobs[jobId] = new SimulatedJob(pages);
        return ValueTask.FromResult(new PrintSubmission(jobId, pages));
    }

    public ValueTask<SpoolerObservation> ReadAsync(
        string printerName,
        uint spoolerJobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!jobs.TryGetValue(spoolerJobId, out var job))
        {
            return ValueTask.FromResult(new SpoolerObservation(false, 0, 0, 0, 0, 0));
        }

        var observationNumber = Interlocked.Increment(ref job.ObservationCount);
        if (observationNumber <= 2)
        {
            const uint jobStatusPrinting = 0x00000010;
            return ValueTask.FromResult(new SpoolerObservation(
                true,
                jobStatusPrinting,
                0,
                0,
                observationNumber == 1 ? 0u : (uint)job.Pages,
                (uint)job.Pages,
                "SIMULATED_PRINTING"));
        }

        return ValueTask.FromResult(new SpoolerObservation(
            false,
            0,
            0,
            0,
            (uint)job.Pages,
            (uint)job.Pages));
    }

    public ValueTask CancelAsync(
        string printerName,
        uint spoolerJobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        jobs.TryRemove(spoolerJobId, out _);
        return ValueTask.CompletedTask;
    }

    private sealed class SimulatedJob(int pages)
    {
        public int Pages { get; } = pages;

        public int ObservationCount;
    }
}
