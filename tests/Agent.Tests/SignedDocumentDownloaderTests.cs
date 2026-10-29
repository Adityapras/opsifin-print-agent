using System.Net;
using System.Security.Cryptography;
using Agent.Hub;

namespace Agent.Tests;

public class SignedDocumentDownloaderTests
{
    [Fact]
    public async Task Mengunduh_pdf_secara_atomik_setelah_ukuran_dan_hash_cocok()
    {
        var content = "%PDF-1.7\nopsifin"u8.ToArray();
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "document.pdf");
        try
        {
            var downloader = CreateDownloader(content);
            var document = CreateDocument(content);

            var result = await downloader.DownloadAsync(document, destination, CancellationToken.None);

            Assert.Equal(content, await File.ReadAllBytesAsync(destination));
            Assert.Equal(content.Length, result.SizeBytes);
            Assert.Equal(document.Sha256, result.Sha256);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Hash_salah_tidak_meninggalkan_file_final_atau_temporary()
    {
        var content = "%PDF-1.7\nopsifin"u8.ToArray();
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "document.pdf");
        try
        {
            var downloader = CreateDownloader(content);
            var document = CreateDocument(content) with { Sha256 = new string('0', 64) };

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => downloader.DownloadAsync(document, destination, CancellationToken.None));

            Assert.Contains("SHA-256", exception.Message);
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static SignedDocumentDownloader CreateDownloader(byte[] content) => new(new HttpClient(
        new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        }))));

    private static ClaimedDocument CreateDocument(byte[] content) => new(
        new Uri("https://hub.example/agent/v1/documents/id?signature=test&expires=1"),
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
        1,
        content.Length);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
