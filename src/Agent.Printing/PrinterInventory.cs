using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Printing;

namespace Agent.Printing;

/// <summary>Satu printer seperti yang terlihat dari akun dan sesi proses ini.</summary>
/// <param name="ServerName">Terisi hanya untuk koneksi ke print server (<c>\\server\share</c>).</param>
/// <param name="Ports">Port dari <c>pPortName</c>; bisa lebih dari satu bila printer memakai port pooling.</param>
public sealed record PrinterInfo(
    string Name,
    string? ServerName,
    string? ShareName,
    string? DriverName,
    string? Location,
    string? Comment,
    IReadOnlyList<PortInfo> Ports,
    uint Status,
    uint Attributes,
    int QueueLength,
    bool IsDefault);

/// <param name="MonitorName">Kosong bila port tidak ada di mesin ini, misalnya port milik print server.</param>
public sealed record PortInfo(string Name, string? MonitorName, string? Description);

public static class PrinterInventory
{
    // LOCAL mencakup printer per mesin (termasuk koneksi hasil `printui /ga`), CONNECTIONS
    // mencakup koneksi jaringan per user. Keduanya diminta supaya B2 bisa membandingkan apa
    // yang hilang saat agent jalan sebagai LocalSystem dibanding sebagai user.
    private const uint EnumFlags = PInvoke.PRINTER_ENUM_LOCAL | PInvoke.PRINTER_ENUM_CONNECTIONS;

    public static unsafe IReadOnlyList<PrinterInfo> List()
    {
        var ports = ListPorts();
        var defaultPrinter = GetDefaultPrinterName();

        var buffer = Enumerate(
            (byte* buf, uint size, out uint needed, out uint count) =>
                PInvoke.EnumPrinters(EnumFlags, null, 2, buf, size, out needed, out count),
            "EnumPrinters",
            out var returned);

        var printers = new List<PrinterInfo>((int)returned);
        fixed (byte* p = buffer)
        {
            var items = (PRINTER_INFO_2W*)p;
            for (var i = 0; i < returned; i++)
            {
                var info = items[i];
                var name = info.pPrinterName.ToString();
                printers.Add(new PrinterInfo(
                    name,
                    NullIfEmpty(info.pServerName.ToString()),
                    NullIfEmpty(info.pShareName.ToString()),
                    NullIfEmpty(info.pDriverName.ToString()),
                    NullIfEmpty(info.pLocation.ToString()),
                    NullIfEmpty(info.pComment.ToString()),
                    ResolvePorts(info.pPortName.ToString(), ports),
                    info.Status,
                    info.Attributes,
                    (int)info.cJobs,
                    string.Equals(name, defaultPrinter, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return printers;
    }

    private static unsafe Dictionary<string, PortInfo> ListPorts()
    {
        var buffer = Enumerate(
            (byte* buf, uint size, out uint needed, out uint count) =>
                PInvoke.EnumPorts(default, 2, buf, size, out needed, out count),
            "EnumPorts",
            out var returned);

        var ports = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);
        fixed (byte* p = buffer)
        {
            var items = (PORT_INFO_2W*)p;
            for (var i = 0; i < returned; i++)
            {
                var name = items[i].pPortName.ToString();
                ports[name] = new PortInfo(
                    name,
                    NullIfEmpty(items[i].pMonitorName.ToString()),
                    NullIfEmpty(items[i].pDescription.ToString()));
            }
        }

        return ports;
    }

    private static PortInfo[] ResolvePorts(string? portName, Dictionary<string, PortInfo> ports) =>
        (portName ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => ports.TryGetValue(name, out var port) ? port : new PortInfo(name, null, null))
            .ToArray();

    private static string? GetDefaultPrinterName()
    {
        uint length = 0;
        PInvoke.GetDefaultPrinter(default, ref length);
        // LocalSystem biasanya tidak punya default printer; itu bukan error bagi inventaris.
        if (length == 0)
        {
            return null;
        }

        var name = new char[length];
        return PInvoke.GetDefaultPrinter(name, ref length) ? new string(name, 0, (int)length - 1) : null;
    }

    private unsafe delegate bool EnumCall(byte* buffer, uint size, out uint needed, out uint returned);

    // Pola dua panggilan winspool: tanya ukuran dulu, lalu isi. Diulang karena printer/port
    // bisa bertambah di antara dua panggilan dan membuat buffer kembali kurang.
    private static unsafe byte[] Enumerate(EnumCall call, string api, out uint returned)
    {
        var buffer = Array.Empty<byte>();
        while (true)
        {
            fixed (byte* p = buffer)
            {
                if (call(p, (uint)buffer.Length, out var needed, out returned))
                {
                    return buffer;
                }

                var error = Marshal.GetLastPInvokeError();

                // EnumPorts dari metadata CsWin32 tidak mempertahankan LastError, tetapi tetap
                // mengisi `needed` sesuai kontrak Win32. Memercayai ukuran yang bertambah juga
                // menangani perubahan daftar printer di antara probe dan pembacaan tanpa
                // menganggap LastError lama dari P/Invoke lain sebagai kegagalan baru.
                if (needed > buffer.Length)
                {
                    buffer = new byte[needed];
                    continue;
                }

                throw new Win32Exception(error, $"{api} failed");
            }
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
