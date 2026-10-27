# Spike B2: printer terlihat dari service

Menjawab stopper S1 (lihat `PRD-ringkasan-keputusan.md`): printer yang dipasang per user tidak terlihat oleh service LocalSystem. Spike ini mencatat printer, status, dan port yang terlihat dari setiap kombinasi akun dan cara pasang printer, supaya kita bisa memilih satu konfigurasi standar.

**Lulus bila** semua printer produksi terlihat di minimal satu konfigurasi yang bisa distandarkan.

## Yang dicatat

Agent membaca `EnumPrinters(LOCAL | CONNECTIONS)` level 2 dan `EnumPorts` level 2, lalu menulis laporan ke `C:\ProgramData\Opsifin\PrintAgent\spike-b2\<MESIN>_<akun>_<waktu>.txt`. Isinya:

- identitas proses: akun, nomor sesi (0 = sesi service), mode console atau service
- per printer: status kontrak (`ONLINE`, `OFFLINE`, `PAPER_OUT`, ...) beserta flag mentah, atribut, port + port monitor (USB Monitor, Standard TCP/IP Port, WSD Port, Local Port, ...), driver, server/share untuk koneksi jaringan, jumlah job di antrean

Atribut yang perlu diperhatikan untuk B2:

| Atribut | Arti |
| --- | --- |
| `LOCAL` | Antrean milik mesin ini (USB, Bluetooth, port TCP/IP lokal) |
| `NETWORK` | Koneksi ke print server (`\\server\share`) |
| `MACHINE` | Koneksi per mesin hasil `printui /ga`, terlihat oleh semua akun |
| `PER_USER`, `PUSHED_USER` | Koneksi milik satu user; diduga tidak terlihat dari service |
| `PUSHED_MACHINE` | Didorong GPO per mesin |
| `TS` | Printer redirect RDP; hanya ada di sesi RDP itu |
| `WORK_OFFLINE` | "Use Printer Offline"; dipetakan ke `OFFLINE` |

Port berlabel `port tidak ada di mesin ini` berarti port-nya milik print server, bukan PC ini.

## Persiapan

Ambil biner dari artifact CI `agent-win-x64`, atau publish di Windows:

```bat
dotnet publish src\Agent.Service -c Release -r win-x64 -o C:\OpsifinB2
```

## Langkah per PC

Jalankan di setiap PC uji (Win10, Win11, Server 2019) dengan semua printer produksi PC itu terpasang seperti biasa.

1. **Mode user.** Buka `cmd` biasa (bukan Run as administrator) sebagai user yang sehari-hari memakai printer:

   ```bat
   C:\OpsifinB2\Agent.Service.exe --list-printers
   ```

2. **LocalSystem.** Dari `cmd` administrator:

   ```bat
   sc.exe create OpsifinB2 binPath= "C:\OpsifinB2\Agent.Service.exe" start= demand
   sc.exe start OpsifinB2
   rem tunggu beberapa detik sampai laporan muncul
   sc.exe stop OpsifinB2
   ```

3. **Akun service khusus.** Buat akun lokal (mis. `opsifin-svc`), lalu di `services.msc` → OpsifinB2 → Log On, isi akun dan password-nya (cara ini sekaligus memberi hak *Log on as a service*). Start lalu stop service seperti langkah 2.

4. **Koneksi per mesin.** Untuk printer jaringan yang di langkah 2 tidak terlihat, pasang ulang sebagai koneksi per mesin lalu ulangi langkah 1-3:

   ```bat
   rundll32 printui.dll,PrintUIEntry /ga /n"\\server\nama-printer"
   net stop spooler && net start spooler
   ```

   Bila printer jaringan tidak lewat print server, alternatifnya pasang ulang dengan port Standard TCP/IP lokal.

5. **Bersihkan.**

   ```bat
   sc.exe delete OpsifinB2
   rundll32 printui.dll,PrintUIEntry /gd /n"\\server\nama-printer"
   ```

Kumpulkan semua file di `C:\ProgramData\Opsifin\PrintAgent\spike-b2\`.

## Rekap hasil

| PC / OS | Printer | Cara pasang | Port monitor | User | LocalSystem | Akun service | Catatan |
| --- | --- | --- | --- | --- | --- | --- | --- |
| | | USB / Bluetooth / jaringan per user / per mesin / TCP/IP lokal | | ✓/✗ | ✓/✗ | ✓/✗ | |

Status yang tampil di spike ini hanya potret saat laporan dibuat. Akurasinya (kertas habis, printer mati, Bluetooth putus) diuji terpisah di B3.
