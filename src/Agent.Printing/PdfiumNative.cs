using System.Runtime.InteropServices;

namespace Agent.Printing;

internal static partial class PdfiumNative
{
    private const string LibraryName = "pdfium";

    [LibraryImport(LibraryName, EntryPoint = "FPDF_InitLibrary")]
    internal static partial void InitLibrary();

    [LibraryImport(LibraryName, EntryPoint = "FPDF_DestroyLibrary")]
    internal static partial void DestroyLibrary();

    [LibraryImport(
        LibraryName,
        EntryPoint = "FPDF_LoadDocument",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint LoadDocument(string filePath, string? password);

    [LibraryImport(LibraryName, EntryPoint = "FPDF_CloseDocument")]
    internal static partial void CloseDocument(nint document);

    [LibraryImport(LibraryName, EntryPoint = "FPDF_GetLastError")]
    internal static partial uint GetLastError();

    [LibraryImport(LibraryName, EntryPoint = "FPDF_GetPageCount")]
    internal static partial int GetPageCount(nint document);

    [LibraryImport(LibraryName, EntryPoint = "FPDF_LoadPage")]
    internal static partial nint LoadPage(nint document, int pageIndex);

    [LibraryImport(LibraryName, EntryPoint = "FPDF_ClosePage")]
    internal static partial void ClosePage(nint page);

    [LibraryImport(LibraryName, EntryPoint = "FPDF_GetPageWidthF")]
    internal static partial float GetPageWidth(nint page);

    [LibraryImport(LibraryName, EntryPoint = "FPDF_GetPageHeightF")]
    internal static partial float GetPageHeight(nint page);

    [LibraryImport(LibraryName, EntryPoint = "FPDF_SetPrintMode")]
    internal static partial int SetPrintMode(int mode);

    [LibraryImport(LibraryName, EntryPoint = "FPDF_RenderPage")]
    internal static partial int RenderPage(
        nint deviceContext,
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);
}
