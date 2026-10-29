using System.Text.Json;

namespace Agent.Runtime;

public sealed class PrintJobJournal(string directory)
{
    public async Task SaveAsync(ActivePrintRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = GetPath(record.JobId);
        var temporaryPath = $"{path}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    record,
                    RuntimeJsonContext.Default.ActivePrintRecord,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<ActivePrintRecord?> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var path = GetPath(jobId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
            stream,
            RuntimeJsonContext.Default.ActivePrintRecord,
            cancellationToken)
            ?? throw new InvalidDataException($"Jurnal job {jobId} kosong.");
    }

    public void Remove(Guid jobId)
    {
        var path = GetPath(jobId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(Guid jobId) => Path.Combine(directory, $"{jobId:N}.json");
}
