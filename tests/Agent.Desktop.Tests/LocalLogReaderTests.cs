using System.Text.Json;
using Agent.Desktop.Services;

namespace Agent.Desktop.Tests;

public sealed class LocalLogReaderTests
{
    [Fact]
    public async Task Mengambil_entry_terbaru_dari_file_terbaru_bukan_tail_file_lama()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-log-reader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var oldPath = Path.Combine(directory, "agent-20261105.jsonl");
            var newPath = Path.Combine(directory, "agent-20261106.jsonl");
            await File.WriteAllLinesAsync(
                oldPath,
                Enumerable.Range(1, 150).Select(index => Entry(
                    new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.FromHours(7)).AddSeconds(index),
                    $"old-{index}")));
            await File.WriteAllTextAsync(
                newPath,
                Entry(new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.FromHours(7)), "newest") + Environment.NewLine);
            File.SetLastWriteTimeUtc(oldPath, new DateTime(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newPath, new DateTime(2026, 11, 6, 7, 0, 0, DateTimeKind.Utc));

            var logs = await new LocalLogReader(directory).ReadLatestAsync(120);

            Assert.Equal("newest", logs[0].Message);
            Assert.DoesNotContain(logs, entry => entry.Message == "old-1");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string Entry(DateTimeOffset timestamp, string message) => JsonSerializer.Serialize(new
    {
        timestamp,
        level = "Information",
        category = "Agent.Service.JobCoordinator",
        event_id = 0,
        message,
        exception = (string?)null,
    });
}
