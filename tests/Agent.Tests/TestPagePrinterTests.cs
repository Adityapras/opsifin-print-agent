using System.Text;
using Agent.Printing;
using Agent.Runtime;

namespace Agent.Tests;

public sealed class TestPagePrinterTests
{
    [Fact]
    public void Pdf_test_page_memiliki_xref_dan_marker_akhir_valid()
    {
        var pdf = TestPagePdfDocument.Create(
            Guid.Parse("019c0000-0000-7000-8000-000000000001"),
            "Printer (QA) \\ A",
            new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.FromHours(7)));
        var text = Encoding.ASCII.GetString(pdf);

        Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);
        Assert.Contains("Printer: Printer \\(QA\\) \\\\ A", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        var startXref = text.LastIndexOf("startxref\n", StringComparison.Ordinal);
        var valueStart = startXref + "startxref\n".Length;
        var valueEnd = text.IndexOf('\n', valueStart);
        var offset = int.Parse(text[valueStart..valueEnd], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("xref\n", text.Substring(offset, 5));
    }

    [Fact]
    public async Task Simulasi_test_page_selesai_dan_file_sementara_dihapus()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var simulated = new SimulatedPrintSystem();
            var printer = new TestPagePrinter(
                simulated,
                new SpoolerJobMonitor(simulated),
                simulated,
                new TestPagePrinterOptions(directory, TimeSpan.Zero, TimeSpan.FromMilliseconds(1)),
                TimeProvider.System);

            var result = await printer.PrintAsync(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "Simulated Printer",
                CancellationToken.None);

            Assert.Equal(SpoolerMonitorOutcome.Completed, result.Outcome);
            Assert.NotNull(result.SpoolerJobId);
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
