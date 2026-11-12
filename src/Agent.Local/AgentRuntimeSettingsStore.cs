using System.Text.Json;

namespace Agent.Local;

public sealed record AgentRuntimeSettings(bool SimulationMode);

public sealed class AgentRuntimeSettingsStore
{
    public AgentRuntimeSettingsStore()
        : this(AgentStatePaths.RuntimeSettingsFile)
    {
    }

    public AgentRuntimeSettingsStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public async Task<AgentRuntimeSettings?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        await using var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
            stream,
            LocalJsonContext.Default.AgentRuntimeSettings,
            cancellationToken)
            ?? throw new InvalidDataException("The local runtime settings file is empty.");
    }

    public async Task SaveAsync(
        AgentRuntimeSettings settings,
        CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("The runtime settings path does not have a directory.");
        if (string.Equals(directory, AgentStatePaths.StateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AgentStatePaths.EnsureSecureStateDirectory();
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{Path}.{Guid.CreateVersion7():N}.tmp";
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
                    settings,
                    LocalJsonContext.Default.AgentRuntimeSettings,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // Penggantian atomik mencegah service membaca JSON setengah tertulis saat GUI
            // menyimpan mode lalu langsung me-restart service.
            File.Move(temporaryPath, Path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
