using Microsoft.Extensions.Hosting.WindowsServices;

namespace Agent.Service;

// Placeholder sampai modul enroll, heartbeat, worker, outbox, dan updater dibangun
// (lihat docs/PRD.md bagian Konsep solusi dan checklist Fase 0).
public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Opsifin Print Agent berjalan; modul belum diimplementasi");

        // Spike B2: printer yang terlihat dari akun service hanya bisa diketahui dari dalam
        // service itu sendiri, jadi laporannya ditulis sekali saat start.
        var mode = WindowsServiceHelpers.IsWindowsService() ? "service" : "console (host)";
        try
        {
            var path = PrinterReport.Save(PrinterReport.Build(mode));
            // Warning supaya tetap masuk Event Log, yang default-nya membuang level Information.
            logger.LogWarning("Spike B2: laporan printer disimpan di {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Spike B2: gagal menyimpan laporan printer");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
