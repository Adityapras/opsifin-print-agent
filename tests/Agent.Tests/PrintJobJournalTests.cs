using Agent.Runtime;

namespace Agent.Tests;

public sealed class PrintJobJournalTests
{
    [Fact]
    public async Task Submission_spooler_bisa_dibaca_setelah_instance_dibuka_ulang()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var jobId = Guid.CreateVersion7();
            var record = new ActivePrintRecord(
                jobId,
                "Printer A",
                42,
                3,
                DateTimeOffset.UtcNow);
            var journal = new PrintJobJournal(directory);
            await journal.SaveAsync(record, CancellationToken.None);

            var reopened = new PrintJobJournal(directory);
            var loaded = await reopened.LoadAsync(jobId, CancellationToken.None);

            Assert.Equal(record, loaded);
            reopened.Remove(jobId);
            Assert.Null(await reopened.LoadAsync(jobId, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void Tracker_pemulihan_boleh_memverifikasi_job_yang_sudah_pernah_terlihat()
    {
        var tracker = new Agent.Printing.SpoolerCompletionTracker(
            TimeSpan.Zero,
            jobWasPreviouslyObserved: true);

        var result = tracker.Observe(
            new Agent.Printing.SpoolerObservation(false, 0, 0, 0, 1, 1),
            DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal(Agent.Printing.SpoolerMonitorOutcome.Completed, result.Outcome);
    }
}
