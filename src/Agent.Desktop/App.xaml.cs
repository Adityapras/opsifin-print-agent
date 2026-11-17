using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Windows;
using Agent.Desktop.Services;

namespace Agent.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? trayMutex;
    private TrayIconService? trayIcon;
    private ControlInstanceCoordinator? controlInstance;

    public App()
    {
        // UI operasional memakai English secara konsisten tanpa mengubah zona waktu WIB.
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var controlRequested = e.Args.Contains("--control", StringComparer.OrdinalIgnoreCase);
        var trayOnly = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (controlRequested)
        {
            if (!IsAdministrator())
            {
                LaunchElevatedControlCenter();
                Shutdown();
                return;
            }

            controlInstance = new ControlInstanceCoordinator();
            if (!controlInstance.TryAcquire())
            {
                controlInstance.TryActivateExisting(TimeSpan.FromSeconds(1));
                Shutdown();
                return;
            }

            OpenControlCenter();
            controlInstance.StartListening(ActivateControlCenter);
            return;
        }

        var ownsTray = StartTray(showWelcome: !trayOnly);
        if (!trayOnly)
        {
            LaunchElevatedControlCenter();
        }

        if (!ownsTray)
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        trayIcon?.Dispose();
        trayMutex?.Dispose();
        controlInstance?.Dispose();
        base.OnExit(e);
    }

    private bool StartTray(bool showWelcome)
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var mutexName = $"Local\\Opsifin.Agent.Control.Tray.Session{sessionId}";
        trayMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            trayMutex.Dispose();
            trayMutex = null;
            return false;
        }

        trayIcon = new TrayIconService();
        trayIcon.OpenRequested += (_, _) => LaunchElevatedControlCenter();
        trayIcon.ExitRequested += (_, _) => Shutdown();
        trayIcon.Start(showWelcome);
        return true;
    }

    private void OpenControlCenter()
    {
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.Activate();
    }

    private void LaunchElevatedControlCenter()
    {
        try
        {
            var coordinator = new ControlInstanceCoordinator();
            if (coordinator.TryActivateExisting(TimeSpan.FromMilliseconds(400)))
            {
                coordinator.Dispose();
                return;
            }

            coordinator.Dispose();
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The application path is unavailable."),
                Arguments = "--control",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            trayIcon?.ShowMessage(
                "Agent Control was not opened",
                "Administrator approval is required to manage pairing and the Windows Service.");
        }
        catch (Exception ex)
        {
            trayIcon?.ShowMessage("Agent Control could not be opened", ex.Message);
        }
    }

    private void ActivateControlCenter()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is not { } window)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();
            // Toggle Topmost membuat Windows membawa window elevated ke depan tanpa
            // meninggalkannya selalu di atas aplikasi lain.
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        });
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
