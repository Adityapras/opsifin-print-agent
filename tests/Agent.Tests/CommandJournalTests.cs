using Agent.Runtime;

namespace Agent.Tests;

public sealed class CommandJournalTests
{
    [Fact]
    public async Task Status_command_bisa_dibaca_setelah_instance_dibuka_ulang()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var commandId = Guid.CreateVersion7();
            var record = new CommandExecutionRecord(
                commandId,
                "test_page",
                CommandExecutionState.Started,
                null,
                null,
                DateTimeOffset.UtcNow);
            var journal = new CommandJournal(directory);
            await journal.SaveAsync(record, CancellationToken.None);

            var reopened = new CommandJournal(directory);
            var loaded = await reopened.LoadAsync(commandId, CancellationToken.None);

            Assert.Equal(record, loaded);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
