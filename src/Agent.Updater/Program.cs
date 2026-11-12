using System.ComponentModel;
using System.Diagnostics;
using Agent.Local;
using Agent.Updater;

try
{
    if (args.Length == 0)
    {
        throw new ArgumentException("An updater command is required.");
    }

    var arguments = UpdaterArguments.Parse(args[1..]);
    Environment.ExitCode = string.Equals(args[0], "apply", StringComparison.OrdinalIgnoreCase)
        ? await ApplyUpdateAsync(arguments)
        : throw new ArgumentException($"Updater command '{args[0]}' is not recognized.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static async Task<int> ApplyUpdateAsync(UpdaterArguments arguments)
{
    var packagePath = arguments.Required("package");
    var targetVersion = arguments.Required("target-version");
    var statePath = arguments.Required("state");
    var requestPath = arguments.Required("request");
    var healthPath = arguments.Required("health");
    var logPath = arguments.Required("log");
    var serviceName = arguments.Required("service");
    var servicePid = arguments.RequiredInt32("service-pid");
    var updateStore = new AgentUpdateStore(statePath, requestPath);
    var state = await updateStore.LoadAsync(CancellationToken.None)
        ?? throw new InvalidDataException("The staged update state is unavailable.");

    try
    {
        await WaitForProcessExitAsync(servicePid, TimeSpan.FromSeconds(90));
        var installStartedAt = DateTimeOffset.UtcNow;
        var exitCode = await RunMsiAsync(packagePath, logPath);
        if (exitCode is not 0 and not 3010)
        {
            await TryStartServiceAsync(serviceName);
            await updateStore.SaveAsync(
                state with
                {
                    Status = AgentUpdateStatuses.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Message = $"Windows Installer returned exit code {exitCode}. The previous installation was restored.",
                },
                CancellationToken.None);
            return exitCode;
        }

        await TryStartServiceAsync(serviceName);
        var healthy = await WaitForHealthyAgentAsync(
            healthPath,
            targetVersion,
            installStartedAt,
            TimeSpan.FromSeconds(90));
        await updateStore.SaveAsync(
            state with
            {
                Status = healthy ? AgentUpdateStatuses.Succeeded : AgentUpdateStatuses.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                ProgressPercent = healthy ? 100 : state.ProgressPercent,
                Message = healthy
                    ? $"Agent {targetVersion} is installed and healthy."
                    : $"Agent {targetVersion} was installed, but the post-update health check timed out. Review the update log.",
            },
            CancellationToken.None);
        updateStore.ClearInstallRequest();
        return healthy ? 0 : 3;
    }
    catch (Exception ex)
    {
        await TryStartServiceAsync(serviceName);
        await updateStore.SaveAsync(
            state with
            {
                Status = AgentUpdateStatuses.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = $"The update failed safely: {ex.Message}",
            },
            CancellationToken.None);
        return 1;
    }
}

static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        using var cancellation = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cancellation.Token);
    }
    catch (ArgumentException)
    {
        // Proses service sudah berhenti sebelum helper mulai menunggu.
    }
}

static async Task<int> RunMsiAsync(string packagePath, string logPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)
        ?? throw new InvalidOperationException("The update log path is invalid."));
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList =
        {
            "/i",
            packagePath,
            "/qn",
            "/norestart",
            "/L*v",
            logPath,
        },
    }) ?? throw new InvalidOperationException("Windows Installer could not be started.");
    await process.WaitForExitAsync();
    return process.ExitCode;
}

static async Task TryStartServiceAsync(string serviceName)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "start", serviceName },
        });
        if (process is not null)
        {
            await process.WaitForExitAsync();
        }
    }
    catch (Win32Exception)
    {
        // MSI biasanya sudah menyalakan service; kegagalan fallback diperiksa oleh health check.
    }
}

static async Task<bool> WaitForHealthyAgentAsync(
    string healthPath,
    string targetVersion,
    DateTimeOffset installedAt,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    var store = new AgentHealthStore(healthPath);
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            var health = await store.LoadAsync(CancellationToken.None);
            if (health is not null
                && string.Equals(health.AgentVersion, targetVersion, StringComparison.OrdinalIgnoreCase)
                && health.UpdatedAt >= installedAt)
            {
                return true;
            }
        }
        catch (IOException)
        {
            // Service mungkin sedang mengganti health snapshot secara atomik.
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    return false;
}
