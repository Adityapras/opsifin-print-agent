using System.Diagnostics;
using System.ServiceProcess;
using Agent.Local;

namespace Agent.Desktop.Services;

internal sealed record AgentWindowsServiceStatus(bool Installed, string State, bool IsRunning);

internal sealed class WindowsServiceManager
{
    public Task<AgentWindowsServiceStatus> GetStatusAsync() => Task.Run(() =>
    {
        try
        {
            using var service = new ServiceController(AgentProduct.ServiceName);
            var status = service.Status;
            return new AgentWindowsServiceStatus(
                true,
                status switch
                {
                    ServiceControllerStatus.Running => "Running",
                    ServiceControllerStatus.Stopped => "Stopped",
                    ServiceControllerStatus.StartPending => "Starting",
                    ServiceControllerStatus.StopPending => "Stopping",
                    ServiceControllerStatus.Paused => "Paused",
                    ServiceControllerStatus.PausePending => "Pausing",
                    ServiceControllerStatus.ContinuePending => "Resuming",
                    _ => status.ToString(),
                },
                status == ServiceControllerStatus.Running);
        }
        catch (InvalidOperationException)
        {
            return new AgentWindowsServiceStatus(false, "Not installed", false);
        }
    });

    public Task StartAsync() => Task.Run(() =>
    {
        using var service = new ServiceController(AgentProduct.ServiceName);
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }
    });

    public Task StopAsync() => Task.Run(() =>
    {
        using var service = new ServiceController(AgentProduct.ServiceName);
        service.Refresh();
        if (service.Status is not ServiceControllerStatus.Stopped and not ServiceControllerStatus.StopPending)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
    });

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public Task<int> StopRemainingConsoleAgentsAsync() => Task.Run(() =>
    {
        var stopped = 0;
        foreach (var process in Process.GetProcessesByName("Agent.Service"))
        {
            using (process)
            {
                if (process.HasExited)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
                stopped++;
            }
        }

        return stopped;
    });
}
