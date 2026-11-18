using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Agent.Service;

internal sealed class WindowsDesktopCompanionManager(
    ILogger<WindowsDesktopCompanionManager> logger) : IDesktopCompanionManager
{
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private readonly string controlPath = Path.Combine(AppContext.BaseDirectory, "Opsifin.Agent.Control.exe");

    public async Task<IReadOnlyCollection<int>> StopInstalledInstancesAsync(
        CancellationToken cancellationToken)
    {
        var sessions = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("Opsifin.Agent.Control"))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!IsInstalledCompanion(process))
                    {
                        continue;
                    }

                    sessions.Add(process.SessionId);
                    process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // Proses selesai di antara enumerasi dan penghentian.
                }
                catch (Exception exception) when (exception is Win32Exception or NotSupportedException)
                {
                    logger.LogWarning(
                        exception,
                        "Agent Control process {ProcessId} could not be stopped during desktop recovery",
                        process.Id);
                }
            }
        }

        return sessions;
    }

    public IReadOnlyCollection<int> GetInteractiveSessionIds()
    {
        var sessions = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId > 0)
                    {
                        sessions.Add(process.SessionId);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Explorer dapat selesai di antara enumerasi dan pembacaan session.
                }
            }
        }

        return sessions;
    }

    public bool TryStartTray(int sessionId, out string? error)
    {
        try
        {
            return TryStartTrayCore(sessionId, out error);
        }
        catch (Exception exception)
        {
            // Interop desktop tidak boleh menjadi single point of failure untuk service.
            error = exception.Message;
            logger.LogWarning(
                exception,
                "Agent Control relaunch failed safely in Windows session {SessionId}",
                sessionId);
            return false;
        }
    }

    private bool TryStartTrayCore(int sessionId, out string? error)
    {
        error = null;
        if (!File.Exists(controlPath))
        {
            error = "The installed Agent Control executable is missing.";
            return false;
        }

        if (!TryGetInteractiveUserToken(sessionId, out var userToken, out error))
        {
            return false;
        }

        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, userToken, inherit: false))
            {
                error = LastWin32Error();
                return false;
            }

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = "winsta0\\default",
            };
            var commandLine = new StringBuilder($"\"{controlPath}\" --tray");
            if (!CreateProcessAsUser(
                    userToken,
                    controlPath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    CreateNewProcessGroup | CreateUnicodeEnvironment,
                    environment,
                    AppContext.BaseDirectory,
                    ref startup,
                    out var processInfo))
            {
                error = LastWin32Error();
                return false;
            }

            CloseHandle(processInfo.Thread);
            CloseHandle(processInfo.Process);
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            CloseHandle(userToken);
        }
    }

    private bool IsInstalledCompanion(Process process)
    {
        var executable = process.MainModule?.FileName;
        return executable is not null
            && string.Equals(
                Path.GetFullPath(executable),
                Path.GetFullPath(controlPath),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetInteractiveUserToken(
        int sessionId,
        out IntPtr userToken,
        out string? error)
    {
        userToken = IntPtr.Zero;
        error = "No interactive Windows shell is active in this session.";
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId)
                    {
                        continue;
                    }

                    if (OpenProcessToken(
                            process.Handle,
                            TokenAssignPrimary | TokenDuplicate | TokenQuery,
                            out userToken))
                    {
                        error = null;
                        return true;
                    }

                    error = LastWin32Error();
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    error = exception.Message;
                }
            }
        }

        return false;
    }

    private static string LastWin32Error() =>
        new Win32Exception(Marshal.GetLastWin32Error()).Message;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }
}
