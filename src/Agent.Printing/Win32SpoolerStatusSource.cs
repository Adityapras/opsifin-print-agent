using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Printing;

namespace Agent.Printing;

/// <summary>Membaca satu job berdasarkan ID; nama dokumen sengaja tidak dipakai sebagai identitas.</summary>
public sealed class Win32SpoolerStatusSource : ISpoolerStatusSource
{
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotFound = 1168;

    public ValueTask<SpoolerObservation> ReadAsync(
        string printerName,
        uint spoolerJobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        if (spoolerJobId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spoolerJobId));
        }

        var printer = PrinterInventory.List().FirstOrDefault(
            item => string.Equals(item.Name, printerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Printer '{printerName}' was not found.");

        var job = ReadJob(printerName, spoolerJobId);
        return ValueTask.FromResult(new SpoolerObservation(
            job.Exists,
            job.Status,
            printer.Status,
            printer.Attributes,
            job.PagesPrinted,
            job.TotalPages,
            job.StatusText));
    }

    private static unsafe JobReadResult ReadJob(string printerName, uint spoolerJobId)
    {
        if (!PInvoke.OpenPrinter(printerName, out var printerHandle, null))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"OpenPrinter failed for '{printerName}'");
        }

        using (printerHandle)
        {
            var probeSucceeded = PInvoke.GetJob(
                printerHandle,
                spoolerJobId,
                1,
                null,
                0,
                out var needed);
            var error = Marshal.GetLastPInvokeError();
            if (needed == 0 && (probeSucceeded || IsMissingJobError(error)))
            {
                return JobReadResult.Missing;
            }

            // Beberapa print provider mengisi ukuran dengan benar tetapi tidak menjaga
            // LastError=122. Ukuran nonzero tetap cukup untuk melanjutkan pembacaan.
            if (needed == 0 || error is not (ErrorSuccess or ErrorInsufficientBuffer))
            {
                throw new Win32Exception(error, $"GetJob probe failed for JobId {spoolerJobId}");
            }

            var buffer = new byte[needed];
            fixed (byte* pointer = buffer)
            {
                if (!PInvoke.GetJob(printerHandle, spoolerJobId, 1, pointer, needed, out _))
                {
                    error = Marshal.GetLastPInvokeError();
                    if (IsMissingJobError(error))
                    {
                        // Job dapat selesai di antara probe ukuran dan pembacaan buffer.
                        return JobReadResult.Missing;
                    }

                    throw new Win32Exception(error, $"GetJob failed for JobId {spoolerJobId}");
                }

                var info = *(JOB_INFO_1W*)pointer;
                return new JobReadResult(
                    true,
                    (uint)info.Status,
                    info.PagesPrinted,
                    info.TotalPages,
                    NullIfEmpty(info.pStatus.ToString()));
            }
        }
    }

    private static bool IsMissingJobError(int error) => error is
        ErrorSuccess or ErrorFileNotFound or ErrorInvalidParameter or ErrorNotFound;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record JobReadResult(
        bool Exists,
        uint Status,
        uint PagesPrinted,
        uint TotalPages,
        string? StatusText)
    {
        public static JobReadResult Missing { get; } = new(false, 0, 0, 0, null);
    }
}
