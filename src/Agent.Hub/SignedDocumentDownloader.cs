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
            ?? throw new ArgumentException("Path dokumen tidak memiliki direktori.", nameof(destinationPath));
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
                    $"Ukuran PDF dari header {contentLength} byte, seharusnya {document.SizeBytes} byte.");
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
                    throw new InvalidDataException("PDF lebih besar daripada ukuran pada claim.");
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
                    $"Ukuran PDF {totalBytes} byte, seharusnya {document.SizeBytes} byte.");
            }

            if (headerLength < header.Length || !header.AsSpan().SequenceEqual("%PDF-"u8))
            {
                throw new InvalidDataException("Dokumen tidak memiliki header PDF yang valid.");
            }

            var actualHash = hash.GetHashAndReset();
            var expectedHash = Convert.FromHexString(document.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException("SHA-256 PDF tidak cocok dengan claim.");
            }

            await destination.FlushAsync(cancellationToken);
            destination.Close();
            File.Move(temporaryPath, destinationPath, true);

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
            throw new InvalidDataException("Signed URL wajib HTTPS; HTTP hanya boleh untuk loopback.");
        }

        if (document.SizeBytes is <= 0 or > MaximumDocumentSizeBytes)
        {
            throw new InvalidDataException(
                $"Ukuran PDF harus 1 sampai {MaximumDocumentSizeBytes} byte.");
        }

        if (document.Sha256.Length != 64)
        {
            throw new InvalidDataException("SHA-256 pada claim harus 64 karakter heksadesimal.");
        }

        try
        {
            _ = Convert.FromHexString(document.Sha256);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("SHA-256 pada claim bukan heksadesimal yang valid.", ex);
        }
    }
}
