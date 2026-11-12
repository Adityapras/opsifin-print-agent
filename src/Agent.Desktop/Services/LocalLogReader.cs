using System.IO;
using System.Text.Json;
using Agent.Local;

namespace Agent.Desktop.Services;

internal sealed record LocalLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message);

internal sealed class LocalLogReader
{
    private readonly string directory;

    public LocalLogReader()
        : this(AgentStatePaths.LogDirectory)
    {
    }

    internal LocalLogReader(string directory)
    {
        this.directory = directory;
    }

    public Task<IReadOnlyList<LocalLogEntry>> ReadLatestAsync(int maximumEntries) => Task.Run(() =>
    {
        if (!Directory.Exists(directory))
        {
            return (IReadOnlyList<LocalLogEntry>)[];
        }

        var lines = Directory.EnumerateFiles(directory, "*.jsonl")
            // File harus digabung dari lama ke baru sebelum TakeLast. Urutan sebaliknya
            // membuat file lama mendominasi dan GUI tampak freeze saat tanggal berganti.
            .OrderBy(File.GetLastWriteTimeUtc)
            .TakeLast(3)
            .SelectMany(path => ReadLinesShared(path))
            .TakeLast(maximumEntries);
        var entries = new List<LocalLogEntry>();
        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                entries.Add(new LocalLogEntry(
                    IndonesiaTime.Convert(root.GetProperty("timestamp").GetDateTimeOffset()),
                    root.GetProperty("level").GetString() ?? "Unknown",
                    root.GetProperty("category").GetString() ?? "Agent",
                    root.GetProperty("message").GetString() ?? string.Empty));
            }
            catch (JsonException)
            {
                // Baris parsial dapat muncul saat service sedang menulis; refresh berikutnya akan membaca ulang.
            }
        }

        return (IReadOnlyList<LocalLogEntry>)entries
            .OrderByDescending(entry => entry.Timestamp)
            .Take(maximumEntries)
            .ToArray();
    });

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
