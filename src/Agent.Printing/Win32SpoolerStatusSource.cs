using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Printing;

namespace Agent.Printing;

/// <summary>Membaca satu job berdasarkan ID; nama dokumen sengaja tidak dipakai sebagai identitas.</summary>
public sealed class Win32SpoolerStatusSource : ISpoolerStatusSource
{
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidParameter = 87;

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
            ?? throw new InvalidOperationException($"Printer '{printerName}' tidak ditemukan.");

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
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"OpenPrinter gagal untuk '{printerName}'");
        }

        using (printerHandle)
        {
            PInvoke.GetJob(printerHandle, spoolerJobId, 1, null, 0, out var needed);
            var error = Marshal.GetLastPInvokeError();
            if (needed == 0 && error == ErrorInvalidParameter)
            {
                return JobReadResult.Missing;
            }

            if (needed == 0 || error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error, $"GetJob probe gagal untuk JobId {spoolerJobId}");
            }

            var buffer = new byte[needed];
            fixed (byte* pointer = buffer)
            {
                if (!PInvoke.GetJob(printerHandle, spoolerJobId, 1, pointer, needed, out _))
                {
                    error = Marshal.GetLastPInvokeError();
                    if (error == ErrorInvalidParameter)
                    {
                        // Job dapat selesai di antara probe ukuran dan pembacaan buffer.
                        return JobReadResult.Missing;
                    }

                    throw new Win32Exception(error, $"GetJob gagal untuk JobId {spoolerJobId}");
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
