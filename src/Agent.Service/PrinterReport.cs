using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Agent.Hub;
using Agent.Printing;

namespace Agent.Service;

// Spike B2 (docs/spike-b2.md): daftar printer yang terlihat dari akun dan sesi proses ini.
// Laporan juga disimpan ke ProgramData karena saat jalan sebagai service tidak ada console,
// dan hasil dari beberapa akun di satu PC perlu dibandingkan berdampingan.
public static class PrinterReport
{
    public static string ReportDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Opsifin", "PrintAgent", "spike-b2");

    public static string Build(string mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Opsifin Print Agent - spike B2: visible printers");
        sb.AppendLine($"Waktu   : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Mesin   : {Environment.MachineName}");
        sb.AppendLine($"OS      : {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Akun    : {Environment.UserDomainName}\\{Environment.UserName}");
        sb.AppendLine($"Sesi    : {Process.GetCurrentProcess().SessionId} (0 = sesi service)");
        sb.AppendLine($"Mode    : {mode}");
        sb.AppendLine();

        IReadOnlyList<PrinterInfo> printers;
        try
        {
            printers = PrinterInventory.List();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"FAILED to read printers: {ex.Message}");
            return sb.ToString();
        }

        sb.AppendLine($"{printers.Count} printer(s) visible");
        for (var i = 0; i < printers.Count; i++)
        {
            var p = printers[i];
            sb.AppendLine();
            sb.AppendLine($"[{i + 1}] {p.Name}{(p.IsDefault ? "  (default)" : "")}");
            sb.AppendLine($"    status  : {PrinterStatusMapper.Map(p.Status, p.Attributes)}  (raw 0x{p.Status:X8}: {Join(PrinterFlags.DescribeStatus(p.Status))})");
            sb.AppendLine($"    atribut : 0x{p.Attributes:X8}: {Join(PrinterFlags.DescribeAttributes(p.Attributes))}");
            foreach (var port in p.Ports)
            {
                sb.AppendLine($"    port    : {port.Name}  [{port.MonitorName ?? "port is not present on this machine"}{(port.Description is null ? "" : $" / {port.Description}")}]");
            }

            if (p.Ports.Count == 0)
            {
                sb.AppendLine("    port    : -");
            }

            sb.AppendLine($"    driver  : {p.DriverName ?? "-"}");
            if (p.ServerName is not null)
            {
                sb.AppendLine($"    server  : {p.ServerName}{(p.ShareName is null ? "" : $" (share {p.ShareName})")}");
            }

            if (p.Location is not null)
            {
                sb.AppendLine($"    lokasi  : {p.Location}");
            }

            sb.AppendLine($"    antrean : {p.QueueLength} job");
        }

        return sb.ToString();
    }

    /// <summary>Menyimpan laporan dan mengembalikan path-nya.</summary>
    public static string Save(string report)
    {
        Directory.CreateDirectory(ReportDirectory);
        var path = Path.Combine(
            ReportDirectory,
            $"{Environment.MachineName}_{Environment.UserName}_{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, report);
        return path;
    }

    private static string Join(IReadOnlyList<string> names) => names.Count == 0 ? "-" : string.Join(", ", names);
}
