using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace Agent.Desktop.Services;

internal sealed class ControlInstanceCoordinator : IDisposable
{
    private readonly string mutexName;
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private Mutex? mutex;
    private Task? listenerTask;

    public ControlInstanceCoordinator()
        : this($"Session{Process.GetCurrentProcess().SessionId}")
    {
    }

    internal ControlInstanceCoordinator(string instanceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey);
        mutexName = $"Local\\Opsifin.Agent.Control.Window.{instanceKey}";
        pipeName = $"Opsifin.Agent.Control.Window.{instanceKey}";
    }

    public bool TryAcquire()
    {
        mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (createdNew)
        {
            return true;
        }

        mutex.Dispose();
        mutex = null;
        return false;
    }

    public void StartListening(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        listenerTask = ListenAsync(activate, lifetime.Token);
    }

    public bool TryActivateExisting(TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            using var cancellation = new CancellationTokenSource(timeout);
            client.ConnectAsync(cancellation.Token).GetAwaiter().GetResult();
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        try
        {
            listenerTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Penutupan aplikasi membatalkan pipe listener secara normal.
        }

        mutex?.Dispose();
        lifetime.Dispose();
    }

    private async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                if (string.Equals(await reader.ReadLineAsync(cancellationToken), "activate", StringComparison.Ordinal))
                {
                    activate();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // Client dapat menutup pipe sebelum pesan selesai; listener berikutnya tetap hidup.
            }
        }
    }
}
