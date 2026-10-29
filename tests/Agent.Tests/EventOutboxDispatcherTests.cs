using System.Net;
using System.Text;
using Agent.Hub;
using Microsoft.Data.Sqlite;

namespace Agent.Tests;

public class EventOutboxDispatcherTests
{
    [Fact]
    public async Task Accepted_dan_duplicate_sama_sama_dihapus_dari_outbox()
    {
        var state = await CreateStateAsync();
        try
        {
            var first = CreateEvent("spooled");
            var second = CreateEvent("spooler_status");
            await state.Outbox.EnqueueAsync(state.JobId, first, CancellationToken.None);
            await state.Outbox.EnqueueAsync(state.JobId, second, CancellationToken.None);
            var client = new AgentHubClient(new HttpClient(new StubHandler(async request =>
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains(first.EventId.ToString("D"), body);
                Assert.Contains(second.EventId.ToString("D"), body);
                return JsonResponse(HttpStatusCode.OK, $$"""
                    {
                      "accepted":["{{first.EventId}}"],
                      "duplicates":["{{second.EventId}}"],
                      "job_state":"printing",
                      "lease_until":"2026-10-27T09:00:00Z",
                      "abort":false
                    }
                    """);
            })));
            var dispatcher = new EventOutboxDispatcher(state.Outbox, client);

            var result = await dispatcher.DispatchDueAsync(
                AgentHubClient.NormalizeBaseAddress("https://hub.example"),
                "agent.secret",
                "0.1.0",
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None);

            Assert.Equal(2, result.Delivered);
            Assert.Equal(0, result.Rescheduled);
            Assert.Equal(0, await state.Outbox.CountAsync(CancellationToken.None));
        }
        finally
        {
            Cleanup(state.Directory);
        }
    }

    [Fact]
    public async Task Lease_lost_menghentikan_retry_dan_menandai_abort()
    {
        var state = await CreateStateAsync();
        try
        {
            var jobEvent = CreateEvent("completed");
            await state.Outbox.EnqueueAsync(state.JobId, jobEvent, CancellationToken.None);
            var client = new AgentHubClient(new HttpClient(new StubHandler(_ => Task.FromResult(
                JsonResponse(
                    HttpStatusCode.Conflict,
                    """{"error":{"code":"lease_lost","message":"Lease pindah"}}""")))));
            var dispatcher = new EventOutboxDispatcher(state.Outbox, client);

            var result = await dispatcher.DispatchDueAsync(
                AgentHubClient.NormalizeBaseAddress("https://hub.example"),
                "agent.secret",
                "0.1.0",
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None);

            Assert.Contains(state.JobId, result.AbortedJobIds);
            Assert.Equal(0, await state.Outbox.CountAsync(CancellationToken.None));
        }
        finally
        {
            Cleanup(state.Directory);
        }
    }

    private static async Task<TestState> CreateStateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        var outbox = new EventOutbox(Path.Combine(directory, "outbox.db"));
        await outbox.InitializeAsync(CancellationToken.None);
        return new TestState(directory, Guid.CreateVersion7(), outbox);
    }

    private static JobEvent CreateEvent(string type) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        type,
        DateTimeOffset.UtcNow,
        null);

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static void Cleanup(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed record TestState(string Directory, Guid JobId, EventOutbox Outbox);

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
