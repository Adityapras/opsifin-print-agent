using System.Text.Json;

namespace Agent.Local;

public static class AgentUpdateStatuses
{
    public const string PendingDownload = "pending_download";
    public const string Downloading = "downloading";
    public const string Verifying = "verifying";
    public const string Ready = "ready";
    public const string WaitingForIdle = "waiting_for_idle";
    public const string Installing = "installing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public static bool IsTerminal(string status) => status is Succeeded or Failed;
}

public static class AgentUpdatePolicies
{
    public const string Manual = "manual";
    public const string Automatic = "automatic";
}

public sealed record AgentUpdateState(
    Guid CommandId,
    string CurrentVersion,
    string TargetVersion,
    string Status,
    string InstallPolicy,
    string PackageUrl,
    string Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ReleaseNotes = null,
    string? PackagePath = null,
    int ProgressPercent = 0,
    string? Message = null,
    int Attempts = 0);

public sealed record AgentUpdateInstallRequest(Guid CommandId, DateTimeOffset RequestedAt);

public sealed class AgentUpdateStore
{
    public AgentUpdateStore()
        : this(AgentStatePaths.UpdateStateFile, AgentStatePaths.UpdateInstallRequestFile)
    {
    }

    public AgentUpdateStore(string statePath, string installRequestPath)
    {
        StatePath = statePath;
        InstallRequestPath = installRequestPath;
    }

    public string StatePath { get; }

    public string InstallRequestPath { get; }

    public Task<AgentUpdateState?> LoadAsync(CancellationToken cancellationToken) => ReadAsync(
        StatePath,
        LocalJsonContext.Default.AgentUpdateState,
        cancellationToken);

    public Task SaveAsync(AgentUpdateState state, CancellationToken cancellationToken) => WriteAsync(
        StatePath,
        state,
        LocalJsonContext.Default.AgentUpdateState,
        cancellationToken);

    public Task RequestInstallAsync(Guid commandId, CancellationToken cancellationToken) => WriteAsync(
        InstallRequestPath,
        new AgentUpdateInstallRequest(commandId, DateTimeOffset.UtcNow),
        LocalJsonContext.Default.AgentUpdateInstallRequest,
        cancellationToken);

    public async Task<bool> IsInstallRequestedAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var request = await ReadAsync(
            InstallRequestPath,
            LocalJsonContext.Default.AgentUpdateInstallRequest,
            cancellationToken);
        return request?.CommandId == commandId;
    }

    public void ClearInstallRequest()
    {
        if (File.Exists(InstallRequestPath))
        {
            File.Delete(InstallRequestPath);
        }
    }

    private static async Task<T?> ReadAsync<T>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken)
            ?? throw new InvalidDataException($"The local update file '{System.IO.Path.GetFileName(path)}' is empty.");
    }

    private static async Task WriteAsync<T>(
        string path,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The local update path does not have a directory.");
        if (string.Equals(directory, AgentStatePaths.StateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AgentStatePaths.EnsureSecureStateDirectory();
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

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
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken);
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
}
