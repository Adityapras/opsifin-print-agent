namespace Agent.Local;

public sealed record AgentStorageRetentionOptions(
    string RootDirectory,
    string LogDirectory,
    string UpdateDirectory,
    string CommandJournalDirectory,
    string CommandAttachmentDirectory,
    string ArchiveDirectory,
    string LegacyIdentityArchiveDirectory,
    string DocumentsDirectory,
    TimeSpan LogRetention,
    long MaximumLogBytes,
    TimeSpan CommandJournalRetention,
    TimeSpan CommandAttachmentRetention,
    TimeSpan StateArchiveRetention,
    int MaximumStateArchives,
    int MaximumUpdateDirectories,
    TimeSpan TemporaryDocumentRetention)
{
    public static AgentStorageRetentionOptions Default { get; } = new(
        AgentStatePaths.RootDirectory,
        AgentStatePaths.LogDirectory,
        AgentStatePaths.UpdateDirectory,
        AgentStatePaths.CommandJournalDirectory,
        AgentStatePaths.CommandAttachmentDirectory,
        AgentStatePaths.ArchiveDirectory,
        Path.Combine(AgentStatePaths.StateDirectory, "identity-archive"),
        AgentStatePaths.DocumentsDirectory,
        TimeSpan.FromDays(14),
        200L * 1024 * 1024,
        TimeSpan.FromDays(30),
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(30),
        3,
        2,
        TimeSpan.FromDays(1));
}

public sealed record AgentStorageCleanupResult(
    int DeletedFiles,
    int DeletedDirectories,
    long ReclaimedBytes,
    IReadOnlyList<string> Errors);

public sealed class AgentStorageCleaner(
    AgentStorageRetentionOptions options,
    TimeProvider timeProvider)
{
    public AgentStorageCleaner()
        : this(AgentStorageRetentionOptions.Default, TimeProvider.System)
    {
    }

    public Task<AgentStorageCleanupResult> CleanAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var state = new CleanupState();

        DeleteOldFiles(options.LogDirectory, "*.jsonl", now - options.LogRetention, state, cancellationToken);
        EnforceDirectorySize(options.LogDirectory, "*.jsonl", options.MaximumLogBytes, state, cancellationToken);
        DeleteOldFiles(options.CommandJournalDirectory, "*.json", now - options.CommandJournalRetention, state, cancellationToken);
        DeleteOldFiles(options.CommandAttachmentDirectory, "*", now - options.CommandAttachmentRetention, state, cancellationToken);
        DeleteOldFiles(options.LegacyIdentityArchiveDirectory, "*", now - options.StateArchiveRetention, state, cancellationToken);
        DeleteOldFiles(options.DocumentsDirectory, "*", now - options.TemporaryDocumentRetention, state, cancellationToken);
        CleanStateArchives(now, state, cancellationToken);
        CleanUpdateDirectories(now, state, cancellationToken);

        return Task.FromResult(new AgentStorageCleanupResult(
            state.DeletedFiles,
            state.DeletedDirectories,
            state.ReclaimedBytes,
            state.Errors));
    }

    private void CleanStateArchives(
        DateTime now,
        CleanupState state,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(options.ArchiveDirectory))
        {
            return;
        }

        var directories = Directory.EnumerateDirectories(options.ArchiveDirectory)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToArray();
        for (var index = 0; index < directories.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories[index];
            if (index < options.MaximumStateArchives
                && directory.LastWriteTimeUtc >= now - options.StateArchiveRetention)
            {
                continue;
            }

            DeleteDirectory(directory.FullName, state);
        }
    }

    private void CleanUpdateDirectories(
        DateTime now,
        CleanupState state,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(options.UpdateDirectory))
        {
            return;
        }

        var directories = Directory.EnumerateDirectories(options.UpdateDirectory)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToArray();
        for (var index = 0; index < directories.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index >= options.MaximumUpdateDirectories)
            {
                DeleteDirectory(directories[index].FullName, state);
                continue;
            }

            // Dua paket terbaru dipertahankan untuk diagnosis/recovery, tetapi file unduhan
            // parsial yang tertinggal tidak memiliki nilai setelah satu hari.
            DeleteOldFiles(
                directories[index].FullName,
                "*.download",
                now - options.TemporaryDocumentRetention,
                state,
                cancellationToken);
            DeleteOldFiles(
                directories[index].FullName,
                "*.tmp",
                now - options.TemporaryDocumentRetention,
                state,
                cancellationToken);
        }
    }

    private static void DeleteOldFiles(
        string directory,
        string pattern,
        DateTime cutoff,
        CleanupState state,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (file.LastWriteTimeUtc < cutoff)
            {
                DeleteFile(file, state);
            }
        }
    }

    private static void EnforceDirectorySize(
        string directory,
        string pattern,
        long maximumBytes,
        CleanupState state,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();
        var totalBytes = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalBytes <= maximumBytes)
            {
                break;
            }

            var length = file.Length;
            if (DeleteFile(file, state))
            {
                totalBytes -= length;
            }
        }
    }

    private static bool DeleteFile(FileInfo file, CleanupState state)
    {
        try
        {
            var length = file.Exists ? file.Length : 0;
            file.Delete();
            state.DeletedFiles++;
            state.ReclaimedBytes += length;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            state.Errors.Add($"{file.FullName}: {exception.Message}");
            return false;
        }
    }

    private static void DeleteDirectory(string path, CleanupState state)
    {
        try
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(file => new FileInfo(file))
                .ToArray();
            var bytes = files.Sum(file => file.Length);
            Directory.Delete(path, recursive: true);
            state.DeletedDirectories++;
            state.DeletedFiles += files.Length;
            state.ReclaimedBytes += bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            state.Errors.Add($"{path}: {exception.Message}");
        }
    }

    private void ValidateOptions()
    {
        var root = Path.GetFullPath(options.RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var path in new[]
                 {
                     options.LogDirectory,
                     options.UpdateDirectory,
                     options.CommandJournalDirectory,
                     options.CommandAttachmentDirectory,
                     options.ArchiveDirectory,
                     options.LegacyIdentityArchiveDirectory,
                     options.DocumentsDirectory,
                 })
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Storage cleanup path '{fullPath}' is outside the Agent data directory.");
            }
        }

        if (options.MaximumLogBytes < 0
            || options.MaximumStateArchives < 0
            || options.MaximumUpdateDirectories < 1)
        {
            throw new InvalidOperationException("Agent storage retention limits are invalid.");
        }
    }

    private sealed class CleanupState
    {
        public int DeletedFiles { get; set; }

        public int DeletedDirectories { get; set; }

        public long ReclaimedBytes { get; set; }

        public List<string> Errors { get; } = [];
    }
}
