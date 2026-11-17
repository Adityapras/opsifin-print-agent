namespace Agent.Local;

public sealed record AgentStateResetResult(string? ArchivePath, bool StateWasPresent);

public sealed class AgentStateResetter
{
    private readonly string stateDirectory;
    private readonly string archiveDirectory;
    private readonly TimeProvider timeProvider;

    public AgentStateResetter()
        : this(AgentStatePaths.StateDirectory, AgentStatePaths.ArchiveDirectory, TimeProvider.System)
    {
    }

    public AgentStateResetter(
        string stateDirectory,
        string archiveDirectory,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        this.archiveDirectory = Path.GetFullPath(archiveDirectory);
        this.timeProvider = timeProvider;
    }

    public AgentStateResetResult ArchiveAndCreateFreshState(Guid? agentId = null)
    {
        EnsureDirectoriesAreSeparate();
        Directory.CreateDirectory(Path.GetDirectoryName(stateDirectory)
            ?? throw new InvalidOperationException("The Agent state directory does not have a parent directory."));

        string? archivePath = null;
        var stateWasPresent = Directory.Exists(stateDirectory);
        try
        {
            if (stateWasPresent)
            {
                EnsureArchiveDirectory();
                archivePath = CreateArchivePath(agentId);
                Directory.Move(stateDirectory, archivePath);
                // Directory.Move mempertahankan timestamp folder lama. Timestamp reset harus
                // diperbarui agar retention tidak langsung menghapus arsip yang baru dibuat.
                Directory.SetLastWriteTimeUtc(archivePath, timeProvider.GetUtcNow().UtcDateTime);
            }

            EnsureStateDirectory();
            return new AgentStateResetResult(archivePath, stateWasPresent);
        }
        catch
        {
            // Reset harus transaksional: bila state baru tidak dapat dibuat, kembalikan
            // state lama supaya service tidak hidup dengan identity yang hilang sebagian.
            if (archivePath is not null
                && Directory.Exists(archivePath))
            {
                RemoveEmptyFreshState();
                if (!Directory.Exists(stateDirectory))
                {
                    Directory.Move(archivePath, stateDirectory);
                }
            }

            throw;
        }
    }

    private void RemoveEmptyFreshState()
    {
        if (Directory.Exists(stateDirectory)
            && !Directory.EnumerateFileSystemEntries(stateDirectory).Any())
        {
            Directory.Delete(stateDirectory);
        }
    }

    private string CreateArchivePath(Guid? agentId)
    {
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss");
        var identity = agentId?.ToString("N") ?? "unpaired";
        return Path.Combine(archiveDirectory, $"state-{timestamp}-{identity}-{Guid.CreateVersion7():N}");
    }

    private void EnsureDirectoriesAreSeparate()
    {
        if (string.Equals(stateDirectory, archiveDirectory, StringComparison.OrdinalIgnoreCase)
            || archiveDirectory.StartsWith(stateDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || stateDirectory.StartsWith(archiveDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Agent state and archive directories must be separate sibling locations.");
        }
    }

    private void EnsureStateDirectory()
    {
        if (string.Equals(stateDirectory, AgentStatePaths.StateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AgentStatePaths.EnsureSecureStateDirectory();
        }
        else
        {
            Directory.CreateDirectory(stateDirectory);
        }
    }

    private void EnsureArchiveDirectory()
    {
        if (string.Equals(archiveDirectory, AgentStatePaths.ArchiveDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AgentStatePaths.EnsureSecureArchiveDirectory();
        }
        else
        {
            Directory.CreateDirectory(archiveDirectory);
        }
    }
}
