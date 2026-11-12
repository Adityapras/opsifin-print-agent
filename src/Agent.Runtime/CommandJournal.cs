using System.Text.Json;

namespace Agent.Runtime;

public enum CommandExecutionState
{
    Started,
    Completed,
}

public sealed record CommandExecutionRecord(
    Guid CommandId,
    string CommandType,
    CommandExecutionState State,
    string? ResultStatus,
    string? ResultMessage,
    DateTimeOffset UpdatedAt,
    string? AttachmentPath = null,
    string? AttachmentName = null,
    string? AttachmentMediaType = null);

/// <summary>Mencegah efek samping command Hub dijalankan ulang setelah heartbeat atau restart.</summary>
public sealed class CommandJournal(string directory)
{
    public async Task SaveAsync(CommandExecutionRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = GetPath(record.CommandId);
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
                    RuntimeJsonContext.Default.CommandExecutionRecord,
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

    public async Task<CommandExecutionRecord?> LoadAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var path = GetPath(commandId);
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
            RuntimeJsonContext.Default.CommandExecutionRecord,
            cancellationToken)
            ?? throw new InvalidDataException($"Command journal {commandId} is empty.");
    }

    private string GetPath(Guid commandId) => Path.Combine(directory, $"{commandId:N}.json");
}
