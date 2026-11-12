using Agent.Hub;

namespace Agent.Tests;

public class AtomicFileCommitTests
{
    [Fact]
    public async Task Menutup_file_temporary_sebelum_memindahkannya_ke_path_final()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "opsifin-agent-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, "agent-update.msi.download");
        var destinationPath = Path.Combine(directory, "agent-update.msi");

        try
        {
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous);
            await destination.WriteAsync("signed-update"u8.ToArray());
            await destination.FlushAsync();

            AtomicFileCommit.CloseAndMove(destination, temporaryPath, destinationPath);

            Assert.False(destination.CanWrite);
            Assert.False(File.Exists(temporaryPath));
            Assert.Equal("signed-update", await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
