using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;

namespace Agent.Printing;

/// <summary>Merender PDF langsung ke HDC printer supaya spooler memberi JobId yang dapat dipantau.</summary>
public sealed class PdfiumPrintEngine : IPrintEngine
{
    private const int PdfiumPrintModeEmf = 0;
    private const int PdfiumFlagAnnotations = 0x01;
    private const int PdfiumFlagPrinting = 0x800;

    // PDFium tidak thread-safe. Satu gerbang proses lebih aman daripada membiarkan dua
    // printer merender bersamaan dan merusak state native global.
    private static readonly SemaphoreSlim PdfiumGate = new(1, 1);

    public async ValueTask<PrintSubmission> SubmitAsync(
        PrintRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        await PdfiumGate.WaitAsync(cancellationToken);
        try
        {
            return SubmitCore(request, cancellationToken);
        }
        finally
        {
            PdfiumGate.Release();
        }
    }

    private static unsafe PrintSubmission SubmitCore(
        PrintRequest request,
        CancellationToken cancellationToken)
    {
        PdfiumNative.InitLibrary();
        try
        {
            var document = PdfiumNative.LoadDocument(request.DocumentPath, null);
            if (document == 0)
            {
                throw new InvalidDataException(
                    $"PDFium tidak dapat membuka dokumen (error {PdfiumNative.GetLastError()}).");
            }

            try
            {
                var pageCount = PdfiumNative.GetPageCount(document);
                if (pageCount <= 0)
                {
                    throw new InvalidDataException("PDF tidak memiliki halaman yang dapat dicetak.");
                }

                if (request.ExpectedPageCount is int expectedPageCount
                    && pageCount != expectedPageCount)
                {
                    throw new InvalidDataException(
                        $"PDF memiliki {pageCount} halaman, claim menyatakan {expectedPageCount}.");
                }

                var deviceContext = PInvoke.CreateDCW(null, request.PrinterName, null, null);
                if (deviceContext.IsNull)
                {
                    throw LastWin32Error($"CreateDC gagal untuk printer '{request.PrinterName}'");
                }

                try
                {
                    return RenderDocument(deviceContext, document, pageCount, request, cancellationToken);
                }
                finally
                {
                    PInvoke.DeleteDC(deviceContext);
                }
            }
            finally
            {
                PdfiumNative.CloseDocument(document);
            }
        }
        finally
        {
            PdfiumNative.DestroyLibrary();
        }
    }

    private static unsafe PrintSubmission RenderDocument(
        HDC deviceContext,
        nint document,
        int pageCount,
        PrintRequest request,
        CancellationToken cancellationToken)
    {
        fixed (char* documentName = request.DocumentName)
        {
            var documentInfo = new DOCINFOW
            {
                cbSize = (int)sizeof(DOCINFOW),
                lpszDocName = documentName,
            };

            var spoolerJobId = PInvoke.StartDoc(deviceContext, in documentInfo);
            if (spoolerJobId <= 0)
            {
                throw LastWin32Error("StartDoc gagal");
            }

            var completed = false;
            try
            {
                if (PdfiumNative.SetPrintMode(PdfiumPrintModeEmf) == 0)
                {
                    throw new InvalidOperationException("PDFium menolak print mode EMF.");
                }

                for (var copy = 0; copy < request.Copies; copy++)
                {
                    for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        RenderPage(deviceContext, document, pageIndex, request.Scale);
                    }
                }

                if (PInvoke.EndDoc(deviceContext) <= 0)
                {
                    throw LastWin32Error("EndDoc gagal");
                }

                completed = true;
                return new PrintSubmission((uint)spoolerJobId, pageCount * request.Copies);
            }
            finally
            {
                if (!completed)
                {
                    // Tanpa AbortDoc, sebagian halaman dapat tertinggal sebagai job yatim dan
                    // kelak disalahartikan sebagai hasil attempt berikutnya.
                    PInvoke.AbortDoc(deviceContext);
                }
            }
        }
    }

    private static unsafe void RenderPage(
        HDC deviceContext,
        nint document,
        int pageIndex,
        PrintScaleMode scaleMode)
    {
        var page = PdfiumNative.LoadPage(document, pageIndex);
        if (page == 0)
        {
            throw new InvalidDataException(
                $"PDFium tidak dapat membuka halaman {pageIndex + 1} (error {PdfiumNative.GetLastError()}).");
        }

        try
        {
            var pageWidthPoints = PdfiumNative.GetPageWidth(page);
            var pageHeightPoints = PdfiumNative.GetPageHeight(page);
            if (pageWidthPoints <= 0 || pageHeightPoints <= 0)
            {
                throw new InvalidDataException($"Ukuran halaman {pageIndex + 1} tidak valid.");
            }

            var layout = PageLayoutCalculator.Calculate(
                ReadPrinterMetrics(deviceContext),
                pageWidthPoints,
                pageHeightPoints,
                scaleMode);
            if (PInvoke.StartPage(deviceContext) <= 0)
            {
                throw LastWin32Error($"StartPage gagal pada halaman {pageIndex + 1}");
            }

            try
            {
                if (PdfiumNative.RenderPage(
                        (nint)deviceContext.Value,
                        page,
                        layout.X,
                        layout.Y,
                        layout.Width,
                        layout.Height,
                        layout.Rotation,
                        PdfiumFlagAnnotations | PdfiumFlagPrinting) == 0)
                {
                    throw new InvalidOperationException(
                        $"PDFium gagal merender halaman {pageIndex + 1}.");
                }
            }
            finally
            {
                if (PInvoke.EndPage(deviceContext) <= 0)
                {
                    throw LastWin32Error($"EndPage gagal pada halaman {pageIndex + 1}");
                }
            }
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    private static PrinterPageMetrics ReadPrinterMetrics(HDC deviceContext)
    {
        var printableWidth = PInvoke.GetDeviceCaps(deviceContext, GET_DEVICE_CAPS_INDEX.HORZRES);
        var printableHeight = PInvoke.GetDeviceCaps(deviceContext, GET_DEVICE_CAPS_INDEX.VERTRES);
        var dpiX = PInvoke.GetDeviceCaps(deviceContext, GET_DEVICE_CAPS_INDEX.LOGPIXELSX);
        var dpiY = PInvoke.GetDeviceCaps(deviceContext, GET_DEVICE_CAPS_INDEX.LOGPIXELSY);
        if (printableWidth <= 0 || printableHeight <= 0 || dpiX <= 0 || dpiY <= 0)
        {
            throw new InvalidOperationException("Driver printer tidak melaporkan area cetak atau DPI yang valid.");
        }

        return new PrinterPageMetrics(printableWidth, printableHeight, dpiX, dpiY);
    }

    private static void Validate(PrintRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PrinterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentName);
        if (!File.Exists(request.DocumentPath))
        {
            throw new FileNotFoundException("Dokumen PDF tidak ditemukan.", request.DocumentPath);
        }

        if (request.Copies is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Jumlah salinan harus 1 sampai 99.");
        }

        // Duplex dan tray membutuhkan DEVMODE driver yang akan masuk Fase pengaturan cetak
        // (FR-22). Gagal terang-terangan mencegah invoice dicetak dengan setting yang salah.
        if (request.Duplex || !string.IsNullOrWhiteSpace(request.Tray))
        {
            throw new NotSupportedException("Pengaturan duplex/tray belum didukung engine PDFium.");
        }
    }

    private static Win32Exception LastWin32Error(string message) =>
        new(Marshal.GetLastPInvokeError(), message);

}
