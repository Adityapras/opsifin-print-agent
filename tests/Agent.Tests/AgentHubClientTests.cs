using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Agent.Hub;

namespace Agent.Tests;

public class AgentHubClientTests
{
    [Fact]
    public void Menormalkan_url_hub_ke_agent_v1()
    {
        Assert.Equal(
            "https://hub.example/agent/v1/",
            AgentHubClient.NormalizeBaseAddress("https://hub.example").AbsoluteUri);
        Assert.Equal(
            "https://hub.example/agent/v1/",
            AgentHubClient.NormalizeBaseAddress("https://hub.example/agent/v1/").AbsoluteUri);
    }

    [Fact]
    public void Menolak_http_non_loopback()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => AgentHubClient.NormalizeBaseAddress("http://hub.example"));

        Assert.Contains("HTTPS", exception.Message);
    }

    [Fact]
    public void Model_enrollment_tidak_mencetak_secret()
    {
        var request = new EnrollRequest("PAIR1234", "pc", "Windows", "0.1.0", "machine");
        var response = new EnrollResponse(Guid.NewGuid(), "device-secret", Guid.NewGuid(), Guid.NewGuid(), new());

        Assert.DoesNotContain("PAIR1234", request.ToString());
        Assert.DoesNotContain("device-secret", response.ToString());
    }

    [Fact]
    public async Task Enroll_memakai_nama_field_kontrak()
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("https://hub.example/agent/v1/enroll", request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            var json = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"pairing_code\":\"ABCDEFGH\"", json);
            Assert.Contains("\"machine_id\":\"machine-1\"", json);

            return JsonResponse(HttpStatusCode.Created, """
                {
                  "agent_id":"019a2b15-0000-7000-8000-000000000001",
                  "device_secret":"secret",
                  "tenant_id":"019a2b15-0000-7000-8000-000000000002",
                  "site_id":"019a2b15-0000-7000-8000-000000000003",
                  "config":{}
                }
                """);
        });
        var client = new AgentHubClient(new HttpClient(handler));

        var result = await client.EnrollAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            new EnrollRequest("ABCDEFGH", "pc-1", "Windows", "0.1.0", "machine-1"),
            CancellationToken.None);

        Assert.Equal("secret", result.DeviceSecret);
        Assert.NotNull(result.Config);
    }

    [Fact]
    public async Task Heartbeat_mengirim_token_dan_versi()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("agent.secret", request.Headers.Authorization?.Parameter);
            Assert.Equal("0.1.0", Assert.Single(request.Headers.GetValues("X-Agent-Version")));
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, """
                {"server_time":"2026-10-27T08:00:00Z","config_version":1}
                """));
        });
        var client = new AgentHubClient(new HttpClient(handler));

        var response = await client.HeartbeatAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.1.0",
            new HeartbeatRequest("0.1.0", 1, DateTimeOffset.UtcNow, 0, [], []),
            CancellationToken.None);

        Assert.Equal(1, response.ConfigVersion);
    }

    [Fact]
    public async Task Heartbeat_melaporkan_lifecycle_update_dengan_nama_field_kontrak()
    {
        var commandId = Guid.CreateVersion7();
        var updatedAt = DateTimeOffset.Parse("2026-11-10T08:30:00Z");
        var handler = new StubHandler(async request =>
        {
            var json = await request.Content!.ReadAsStringAsync();
            Assert.Contains($"\"command_id\":\"{commandId}\"", json);
            Assert.Contains("\"target_version\":\"0.3.10\"", json);
            Assert.Contains("\"status\":\"downloading\"", json);
            Assert.Contains("\"progress_percent\":42", json);
            return JsonResponse(HttpStatusCode.OK, """
                {"server_time":"2026-11-10T08:30:00Z","config_version":1}
                """);
        });
        var client = new AgentHubClient(new HttpClient(handler));

        await client.HeartbeatAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.3.8",
            new HeartbeatRequest(
                "0.3.8",
                1,
                updatedAt,
                1,
                [],
                [],
                new AgentUpdateHeartbeat(
                    commandId,
                    "0.3.10",
                    "downloading",
                    42,
                    "Downloading update package.",
                    updatedAt)),
            CancellationToken.None);
    }

    [Fact]
    public async Task Heartbeat_menormalkan_print_settings_null_tanpa_memutus_koneksi()
    {
        var printerId = Guid.CreateVersion7();
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, $$"""
            {
              "server_time":"2026-11-05T08:00:00Z",
              "config_version":9,
              "config":{
                "claim_interval_s":5,
                "heartbeat_interval_s":30,
                "source_poll_interval_s":45,
                "printers":[{
                  "printer_id":"{{printerId}}",
                  "windows_name":"Fax",
                  "print":{
                    "engine":"pdfium",
                    "copies":null,
                    "duplex":false,
                    "tray":null,
                    "scale":null
                  }
                }]
              }
            }
            """)));
        var client = new AgentHubClient(new HttpClient(handler));

        var response = await client.HeartbeatAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.3.1",
            new HeartbeatRequest("0.3.1", 1, DateTimeOffset.UtcNow, 8, [], []),
            CancellationToken.None);

        Assert.NotNull(response.Config);
        Assert.Equal(45, response.Config.SourcePollIntervalS);
        var print = Assert.Single(response.Config.Printers).Print;
        Assert.NotNull(print);
        Assert.Equal(1, print.Copies);
        Assert.Equal("noscale", print.Scale);
        Assert.Contains(print.NormalizationWarnings, warning => warning.StartsWith("copies", StringComparison.Ordinal));
        Assert.Contains(print.NormalizationWarnings, warning => warning.StartsWith("scale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Error_hub_mempertahankan_status_dan_kode()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.Unauthorized,
            """{"error":{"code":"invalid_token","message":"Token ditolak"}}""")));
        var client = new AgentHubClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<AgentApiException>(() => client.HeartbeatAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.1.0",
            new HeartbeatRequest("0.1.0", 1, DateTimeOffset.UtcNow, 0, [], []),
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("invalid_token", exception.ErrorCode);
    }

    [Fact]
    public async Task Rate_limit_membawa_retry_after()
    {
        var handler = new StubHandler(_ =>
        {
            var response = JsonResponse(
                HttpStatusCode.TooManyRequests,
                """{"error":{"code":"rate_limited","message":"Terlalu cepat"}}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return Task.FromResult(response);
        });
        var client = new AgentHubClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<AgentApiException>(() => client.HeartbeatAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.1.0",
            new HeartbeatRequest("0.1.0", 1, DateTimeOffset.UtcNow, 0, [], []),
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(45), exception.RetryAfter);
    }

    [Fact]
    public async Task Claim_204_berarti_tidak_ada_job()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://hub.example/agent/v1/jobs/claim", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var client = new AgentHubClient(new HttpClient(handler));

        var jobs = await client.ClaimJobsAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.1.0",
            [Guid.CreateVersion7()],
            CancellationToken.None);

        Assert.Empty(jobs);
    }

    [Fact]
    public async Task Claim_membaca_job_lengkap_sesuai_kontrak()
    {
        var jobId = Guid.CreateVersion7();
        var attemptId = Guid.CreateVersion7();
        var printerId = Guid.CreateVersion7();
        var handler = new StubHandler(async request =>
        {
            var json = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"printer_ids\"", json);
            return JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "jobs":[{
                    "job_id":"{{jobId}}",
                    "attempt_id":"{{attemptId}}",
                    "printer_id":"{{printerId}}",
                    "lease_until":"2026-10-27T09:00:00Z",
                    "display":{
                      "invoice_no":"INV-2026-0001",
                      "item_type":"INVOICE",
                      "received_at":"2026-10-27T08:55:00Z"
                    },
                    "document":{
                      "url":"https://hub.example/agent/v1/documents/doc?signature=x&expires=1",
                      "sha256":"{{new string('a', 64)}}",
                      "pages":2,
                      "size_bytes":100
                    },
                    "print":{"engine":"pdfium","copies":1,"duplex":false,"tray":null,"scale":"noscale"}
                  }]
                }
                """);
        });
        var client = new AgentHubClient(new HttpClient(handler));

        var job = Assert.Single(await client.ClaimJobsAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.1.0",
            [printerId],
            CancellationToken.None));

        Assert.Equal(jobId, job.JobId);
        Assert.Equal(attemptId, job.AttemptId);
        Assert.Equal(2, job.Document.Pages);
        Assert.Equal("pdfium", job.Print.Engine);
        Assert.Equal("INV-2026-0001", job.Display?.InvoiceNo);
        Assert.Equal("INVOICE", job.Display?.ItemType);
    }

    [Fact]
    public async Task Claim_dari_hub_lama_tetap_berfungsi_tanpa_display_metadata()
    {
        var printerId = Guid.CreateVersion7();
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, $$"""
            {
              "jobs":[{
                "job_id":"{{Guid.CreateVersion7()}}",
                "attempt_id":"{{Guid.CreateVersion7()}}",
                "printer_id":"{{printerId}}",
                "lease_until":"2026-11-05T10:00:00Z",
                "document":{
                  "url":"https://hub.example/agent/v1/documents/doc?signature=x&expires=1",
                  "sha256":"{{new string('b', 64)}}",
                  "pages":1,
                  "size_bytes":10
                },
                "print":{}
              }]
            }
            """)));
        var client = new AgentHubClient(new HttpClient(handler));

        var job = Assert.Single(await client.ClaimJobsAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.3.1",
            [printerId],
            CancellationToken.None));

        Assert.Null(job.Display);
        Assert.Equal(1, job.Print.Copies);
        Assert.Equal("noscale", job.Print.Scale);
    }

    [Fact]
    public async Task Event_job_dikirim_ke_path_job_dan_ack_dibaca()
    {
        var jobId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var handler = new StubHandler(async request =>
        {
            Assert.Equal($"https://hub.example/agent/v1/jobs/{jobId:D}/events", request.RequestUri?.AbsoluteUri);
            var json = await request.Content!.ReadAsStringAsync();
            Assert.Contains($"\"event_id\":\"{eventId:D}\"", json);
            return JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "accepted":["{{eventId}}"],
                  "duplicates":[],
                  "job_state":"printing",
                  "lease_until":"2026-10-27T09:00:00Z",
                  "abort":false
                }
                """);
        });
        var client = new AgentHubClient(new HttpClient(handler));
        var jobEvent = new JobEvent(
            eventId,
            Guid.CreateVersion7(),
            "spooled",
            DateTimeOffset.UtcNow,
            null);

        var response = await client.SendJobEventsAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.1.0",
            jobId,
            [jobEvent],
            CancellationToken.None);

        Assert.Equal(eventId, Assert.Single(response.Accepted));
        Assert.False(response.Abort);
    }

    [Fact]
    public async Task Hasil_command_dikirim_sebagai_multipart_terautentikasi()
    {
        var commandId = Guid.CreateVersion7();
        var handler = new StubHandler(async request =>
        {
            Assert.Equal(
                $"https://hub.example/agent/v1/commands/{commandId:D}/result",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("succeeded", body);
            Assert.Contains("sudah diterapkan", body);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new AgentHubClient(new HttpClient(handler));

        await client.SendCommandResultAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.1.0",
            commandId,
            "succeeded",
            "Konfigurasi sudah diterapkan",
            CancellationToken.None);
    }

    [Fact]
    public async Task Hasil_command_bisa_menyertakan_arsip_log()
    {
        var commandId = Guid.CreateVersion7();
        var handler = new StubHandler(async request =>
        {
            Assert.Equal(
                $"https://hub.example/agent/v1/commands/{commandId:D}/result",
                request.RequestUri?.AbsoluteUri);
            var body = await request.Content!.ReadAsByteArrayAsync();
            var text = Encoding.UTF8.GetString(body);
            Assert.Contains("name=attachment", text, StringComparison.Ordinal);
            Assert.Contains("filename=agent-logs.zip", text, StringComparison.Ordinal);
            Assert.Contains("application/zip", text, StringComparison.Ordinal);
            Assert.Contains("log-content", text, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new AgentHubClient(new HttpClient(handler));
        var stream = new MemoryStream("log-content"u8.ToArray());

        await client.SendCommandResultAsync(
            AgentHubClient.NormalizeBaseAddress("https://hub.example"),
            "agent.secret",
            "0.1.0",
            commandId,
            "succeeded",
            "Log terlampir",
            new CommandResultAttachment(stream, "agent-logs.zip", "application/zip"),
            CancellationToken.None);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
