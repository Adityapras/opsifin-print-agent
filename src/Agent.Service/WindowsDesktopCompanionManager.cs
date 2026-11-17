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

    public bool TryStartTray(int sessionId, out string? error)
    {
        error = null;
        if (!File.Exists(controlPath))
        {
            error = "The installed Agent Control executable is missing.";
            return false;
        }

        if (!WtsQueryUserToken((uint)sessionId, out var userToken))
        {
            error = LastWin32Error();
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

    private static string LastWin32Error() =>
        new Win32Exception(Marshal.GetLastWin32Error()).Message;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsQueryUserToken(uint sessionId, out IntPtr token);

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
