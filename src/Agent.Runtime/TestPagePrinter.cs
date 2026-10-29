using System.Globalization;
using System.Text;
using Agent.Printing;

namespace Agent.Runtime;

public sealed record TestPagePrinterOptions(
    string DocumentsDirectory,
    TimeSpan CompletionVerificationWindow,
    TimeSpan SpoolerPollInterval);

public sealed record TestPageResult(
    SpoolerMonitorOutcome Outcome,
    uint? SpoolerJobId,
    string Message);

/// <summary>Mencetak satu halaman diagnostik tanpa bergantung pada dokumen dari Hub.</summary>
public sealed class TestPagePrinter(
    IPrintEngine printEngine,
    SpoolerJobMonitor spoolerMonitor,
    ISpoolerJobController spoolerJobController,
    TestPagePrinterOptions options,
    TimeProvider timeProvider)
{
    public async Task<TestPageResult> PrintAsync(
        Guid commandId,
        Guid agentId,
        string printerName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        Directory.CreateDirectory(options.DocumentsDirectory);
        var documentPath = Path.Combine(options.DocumentsDirectory, $"test-page-{commandId:N}.pdf");
        uint? spoolerJobId = null;

        try
        {
            var document = TestPagePdfDocument.Create(
                agentId,
                printerName,
                timeProvider.GetUtcNow());
            await File.WriteAllBytesAsync(documentPath, document, cancellationToken);

            var submission = await printEngine.SubmitAsync(
                new PrintRequest(
                    documentPath,
                    printerName,
                    $"Opsifin Print Agent test page - {commandId:D}",
                    ExpectedPageCount: 1),
                cancellationToken);
            spoolerJobId = submission.SpoolerJobId;

            var monitorResult = await spoolerMonitor.MonitorAsync(
                printerName,
                submission.SpoolerJobId,
                pollInterval: options.SpoolerPollInterval,
                verificationWindow: options.CompletionVerificationWindow,
                cancellationToken: cancellationToken);
            return new TestPageResult(
                monitorResult.Outcome,
                submission.SpoolerJobId,
                monitorResult.Detail ?? "Pemantauan spooler selesai.");
        }
        catch (OperationCanceledException) when (spoolerJobId is not null)
        {
            // Command yang dihentikan tidak boleh meninggalkan job uji tertahan di spooler.
            await spoolerJobController.CancelAsync(printerName, spoolerJobId.Value, CancellationToken.None);
            throw;
        }
        finally
        {
            if (File.Exists(documentPath))
            {
                File.Delete(documentPath);
            }
        }
    }
}

public static class TestPagePdfDocument
{
    public static byte[] Create(Guid agentId, string printerName, DateTimeOffset generatedAt)
    {
        var lines = new[]
        {
            "Opsifin Print Agent - Test Page",
            $"Agent ID: {agentId:D}",
            $"Printer: {NormalizeText(printerName)}",
            $"Generated: {generatedAt:yyyy-MM-dd HH:mm:ss zzz}",
            "Jika halaman ini tercetak, jalur PDFium, GDI, dan Windows spooler aktif.",
        };
        var content = new StringBuilder("BT\n/F1 22 Tf\n72 760 Td\n");
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                content.Append("0 -36 Td\n");
                content.Append(index == lines.Length - 1 ? "/F1 10 Tf\n" : "/F1 12 Tf\n");
            }

            content.Append('(').Append(EscapePdfString(lines[index])).Append(") Tj\n");
        }

        content.Append("ET\n");
        var contentText = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contentText)} >>\nstream\n{contentText}endstream",
        };

        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-1.4\n%Opsifin\n");
        var offsets = new long[objects.Length + 1];
        for (var index = 0; index < objects.Length; index++)
        {
            offsets[index + 1] = output.Position;
            WriteAscii(output, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xrefOffset = output.Position;
        WriteAscii(output, $"xref\n0 {objects.Length + 1}\n");
        WriteAscii(output, "0000000000 65535 f \n");
        for (var index = 1; index < offsets.Length; index++)
        {
            WriteAscii(output, offsets[index].ToString("D10", CultureInfo.InvariantCulture));
            WriteAscii(output, " 00000 n \n");
        }

        WriteAscii(
            output,
            $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static string NormalizeText(string value) => new(
        value.Select(character => character is >= ' ' and <= '~' ? character : '?').ToArray());

    private static string EscapePdfString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));
}
