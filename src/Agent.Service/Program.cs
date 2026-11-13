using Agent.Service;
using Agent.Hub;
using Agent.Local;
using Agent.Printing;
using Agent.Runtime;
using Microsoft.Extensions.Hosting.WindowsServices;

// Spike B2: `Agent.Service.exe --list-printers` mencetak printer yang terlihat dari akun
// user saat ini lalu keluar, tanpa menyalakan host.
if (args.Contains("--list-printers"))
{
    var report = PrinterReport.Build("console");
    Console.WriteLine(report);
    Console.WriteLine($"The report was saved to {PrinterReport.Save(report)}");
    return;
}

if (args.Length > 0 && string.Equals(args[0], "enroll", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await EnrollmentCommand.RunAsync(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "print-file", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await PrintFileCommand.RunAsync(args[1..]);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
var localRuntimeSettings = await LoadLocalRuntimeSettingsAsync();
var executionOptions = new AgentExecutionOptions(
    localRuntimeSettings?.SimulationMode
        ?? builder.Configuration.GetValue("Agent:SimulationMode", false));
builder.Logging.AddProvider(new JsonFileLoggerProvider(AgentStatePaths.LogDirectory, TimeProvider.System));

// Saat dijalankan oleh Service Control Manager, host memakai lifetime Windows service
// dan log ke Event Log; saat dijalankan dari console (dev), perilakunya biasa.
builder.Services.AddWindowsService(options => options.ServiceName = AgentProduct.ServiceDisplayName);
builder.Services.AddSingleton(new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30),
});
builder.Services.AddSingleton(new AuthenticatedUpdateDownloader(new HttpClient
{
    // Paket MSI jauh lebih besar daripada heartbeat/PDF metadata; timeout terpisah mencegah
    // koneksi client yang lambat menggagalkan update sehat setelah 30 detik.
    Timeout = TimeSpan.FromMinutes(15),
}));
builder.Services.AddSingleton<AgentHubClient>();
builder.Services.AddSingleton<SignedDocumentDownloader>();
builder.Services.AddSingleton<DeviceIdentityStore>();
builder.Services.AddSingleton<AgentHealthStore>();
builder.Services.AddSingleton<AgentUpdateStore>();
builder.Services.AddSingleton<AgentHealthPublisher>();
builder.Services.AddSingleton(_ => new EventOutbox(AgentStatePaths.OutboxDatabase));
builder.Services.AddSingleton<EventOutboxDispatcher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(executionOptions);
if (executionOptions.SimulationMode)
{
    builder.Services.AddSingleton<SimulatedPrintSystem>();
    builder.Services.AddSingleton<IPrintEngine>(provider => provider.GetRequiredService<SimulatedPrintSystem>());
    builder.Services.AddSingleton<ISpoolerStatusSource>(provider => provider.GetRequiredService<SimulatedPrintSystem>());
    builder.Services.AddSingleton<ISpoolerJobController>(provider => provider.GetRequiredService<SimulatedPrintSystem>());
}
else
{
    builder.Services.AddSingleton<IPrintEngine, PdfiumPrintEngine>();
    builder.Services.AddSingleton<ISpoolerStatusSource, Win32SpoolerStatusSource>();
    builder.Services.AddSingleton<ISpoolerJobController, Win32SpoolerJobController>();
}

builder.Services.AddSingleton<SpoolerJobMonitor>();
builder.Services.AddSingleton(new JobProcessorOptions(
    AgentStatePaths.DocumentsDirectory,
    executionOptions.SimulationMode ? TimeSpan.Zero : TimeSpan.FromSeconds(60),
    executionOptions.SimulationMode ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1)));
builder.Services.AddSingleton(new PrintJobJournal(AgentStatePaths.PrintJournalDirectory));
builder.Services.AddSingleton(new CommandJournal(AgentStatePaths.CommandJournalDirectory));
builder.Services.AddSingleton(new AgentLogArchiveOptions(
    AgentStatePaths.LogDirectory,
    AgentStatePaths.CommandAttachmentDirectory));
builder.Services.AddSingleton<AgentLogArchive>();
builder.Services.AddSingleton(new TestPagePrinterOptions(
    AgentStatePaths.DocumentsDirectory,
    executionOptions.SimulationMode ? TimeSpan.Zero : TimeSpan.FromSeconds(60),
    executionOptions.SimulationMode ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(1)));
builder.Services.AddSingleton<TestPagePrinter>();
builder.Services.AddSingleton<JobProcessor>();
builder.Services.AddSingleton<JobCoordinator>();
builder.Services.AddSingleton<AgentCommandHandler>();
builder.Services.AddSingleton<AgentUpdateCoordinator>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentUpdateCoordinator>());

var host = builder.Build();
host.Run();

static async Task<AgentRuntimeSettings?> LoadLocalRuntimeSettingsAsync()
{
    try
    {
        return await new AgentRuntimeSettingsStore().LoadAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        // File lokal yang rusak tidak boleh diam-diam mengaktifkan simulasi. Production dari
        // appsettings menjadi fallback aman dan detail kerusakan tetap masuk ke bootstrap log.
        Console.Error.WriteLine($"Local runtime settings could not be read: {ex.Message}");
        return null;
    }
}
