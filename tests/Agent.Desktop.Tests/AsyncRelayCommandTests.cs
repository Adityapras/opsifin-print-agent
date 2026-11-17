using Agent.Desktop.Infrastructure;

namespace Agent.Desktop.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task Exception_dari_command_dilaporkan_dan_command_dapat_dijalankan_kembali()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            () => throw new InvalidOperationException("test failure"),
            onException: exception => reported.TrySetResult(exception));

        command.Execute(null);
        var exception = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task Cancellation_tidak_dilaporkan_sebagai_error()
    {
        var reported = false;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            () => throw new OperationCanceledException(),
            onException: _ => reported = true);
        command.CanExecuteChanged += (_, _) =>
        {
            if (command.CanExecute(null))
            {
                finished.TrySetResult();
            }
        };

        command.Execute(null);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(reported);
    }

    [Fact]
    public async Task Exception_dari_event_setelah_command_selesai_tidak_menutup_dispatcher()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            () => Task.CompletedTask,
            onException: exception => reported.TrySetResult(exception));
        command.CanExecuteChanged += (_, _) =>
        {
            if (command.CanExecute(null))
            {
                throw new InvalidOperationException("event failure");
            }
        };

        command.Execute(null);
        var exception = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.True(command.CanExecute(null));
    }
}
