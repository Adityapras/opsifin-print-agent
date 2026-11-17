using Agent.Local;

namespace Agent.Local.Tests;

public sealed class AgentStateResetterTests
{
    [Fact]
    public async Task Reset_mengarsipkan_seluruh_state_dan_membuat_state_baru_yang_kosong()
    {
        var root = CreateTestDirectory();
        var state = Path.Combine(root, "state");
        var archives = Path.Combine(root, "archives");
        var resetAt = new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero);
        try
        {
            Directory.CreateDirectory(Path.Combine(state, "commands"));
            await File.WriteAllTextAsync(Path.Combine(state, "identity.json"), "old-identity");
            await File.WriteAllTextAsync(Path.Combine(state, "agent.db"), "pending-outbox");
            await File.WriteAllTextAsync(Path.Combine(state, "commands", "command.json"), "pending-command");

            Directory.SetLastWriteTimeUtc(state, resetAt.AddDays(-90).UtcDateTime);
            var resetter = new AgentStateResetter(state, archives, new FixedTimeProvider(resetAt));

            var result = resetter.ArchiveAndCreateFreshState(Guid.NewGuid());

            Assert.True(result.StateWasPresent);
            Assert.NotNull(result.ArchivePath);
            Assert.True(Directory.Exists(state));
            Assert.Empty(Directory.EnumerateFileSystemEntries(state));
            Assert.Equal(resetAt.UtcDateTime, Directory.GetLastWriteTimeUtc(result.ArchivePath!));
            Assert.Equal("old-identity", await File.ReadAllTextAsync(Path.Combine(result.ArchivePath!, "identity.json")));
            Assert.Equal("pending-outbox", await File.ReadAllTextAsync(Path.Combine(result.ArchivePath!, "agent.db")));
            Assert.Equal(
                "pending-command",
                await File.ReadAllTextAsync(Path.Combine(result.ArchivePath!, "commands", "command.json")));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact]
    public void Reset_menolak_archive_yang_berada_di_dalam_state()
    {
        var root = CreateTestDirectory();
        try
        {
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(state);
            var resetter = new AgentStateResetter(state, Path.Combine(state, "archives"), TimeProvider.System);

            var exception = Assert.Throws<InvalidOperationException>(() => resetter.ArchiveAndCreateFreshState());

            Assert.Contains("separate sibling", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
