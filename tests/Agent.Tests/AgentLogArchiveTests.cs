using System.IO.Compression;
using Agent.Runtime;

namespace Agent.Tests;

public sealed class AgentLogArchiveTests
{
    [Fact]
    public async Task Arsip_hanya_memuat_log_dalam_rentang_yang_diminta()
    {
        var root = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        var archives = Path.Combine(root, "archives");
        Directory.CreateDirectory(logs);
        try
        {
            var now = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.Zero);
            var recent = Path.Combine(logs, "agent-20261029.jsonl");
            var old = Path.Combine(logs, "agent-20261020.jsonl");
            await File.WriteAllTextAsync(recent, "recent");
            await File.WriteAllTextAsync(old, "old");
            File.SetLastWriteTimeUtc(recent, now.UtcDateTime.AddHours(-1));
            File.SetLastWriteTimeUtc(old, now.UtcDateTime.AddDays(-9));
            var service = new AgentLogArchive(
                new AgentLogArchiveOptions(logs, archives),
                new FixedTimeProvider(now));

            var result = await service.CreateAsync(Guid.CreateVersion7(), 24, CancellationToken.None);

            using var archive = ZipFile.OpenRead(result);
            var entry = Assert.Single(archive.Entries);
            Assert.Equal(Path.GetFileName(recent), entry.FullName);
            using var reader = new StreamReader(entry.Open());
            Assert.Equal("recent", await reader.ReadToEndAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Rentang_log_di_luar_batas_ditolak()
    {
        var service = new AgentLogArchive(
            new AgentLogArchiveOptions("logs", "archives"),
            TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.CreateAsync(Guid.CreateVersion7(), 0, CancellationToken.None));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
