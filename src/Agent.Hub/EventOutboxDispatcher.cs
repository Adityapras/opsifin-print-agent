using System.Net;

namespace Agent.Hub;

public sealed record OutboxDispatchResult(
    int Delivered,
    int Rescheduled,
    IReadOnlySet<Guid> AbortedJobIds);

public sealed class EventOutboxDispatcher(
    EventOutbox outbox,
    AgentHubClient hubClient)
{
    public async Task<OutboxDispatchResult> DispatchDueAsync(
        Uri baseAddress,
        string bearerToken,
        string agentVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = await outbox.GetDueAsync(now, 100, cancellationToken);
        if (pending.Count == 0)
        {
            return new OutboxDispatchResult(0, 0, new HashSet<Guid>());
        }

        var delivered = 0;
        var rescheduled = 0;
        var abortedJobs = new HashSet<Guid>();
        foreach (var group in pending.GroupBy(item => item.JobId))
        {
            var batch = group.ToArray();
            try
            {
                var response = await hubClient.SendJobEventsAsync(
                    baseAddress,
                    bearerToken,
                    agentVersion,
                    group.Key,
                    batch.Select(item => item.Event).ToArray(),
                    cancellationToken);
                var batchIds = batch.Select(item => item.Event.EventId).ToHashSet();
                var acknowledged = response.Accepted
                    .Concat(response.Duplicates)
                    .Where(batchIds.Contains)
                    .ToHashSet();
                if (acknowledged.Count > 0)
                {
                    await outbox.MarkDeliveredAsync(acknowledged, cancellationToken);
                    delivered += acknowledged.Count;
                }

                foreach (var item in batch.Where(item => !acknowledged.Contains(item.Event.EventId)))
                {
                    await RescheduleAsync(item, now, "Hub tidak mengakui event.", cancellationToken);
                    rescheduled++;
                }

                if (response.Abort)
                {
                    abortedJobs.Add(group.Key);
                }
            }
            catch (AgentApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Lease yang sudah pindah tidak akan menerima event lama pada retry berikutnya.
                // Hapus dari outbox dan teruskan sinyal abort agar job coordinator menghentikan
                // pekerjaan fisik tanpa berisiko mencetak ulang.
                await outbox.MarkDeliveredAsync(
                    batch.Select(item => item.Event.EventId).ToArray(),
                    cancellationToken);
                delivered += batch.Length;
                abortedJobs.Add(group.Key);
            }
            catch (AgentApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized
                                               || (int?)ex.StatusCode == 426)
            {
                foreach (var item in batch)
                {
                    await outbox.RescheduleAsync(
                        item.Event.EventId,
                        now.AddMinutes(5),
                        ex.Message,
                        cancellationToken);
                    rescheduled++;
                }
            }
            catch (AgentApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAt = now.Add(ex.RetryAfter ?? TimeSpan.FromSeconds(30));
                foreach (var item in batch)
                {
                    await outbox.RescheduleAsync(
                        item.Event.EventId,
                        retryAt,
                        ex.Message,
                        cancellationToken);
                    rescheduled++;
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                foreach (var item in batch)
                {
                    await RescheduleAsync(item, now, ex.Message, cancellationToken);
                    rescheduled++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var item in batch)
                {
                    await RescheduleAsync(item, now, ex.Message, cancellationToken);
                    rescheduled++;
                }
            }
        }

        return new OutboxDispatchResult(delivered, rescheduled, abortedJobs);
    }

    private async Task RescheduleAsync(
        PendingOutboxEvent item,
        DateTimeOffset now,
        string error,
        CancellationToken cancellationToken)
    {
        var nextAttemptAt = now.AddSeconds(CalculateBackoffSeconds(item.Attempts + 1));
        await outbox.RescheduleAsync(item.Event.EventId, nextAttemptAt, error, cancellationToken);
    }

    private static int CalculateBackoffSeconds(int attempt)
    {
        var exponent = Math.Min(Math.Max(attempt - 1, 0), 7);
        var baseDelay = Math.Min(300, 2 << exponent);
        var jitter = Random.Shared.Next(0, Math.Max(1, baseDelay / 4));
        return baseDelay + jitter;
    }
}
