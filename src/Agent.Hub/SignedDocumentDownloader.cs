using System.Security.Cryptography;

namespace Agent.Hub;

public sealed record DownloadedDocument(string Path, long SizeBytes, string Sha256);

public sealed class SignedDocumentDownloader(HttpClient httpClient)
{
    public const long MaximumDocumentSizeBytes = 100 * 1024 * 1024;

    public async Task<DownloadedDocument> DownloadAsync(
        ClaimedDocument document,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ValidateDocument(document);

        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The document path does not have a directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{destinationPath}.{Guid.CreateVersion7():N}.tmp";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, document.Url);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long contentLength
                && contentLength != document.SizeBytes)
            {
                throw new InvalidDataException(
                    $"The PDF header reports {contentLength} bytes; expected {document.SizeBytes} bytes.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[64 * 1024];
            var header = new byte[5];
            var headerLength = 0;
            long totalBytes = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > document.SizeBytes)
                {
                    throw new InvalidDataException("The PDF is larger than the size declared in the claim.");
                }

                if (headerLength < header.Length)
                {
                    var copyLength = Math.Min(header.Length - headerLength, read);
                    buffer.AsSpan(0, copyLength).CopyTo(header.AsSpan(headerLength));
                    headerLength += copyLength;
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (totalBytes != document.SizeBytes)
            {
                throw new InvalidDataException(
                    $"The PDF size is {totalBytes} bytes; expected {document.SizeBytes} bytes.");
            }

            if (headerLength < header.Length || !header.AsSpan().SequenceEqual("%PDF-"u8))
            {
                throw new InvalidDataException("The document does not have a valid PDF header.");
            }

            var actualHash = hash.GetHashAndReset();
            var expectedHash = Convert.FromHexString(document.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException("The PDF SHA-256 does not match the claim.");
            }

            await destination.FlushAsync(cancellationToken);
            AtomicFileCommit.CloseAndMove(destination, temporaryPath, destinationPath);

            return new DownloadedDocument(destinationPath, totalBytes, Convert.ToHexString(actualHash).ToLowerInvariant());
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateDocument(ClaimedDocument document)
    {
        if (!string.Equals(document.Url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(document.Url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && document.Url.IsLoopback))
        {
            throw new InvalidDataException("The signed URL must use HTTPS; HTTP is allowed only for loopback.");
        }

        if (document.SizeBytes is <= 0 or > MaximumDocumentSizeBytes)
        {
            throw new InvalidDataException(
                $"The PDF size must be between 1 and {MaximumDocumentSizeBytes} bytes.");
        }

        if (document.Sha256.Length != 64)
        {
            throw new InvalidDataException("The claim SHA-256 must contain 64 hexadecimal characters.");
        }

        try
        {
            _ = Convert.FromHexString(document.Sha256);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The claim SHA-256 is not valid hexadecimal.", ex);
        }
    }
}
