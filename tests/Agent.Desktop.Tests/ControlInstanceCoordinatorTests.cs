using Agent.Desktop.Services;

namespace Agent.Desktop.Tests;

public sealed class ControlInstanceCoordinatorTests
{
    [Fact]
    public async Task Instance_kedua_mengaktifkan_window_milik_instance_pertama()
    {
        var key = $"Test{Guid.NewGuid():N}";
        using var first = new ControlInstanceCoordinator(key);
        using var second = new ControlInstanceCoordinator(key);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(first.TryAcquire());
        first.StartListening(() => activated.TrySetResult());
        Assert.False(second.TryAcquire());

        Assert.True(second.TryActivateExisting(TimeSpan.FromSeconds(5)));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
