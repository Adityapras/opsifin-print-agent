using System.IO.Compression;

namespace Agent.Runtime;

public sealed record AgentLogArchiveOptions(string LogDirectory, string ArchiveDirectory);

public sealed class AgentLogArchive(AgentLogArchiveOptions options, TimeProvider timeProvider)
{
    public async Task<string> CreateAsync(
        Guid commandId,
        int hours,
        CancellationToken cancellationToken)
    {
        if (hours is < 1 or > 168)
        {
            throw new ArgumentOutOfRangeException(nameof(hours), "Rentang log harus 1 sampai 168 jam.");
        }

        Directory.CreateDirectory(options.ArchiveDirectory);
        var cutoff = timeProvider.GetUtcNow().Subtract(TimeSpan.FromHours(hours));
        var destination = Path.Combine(options.ArchiveDirectory, $"agent-logs-{commandId:N}.zip");
        var temporary = $"{destination}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (Directory.Exists(options.LogDirectory))
                {
                    foreach (var path in Directory.EnumerateFiles(options.LogDirectory, "*.jsonl")
                                 .Where(path => File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime)
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await using var source = new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            81920,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await source.CopyToAsync(entryStream, cancellationToken);
                    }
                }
            }

            File.Move(temporary, destination, true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
