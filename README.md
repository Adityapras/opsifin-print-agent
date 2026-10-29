# Opsifin Print Agent

Windows service (.NET 10) yang mencetak invoice dari Opsifin Print Hub ke printer di PC klien.

- PRD: [`docs/PRD.md`](docs/PRD.md) · ringkasan keputusan: [`docs/PRD-ringkasan-keputusan.md`](docs/PRD-ringkasan-keputusan.md)
- Kontrak API (salinan dari repo Hub): [`contracts/agent-v1.yaml`](contracts/agent-v1.yaml)
- Hub: repo `opsifin-print-hub`

## Status implementasi

Versi pengembangan `0.1.0` sudah memiliki inventaris printer Win32, enrollment ke Hub,
penyimpanan device secret dengan DPAPI + ACL lokal, dan heartbeat terautentikasi dengan
remote config. Klien claim/event, unduhan signed PDF dengan verifikasi ukuran + SHA-256,
serta SQLite event outbox dengan retry juga sudah tersedia. Mesin PDFium sudah merender PDF
langsung ke GDI, mengembalikan `JobId` spooler, dan monitor hanya mengonfirmasi selesai setelah
printer sehat selama 60 detik. Claim coordinator kini berjalan setelah heartbeat memberikan
binding printer: satu job per printer, event selalu masuk SQLite sebelum dikirim, dan abort Hub
menghapus job spooler aktif. Mode simulasi tersedia untuk menguji seluruh pipeline tanpa printer.
Dukungan duplex/tray dan updater bertanda tangan masih dalam pengembangan. MSI service dasar
sudah tersedia. Command Hub dicatat sebelum dijalankan agar retry/restart tidak mengulang efek
samping, dan remote `test_page` sudah mencetak PDF diagnostik melalui pipeline PDFium + spooler
yang sama dengan job normal. Rotasi device secret tersimpan atomik lewat DPAPI. Agent juga
menulis log JSON Lines lokal dan dapat mengunggah ZIP log 1–168 jam lewat command `upload_logs`.
Updater bertanda tangan masih menunggu keputusan format signature dan public key dari Hub.

## Build

```bash
dotnet build
dotnet test
dotnet publish src/Agent.Service -c Release -r win-x64 -o artifacts/agent
```

Publish Native AOT wajib dijalankan di Windows dengan .NET 10 SDK dan Visual Studio Build
Tools workload **Desktop development with C++**.

## Perintah agent

```powershell
# Inventaris printer untuk spike B2
.\artifacts\agent\Agent.Service.exe --list-printers

# Pairing satu kali. Secret tidak ditulis ke console dan disimpan terenkripsi di ProgramData.
.\artifacts\agent\Agent.Service.exe enroll `
  --hub-url https://print-hub.example `
  --pairing-code ABCD1234

# Uji satu PDF langsung ke printer, lalu pantau JobId sampai hasil akhir.
.\artifacts\agent\Agent.Service.exe print-file `
  --file C:\Temp\invoice.pdf `
  --printer "Nama Printer Persis di Windows" `
  --scale noscale

# Mode console untuk pengembangan; mode service memakai executable yang sama.
.\artifacts\agent\Agent.Service.exe
```

Untuk simulasi tanpa printer, jalankan dengan environment `Development`; konfigurasi
`appsettings.Development.json` mengganti PDFium/spooler dengan simulator deterministik. Hub tetap
harus mengirim binding printer dan job sesuai kontrak, tetapi tidak ada kertas yang dicetak:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
.\artifacts\agent\Agent.Service.exe
```

Identity dan outbox agent tersimpan di
`C:\ProgramData\Opsifin\PrintAgent\state\`. Device secret dilindungi DPAPI scope mesin;
folder state hanya dapat diakses oleh `SYSTEM` serta Administrators. Jalankan enrollment
dari terminal Administrator. Log lokal tersimpan sebagai JSON Lines di subfolder `logs` dan
arsip hasil command berada di subfolder `command-attachments`, keduanya memakai ACL state yang
sama.

Untuk menjalankan langsung dari source:

```bash
dotnet run --project src/Agent.Service
dotnet run --project src/Agent.Service -- --list-printers   # spike B2, lihat docs/spike-b2.md
dotnet run --project src/Agent.Service -- print-file --file C:\Temp\invoice.pdf --printer "Nama Printer"
```

Menjalankan dan mencetak butuh Windows 10/11 dengan printer nyata. Panduan lengkap ada di [`CLAUDE.md`](CLAUDE.md).
