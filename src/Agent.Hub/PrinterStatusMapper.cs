namespace Agent.Hub;

/// <summary>
/// Memetakan <c>PRINTER_INFO_2.Status</c> dan <c>.Attributes</c> mentah ke enum
/// <c>PrinterReport.status</c> di kontrak. Ada di sini, bukan di Agent.Printing, karena
/// hasilnya nilai kontrak dan supaya bisa dites di Linux.
/// </summary>
public static class PrinterStatusMapper
{
    public const string Online = "ONLINE";
    public const string Offline = "OFFLINE";
    public const string PaperOut = "PAPER_OUT";
    public const string PaperJam = "PAPER_JAM";
    public const string NoToner = "NO_TONER";
    public const string DoorOpen = "DOOR_OPEN";
    public const string Error = "ERROR";

    private const uint StatusError = 0x00000002;
    private const uint StatusPendingDeletion = 0x00000004;
    private const uint StatusPaperJam = 0x00000008;
    private const uint StatusPaperOut = 0x00000010;
    private const uint StatusPaperProblem = 0x00000040;
    private const uint StatusOffline = 0x00000080;
    private const uint StatusOutputBinFull = 0x00000800;
    private const uint StatusNotAvailable = 0x00001000;
    private const uint StatusNoToner = 0x00040000;
    private const uint StatusPagePunt = 0x00080000;
    private const uint StatusUserIntervention = 0x00100000;
    private const uint StatusOutOfMemory = 0x00200000;
    private const uint StatusDoorOpen = 0x00400000;
    private const uint StatusServerUnknown = 0x00800000;
    private const uint StatusServerOffline = 0x02000000;

    private const uint AttributeWorkOffline = 0x00000400;

    private const uint OfflineMask = StatusOffline | StatusNotAvailable | StatusServerUnknown | StatusServerOffline;

    private const uint ErrorMask = StatusError | StatusPendingDeletion | StatusPaperProblem | StatusOutputBinFull
        | StatusPagePunt | StatusUserIntervention | StatusOutOfMemory;

    // Draft untuk spike; urutan dan isi mask divalidasi di benchmark B3 per model printer.
    // Penyebab fisik spesifik didahulukan karena paling bisa ditindaklanjuti operator. PAUSED,
    // BUSY, POWER_SAVE, dan sejenisnya bukan error sehingga tetap ONLINE.
    public static string Map(uint status, uint attributes)
    {
        if ((status & StatusPaperJam) != 0) return PaperJam;
        if ((status & StatusPaperOut) != 0) return PaperOut;
        if ((status & StatusNoToner) != 0) return NoToner;
        if ((status & StatusDoorOpen) != 0) return DoorOpen;
        // Printer USB yang dicabut sering hanya ditandai lewat atribut WORK_OFFLINE, bukan Status.
        if ((status & OfflineMask) != 0 || (attributes & AttributeWorkOffline) != 0) return Offline;
        if ((status & ErrorMask) != 0) return Error;
        return Online;
    }
}
