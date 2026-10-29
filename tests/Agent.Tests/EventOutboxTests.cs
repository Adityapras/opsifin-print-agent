using System.Text.Json;
using Agent.Hub;
using Microsoft.Data.Sqlite;

namespace Agent.Tests;

public class EventOutboxTests
{
    [Fact]
    public async Task Event_persisten_idempoten_dan_bisa_dijadwalkan_ulang()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "outbox.db");
        try
        {
            var outbox = new EventOutbox(databasePath);
            await outbox.InitializeAsync(CancellationToken.None);
            var jobId = Guid.CreateVersion7();
            var jobEvent = CreateEvent();

            Assert.True(await outbox.EnqueueAsync(jobId, jobEvent, CancellationToken.None));
            Assert.False(await outbox.EnqueueAsync(jobId, jobEvent, CancellationToken.None));
            Assert.Equal(1, await outbox.CountAsync(CancellationToken.None));

            // Instance baru membuktikan event sudah durable di file, bukan hanya tersimpan di memori.
            var reopened = new EventOutbox(databasePath);
            await reopened.InitializeAsync(CancellationToken.None);
            var due = Assert.Single(await reopened.GetDueAsync(
                DateTimeOffset.UtcNow.AddSeconds(1),
                10,
                CancellationToken.None));
            Assert.Equal(jobId, due.JobId);
            Assert.Equal(jobEvent.EventId, due.Event.EventId);
            Assert.Equal("spooled", due.Event.Type);

            var retryAt = DateTimeOffset.UtcNow.AddMinutes(2);
            await reopened.RescheduleAsync(jobEvent.EventId, retryAt, "network", CancellationToken.None);
            Assert.Empty(await reopened.GetDueAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None));

            due = Assert.Single(await reopened.GetDueAsync(
                retryAt.AddMilliseconds(1),
                10,
                CancellationToken.None));
            Assert.Equal(1, due.Attempts);
            Assert.Equal("network", due.LastError);

            await reopened.MarkDeliveredAsync([jobEvent.EventId], CancellationToken.None);
            Assert.Equal(0, await reopened.CountAsync(CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static JobEvent CreateEvent()
    {
        using var document = JsonDocument.Parse("""{"spooler_job_id":42}""");
        return new JobEvent(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "spooled",
            DateTimeOffset.UtcNow,
            document.RootElement.Clone());
    }
}
