using System.Net;
using System.Security.Cryptography;
using Agent.Hub;
using Agent.Printing;
using Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace Agent.Tests;

public sealed class JobProcessorTests
{
    [Fact]
    public async Task Pipeline_simulasi_menghasilkan_event_sampai_completed()
    {
        var pdf = "%PDF-1.4\nopsifin-simulation"u8.ToArray();
        var fixture = await Fixture.CreateAsync(pdf);
        try
        {
            var progress = new List<JobProgressUpdate>();
            var result = await fixture.Processor.ProcessAsync(
                fixture.CreateJob(pdf, pages: 2),
                "Simulated Printer",
                (update, _) =>
                {
                    progress.Add(update);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.Equal(JobProcessOutcome.Completed, result.Outcome);
            Assert.NotNull(result.SpoolerJobId);
            var events = await fixture.Outbox.GetDueAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                100,
                CancellationToken.None);
            var types = events.Select(item => item.Event.Type).ToArray();
            Assert.True(Array.IndexOf(types, "download_started") < Array.IndexOf(types, "download_completed"));
            Assert.True(Array.IndexOf(types, "download_completed") < Array.IndexOf(types, "spooled"));
            Assert.True(Array.IndexOf(types, "spooled") < Array.IndexOf(types, "completed"));
            Assert.Contains("download_started", types);
            Assert.Contains("download_completed", types);
            Assert.Contains("spooled", types);
            Assert.Contains("spooler_status", types);
            Assert.Contains("completed", types);
            Assert.DoesNotContain("print_error", types);
            Assert.Contains(progress, update => update.Stage == JobProgressStages.Downloading);
            Assert.Contains(progress, update => update.Stage == JobProgressStages.SendingToPrinter);
            Assert.Contains(progress, update => update.Stage == JobProgressStages.Printing);
            Assert.Contains(progress, update => update.Stage == JobProgressStages.VerifyingCompletion);
            Assert.False(Directory.EnumerateFiles(fixture.DocumentsDirectory).Any());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Pdf_tidak_valid_menghasilkan_print_error_durable()
    {
        var invalidPdf = "NOT-A-PDF"u8.ToArray();
        var fixture = await Fixture.CreateAsync(invalidPdf);
        try
        {
            var result = await fixture.Processor.ProcessAsync(
                fixture.CreateJob(invalidPdf, pages: 1),
                "Simulated Printer",
                CancellationToken.None);

            Assert.Equal(JobProcessOutcome.Failed, result.Outcome);
            Assert.Equal("PDF_INVALID", result.ErrorCode);
            var events = await fixture.Outbox.GetDueAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                100,
                CancellationToken.None);
            Assert.Contains(events, item => item.Event.Type == "download_started");
            var error = Assert.Single(events, item => item.Event.Type == "print_error");
            Assert.Equal("PDF_INVALID", error.Event.Data?.GetProperty("error_code").GetString());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Abort_setelah_spooled_menghapus_job_spooler_dan_tidak_mengirim_completed()
    {
        var pdf = "%PDF-1.4\nabort-test"u8.ToArray();
        var printSystem = new HangingPrintSystem();
        var fixture = await Fixture.CreateAsync(pdf, printSystem, printSystem, printSystem);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            var result = await fixture.Processor.ProcessAsync(
                fixture.CreateJob(pdf, pages: 1),
                "Simulated Printer",
                cancellation.Token);

            Assert.Equal(JobProcessOutcome.Aborted, result.Outcome);
            Assert.True(printSystem.WasCancelled);
            var events = await fixture.Outbox.GetDueAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                100,
                CancellationToken.None);
            Assert.Contains(events, item => item.Event.Type == "spooled");
            Assert.DoesNotContain(events, item => item.Event.Type is "completed" or "print_error");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Kegagalan_snapshot_progress_tidak_menggagalkan_print_job()
    {
        var pdf = "%PDF-1.4\nprogress-observability"u8.ToArray();
        var fixture = await Fixture.CreateAsync(pdf);
        try
        {
            var result = await fixture.Processor.ProcessAsync(
                fixture.CreateJob(pdf, pages: 1),
                "Simulated Printer",
                (_, _) => throw new IOException("Simulated local snapshot failure"),
                CancellationToken.None);

            Assert.Equal(JobProcessOutcome.Completed, result.Outcome);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Job_yang_sudah_hilang_sebelum_poll_pertama_tetap_diverifikasi_selesai()
    {
        var pdf = "%PDF-1.4\nfast-physical-printer"u8.ToArray();
        var printSystem = new ImmediatelyCompletedPrintSystem();
        var fixture = await Fixture.CreateAsync(pdf, printSystem, printSystem, printSystem);
        try
        {
            var result = await fixture.Processor.ProcessAsync(
                fixture.CreateJob(pdf, pages: 1),
                "Fast Physical Printer",
                CancellationToken.None);

            Assert.Equal(JobProcessOutcome.Completed, result.Outcome);
            var events = await fixture.Outbox.GetDueAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                100,
                CancellationToken.None);
            Assert.Contains(events, item => item.Event.Type == "spooled");
            Assert.Contains(events, item => item.Event.Type == "completed");
            Assert.DoesNotContain(events, item => item.Event.Type == "print_error");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Printer_interaktif_gagal_cepat_dengan_error_durable()
    {
        var pdf = "%PDF-1.4\ninteractive-printer"u8.ToArray();
        var simulated = new SimulatedPrintSystem();
        var fixture = await Fixture.CreateAsync(
            pdf,
            new InteractivePrinterPrintEngine(),
            simulated,
            simulated);
        try
        {
            var result = await fixture.Processor.ProcessAsync(
                fixture.CreateJob(pdf, pages: 1),
                "Microsoft Print to PDF",
                CancellationToken.None);

            Assert.Equal(JobProcessOutcome.Failed, result.Outcome);
            Assert.Equal("PRINTER_REQUIRES_INTERACTION", result.ErrorCode);
            var events = await fixture.Outbox.GetDueAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                100,
                CancellationToken.None);
            var error = Assert.Single(events, item => item.Event.Type == "print_error");
            Assert.Equal(
                "PRINTER_REQUIRES_INTERACTION",
                error.Event.Data?.GetProperty("error_code").GetString());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string rootDirectory;

        private Fixture(
            string rootDirectory,
            byte[] responseBody,
            EventOutbox outbox,
            IPrintEngine printEngine,
            ISpoolerStatusSource statusSource,
            ISpoolerJobController jobController)
        {
            this.rootDirectory = rootDirectory;
            Outbox = outbox;
            DocumentsDirectory = Path.Combine(rootDirectory, "documents");

            var httpClient = new HttpClient(new DocumentHandler(responseBody));
            Processor = new JobProcessor(
                new SignedDocumentDownloader(httpClient),
                outbox,
                printEngine,
                new SpoolerJobMonitor(statusSource),
                jobController,
                new PrintJobJournal(Path.Combine(rootDirectory, "journal")),
                new JobProcessorOptions(
                    DocumentsDirectory,
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(1)),
                TimeProvider.System);
        }

        public EventOutbox Outbox { get; }

        public JobProcessor Processor { get; }

        public string DocumentsDirectory { get; }

        public static Task<Fixture> CreateAsync(byte[] responseBody)
        {
            var simulated = new SimulatedPrintSystem();
            return CreateAsync(responseBody, simulated, simulated, simulated);
        }

        public static async Task<Fixture> CreateAsync(
            byte[] responseBody,
            IPrintEngine printEngine,
            ISpoolerStatusSource statusSource,
            ISpoolerJobController jobController)
        {
            var root = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
            var outbox = new EventOutbox(Path.Combine(root, "agent.db"));
            await outbox.InitializeAsync(CancellationToken.None);
            return new Fixture(
                root,
                responseBody,
                outbox,
                printEngine,
                statusSource,
                jobController);
        }

        public ClaimedJob CreateJob(byte[] body, int pages)
        {
            var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            return new ClaimedJob(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow.AddMinutes(2),
                new ClaimedDocument(
                    new Uri("http://localhost/document.pdf"),
                    hash,
                    pages,
                    body.LongLength),
                new PrintSettings());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, true);
            }
        }

        private sealed class DocumentHandler(byte[] body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body),
                });
        }
    }

    private sealed class HangingPrintSystem : IPrintEngine, ISpoolerStatusSource, ISpoolerJobController
    {
        public bool WasCancelled { get; private set; }

        public ValueTask<PrintSubmission> SubmitAsync(
            PrintRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PrintSubmission(42, request.ExpectedPageCount ?? 1));

        public ValueTask<SpoolerObservation> ReadAsync(
            string printerName,
            uint spoolerJobId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SpoolerObservation(true, 0x10, 0, 0, 0, 1));
        }

        public ValueTask CancelAsync(
            string printerName,
            uint spoolerJobId,
            CancellationToken cancellationToken = default)
        {
            WasCancelled = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediatelyCompletedPrintSystem : IPrintEngine, ISpoolerStatusSource, ISpoolerJobController
    {
        public ValueTask<PrintSubmission> SubmitAsync(
            PrintRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PrintSubmission(3, request.ExpectedPageCount ?? 1));

        public ValueTask<SpoolerObservation> ReadAsync(
            string printerName,
            uint spoolerJobId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SpoolerObservation(false, 0, 0, 0, 0, 0));

        public ValueTask CancelAsync(
            string printerName,
            uint spoolerJobId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class InteractivePrinterPrintEngine : IPrintEngine
    {
        public ValueTask<PrintSubmission> SubmitAsync(
            PrintRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<PrintSubmission>(
                new UnattendedPrintingNotSupportedException("Interactive printer test."));
    }
}
