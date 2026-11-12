# Opsifin Print Agent

![Opsifin Print Agent icon](assets/branding/opsifin-agent.png)

Windows service (.NET 10) yang mencetak invoice dari Opsifin Print Hub ke printer di PC klien.

Paket juga menyertakan **Opsifin Agent Control**, aplikasi Windows modern untuk pairing,
status service/heartbeat, kontrol start-stop-restart, dan diagnostics tanpa command line.

- PRD: [`docs/PRD.md`](docs/PRD.md) · ringkasan keputusan: [`docs/PRD-ringkasan-keputusan.md`](docs/PRD-ringkasan-keputusan.md)
- Kontrak API (salinan dari repo Hub): [`contracts/agent-v1.yaml`](contracts/agent-v1.yaml)
- Hub: repo `opsifin-print-hub`

## Status implementasi

Versi `1.0.0` menjadi baseline fresh-install dengan inventaris printer Win32, enrollment ke Hub,
penyimpanan device secret dengan DPAPI + ACL lokal, dan heartbeat terautentikasi dengan
remote config. Klien claim/event, unduhan signed PDF dengan verifikasi ukuran + SHA-256,
serta SQLite event outbox dengan retry juga sudah tersedia. Mesin PDFium sudah merender PDF
langsung ke GDI, mengembalikan `JobId` spooler, dan monitor hanya mengonfirmasi selesai setelah
printer sehat selama 60 detik. Claim coordinator kini berjalan setelah heartbeat memberikan
binding printer: satu job per printer, event selalu masuk SQLite sebelum dikirim, dan abort Hub
menghapus job spooler aktif. Mode simulasi tersedia untuk menguji seluruh pipeline tanpa printer.
MSI service dasar
sudah tersedia. Command Hub dicatat sebelum dijalankan agar retry/restart tidak mengulang efek
samping, dan remote `test_page` sudah mencetak PDF diagnostik melalui pipeline PDFium + spooler
yang sama dengan job normal. Rotasi device secret tersimpan atomik lewat DPAPI. Agent juga
menulis log JSON Lines lokal dan dapat mengunggah ZIP log 1–168 jam lewat command `upload_logs`.
Updater men-download paket dari Hub di background, memeriksa SHA-256, menunggu job aktif serta event
outbox selesai, lalu menyerahkan instalasi silent kepada helper terpisah. Release cukup berupa satu
MSI yang diunggah ke Hub; tidak ada certificate, private key, signing PC, atau manifest terpisah.

Printer virtual yang membutuhkan dialog desktop atau nama file—misalnya Microsoft Print to PDF,
XPS Document Writer, Fax, dan OneNote—ditolak sebelum pemanggilan GDI dengan error
`PRINTER_REQUIRES_INTERACTION`. Windows Service berjalan di Session 0 sehingga tidak dapat
menjawab dialog **Save As**. Gunakan printer fisik/network untuk produksi atau mode simulasi
Agent untuk pengujian tanpa perangkat cetak.

## Opsifin Agent Control

Setelah instalasi interaktif selesai, MSI menjalankan companion tray dan otomatis membuka
**Opsifin Agent Control**. Shortcut juga tersedia di Desktop serta Start Menu. Companion tray
dimulai otomatis saat user login tanpa UAC; jendela kontrol meminta UAC hanya ketika dibuka
karena pairing dan kontrol service memerlukan hak Administrator. Area utama yang tersedia:

- **Dashboard:** kesehatan service, koneksi Hub, heartbeat terakhir, serta inventaris printer
  lengkap (nama, status, driver, port, default, dan antrean) dari heartbeat yang diterima Hub.
  Dashboard juga menampilkan job cetak aktif per printer, nomor invoice, tipe dokumen, stage
  lokal dari Windows print spooler, durasi, jumlah halaman, interval Agent memeriksa job baru,
  dan interval Hub menarik data invoice. Hub lama tetap didukung dengan Job ID pendek sebagai
  fallback. Remote print settings yang invalid dinormalisasi ke default aman agar satu printer
  tidak memutus seluruh heartbeat.
- **Pairing & setup:** alamat Hub + pairing code 8 karakter, tanpa terminal.
- **Local testing mode:** mengaktifkan/menonaktifkan simulation mode dari GUI dan me-restart
  service otomatis. Banner jingga pada Dashboard mencegah hasil simulasi disalahartikan sebagai
  cetakan fisik.
- **Diagnostics:** error aktif, log JSON lokal, dan kontrol start/stop/restart service.
- **Software update:** notifikasi tray, download/progress di Dashboard, tombol install, serta status
  waiting-for-idle/installing/succeeded/failed. Update tidak pernah mematikan paksa job aktif.
- **System Check:** pemeriksaan kesiapan Windows/service/spooler/printer/Hub serta ekspor ZIP
  diagnostik yang meredaksi device secret dan bearer token.

GUI hanya mengelola bootstrap dan diagnosis perangkat. Routing printer, retry, jadwal, dan
aturan bisnis tetap dikirim oleh Hub melalui remote config agar seluruh PC konsisten.

## Build

```powershell
.\tools\build-installer.ps1
```

Publish Native AOT wajib dijalankan di Windows dengan .NET 10 SDK dan Visual Studio Build
Tools workload **Desktop development with C++**.

Build MSI Release juga menghasilkan satu artefak distribusi mandiri di
`dist/OpsifinPrintAgent-Setup-<version>-win-x64.msi`. Hanya file MSI tersebut yang diberikan
kepada user; folder `artifacts/package` adalah input build internal dan tidak perlu dikirim.

Untuk membuat versi tertentu tanpa mengubah source terlebih dahulu:

```powershell
.\tools\build-installer.ps1 -Version 1.0.1
```

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

Untuk trial end-to-end tanpa printer, buka **Pairing & setup → Local testing mode → Enable
simulation mode**. Agent me-restart service otomatis, tetap melakukan claim dan download dari Hub,
lalu menjalankan simulated spooler dan mengirim event sampai `completed`. Tidak ada data dummy di
Hub dan tidak ada kertas yang dicetak. Mode tersimpan di ProgramData sehingga tetap aktif setelah
restart Windows maupun upgrade MSI, sampai operator mengembalikannya ke production mode.

Environment `Development` tetap tersedia hanya untuk developer yang menjalankan source secara
langsung; konfigurasi `appsettings.Development.json` mengaktifkan simulator deterministik:

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

Timestamp log lokal dan tampilan diagnostics menggunakan `Asia/Jakarta` (WIB, UTC+07:00).
Timestamp kontrak API dan event yang dikirim ke Hub tetap UTC untuk menjaga urutan lintas sistem.

Untuk menjalankan langsung dari source:

```bash
dotnet run --project src/Agent.Service
dotnet run --project src/Agent.Service -- --list-printers   # spike B2, lihat docs/spike-b2.md
dotnet run --project src/Agent.Service -- print-file --file C:\Temp\invoice.pdf --printer "Nama Printer"
```

Menjalankan dan mencetak butuh Windows 10/11 dengan printer nyata. Panduan lengkap ada di [`CLAUDE.md`](CLAUDE.md).
