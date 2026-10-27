using Agent.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

// Spike B2: `Agent.Service.exe --list-printers` mencetak printer yang terlihat dari akun
// user saat ini lalu keluar, tanpa menyalakan host.
if (args.Contains("--list-printers"))
{
    var report = PrinterReport.Build("console");
    Console.WriteLine(report);
    Console.WriteLine($"Laporan disimpan di {PrinterReport.Save(report)}");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Saat dijalankan oleh Service Control Manager, host memakai lifetime Windows service
// dan log ke Event Log; saat dijalankan dari console (dev), perilakunya biasa.
builder.Services.AddWindowsService(options => options.ServiceName = "Opsifin Print Agent");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
