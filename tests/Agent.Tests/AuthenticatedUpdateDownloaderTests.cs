using System.Net;
using Agent.Hub;

namespace Agent.Tests;

public sealed class AuthenticatedUpdateDownloaderTests
{
    [Fact]
    public async Task Download_update_mengirim_identity_hanya_ke_endpoint_Hub_yang_sama()
    {
        var called = false;
        var handler = new StubHandler(request =>
        {
            called = true;
            Assert.Equal(
                "https://hub.example/agent/v1/updates/deployment-id/package?signature=test",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("agent-id.device-secret", request.Headers.Authorization?.Parameter);
            Assert.Equal("1.0.2", Assert.Single(request.Headers.GetValues("X-Agent-Version")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("msi-content"u8.ToArray()),
            });
        });
        var downloader = new AuthenticatedUpdateDownloader(new HttpClient(handler));

        using var response = await downloader.DownloadAsync(
            new Uri("https://hub.example/agent/v1/"),
            new Uri("https://hub.example/agent/v1/updates/deployment-id/package?signature=test"),
            "agent-id.device-secret",
            "1.0.2",
            CancellationToken.None);

        Assert.True(called);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("https://attacker.example/agent/v1/updates/id/package")]
    [InlineData("https://hub.example/other/updates/id/package")]
    [InlineData("https://user@hub.example/agent/v1/updates/id/package")]
    public async Task Download_update_menolak_URL_yang_bisa_membocorkan_bearer_token(string packageUrl)
    {
        var called = false;
        var downloader = new AuthenticatedUpdateDownloader(new HttpClient(new StubHandler(_ =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        })));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new Uri("https://hub.example/agent/v1/"),
            new Uri(packageUrl),
            "agent-id.device-secret",
            "1.0.2",
            CancellationToken.None));

        Assert.Contains("paired Print Hub", exception.Message, StringComparison.Ordinal);
        Assert.False(called);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
