# CLAUDE.md

Panduan untuk Claude Code (dan agent lain lewat `AGENTS.md`) saat bekerja di repo ini.

## Apa ini

**Opsifin Print Agent** adalah Windows service kecil yang dipasang di PC klien yang punya akses ke printer. Agent mengklaim job dari Print Hub, mengunduh PDF, mencetaknya ke printer Windows, memantau spooler, lalu melaporkan setiap perubahan ke Hub. Agent tidak pernah bicara dengan opsifin-main dan tidak menyimpan credential Opsifin.

Hub ada di repo `opsifin-print-hub`. Spesifikasi di `docs/PRD.md` dan `docs/PRD-ringkasan-keputusan.md` (ekspor dari https://claude.ai/artifact/Eg6irNPQwFVvrc2i1eB8zJ, sumber kebenaran).

Prinsip: **agent "bodoh" dan stabil, Hub yang pintar.** Logika bisnis (routing, retry, jadwal) ada di Hub; agent hanya melakukan pekerjaan fisik di PC. Pengaturan dikirim lewat remote config di balasan heartbeat, bukan lewat rilis agent baru.

## Stack

.NET 10 LTS Worker Service (Native AOT) · CsWin32 untuk winspool + GDI · PDFium (belum ditambahkan, Fase 0) · SQLite (`Microsoft.Data.Sqlite`) untuk outbox · DPAPI untuk device secret · installer MSI (WiX, belum dibuat).

## Struktur

| Proyek | TFM | Isi |
| --- | --- | --- |
| `src/Agent.Service` | `net10.0-windows` | Host Windows service, loop heartbeat/klaim/cetak, outbox SQLite, updater |
| `src/Agent.Printing` | `net10.0-windows` | Inventaris printer, cetak PDF (PDFium → GDI), pemantauan job spooler. API Win32 didaftarkan di `NativeMethods.txt` |
| `src/Agent.Hub` | `net10.0` | Klien HTTP untuk `/agent/v1`; bebas Windows supaya bisa dites di Linux |
| `tests/Agent.Tests` | `net10.0` | Test unit dan kontrak; jalan di Linux/CI |

## Build dan test

Di Windows dengan .NET 10 SDK:

```bash
dotnet build
dotnet test
dotnet run --project src/Agent.Service           # mode console
dotnet publish src/Agent.Service -c Release -r win-x64   # Native AOT, wajib di Windows
```

Di WSL/Linux (tanpa SDK lokal), lewat container:

```bash
docker run --rm -u $(id -u):$(id -g) -e HOME=/tmp -v $PWD:/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

Build di Linux bisa (`EnableWindowsTargeting`), tetapi **menjalankan agent, mencetak, dan publish AOT butuh Windows** dengan printer nyata.

## Kontrak dengan Hub

`contracts/agent-v1.yaml` adalah **salinan** dari `opsifin-print-hub/contracts/agent-v1.yaml`. Ubah kontrak di repo Hub dulu, lalu salin ke sini. `ContractTests` gagal bila endpoint yang dipakai agent hilang dari salinan.

Aturan yang wajib dipatuhi agent:

- Setiap event membawa `event_id` UUIDv7 buatan agent; event ditulis ke outbox SQLite sebelum dikirim, dan dikirim ulang sampai Hub menerima (idempoten).
- Konfirmasi `completed` hanya setelah job hilang dari antrean spooler tanpa status ERROR/DELETED dan printer tidak error dalam 60 detik berikutnya. Pantau berdasarkan `JobId` spooler, bukan nama dokumen.
- Bila respons event berisi `abort: true` atau `409 lease_lost`, hentikan job itu dan jangan cetak ulang.
- `401` → berhenti klaim, coba lagi tiap 5 menit. `426` → tunggu perintah `update`. `5xx`/jaringan → backoff eksponensial + jitter.

## Konvensi

- `TreatWarningsAsErrors` aktif untuk semua proyek (`Directory.Build.props`).
- Hindari reflection dan library yang tidak kompatibel dengan Native AOT; CsWin32 dipakai dengan `allowMarshaling: false`.
- Komentar kode dalam Bahasa Indonesia, menjelaskan *kenapa*, bukan *apa*.
