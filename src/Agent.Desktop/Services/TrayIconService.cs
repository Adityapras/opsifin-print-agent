using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Agent.Local;
using Forms = System.Windows.Forms;

namespace Agent.Desktop.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly WindowsServiceManager serviceManager = new();
    private readonly AgentUpdateStore updateStore = new();
    private readonly DispatcherTimer refreshTimer;
    private readonly Icon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ToolStripMenuItem statusItem;
    private readonly Forms.ToolStripMenuItem updateItem;
    private Guid? notifiedUpdateId;
    private bool disposed;

    public TrayIconService()
    {
        icon = LoadIcon();
        statusItem = new Forms.ToolStripMenuItem("Service: Checking…")
        {
            Enabled = false,
        };

        var openItem = new Forms.ToolStripMenuItem("Open Agent Control");
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var refreshItem = new Forms.ToolStripMenuItem("Refresh service status");
        refreshItem.Click += async (_, _) => await RefreshStatusAsync();

        updateItem = new Forms.ToolStripMenuItem("Software update")
        {
            Visible = false,
        };
        updateItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new Forms.ToolStripMenuItem("Exit tray icon");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu = new Forms.ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(updateItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = icon,
            Text = "Opsifin Print Agent — Checking service",
        };
        notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        notifyIcon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    public void Start(bool showWelcome)
    {
        notifyIcon.Visible = true;
        refreshTimer.Start();
        _ = RefreshStatusAsync();
        if (showWelcome)
        {
            ShowMessage(
                "Opsifin Print Agent is ready",
                "The agent continues running in the background. Double-click this icon to open Agent Control.");
        }
    }

    public void ShowMessage(string title, string message)
    {
        if (disposed)
        {
            return;
        }

        notifyIcon.BalloonTipTitle = title;
        notifyIcon.BalloonTipText = message;
        notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        notifyIcon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        refreshTimer.Stop();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        menu.Dispose();
        icon.Dispose();
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var status = await serviceManager.GetStatusAsync();
            statusItem.Text = $"Service: {status.State}";
            notifyIcon.Text = status.IsRunning
                ? "Opsifin Print Agent — Service running"
                : $"Opsifin Print Agent — {status.State}";

            try
            {
                var update = await updateStore.LoadAsync(CancellationToken.None);
                updateItem.Visible = update is not null
                    && update.Status is not AgentUpdateStatuses.Succeeded;
                if (update is not null)
                {
                    updateItem.Text = update.Status switch
                    {
                        AgentUpdateStatuses.Ready => $"Install update {update.TargetVersion}",
                        AgentUpdateStatuses.Downloading => $"Downloading update {update.TargetVersion}…",
                        AgentUpdateStatuses.WaitingForIdle => "Update waiting for printing",
                        AgentUpdateStatuses.Installing => $"Installing update {update.TargetVersion}…",
                        AgentUpdateStatuses.Failed => $"Update {update.TargetVersion} needs attention",
                        _ => $"Update {update.TargetVersion}: {update.Status.Replace('_', ' ')}",
                    };
                    updateItem.Enabled = update.Status is AgentUpdateStatuses.Ready or AgentUpdateStatuses.Failed;

                    if (update.Status == AgentUpdateStatuses.Ready && notifiedUpdateId != update.CommandId)
                    {
                        notifiedUpdateId = update.CommandId;
                        ShowMessage(
                            $"Agent {update.TargetVersion} is ready",
                            "The update package is ready. Open Agent Control to install it after current printing finishes.");
                    }
                }
            }
            catch
            {
                // Kerusakan status update tidak boleh menyembunyikan status Windows Service.
                updateItem.Visible = true;
                updateItem.Enabled = true;
                updateItem.Text = "Update status needs attention";
            }
        }
        catch
        {
            // Tray tetap tersedia sebagai pintu masuk meskipun pembacaan SCM sementara gagal.
            statusItem.Text = "Service: Status unavailable";
            notifyIcon.Text = "Opsifin Print Agent — Status unavailable";
        }
    }

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/opsifin-agent.ico"))
            ?? throw new InvalidOperationException("The tray icon resource is unavailable.");
        using var source = new Icon(resource.Stream);
        return (Icon)source.Clone();
    }
}
