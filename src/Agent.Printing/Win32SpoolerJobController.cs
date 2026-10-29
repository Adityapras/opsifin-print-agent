using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;

namespace Agent.Printing;

public sealed class Win32SpoolerJobController : ISpoolerJobController
{
    public ValueTask CancelAsync(
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

        if (!PInvoke.OpenPrinter(printerName, out var printerHandle, null))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"OpenPrinter gagal untuk '{printerName}'");
        }

        using (printerHandle)
        {
            // Dokumentasi SetJob meminta DELETE, bukan JOB_CONTROL_CANCEL yang sudah usang.
            if (!PInvoke.SetJob(printerHandle, spoolerJobId, 0, PInvoke.JOB_CONTROL_DELETE))
            {
                var error = Marshal.GetLastPInvokeError();
                // Job yang sudah hilang tidak lagi membutuhkan pembatalan.
                if (error != 87)
                {
                    throw new Win32Exception(error, $"SetJob DELETE gagal untuk JobId {spoolerJobId}");
                }
            }
        }

        return ValueTask.CompletedTask;
    }
}
