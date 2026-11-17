using Agent.Local;

namespace Agent.Local.Tests;

public sealed class AgentStorageCleanerTests
{
    [Fact]
    public async Task Cleanup_menghapus_artifact_lama_tanpa_menyentuh_outbox_aktif()
    {
        var root = CreateTestDirectory();
        var now = DateTimeOffset.UtcNow;
        try
        {
            var state = Path.Combine(root, "state");
            var logs = Path.Combine(state, "logs");
            var updates = Path.Combine(state, "updates");
            var commands = Path.Combine(state, "commands");
            var attachments = Path.Combine(state, "command-attachments");
            var archives = Path.Combine(root, "archives");
            var legacyArchives = Path.Combine(state, "identity-archive");
            var documents = Path.Combine(state, "documents");
            var outbox = Path.Combine(state, "agent.db");

            await CreateFileAsync(Path.Combine(logs, "old.jsonl"), "old", now.AddDays(-20));
            await CreateFileAsync(Path.Combine(logs, "current.jsonl"), "current", now.AddHours(-1));
            await CreateFileAsync(Path.Combine(commands, "old.json"), "command", now.AddDays(-40));
            await CreateFileAsync(Path.Combine(attachments, "old.pdf"), "attachment", now.AddDays(-10));
            await CreateFileAsync(Path.Combine(documents, "old.pdf"), "document", now.AddDays(-2));
            await CreateFileAsync(outbox, "pending-event", now.AddDays(-100));

            for (var index = 0; index < 4; index++)
            {
                var archive = Path.Combine(archives, $"archive-{index}");
                await CreateFileAsync(Path.Combine(archive, "identity.json"), index.ToString(), now.AddDays(-index));
                Directory.SetLastWriteTimeUtc(archive, now.AddDays(-index).UtcDateTime);
            }

            for (var index = 0; index < 3; index++)
            {
                var update = Path.Combine(updates, $"1.0.{index}");
                await CreateFileAsync(Path.Combine(update, "agent.msi"), index.ToString(), now.AddHours(-index));
                Directory.SetLastWriteTimeUtc(update, now.AddHours(-index).UtcDateTime);
            }

            var options = new AgentStorageRetentionOptions(
                root,
                logs,
                updates,
                commands,
                attachments,
                archives,
                legacyArchives,
                documents,
                TimeSpan.FromDays(14),
                200L * 1024 * 1024,
                TimeSpan.FromDays(30),
                TimeSpan.FromDays(7),
                TimeSpan.FromDays(30),
                3,
                2,
                TimeSpan.FromDays(1));
            var cleaner = new AgentStorageCleaner(options, new FixedTimeProvider(now));

            var result = await cleaner.CleanAsync(CancellationToken.None);

            Assert.Empty(result.Errors);
            Assert.False(File.Exists(Path.Combine(logs, "old.jsonl")));
            Assert.True(File.Exists(Path.Combine(logs, "current.jsonl")));
            Assert.False(File.Exists(Path.Combine(commands, "old.json")));
            Assert.False(File.Exists(Path.Combine(attachments, "old.pdf")));
            Assert.False(File.Exists(Path.Combine(documents, "old.pdf")));
            Assert.True(File.Exists(outbox));
            Assert.Equal(3, Directory.EnumerateDirectories(archives).Count());
            Assert.Equal(2, Directory.EnumerateDirectories(updates).Count());
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static async Task CreateFileAsync(string path, string content, DateTimeOffset lastWrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
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
