using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Agent.Desktop.Infrastructure;
using Agent.Desktop.Services;
using Agent.Hub;
using Agent.Local;

namespace Agent.Desktop.ViewModels;

internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DeviceIdentityStore identityStore = new();
    private readonly AgentHealthStore healthStore = new();
    private readonly AgentRuntimeSettingsStore runtimeSettingsStore = new();
    private readonly AgentUpdateStore updateStore = new();
    private readonly WindowsServiceManager serviceManager = new();
    private readonly LocalLogReader logReader = new();
    private readonly SystemCheckService systemCheckService = new();
    private readonly SystemCheckReportExporter systemCheckExporter = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private AgentWindowsServiceStatus latestServiceStatus = new(false, "Not installed", false);
    private DeviceIdentity? latestIdentity;
    private AgentHealthSnapshot? latestHealth;
    private AgentUpdateState? latestUpdate;
    private SystemCheckReport? latestSystemCheck;
    private string currentPage = "dashboard";
    private string overallTitle = "Checking agent";
    private string overallDescription = "Reading the Windows Service and Hub connection status…";
    private string overallKind = "neutral";
    private string serviceState = "Checking";
    private string hubState = "Checking";
    private string lastHeartbeat = "—";
    private string lastError = "No active errors";
    private string printerCount = "0";
    private string configVersion = "0";
    private string version = AgentProduct.Version;
    private string activeJobCountText = "0 IN PROGRESS";
    private string claimIntervalDescription = "Waiting for the Agent polling configuration";
    private string sourcePollDescription = string.Empty;
    private string agentId = "Not paired";
    private string hubAddress = "—";
    private string siteId = "—";
    private string runtimeMode = "Production";
    private string lastStatusRefresh = "Waiting for first refresh";
    private bool isServiceInstalled;
    private bool isServiceRunning;
    private bool isPaired;
    private bool isBusy;
    private bool isSimulationModeEnabled;
    private bool isSimulationModeActive;
    private string pairingHubUrl = "http://localhost:8000";
    private string pairingCode = string.Empty;
    private string operationMessage = string.Empty;
    private string operationKind = "neutral";
    private string systemCheckTitle = "Not checked yet";
    private string systemCheckDescription = "Run this check before pairing or deploying to a client PC.";
    private string systemCheckKind = "neutral";
    private string systemCheckGeneratedAt = "Never run";
    private string systemCheckExportMessage = string.Empty;

    public MainViewModel()
    {
        ShowDashboardCommand = new RelayCommand(() => CurrentPage = "dashboard");
        ShowSetupCommand = new RelayCommand(() => CurrentPage = "setup");
        ShowSystemCheckCommand = new RelayCommand(() => CurrentPage = "system-check");
        ShowDiagnosticsCommand = new RelayCommand(() => CurrentPage = "diagnostics");
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        PairCommand = new AsyncRelayCommand(PairAsync, () => !IsBusy && !IsPaired);
        ResetPairingCommand = new AsyncRelayCommand(ResetPairingAsync, () => !IsBusy && IsPaired);
        StartServiceCommand = new AsyncRelayCommand(StartServiceAsync, () => !IsBusy && IsServiceInstalled && !IsServiceRunning);
        RestartServiceCommand = new AsyncRelayCommand(RestartServiceAsync, () => !IsBusy && IsServiceInstalled && IsServiceRunning);
        StopServiceCommand = new AsyncRelayCommand(StopServiceAsync, () => !IsBusy && IsServiceInstalled && IsServiceRunning);
        ToggleSimulationModeCommand = new AsyncRelayCommand(ToggleSimulationModeAsync, () => !IsBusy);
        InstallUpdateCommand = new AsyncRelayCommand(
            InstallUpdateAsync,
            () => !IsBusy && latestUpdate?.Status == AgentUpdateStatuses.Ready);
        RunSystemCheckCommand = new AsyncRelayCommand(RunSystemCheckAsync, () => !IsBusy);
        ExportSystemCheckCommand = new AsyncRelayCommand(
            ExportSystemCheckAsync,
            () => !IsBusy && latestSystemCheck is not null);
        OpenLogsCommand = new RelayCommand(OpenLogsFolder);
    }

    public string Version { get => version; private set => SetProperty(ref version, value); }

    public string MachineName => Environment.MachineName;

    public ObservableCollection<LocalLogEntry> Logs { get; } = [];

    public ObservableCollection<DetectedPrinterViewModel> Printers { get; } = [];

    public ObservableCollection<ActivePrintJobViewModel> ActivePrintJobs { get; } = [];

    public ObservableCollection<SystemCheckItem> SystemCheckItems { get; } = [];

    public RelayCommand ShowDashboardCommand { get; }

    public RelayCommand ShowSetupCommand { get; }

    public RelayCommand ShowSystemCheckCommand { get; }

    public RelayCommand ShowDiagnosticsCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PairCommand { get; }

    public AsyncRelayCommand ResetPairingCommand { get; }

    public AsyncRelayCommand StartServiceCommand { get; }

    public AsyncRelayCommand RestartServiceCommand { get; }

    public AsyncRelayCommand StopServiceCommand { get; }

    public AsyncRelayCommand ToggleSimulationModeCommand { get; }

    public AsyncRelayCommand InstallUpdateCommand { get; }

    public AsyncRelayCommand RunSystemCheckCommand { get; }

    public AsyncRelayCommand ExportSystemCheckCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public string CurrentPage
    {
        get => currentPage;
        set
        {
            if (SetProperty(ref currentPage, value))
            {
                OnPropertyChanged(nameof(DashboardVisibility));
                OnPropertyChanged(nameof(SetupVisibility));
                OnPropertyChanged(nameof(SystemCheckVisibility));
                OnPropertyChanged(nameof(DiagnosticsVisibility));
            }
        }
    }

    public Visibility DashboardVisibility => CurrentPage == "dashboard" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SetupVisibility => CurrentPage == "setup" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SystemCheckVisibility => CurrentPage == "system-check" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DiagnosticsVisibility => CurrentPage == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;

    public string OverallTitle { get => overallTitle; private set => SetProperty(ref overallTitle, value); }

    public string OverallDescription { get => overallDescription; private set => SetProperty(ref overallDescription, value); }

    public string OverallKind { get => overallKind; private set => SetProperty(ref overallKind, value); }

    public string ServiceState { get => serviceState; private set => SetProperty(ref serviceState, value); }

    public string HubState { get => hubState; private set => SetProperty(ref hubState, value); }

    public string LastHeartbeat { get => lastHeartbeat; private set => SetProperty(ref lastHeartbeat, value); }

    public string LastError { get => lastError; private set => SetProperty(ref lastError, value); }

    public string PrinterCount { get => printerCount; private set => SetProperty(ref printerCount, value); }

    public Visibility PrinterListVisibility => Printers.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PrinterEmptyVisibility => Printers.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ActiveJobCountText
    {
        get => activeJobCountText;
        private set => SetProperty(ref activeJobCountText, value);
    }

    public string ClaimIntervalDescription
    {
        get => claimIntervalDescription;
        private set => SetProperty(ref claimIntervalDescription, value);
    }

    public string SourcePollDescription
    {
        get => sourcePollDescription;
        private set
        {
            if (SetProperty(ref sourcePollDescription, value))
            {
                OnPropertyChanged(nameof(SourcePollVisibility));
            }
        }
    }

    public Visibility ActiveJobListVisibility => ActivePrintJobs.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ActiveJobEmptyVisibility => ActivePrintJobs.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SourcePollVisibility => string.IsNullOrWhiteSpace(SourcePollDescription)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string ConfigVersion { get => configVersion; private set => SetProperty(ref configVersion, value); }

    public string AgentId { get => agentId; private set => SetProperty(ref agentId, value); }

    public string HubAddress { get => hubAddress; private set => SetProperty(ref hubAddress, value); }

    public string SiteId { get => siteId; private set => SetProperty(ref siteId, value); }

    public string RuntimeMode { get => runtimeMode; private set => SetProperty(ref runtimeMode, value); }

    public string LastStatusRefresh { get => lastStatusRefresh; private set => SetProperty(ref lastStatusRefresh, value); }

    public bool IsSimulationModeEnabled
    {
        get => isSimulationModeEnabled;
        private set
        {
            if (SetProperty(ref isSimulationModeEnabled, value))
            {
                OnPropertyChanged(nameof(SimulationModeTitle));
                OnPropertyChanged(nameof(SimulationModeDescription));
                OnPropertyChanged(nameof(SimulationModeButtonText));
                OnPropertyChanged(nameof(SimulationModeBadge));
                OnPropertyChanged(nameof(SimulationModeWarningVisibility));
            }
        }
    }

    public string SimulationModeTitle => IsSimulationModeEnabled
        ? "Simulation mode is enabled"
        : "Production printing is enabled";

    public string SimulationModeDescription => IsSimulationModeEnabled
        ? "Jobs from Print Hub run through claim, download, simulated spooler, and completion without printing paper."
        : "Jobs are sent to the selected Windows printer and require an unattended physical or network printer.";

    public string SimulationModeButtonText => IsSimulationModeEnabled
        ? "Return to production mode"
        : "Enable simulation mode";

    public string SimulationModeBadge => IsSimulationModeEnabled
        ? "SIMULATION · NO PAPER"
        : "PRODUCTION";

    public Visibility SimulationModeWarningVisibility => IsSimulationModeEnabled
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsSimulationModeActive
    {
        get => isSimulationModeActive;
        private set
        {
            if (SetProperty(ref isSimulationModeActive, value))
            {
                OnPropertyChanged(nameof(SimulationModeActiveVisibility));
            }
        }
    }

    public Visibility SimulationModeActiveVisibility => IsSimulationModeActive
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility UpdateVisibility => latestUpdate is null
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string UpdateTitle => latestUpdate?.Status switch
    {
        AgentUpdateStatuses.PendingDownload => "Update scheduled",
        AgentUpdateStatuses.Downloading => "Downloading software update",
        AgentUpdateStatuses.Verifying => "Verifying software update",
        AgentUpdateStatuses.Ready => $"Agent {latestUpdate.TargetVersion} is ready",
        AgentUpdateStatuses.WaitingForIdle => "Waiting for printing to finish",
        AgentUpdateStatuses.Installing => "Installing software update",
        AgentUpdateStatuses.Succeeded => "Agent update completed",
        AgentUpdateStatuses.Failed => "Agent update needs attention",
        _ => "Software update",
    };

    public string UpdateDescription => latestUpdate?.Message
        ?? "The Agent is up to date.";

    public string UpdateVersionText => latestUpdate is null
        ? string.Empty
        : $"{latestUpdate.CurrentVersion}  →  {latestUpdate.TargetVersion}";

    public double UpdateProgress => latestUpdate?.ProgressPercent ?? 0;

    public string UpdateProgressText => $"{latestUpdate?.ProgressPercent ?? 0}%";

    public string UpdateKind => latestUpdate?.Status switch
    {
        AgentUpdateStatuses.Failed => "danger",
        AgentUpdateStatuses.Succeeded => "success",
        AgentUpdateStatuses.Ready => "ready",
        _ => "active",
    };

    public Visibility InstallUpdateVisibility => latestUpdate?.Status == AgentUpdateStatuses.Ready
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string SystemCheckTitle { get => systemCheckTitle; private set => SetProperty(ref systemCheckTitle, value); }

    public string SystemCheckDescription { get => systemCheckDescription; private set => SetProperty(ref systemCheckDescription, value); }

    public string SystemCheckKind { get => systemCheckKind; private set => SetProperty(ref systemCheckKind, value); }

    public string SystemCheckGeneratedAt { get => systemCheckGeneratedAt; private set => SetProperty(ref systemCheckGeneratedAt, value); }

    public string SystemCheckPassedCount => latestSystemCheck?.SuccessCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";

    public string SystemCheckWarningCount => latestSystemCheck?.WarningCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";

    public string SystemCheckFailedCount => latestSystemCheck?.DangerCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";

    public string SystemCheckButtonText => IsBusy ? "Running checks…" : "Run system check";

    public Visibility SystemCheckResultsVisibility => SystemCheckItems.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SystemCheckEmptyVisibility => SystemCheckItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string SystemCheckExportMessage
    {
        get => systemCheckExportMessage;
        private set
        {
            if (SetProperty(ref systemCheckExportMessage, value))
            {
                OnPropertyChanged(nameof(SystemCheckExportMessageVisibility));
            }
        }
    }

    public Visibility SystemCheckExportMessageVisibility => string.IsNullOrWhiteSpace(SystemCheckExportMessage)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsServiceInstalled
    {
        get => isServiceInstalled;
        private set
        {
            if (SetProperty(ref isServiceInstalled, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsServiceRunning
    {
        get => isServiceRunning;
        private set
        {
            if (SetProperty(ref isServiceRunning, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsPaired
    {
        get => isPaired;
        private set
        {
            if (SetProperty(ref isPaired, value))
            {
                OnPropertyChanged(nameof(PairingStatusText));
                OnPropertyChanged(nameof(ResetPairingVisibility));
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(PairButtonText));
                OnPropertyChanged(nameof(SystemCheckButtonText));
                RaiseCommandStates();
            }
        }
    }

    public string PairingHubUrl
    {
        get => pairingHubUrl;
        set => SetProperty(ref pairingHubUrl, value);
    }

    public string PairingCode
    {
        get => pairingCode;
        set => SetProperty(ref pairingCode, value);
    }

    public string PairButtonText => IsBusy ? "Connecting…" : "Connect agent";

    public string PairingStatusText => IsPaired
        ? "This device is connected. Printer configuration and routing are managed by the Hub."
        : "Enter the Hub address and the 8-character pairing code created by an administrator.";

    public Visibility ResetPairingVisibility => IsPaired ? Visibility.Visible : Visibility.Collapsed;

    public string OperationMessage
    {
        get => operationMessage;
        private set
        {
            if (SetProperty(ref operationMessage, value))
            {
                OnPropertyChanged(nameof(OperationVisibility));
            }
        }
    }

    public string OperationKind { get => operationKind; private set => SetProperty(ref operationKind, value); }

    public Visibility OperationVisibility => string.IsNullOrWhiteSpace(OperationMessage)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public async Task StartAsync()
    {
        await RefreshAsync();
        _ = RunRefreshLoopAsync(lifetime.Token);
    }

    public void Dispose()
    {
        lifetime.Cancel();
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await RefreshAsync();
                }
                catch (Exception ex)
                {
                    SetOperation($"Local status could not be refreshed: {ex.Message}", "warning");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Menutup jendela menghentikan polling status secara normal.
        }
    }

    private async Task RefreshAsync()
    {
        if (!await refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var service = await serviceManager.GetStatusAsync();
            latestServiceStatus = service;
            IsServiceInstalled = service.Installed;
            IsServiceRunning = service.IsRunning;
            ServiceState = service.State;

            DeviceIdentity? identity = null;
            try
            {
                identity = await identityStore.LoadAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LastError = $"The local identity could not be read: {ex.Message}";
            }

            IsPaired = identity is not null;
            latestIdentity = identity;
            AgentId = identity?.AgentId.ToString("D") ?? "Not paired";
            HubAddress = identity?.HubBaseAddress.GetLeftPart(UriPartial.Authority) ?? "—";
            SiteId = identity?.SiteId.ToString("D") ?? "—";

            AgentHealthSnapshot? health = null;
            try
            {
                health = await healthStore.LoadAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LastError = $"The health snapshot could not be read: {ex.Message}";
            }

            AgentRuntimeSettings? runtimeSettings = null;
            try
            {
                runtimeSettings = await runtimeSettingsStore.LoadAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LastError = $"The local printing mode could not be read: {ex.Message}";
            }

            AgentUpdateState? update = null;
            try
            {
                update = await updateStore.LoadAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LastError = $"The local update status could not be read: {ex.Message}";
            }

            ApplyHealth(service, health);
            IsSimulationModeEnabled = runtimeSettings?.SimulationMode ?? health?.SimulationMode == true;
            ApplyUpdate(update);
            latestHealth = health;
            var logs = await logReader.ReadLatestAsync(120);
            Logs.Clear();
            foreach (var entry in logs)
            {
                Logs.Add(entry);
            }

            LastStatusRefresh = $"Last refreshed {IndonesiaTime.Now(TimeProvider.System):HH:mm:ss} WIB";
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private void ApplyHealth(AgentWindowsServiceStatus service, AgentHealthSnapshot? health)
    {
        Version = AgentVersionResolver.Resolve(AgentProduct.Version, health?.AgentVersion);
        IsSimulationModeActive = health?.SimulationMode == true;
        RuntimeMode = IsSimulationModeActive ? "SIMULATION · NO PAPER" : "Production";
        PrinterCount = health?.PrinterCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        ApplyPrinters(health?.Printers);
        ConfigVersion = health?.ConfigVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        ClaimIntervalDescription = health is null
            ? "Waiting for the new-job polling configuration"
            : $"New print jobs are checked every {FormatInterval(health.ClaimIntervalSeconds)}";
        SourcePollDescription = health?.SourcePollIntervalSeconds is { } sourceInterval
            ? $"Print Hub refreshes invoice data every {FormatInterval(sourceInterval)}"
            : string.Empty;
        LastHeartbeat = health?.LastHeartbeatSuccessAt is { } heartbeat
            ? $"{IndonesiaTime.Convert(heartbeat):dd MMM yyyy, HH:mm:ss} WIB"
            : "Not available";
        LastError = health?.LastErrorMessage ?? "No active errors";

        var snapshotIsFresh = health is not null
            && DateTimeOffset.UtcNow - health.UpdatedAt <= TimeSpan.FromSeconds(90);
        ApplyActivePrintJobs(snapshotIsFresh ? health!.ActivePrintJobs : null);
        var consoleProcessIsActive = snapshotIsFresh && IsProcessActive(health!.ProcessId);
        if (!service.Installed && consoleProcessIsActive)
        {
            ServiceState = "Console mode";
            IsServiceRunning = true;
            OverallKind = health!.ConnectionState == AgentConnectionStates.Online ? "warning" : "neutral";
            OverallTitle = health.ConnectionState == AgentConnectionStates.Online
                ? "Agent connected in console mode"
                : "Agent running in console mode";
            OverallDescription = "The connection is active for development, but the MSI is required to start the agent automatically after Windows restarts.";
            HubState = health.ConnectionState == AgentConnectionStates.Online ? "Connected" : "Connecting";
            return;
        }

        if (!service.Installed)
        {
            OverallKind = "danger";
            OverallTitle = "Service is not installed";
            OverallDescription = "Run the MSI installer so the agent starts automatically with Windows.";
            HubState = IsPaired ? "Identity available" : "Not connected";
            return;
        }

        if (!service.IsRunning)
        {
            OverallKind = "danger";
            OverallTitle = "Agent is stopped";
            OverallDescription = health?.ConnectionState == AgentConnectionStates.Stopping
                ? "The service stopped normally. Select Start service to enable it again."
                : "The service is not running and may have stopped unexpectedly. Review the logs, then restart it.";
            HubState = "Inactive";
            return;
        }

        if (health is null)
        {
            OverallKind = "warning";
            OverallTitle = "Waiting for service status";
            OverallDescription = "The Windows Service is running, but no health snapshot is available yet.";
            HubState = "Checking";
            return;
        }

        if (!snapshotIsFresh)
        {
            var presentation = AgentStatusPresentationFactory.ForStaleSnapshot(
                health,
                IsProcessActive(health.ProcessId));
            OverallKind = presentation.Kind;
            OverallTitle = presentation.Title;
            OverallDescription = presentation.Description;
            HubState = presentation.HubState;
            return;
        }

        switch (health.ConnectionState)
        {
            case AgentConnectionStates.Online:
                OverallKind = "success";
                OverallTitle = "Agent is healthy and connected";
                OverallDescription = "The service, Hub authentication, heartbeat, and remote configuration are working normally.";
                HubState = "Connected";
                break;
            case AgentConnectionStates.AwaitingPairing:
                OverallKind = "warning";
                OverallTitle = "Agent is waiting for pairing";
                OverallDescription = "Open Pairing & setup to connect this device to the Hub.";
                HubState = "Not paired";
                break;
            case AgentConnectionStates.AuthenticationFailed:
                OverallKind = "danger";
                OverallTitle = "Authentication rejected by the Hub";
                OverallDescription = "The identity was revoked or its secret is no longer valid. Review the agent in the Hub.";
                HubState = "Rejected";
                break;
            case AgentConnectionStates.UpdateRequired:
                OverallKind = "warning";
                OverallTitle = "Agent update required";
                OverallDescription = "The Hub rejected this agent version. Install the latest release to continue.";
                HubState = "Update required";
                break;
            case AgentConnectionStates.Degraded:
                OverallKind = "warning";
                OverallTitle = "Agent connection is degraded";
                OverallDescription = "The service remains active and will reconnect automatically.";
                HubState = "Retrying";
                break;
            default:
                OverallKind = "neutral";
                OverallTitle = "Agent is preparing the connection";
                OverallDescription = "The service is loading its identity and Hub configuration.";
                HubState = "Connecting";
                break;
        }
    }

    private void ApplyUpdate(AgentUpdateState? update)
    {
        latestUpdate = update;
        OnPropertyChanged(nameof(UpdateVisibility));
        OnPropertyChanged(nameof(UpdateTitle));
        OnPropertyChanged(nameof(UpdateDescription));
        OnPropertyChanged(nameof(UpdateVersionText));
        OnPropertyChanged(nameof(UpdateProgress));
        OnPropertyChanged(nameof(UpdateProgressText));
        OnPropertyChanged(nameof(UpdateKind));
        OnPropertyChanged(nameof(InstallUpdateVisibility));
        InstallUpdateCommand.RaiseCanExecuteChanged();
    }

    private void ApplyPrinters(IReadOnlyCollection<LocalPrinterSnapshot>? snapshots)
    {
        Printers.Clear();
        foreach (var printer in snapshots ?? [])
        {
            Printers.Add(new DetectedPrinterViewModel(
                printer.Name,
                printer.Status,
                string.IsNullOrWhiteSpace(printer.Driver) ? "—" : printer.Driver,
                string.IsNullOrWhiteSpace(printer.Port) ? "—" : printer.Port,
                printer.QueueLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                printer.IsDefault ? Visibility.Visible : Visibility.Collapsed));
        }

        OnPropertyChanged(nameof(PrinterListVisibility));
        OnPropertyChanged(nameof(PrinterEmptyVisibility));
    }

    private void ApplyActivePrintJobs(IReadOnlyCollection<LocalActivePrintJobSnapshot>? snapshots)
    {
        ActivePrintJobs.Clear();
        foreach (var job in snapshots ?? [])
        {
            var elapsed = DateTimeOffset.UtcNow - job.StartedAt;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            ActivePrintJobs.Add(new ActivePrintJobViewModel(
                string.IsNullOrWhiteSpace(job.InvoiceNumber)
                    ? $"Job {job.JobId:N}"[..12].ToUpperInvariant()
                    : job.InvoiceNumber,
                string.IsNullOrWhiteSpace(job.ItemType) ? "Print job" : job.ItemType,
                job.WindowsPrinterName,
                FormatJobStage(job.Stage),
                job.Pages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}"));
        }

        ActiveJobCountText = $"{ActivePrintJobs.Count} IN PROGRESS";
        OnPropertyChanged(nameof(ActiveJobListVisibility));
        OnPropertyChanged(nameof(ActiveJobEmptyVisibility));
    }

    private static string FormatJobStage(string stage) => stage switch
    {
        LocalPrintJobStages.Claimed => "Claimed",
        LocalPrintJobStages.Downloading => "Downloading PDF",
        LocalPrintJobStages.SendingToPrinter => "Sending to printer",
        LocalPrintJobStages.Printing => "Printing",
        LocalPrintJobStages.VerifyingCompletion => "Verifying completion",
        _ => "Processing",
    };

    private static string FormatInterval(int seconds) => seconds switch
    {
        60 => "1 minute",
        > 60 when seconds % 60 == 0 => $"{seconds / 60} minutes",
        _ => $"{seconds} seconds",
    };

    private async Task PairAsync()
    {
        if (IsPaired)
        {
            SetOperation("This device is already paired. Revoke the existing agent in the Hub before pairing again.", "warning");
            return;
        }

        var code = PairingCode.Trim().ToUpperInvariant();
        if (code.Length != 8)
        {
            SetOperation("The pairing code must contain exactly 8 characters.", "danger");
            return;
        }

        IsBusy = true;
        SetOperation("Contacting the Hub and preparing the protected identity…", "neutral");
        try
        {
            var baseAddress = AgentHubClient.NormalizeBaseAddress(PairingHubUrl.Trim());
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var client = new AgentHubClient(httpClient);
            var response = await client.EnrollAsync(
                baseAddress,
                new EnrollRequest(
                    code,
                    Environment.MachineName,
                    RuntimeInformation.OSDescription,
                    AgentProduct.Version,
                    MachineIdentity.ReadMachineGuid()),
                CancellationToken.None);
            await identityStore.SaveAsync(
                new DeviceIdentity(
                    response.AgentId,
                    response.TenantId,
                    response.SiteId,
                    baseAddress,
                    response.DeviceSecret),
                CancellationToken.None);

            PairingCode = string.Empty;
            if (IsServiceInstalled)
            {
                if (IsServiceRunning)
                {
                    await serviceManager.RestartAsync();
                }
                else
                {
                    await serviceManager.StartAsync();
                }
            }

            SetOperation("Pairing completed. The secret is protected and the service is connecting to the Hub.", "success");
            CurrentPage = "dashboard";
        }
        catch (AgentApiException ex)
        {
            SetOperation($"The Hub rejected the pairing request: {ex.Message}", "danger");
        }
        catch (Exception ex)
        {
            SetOperation($"Pairing failed: {ex.Message}", "danger");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private Task StartServiceAsync() => RunServiceOperationAsync(
        "Starting the Windows Service…",
        "The service started successfully.",
        serviceManager.StartAsync);

    private Task RestartServiceAsync() => RunServiceOperationAsync(
        "Restarting the Windows Service…",
        "The service restarted successfully.",
        serviceManager.RestartAsync);

    private Task StopServiceAsync() => RunServiceOperationAsync(
        "Stopping the Windows Service…",
        "The service stopped successfully.",
        serviceManager.StopAsync);

    private async Task ToggleSimulationModeAsync()
    {
        var enableSimulation = !IsSimulationModeEnabled;
        var confirmation = System.Windows.MessageBox.Show(
            enableSimulation
                ? "Simulation mode completes Print Hub jobs without sending anything to a physical printer.\n\n"
                  + "Use this only for end-to-end testing. The Hub will receive normal progress and completion events.\n\n"
                  + "Enable simulation mode and restart the Agent service?"
                : "Production mode sends new jobs to the Windows printers configured by Print Hub.\n\n"
                  + "Make sure an unattended physical or network printer is ready before continuing.\n\n"
                  + "Return to production mode and restart the Agent service?",
            enableSimulation ? "Enable simulation mode" : "Return to production mode",
            MessageBoxButton.YesNo,
            enableSimulation ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        CurrentPage = "setup";
        SetOperation(
            enableSimulation
                ? "Saving simulation mode and restarting the Agent…"
                : "Saving production mode and restarting the Agent…",
            "neutral");
        try
        {
            await runtimeSettingsStore.SaveAsync(
                new AgentRuntimeSettings(enableSimulation),
                CancellationToken.None);
            IsSimulationModeEnabled = enableSimulation;

            if (IsServiceInstalled && IsServiceRunning)
            {
                await serviceManager.RestartAsync();
                SetOperation(
                    enableSimulation
                        ? "Simulation mode is active. Hub jobs will complete without paper output."
                        : "Production mode is active. New jobs will use the Windows printer configured by the Hub.",
                    "success");
            }
            else
            {
                SetOperation(
                    enableSimulation
                        ? "Simulation mode is saved and will activate when the Agent service starts."
                        : "Production mode is saved and will activate when the Agent service starts.",
                    "success");
            }
        }
        catch (Exception ex)
        {
            SetOperation($"The printing mode could not be changed: {ex.Message}", "danger");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task InstallUpdateAsync()
    {
        var update = latestUpdate;
        if (update?.Status != AgentUpdateStatuses.Ready)
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            $"Agent {update.TargetVersion} is verified and ready.\n\n"
            + "The Agent will stop accepting new jobs, wait for current printing and pending Hub events to finish, then restart automatically.\n\n"
            + "Active print jobs will not be force-stopped. Continue?",
            $"Install Agent {update.TargetVersion}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await updateStore.RequestInstallAsync(update.CommandId, CancellationToken.None);
            ApplyUpdate(update with
            {
                Message = "Installation requested. The Agent is preparing a safe update window.",
            });
            SetOperation(
                "The update request was sent. Current print activity will finish before the service restarts.",
                "success");
        }
        catch (Exception ex)
        {
            SetOperation($"The update could not be requested: {ex.Message}", "danger");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResetPairingAsync()
    {
        var confirmation = System.Windows.MessageBox.Show(
            "Pairing again will stop the service, close any console-mode agent, and archive the previous local identity.\n\n"
            + "Before continuing, revoke or remove the previous agent in the Hub to avoid duplicate devices.\n\n"
            + "Continue with the local pairing reset?",
            "Reset Opsifin Print Agent pairing",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        CurrentPage = "setup";
        SetOperation("Stopping the service and previous agent processes…", "neutral");
        try
        {
            if (IsServiceInstalled)
            {
                await serviceManager.StopAsync();
            }

            var consoleProcesses = await serviceManager.StopRemainingConsoleAgentsAsync();
            var archivePath = identityStore.ArchiveExisting();
            healthStore.Remove();

            if (IsServiceInstalled)
            {
                await serviceManager.StartAsync();
            }

            PairingCode = string.Empty;
            SetOperation(
                archivePath is null
                    ? "The local identity is already empty. Create a new pairing code in the Hub and complete the form above."
                    : $"The previous pairing was reset. {consoleProcesses} console process(es) were stopped. Create a new Hub pairing code and connect again.",
                "success");
        }
        catch (Exception ex)
        {
            SetOperation($"Pairing reset failed: {ex.Message}", "danger");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task RunServiceOperationAsync(string progress, string success, Func<Task> operation)
    {
        IsBusy = true;
        SetOperation(progress, "neutral");
        try
        {
            await operation();
            SetOperation(success, "success");
        }
        catch (Exception ex)
        {
            SetOperation($"The service operation failed: {ex.Message}", "danger");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task RunSystemCheckAsync()
    {
        IsBusy = true;
        SystemCheckExportMessage = string.Empty;
        SystemCheckTitle = "System check in progress";
        SystemCheckDescription = "Checking Windows, services, printers, storage, and the Hub connection…";
        SystemCheckKind = "neutral";
        try
        {
            await RefreshAsync();
            var fallbackHubAddress = TryGetFallbackHubAddress();
            var report = await systemCheckService.RunAsync(
                latestServiceStatus,
                latestIdentity,
                latestHealth,
                fallbackHubAddress,
                CancellationToken.None);
            ApplySystemCheck(report);
        }
        catch (Exception ex)
        {
            SystemCheckKind = "danger";
            SystemCheckTitle = "System check could not be completed";
            SystemCheckDescription = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportSystemCheckAsync()
    {
        if (latestSystemCheck is null)
        {
            return;
        }

        var generatedAt = latestSystemCheck.GeneratedAt;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save System Check report",
            Filter = "ZIP diagnostics (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"Opsifin-System-Check-{Environment.MachineName}-{generatedAt:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        SystemCheckExportMessage = "Preparing the diagnostic report without device credentials…";
        try
        {
            await systemCheckExporter.ExportAsync(
                dialog.FileName,
                latestSystemCheck,
                latestIdentity,
                latestHealth,
                Logs.ToArray(),
                CancellationToken.None);
            SystemCheckExportMessage = $"Report saved successfully: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            SystemCheckExportMessage = $"The report could not be saved: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySystemCheck(SystemCheckReport report)
    {
        latestSystemCheck = report;
        SystemCheckItems.Clear();
        foreach (var item in report.Items
            .OrderBy(item => item.Kind == SystemCheckKinds.Danger ? 0 : item.Kind == SystemCheckKinds.Warning ? 1 : 2))
        {
            SystemCheckItems.Add(item);
        }

        SystemCheckGeneratedAt = $"Checked {report.GeneratedAt:dd MMM yyyy, HH:mm:ss} WIB";
        if (report.DangerCount > 0)
        {
            SystemCheckKind = "danger";
            SystemCheckTitle = $"{report.DangerCount} issue(s) must be fixed";
            SystemCheckDescription = "Do not deploy to production until every FAILED check has been resolved.";
        }
        else if (report.WarningCount > 0)
        {
            SystemCheckKind = "warning";
            SystemCheckTitle = $"Ready with {report.WarningCount} warning(s)";
            SystemCheckDescription = "No critical failures were found, but review the warnings before test printing.";
        }
        else
        {
            SystemCheckKind = "success";
            SystemCheckTitle = "This computer is ready";
            SystemCheckDescription = "All service, printer, storage, and Hub connection checks passed.";
        }

        OnPropertyChanged(nameof(SystemCheckPassedCount));
        OnPropertyChanged(nameof(SystemCheckWarningCount));
        OnPropertyChanged(nameof(SystemCheckFailedCount));
        OnPropertyChanged(nameof(SystemCheckResultsVisibility));
        OnPropertyChanged(nameof(SystemCheckEmptyVisibility));
        RaiseCommandStates();
    }

    private Uri? TryGetFallbackHubAddress()
    {
        if (!Uri.TryCreate(PairingHubUrl.Trim(), UriKind.Absolute, out var address))
        {
            return null;
        }

        return address.Scheme is "http" or "https" ? address : null;
    }

    private static void OpenLogsFolder()
    {
        Directory.CreateDirectory(AgentStatePaths.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = AgentStatePaths.LogDirectory,
            UseShellExecute = true,
        });
    }

    private static bool IsProcessActive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void SetOperation(string message, string kind)
    {
        OperationKind = kind;
        OperationMessage = message;
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        PairCommand.RaiseCanExecuteChanged();
        ResetPairingCommand.RaiseCanExecuteChanged();
        StartServiceCommand.RaiseCanExecuteChanged();
        RestartServiceCommand.RaiseCanExecuteChanged();
        StopServiceCommand.RaiseCanExecuteChanged();
        ToggleSimulationModeCommand.RaiseCanExecuteChanged();
        InstallUpdateCommand.RaiseCanExecuteChanged();
        RunSystemCheckCommand.RaiseCanExecuteChanged();
        ExportSystemCheckCommand.RaiseCanExecuteChanged();
    }
}

internal sealed record DetectedPrinterViewModel(
    string Name,
    string Status,
    string Driver,
    string Port,
    string QueueLength,
    Visibility DefaultVisibility);

internal sealed record ActivePrintJobViewModel(
    string Reference,
    string Type,
    string Printer,
    string Stage,
    string Pages,
    string Elapsed);
