namespace Agent.Printing;

public enum PrintScaleMode
{
    NoScale,
    Fit,
    Shrink,
}

public sealed record PrintRequest(
    string DocumentPath,
    string PrinterName,
    string DocumentName,
    int Copies = 1,
    bool Duplex = false,
    string? Tray = null,
    PrintScaleMode Scale = PrintScaleMode.NoScale,
    int? ExpectedPageCount = null);

public sealed record PrintSubmission(uint SpoolerJobId, int PageCount);

/// <summary>Mengirim dokumen ke spooler; keberhasilan fisik ditentukan monitor terpisah.</summary>
public interface IPrintEngine
{
    ValueTask<PrintSubmission> SubmitAsync(
        PrintRequest request,
        CancellationToken cancellationToken = default);
}

public interface ISpoolerJobController
{
    ValueTask CancelAsync(
        string printerName,
        uint spoolerJobId,
        CancellationToken cancellationToken = default);
}
