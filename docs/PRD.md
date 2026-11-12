> Ekspor dari https://claude.ai/artifact/Eg6irNPQwFVvrc2i1eB8zJ pada 2026-10-23. Dokumen itu sumber kebenaran; diagram hanya tampil di sana. Perbarui ekspor ini setiap Gate.

# PRD — Opsifin Print Hub (Autoprint Terpusat Multi-Tenant)

Oct 5, 2026 · @Aditya

## Ringkasan

Opsifin Print Hub menggantikan autoprint lama dengan dua komponen: **Hub** terpusat di cloud dan **Print Agent** ringan di setiap lokasi.

- **Hub** menjadi satu-satunya tempat mengelola tenant, lokasi, printer, aturan routing, dan riwayat cetak.
- **Agent** dipasang satu kali per PC lokasi dan menangani banyak printer sekaligus. Agent tidak menyimpan credential Opsifin; ia hanya memegang device token yang bisa dicabut kapan saja dari Hub.
- Setiap invoice menjadi satu **print job** dengan riwayat event lengkap, dari diambil dari Opsifin sampai kertas keluar dan status dilaporkan kembali.

| Atribut | Nilai |
| --- | --- |
| Status dokumen | Draft v0.1 untuk review |
| Produk | Opsifin Print Hub (pengganti opsifin-autoprint) |
| Sistem sumber | opsifin-main, modul `opsifin_api_print` |
| Ruang lingkup | Hub web multi-tenant, Print Agent Windows, adapter ke opsifin-main |

## Latar belakang dan masalah

Autoprint saat ini adalah aplikasi Python per PC. Satu instalasi hanya melayani satu printer, dan credential REST Opsifin disimpan di `data.db` lokal.

**Cara kerja sekarang** (dari kode `opsifin-autoprint` dan modul `opsifin_api_print` di opsifin-main):

1. Setiap 60 detik, agent memanggil `POST /PrinterStatus`. Status ditulis ke `mst_customer_printer_name.PrinterStatus` dan `LastConnected`.
2. Agent memanggil `POST /getData` dengan `PrinterName`, `PrinterVersion` (1 normal, 2 kop, 3 billing), dan `Identifier`. Server mengambil maksimal 50 baris `log_print_inv` yang `PrintProcess` New/Process, lalu langsung menandainya `Process`. Untuk billing, server juga mengisi `log_billing_process_file`. Jam 22.00 sampai 05.00 server mengembalikan daftar kosong.
3. Agent mengunduh PDF dari `Link`, mencetak lewat `ShellExecute printto`, lalu memanggil `POST /` (root `opsifin_api_print`). Panggilan ini memperbarui `print_status` di tabel `trx_*_invoice`, `log_print_inv`, `log_print_other`, atau `log_billing_process_file` sesuai `ItemType`.

**Masalah utama**

| Masalah | Dampak |
| --- | --- |
| Satu aplikasi untuk satu printer | Sepuluh printer berarti sepuluh instalasi `.exe` beserta `data.db`-nya masing-masing |
| Credential REST disimpan di PC user | Kunci Fernet ada di sebelah password. Mengganti password harus dilakukan di setiap PC |
| Konfigurasi tersebar di setiap PC | Tidak ada satu layar pun untuk melihat semua printer, versi, dan statusnya |
| Status job hanya tersirat | Tidak ada jejak antara "diambil" dan "tercetak". Job berstatus `Process` yang gagal di tengah jalan tidak terlihat |
| Penanda "sudah dicetak" tidak akurat | `printChecker` hanya memeriksa job pertama di antrean, sehingga `Success` bisa terkirim sebelum invoice benar-benar tercetak |
| PDF rusak tidak tercatat | Invoice diulang tanpa batas tanpa log dan tanpa status `Failed` |
| Retry tidak berujung | Satu invoice yang gagal diunduh atau di-POST menahan seluruh antrean di printer itu |
| Status online dihitung dari `LastConnected` | Status bergantung pada siklus 60 detik dan tidak membedakan PC mati dari printer offline |
| Upgrade dilakukan manual | Setiap perbaikan berarti build PyInstaller lalu menyalin `.exe` ke setiap PC |

## Tujuan, non-goals, dan metrik

Targetnya: semua printer di semua tenant dikelola dari satu Hub, dan setiap invoice bisa dilacak dari diambil sampai tercetak.

**Tujuan**

1. **Terpusat.** Tenant, lokasi, printer, routing, credential, dan jadwal diatur di Hub. Di PC lokasi tidak ada lagi konfigurasi selain pairing satu kali.
2. **Multi-tenant.** Satu Hub melayani banyak instance opsifin-main (satu tenant = satu instance) dengan data yang terisolasi.
3. **Banyak printer per agent.** Satu agent melayani semua printer yang terlihat dari PC tersebut.
4. **Bisa dilacak end-to-end.** Setiap perubahan status job tercatat sebagai event beserta waktu, aktor, dan detailnya.
5. **Andal dan jujur.** Status `Success` hanya dikirim setelah spooler Windows mengonfirmasi job selesai. Kegagalan berakhir sebagai `Failed` yang terlihat dan bisa dicetak ulang, bukan loop tanpa batas.
6. **Mudah dirawat.** Stack yang umum dipakai, satu repo per komponen, dan agent yang memperbarui dirinya sendiri.

**Non-goals (tidak masuk v1)**

- Membuat atau merender PDF invoice. Tetap menjadi tugas opsifin-main.
- Driver printer sendiri atau mencetak tanpa Windows spooler. Agent memakai printer yang sudah terpasang di Windows.
- Agent untuk macOS/Linux. Arsitekturnya memungkinkan, tetapi belum dibangun.
- Mengubah logika bisnis invoice (siapa yang perlu dicetak). Aturan itu tetap milik opsifin-main.
- Aplikasi mobile.

**Metrik keberhasilan** (diukur 30 hari setelah semua tenant dimigrasi)

| Metrik | Target | Cara ukur |
| --- | --- | --- |
| Waktu pasang printer baru | < 5 menit, tanpa menyentuh credential | Dari pairing sampai test page |
| Job yang statusnya keliru (`Success` tapi tidak tercetak) | 0 laporan | Tiket support |
| Job terlacak lengkap (punya event awal dan akhir) | 100% | Query `print_jobs` vs `print_job_events` |
| Latensi dari invoice siap sampai mulai cetak (p95) | < 45 detik | `queued_at` sampai `printing_at` |
| Job gagal yang terlihat di dashboard dalam 1 menit | 100% | Alert dan event `failed` |
| Instalasi `.exe` per printer | 0 (diganti 1 agent per PC) | Inventaris agent |

## Persona dan peran

Ada empat peran manusia dan satu aktor mesin. Hak akses diberikan per tenant, kecuali Platform Admin.

| Peran | Siapa | Kebutuhan utama | Hak akses |
| --- | --- | --- | --- |
| Platform Admin | Tim Opsigo (support/devops) | Menambah tenant, memantau semua agent, menangani insiden lintas tenant | Semua tenant, termasuk impersonate dengan audit |
| Tenant Admin | IT atau finance lead di sisi customer Opsifin | Mendaftarkan agent, mengatur printer dan routing, melihat laporan | Semua data di tenant-nya |
| Operator | Staf finance atau front office di lokasi | Melihat antrean, mencetak ulang job gagal, menjeda printer | Job dan printer di lokasi yang ditugaskan |
| Viewer / Auditor | Manajemen atau auditor | Riwayat cetak dan bukti cetak per invoice | Baca saja |
| Print Agent | Service Windows di PC lokasi | Mengambil job, mencetak, melaporkan status | Hanya endpoint `/agent/*` dengan device token |

**Skenario kunci**

- Tenant Admin membeli printer baru untuk cabang Surabaya. Printer dipasang di Windows, lalu tombol "Aktifkan" diklik di Hub dan routing "Invoice kop, cabang SBY" dipilih. Tidak perlu install apa pun.
- Operator melihat job `INV-2026-001` gagal karena kertas habis. Kertas diisi, lalu job dicetak ulang dari dashboard.
- Auditor bertanya kapan dan di printer mana `INV-2026-001` dicetak. Jawabannya ada di timeline job.
- Password REST opsifin-main dirotasi. Tenant Admin cukup memperbaruinya sekali di Hub; semua agent tetap berjalan.

## Konsep solusi dan arsitektur

Arsitekturnya *hub-and-agent*: satu Hub di cloud menjadi otak, dan agent tipis di setiap PC lokasi menjadi tangan yang menyentuh printer. Agent tetap diperlukan karena printer berada di LAN lokasi dan tidak bisa dijangkau langsung dari cloud. Yang dibuang adalah semua yang membuat agent lama gemuk: credential, konfigurasi, logika bisnis, dan batasan satu printer.

&#91;embedded content: arsitektur · Hub terpusat, 2 tenant, 2 site\]

Hanya Hub yang berbicara dengan opsifin-main. Agent tidak pernah memegang credential Opsifin dan tidak butuh port masuk.

**Keputusan arsitektur (ADR ringkas)**

| # | Keputusan | Alasan | Yang ditolak |
| --- | --- | --- | --- |
| ADR-1 | Hub terpusat + edge agent per PC (bukan per printer) | Printer ada di LAN lokasi. Satu agent bisa melihat semua printer Windows di PC-nya | Agent per printer (kondisi sekarang); cloud print/IPP langsung (butuh buka port atau VPN ke setiap lokasi) |
| ADR-2 | Agent menarik job lewat HTTPS keluar (poll klaim 5 detik) | Lolos firewall/proxy kantor, stateless, mudah di-debug. Push WebSocket (Reverb) bisa ditambahkan nanti sebagai sinyal "bangun" | WebSocket sejak hari pertama; port masuk di PC |
| ADR-3 | Hanya Hub yang memanggil opsifin-main | Credential terpusat dan rotasi cukup sekali. Adapter dan aturan kompatibilitas ada di satu tempat | Agent memanggil opsifin-main langsung |
| ADR-4 | Monolit modular Laravel (web + worker + scheduler dari satu codebase) | Tim kecil, satu deploy, transaksi DB sederhana. Modul dipisah per folder domain | Microservices, message broker terpisah |
| ADR-5 | Satu DB bersama + `tenant_id` + global scope | Operasional sederhana untuk puluhan tenant; isolasi dijaga oleh global scope dan pemeriksaan otomatis di CI | DB per tenant (migrasi dan backup berlipat) |
| ADR-6 | Tabel state saat ini + log event append-only | Jejak hulu ke hilir lengkap, tetapi query daftar job tetap sederhana | Event sourcing penuh (proyeksi dan replay menambah kompleksitas) |
| ADR-7 | Hub mengunduh dan menyimpan snapshot PDF; agent menerima signed URL | Validasi PDF sebelum dikirim ke printer, bukti cetak (hash), dan agent tidak perlu akses ke opsifin-main | Agent mengunduh langsung dari `Link` |
| ADR-8 | Cetak lewat PDFium ke GDI, SumatraPDF CLI sebagai cadangan | Hasil konsisten, tidak bergantung pada aplikasi PDF default di PC | `ShellExecute printto` (kondisi sekarang) |
|  |  |  |  |

**Modul di dalam Hub:** `Tenancy`, `Identity` (user, role, 2FA), `Fleet` (site, agent, printer, binding), `Sources` (adapter opsifin-main v2/v3), `Jobs` (state machine, klaim, retry), `Delivery` (callback outbox), `Notifications`, `Reporting`, `Audit`. Setiap modul punya folder sendiri di `app/Domain/*` dan hanya boleh dipanggil lewat service class publiknya.

**Modul di dalam Agent:** `enroll` (pairing + DPAPI), `heartbeat` (inventaris printer + status), `worker` (klaim, unduh, cetak, pantau spooler), `outbox` (SQLite, kirim ulang event), `updater` (self-update HTTPS + SHA-256).

## Tech stack

Rekomendasinya Laravel + MySQL untuk Hub dan .NET 10 (C#) untuk Agent. Hub memaksimalkan keahlian PHP tim, sementara agent memakai tooling Windows resmi Microsoft untuk spooler dan printer (alasan lengkap ada di tab Ringkasan & Keputusan).

| Lapisan | Pilihan | Alasan | Alternatif yang dipertimbangkan |
| --- | --- | --- | --- |
| Backend Hub | Laravel 13 di PHP 8.5 | Tim sudah menguasai PHP (opsifin-main memakai CI3). Ekosistemnya lengkap untuk queue, scheduler, auth, dan encryption. Rilisnya rutin setiap tahun | NestJS (TypeScript): tim harus belajar bahasa baru. Go: terlalu banyak yang harus dibangun untuk CRUD/admin |
| Admin UI | Filament 5 (di atas Livewire) | Tabel, filter, form, dan dashboard tersedia tanpa membangun SPA, sehingga kode UI berkurang 60-70% | Inertia + React, bila nanti butuh UX yang sangat custom |
| Real-time UI | Livewire polling 5 detik (v1), Laravel Reverb (v2) | Polling cukup untuk dashboard internal. Reverb adalah WebSocket resmi Laravel bila nanti diperlukan | Pusher / Soketi |
| Database | MySQL 9.7 LTS | Sama dengan opsifin-main, jadi tim sudah terbiasa mengoperasikan, mem-backup, dan men-tuning. `FOR UPDATE SKIP LOCKED` untuk klaim job, kolom `JSON` untuk payload event, partisi bulanan (`RANGE COLUMNS`) untuk tabel event. Isolasi tenant dijaga di aplikasi + CI karena MySQL tidak punya RLS | PostgreSQL 18: punya RLS dan `jsonb` yang lebih kuat, tetapi tim belum terbiasa mengoperasikannya |
| Queue, cache, lock | Redis 8 + Laravel Horizon | Antrean untuk worker pull, callback, dan notifikasi, lengkap dengan dashboard retry | Database queue: cukup untuk awal, tetapi lebih lambat |
| Penyimpanan PDF | Disk lokal (MVP), S3-compatible saat Hub lebih dari 1 instance | Snapshot PDF yang dicetak sebagai bukti, dengan retensi per tenant. Opsifin-main sudah punya config AWS, GCS, dan Azure | Disk lokal: tidak bisa di-scale |
| Print Agent | .NET 10 LTS Worker Service (Native AOT) | Akses winspool + GDI lewat CsWin32 (generator resmi Microsoft); WMI, DPAPI, dan Windows service host resmi. Biner Native AOT \~15-25 MB tanpa runtime terpisah | Go: biner lebih kecil, tetapi library printernya tidak aktif sejak 2020 dan cetak vektor PDFium butuh cgo |
| Mesin cetak PDF | PDFium ke GDI printer (vektor, default) + SumatraPDF CLI (`-print-to <printer> -silent`) sebagai cadangan | Tidak bergantung pada asosiasi file `.pdf` di PC (sumber masalah `printto` saat ini). Mesin bisa dipilih per printer dari Hub | ShellExecute printto (kondisi sekarang) |
| Penyimpanan lokal agent | SQLite (`Microsoft.Data.Sqlite`) | Outbox event saat offline dan cache job yang sedang diproses | File JSON |
| Credential agent | Windows DPAPI (machine scope) | Device token terenkripsi oleh OS dan tidak bisa dibaca dari PC lain | File plaintext (kondisi saat ini) |
| Observability | Sentry (error), Laravel Pulse (performa), log JSON terstruktur | Cukup untuk tim kecil. OpenTelemetry bisa ditambahkan kemudian | Stack Grafana penuh |
| Deployment | Docker Compose atau container di cloud yang ada; GitHub Actions untuk CI | Selaras dengan `docker-compose.yml` yang sudah ada di opsifin-main | Kubernetes: berlebihan untuk skala ini |
| Installer agent | MSI (WiX), distribusi HTTPS, dan verifikasi SHA-256 | Build dan release sederhana tanpa certificate berbayar; instalasi pertama dapat menampilkan SmartScreen | Inno Setup |

**Kenapa tidak lebih sederhana:** sistem tanpa agent tidak mungkin, karena printer berada di LAN lokasi dan tidak bisa dijangkau dari cloud.

**Kenapa tidak lebih rumit:** tidak memakai microservices, Kafka, atau Kubernetes. Hub adalah satu aplikasi Laravel (web + worker + scheduler), dan Agent adalah satu biner.

## Multi-tenant dan keamanan

Satu database bersama dipakai, dan setiap tabel bisnis punya kolom `tenant_id` yang dijaga di dua lapis: aplikasi dan database. Satu tenant setara dengan satu instance opsifin-main. Alasannya, setiap instance opsifin-main punya DB dan `REST_USERNAME`/`REST_PASSWORD` sendiri lewat env.

**Isolasi tenant**

- **Lapis aplikasi.** Trait `BelongsToTenant` menambahkan global scope dan otomatis mengisi `tenant_id`. Request web mengambil tenant dari sesi user, request agent dari device token.
- **Lapis CI (pengganti RLS).** MySQL tidak punya Row Level Security, jadi lapis kedua dipasang di pipeline. Test arsitektur (Pest `arch()`) memastikan setiap model bisnis memakai `BelongsToTenant`. Static analysis (aturan PHPStan) menolak query mentah `DB::table`/`DB::select` di luar repository class yang sudah di-review. Setiap tabel bisnis wajib punya index komposit yang diawali `tenant_id`.
- **Storage.** Prefix objek ditulis sebagai `tenants/{tenant_id}/documents/...`. Signed URL berlaku 5 menit.
- **Test wajib.** Setiap endpoint diuji silang: user atau agent tenant A tidak bisa membaca atau mengubah data tenant B.

**Credential ke opsifin-main**

- Disimpan sekali per tenant di Hub (`source_connections`), dienkripsi dengan Laravel encrypter (AES-256-GCM). Untuk produksi, kuncinya dibungkus KMS (envelope encryption).
- Credential tidak pernah dikirim ke agent. Hanya Hub yang memanggil opsifin-main.
- Rotasi: Tenant Admin mengganti password di Hub, lalu menekan "Test connection" (memanggil `PrinterStatus` dengan payload dummy). Nilai lama tetap disimpan 24 jam untuk rollback.

**Autentikasi agent (pairing)**

1. Tenant Admin membuat **pairing code** di Hub: 8 karakter, berlaku 15 menit, sekali pakai, terikat ke satu site.
2. Installer MSI meminta kode tersebut (atau menerimanya lewat parameter `PAIRING_CODE=` untuk instalasi massal).
3. Agent menukar kode dengan `agent_id` + `device_secret` acak 256-bit. Hub hanya menyimpan hash SHA-256-nya.
4. Agent menyimpan secret dengan DPAPI, lalu setiap request membawa `Authorization: Bearer <agent_id>.<secret>`.
5. Agent bisa dicabut (revoke) dari Hub dan langsung ditolak (401). Secret dirotasi otomatis setiap 90 hari.

**Kontrol lain**

- User login dengan email + password + TOTP (wajib untuk Tenant Admin dan Platform Admin). SSO menyusul di v2.
- RBAC per tenant dan per site (lihat Persona).
- Semua perubahan konfigurasi masuk `audit_logs`: siapa, kapan, nilai lama dan baru, dengan secret disamarkan.
- Rate limit per agent (60 req/menit) dan per user.
- Agent hanya membuka koneksi keluar (HTTPS 443). Tidak ada port masuk di PC lokasi.
- Update Agent hanya diterima dari command Hub terautentikasi, URL production HTTPS, versi lebih baru, dan SHA-256 yang cocok. Tidak ada migrasi versi lama karena baseline berikutnya dipasang sebagai fresh install.

## Kebutuhan fungsional

Ada 39 kebutuhan. Yang berlabel P0 membentuk MVP dan harus selesai sebelum pilot; P1 masuk rollout; P2 menyusul.

| ID | Modul | Kebutuhan | Prioritas |
| --- | --- | --- | --- |
| FR-01 | Tenant & user | Platform Admin membuat tenant: nama, kode, timezone, retensi data | P0 (MVP) |
| FR-02 | Tenant & user | Undang user lewat email dengan role per tenant atau per site | P0 (MVP) |
| FR-03 | Tenant & user | 2FA TOTP wajib untuk Tenant Admin dan Platform Admin | P0 (MVP) |
| FR-04 | Tenant & user | Platform Admin bisa impersonate dengan banner dan audit | P1 |
| FR-05 | Source | Atur base URL opsifin-main + credential REST, lengkap dengan tombol Test connection | P0 (MVP) |
| FR-06 | Source | Jam aktif / quiet hours per tenant (default 05.00 sampai 22.00, sama dengan aturan `getData`) | P0 (MVP) |
| FR-07 | Source | Interval pull per binding bisa diatur (default 30 detik, minimum 15 detik, maksimal 2 paralel per tenant) | P0 (MVP) |
| FR-08 | Site & agent | CRUD site (cabang/lokasi) dengan alamat dan PIC | P0 (MVP) |
| FR-09 | Site & agent | Buat pairing code; installer MSI melakukan pairing secara interaktif atau diam | P0 (MVP) |
| FR-10 | Site & agent | Heartbeat agent tiap 30 detik: versi, hostname, OS, uptime, daftar printer Windows + status | P0 (MVP) |
| FR-11 | Site & agent | Status agent: Online (< 90 detik), Stale (90-180 detik), Offline (> 180 detik), lengkap dengan alert | P0 (MVP) |
| FR-12 | Site & agent | Revoke dan rotasi secret agent | P0 (MVP) |
| FR-13 | Site & agent | Update agent dari jarak jauh, bertahap (canary per persentase lalu semua) | P1 |
| FR-14 | Site & agent | Minta log agent 24 jam terakhir dari jarak jauh | P1 |
| FR-15 | Printer | Printer terdeteksi otomatis dari agent; admin mengaktifkan dan memberi alias | P0 (MVP) |
| FR-16 | Printer | Binding printer ke opsifin-main (`PrinterName`/`Identifier` sumber + versi 1/2/3); satu printer bisa punya banyak binding | P0 (MVP) |
| FR-17 | Printer | Sinkronkan binding ke `mst_customer_printer_name` lewat `/printerName` saat dibuat atau diubah | P0 (MVP) |
| FR-18 | Printer | Pause/resume printer; job tetap antre dan tidak dianggap gagal | P0 (MVP) |
| FR-19 | Printer | Status printer (ONLINE, OFFLINE, PAPER\_OUT, PAPER\_JAM, NO\_TONER, dll) tampil di Hub dan diteruskan ke `/PrinterStatus` | P0 (MVP) |
| FR-20 | Printer | Test page dari Hub | P1 |
| FR-21 | Printer | Printer cadangan otomatis bila printer utama error lebih dari N menit | P2 |
| FR-22 | Printer | Pengaturan cetak per printer: tray, duplex, jumlah salinan, ukuran kertas | P1 |
| FR-23 | Print job | Pull job dari opsifin-main per binding, dedupe per (tenant, InvNo, ItemType, versi) | P0 (MVP) |
| FR-24 | Print job | Validasi PDF di Hub (bisa dibuka, jumlah halaman, ukuran), simpan snapshot + SHA-256 | P0 (MVP) |
| FR-25 | Print job | `IsHardCopyInv = N`: job ditutup sebagai `skipped` dan callback Success dikirim (perilaku lama), tetapi tercatat | P0 (MVP) |
| FR-26 | Print job | Agent mengklaim job dengan lease 120 detik; satu job hanya dipegang satu agent | P0 (MVP) |
| FR-27 | Print job | Agent mencetak, memantau job di spooler berdasarkan JobId, lalu melaporkan setiap perubahan | P0 (MVP) |
| FR-28 | Print job | Callback hasil ke opsifin-main (Success/Failed) lewat outbox, retry dengan backoff sampai 24 jam | P0 (MVP) |
| FR-29 | Print job | Retry otomatis untuk kegagalan transien, maksimal 3 attempt, lalu Failed + notifikasi | P0 (MVP) |
| FR-30 | Print job | Cetak ulang manual: membuat attempt baru dan alasan wajib diisi | P0 (MVP) |
| FR-31 | Print job | Batalkan job yang belum tercetak | P0 (MVP) |
| FR-32 | Print job | Detail job: timeline event, daftar attempt, printer, agent, snapshot PDF, payload dan respons callback | P0 (MVP) |
| FR-33 | Print job | Prioritas antrean per jenis dokumen (mis. billing lebih dulu) | P2 |
| FR-34 | Monitoring | Dashboard: printer online/offline, kedalaman antrean, job gagal 24 jam, agent offline | P0 (MVP) |
| FR-35 | Monitoring | Notifikasi (email, Telegram/Slack, webhook) untuk agent offline, printer error > 5 menit, job Failed, callback gagal | P1 |
| FR-36 | Monitoring | Laporan volume per hari/printer/site, rasio gagal, alasan gagal; ekspor CSV | P1 |
| FR-37 | Monitoring | Audit log untuk semua perubahan konfigurasi | P0 (MVP) |
| FR-38 | API | API status job per InvNo (token per tenant) agar opsifin-main bisa menampilkan status cetak | P1 |
| FR-39 | API | Endpoint push dari opsifin-main ("ada job baru") agar latensi tidak bergantung interval pull | P2 |

## Lifecycle print job (hulu ke hilir)

Setiap dokumen menjadi satu baris `print_jobs` yang bergerak di state machine. Setiap transisi menulis satu event ke `print_job_events` (siapa, kapan, data teknis). Timeline di layar Detail Job dibentuk dari event-event tersebut.

&#91;embedded content: state machine print job · 7 state + callback\]

Jalur utama berjalan dari kiri ke kanan. Kegagalan transien kembali ke `queued`; kegagalan permanen berhenti di `failed` sampai operator mencetak ulang. `cancelled` (dibatalkan user dari `queued`/`failed`) tidak digambar.

| State | Arti | Masuk lewat | Keluar ke |
| --- | --- | --- | --- |
| `received` | Baris dari `getData` tersimpan, PDF belum dicek | Source Puller | `queued`, `skipped`, `failed` |
| `queued` | PDF valid dan tersimpan di storage; menunggu printer dan agent | Validasi OK, retry, cetak ulang | `claimed`, `cancelled` |
| `claimed` | Agent memegang lease 120 detik (diperpanjang lewat heartbeat job) | `POST /agent/v1/jobs/claim` | `printing`, `queued` (lease habis/error transien) |
| `printing` | Dikirim ke spooler Windows; `spooler_job_id` diketahui | Event agent `spooled` | `printed`, `queued`, `failed` |
| `printed` | Spooler melaporkan job selesai tanpa status error | Event agent `completed` | Callback `Success`; cetak ulang manual membuat attempt baru |
| `skipped` | `IsHardCopyInv = N`, tidak perlu dicetak | Source Puller | Callback `Success` |
| `failed` | Attempt habis atau error permanen (`PDF_INVALID`, `PRINTER_NOT_FOUND`) | Job Engine | Callback `Failed`; `queued` lewat cetak ulang |
| `cancelled` | Dibatalkan user, wajib dengan alasan | Operator/Admin | Terminal (tanpa callback) |

**Katalog event** (kolom `type`)

| Tahap | Event | Data yang dicatat |
| --- | --- | --- |
| Hulu (sumber) | `received`, `duplicate_ignored` | Payload `getData` asli, binding, latensi request |
| Dokumen | `pdf_downloaded`, `pdf_validated`, `pdf_invalid` | URL sumber, ukuran, jumlah halaman, SHA-256, pesan error parser |
| Antrean | `queued`, `claimed`, `lease_extended`, `lease_expired`, `released` | Agent, printer, nomor attempt, lease sampai kapan |
| Agent | `download_started`, `download_completed`, `spooled`, `spooler_status`, `completed`, `print_error` | Durasi unduh, `spooler_job_id`, status mentah spooler (mis. `PAPEROUT`), jumlah halaman tercetak, versi agent |
| Manusia | `reprint_requested`, `cancelled`, `paused_by_printer` | User, alasan, printer tujuan |
| Hilir (callback) | `callback_sent`, `callback_failed`, `callback_succeeded` | Endpoint, payload, status HTTP, isi respons, percobaan ke-n |

**Aturan keandalan**

- **Klaim atomik.** `SELECT id FROM print_jobs WHERE printer_id IN (:printers) AND state = 'queued' ORDER BY received_at LIMIT :n FOR UPDATE SKIP LOCKED`. Query ini dan UPDATE baris terpilih menjadi claimed berjalan dalam satu transaksi (MySQL tidak mengizinkan UPDATE dengan subquery ke tabel yang sama). Agent mengambil maksimal 1 job per printer sekaligus, sehingga urutan cetak di setiap printer terjaga.
- **Lease.** Job `claimed`/`printing` tanpa event dari agent selama 120 detik kembali ke `queued` (event `lease_expired`). Untuk mencegah cetak ganda saat agent hanya terputus jaringan, agent mengecek spooler lokal sebelum mencetak ulang job yang di-lease ulang olehnya sendiri.
- **Idempotensi.** Setiap event dari agent membawa `event_id` (UUIDv7) yang dibuat agent. Hub mengabaikan `event_id` yang sudah ada, jadi agent aman mengirim ulang dari outbox.
- **Retry otomatis.** Error transien (printer offline, spooler sibuk, unduh gagal) di-retry dengan jeda 30 detik, 2 menit, lalu 10 menit. Setelah attempt ke-3, job menjadi `failed`. Error permanen langsung `failed`.
- **Konfirmasi cetak.** Agent memantau job berdasarkan `spooler_job_id` (bukan nama dokumen). `completed` hanya dikirim bila job hilang dari antrean tanpa pernah berstatus `ERROR`/`DELETED`, dan printer tidak berstatus error dalam 60 detik berikutnya. Kalau printer error dalam jendela itu, event `printed_unverified` dicatat.
- **Pause printer.** Job tetap `queued` dan tidak dihitung sebagai attempt.
- **Waktu.** `occurred_at` diisi dari jam agent (disinkronkan dengan jam server lewat heartbeat), `recorded_at` dari jam server. Selisih > 60 detik memicu peringatan clock drift.

## Kontrak API Agent ↔ Hub

Agent hanya bicara ke Hub lewat 6 endpoint di bawah `/agent/v1`, semuanya HTTPS + JSON dan dimulai oleh agent. Hub tidak pernah membuka koneksi ke PC klien, jadi tidak perlu port masuk atau VPN. Kontrak ini ditulis sebagai OpenAPI 3.1 di `contracts/agent-v1.yaml` (repo Hub) dan menjadi acuan kedua repo.

| Endpoint | Kapan dipanggil | Request utama | Response utama |
| --- | --- | --- | --- |
| `POST /agent/v1/enroll` | Sekali saat instalasi (tanpa token, rate limit per IP) | `pairing_code`, `hostname`, `os`, `agent_version`, `machine_id` | `agent_id`, `device_secret`, `tenant_id`, `site_id`, `config` awal |
| `POST /agent/v1/heartbeat` | Setiap 30 detik | `agent_version`, `uptime_s`, `agent_time`, `printers[]` (nama, driver, port, status, status mentah spooler, panjang antrean) | `server_time`, `config` + `config_version`, `commands[]` |
| `POST /agent/v1/jobs/claim` | Setiap 5 detik bila ada printer aktif | `printer_ids[]` yang sedang kosong | `200` + `jobs[]` (maksimal 1 per printer), atau `204` bila tidak ada job |
| `GET` signed URL dari `claim` | Setelah klaim | (tanpa body; URL berlaku 5 menit) | Isi PDF; agent mencocokkan `sha256` sebelum mencetak |
| `POST /agent/v1/jobs/{job_id}/events` | Setiap perubahan status job, juga saat mengirim ulang dari outbox | `events[]`: `event_id` (UUIDv7), `attempt_id`, `type`, `occurred_at`, `data` | `accepted[]`, `duplicates[]`, `job_state`, `lease_until`, `abort` |
| `POST /agent/v1/commands/{command_id}/result` | Setelah menjalankan perintah dari heartbeat | `status`, `message`, lampiran opsional (mis. zip log) | `204` |

**Contoh respons klaim**

```json
{
  "jobs": [{
    "job_id": "0192f1c4-7a1e-7c3b-9f00-3b1d2c4e5f60",
    "attempt_id": "0192f1c4-7a2b-7d11-8a00-91e2f3a4b5c6",
    "printer_id": "0192a0b1-0000-7000-8000-000000000001",
    "lease_until": "2026-10-23T08:02:00Z",
    "document": {
      "url": "https://hub.example/agent/v1/documents/...?sig=...",
      "sha256": "9f86d081...",
      "pages": 2,
      "size_bytes": 184320
    },
    "print": { "engine": "pdfium", "copies": 1, "duplex": false, "tray": null, "scale": "noscale" }
  }]
}
```

**Aturan kontrak**

- **Auth.** Semua endpoint kecuali `enroll` memakai `Authorization: Bearer <agent_id>.<secret>`. Rotasi secret dikirim sebagai perintah `rotate_secret` lewat heartbeat; secret lama masih diterima 10 menit setelah secret baru pertama dipakai.
- **Lease.** Setiap event yang diterima memperpanjang `lease_until`. Bila job dibatalkan operator atau lease sudah pindah, respons event membawa `abort: true` (atau `409 lease_lost`); agent menghentikan job itu dan tidak mencetak ulang.
- **Perintah dari Hub.** Tipe di `commands[]`: `test_page`, `upload_logs`, `update` (URL + SHA-256 + tanda tangan paket), `rotate_secret`, `reload_config`. Setiap perintah punya `id` dan hasilnya dilaporkan lewat `commands/{id}/result`.
- **Kode error.** Format `{"error": {"code", "message"}}`. `401` = token salah/dicabut: agent berhenti klaim dan mencoba lagi tiap 5 menit. `409` = lease hilang. `422` = payload tidak valid (bug, dicatat di log). `426` = versi agent terlalu lama, harus update. `429` = ikuti `Retry-After`. `5xx`/jaringan = backoff eksponensial dengan jitter, event tetap di outbox.
- **Versi.** Selama `v1`, perubahan hanya boleh menambah field opsional. Agent mengirim header `X-Agent-Version`; Hub menolak versi di bawah batas minimum dengan `426`. Perubahan yang memecah kompatibilitas menjadi `/agent/v2`, dan v1 tetap hidup sampai semua agent ter-update.
- **Waktu.** `server_time` di heartbeat dipakai agent untuk menghitung selisih jam; selisih lebih dari 60 detik memicu peringatan clock drift.
- **Uji kontrak.** CI repo Hub memvalidasi request/response terhadap `agent-v1.yaml`. CI repo agent menjalankan tes yang sama terhadap Hub tiruan, jadi kedua sisi tidak bisa menyimpang diam-diam.

## Integrasi dengan opsifin-main

Fase 1 tidak mengubah apa pun di opsifin-main. Hub berperan sebagai "agent lama" untuk setiap binding dan memanggil endpoint `opsifin_api_print` yang sudah ada dengan payload yang sama persis. Fase 3 menambahkan API antrean v3 yang lebih bersih.

**Fase 1: adapter kompatibel (Hub memanggil endpoint lama)**

| Kapan | Endpoint opsifin-main | Payload yang dikirim Hub | Efek di opsifin-main |
| --- | --- | --- | --- |
| Binding dibuat atau diubah | `POST /printerName` | `PrinterName`, `PrinterNameOld`, `PrinterAlias`, `PrinterAliasOld`, `PrinterVersion`, `Identifier` | Insert/update `mst_customer_printer_name` + `log_mst_customer_printer` |
| Setiap heartbeat agent (maksimal 1x per 60 detik per printer) | `POST /PrinterStatus` | `PrinterName`, `Identifier`, `PrinterStatus` | Update `PrinterStatus` + `LastConnected`; layar `customer_printer` tetap akurat |
| Setiap interval pull per binding (default 30 detik) | `POST /getData` | `VersionAPI: 2`, `PrinterName`, `PrinterVersion`, `Identifier` | Mengembalikan maksimal 50 item dan menandainya `Process` (billing: `log_billing_process_file`) |
| Job selesai, gagal, atau di-skip | `POST /` (root `opsifin_api_print`) | `InvNo`, `ItemType`, `PrinterName`, `PrintProcess` atau `PrintProcessV2` = `Success`/`Failed` | Update `trx_*_invoice.print_status`, `log_print_inv`, `log_print_other`, atau `log_billing_process_file` |
|  |  |  |  |

**Aturan adapter yang harus dipertahankan** (diambil dari perilaku agent lama)

- `ItemType` `PO/TO` disingkat jadi `PO` untuk nama file, tetapi dikirim apa adanya di callback.
- Field status: `PrintProcessV2` diisi bila `PrinterVersion = 1` dan `VersionInv = 2`; selain itu `PrintProcess`.
- `IsHardCopyInv = N`: tidak dicetak, langsung callback `Success` tanpa `PrinterName` (FR-25).
- `getData` menandai `Process` saat item diambil, dan item `Process` ikut terambil lagi di panggilan berikutnya. Karena itu Hub wajib melakukan dedupe dengan kunci `(tenant, InvNo, ItemType, VersionInv)` agar satu invoice tidak dicetak dua kali.
- **Baru:** kegagalan permanen dikirim sebagai `Failed`. Endpoint lama sudah menerimanya (`print_status = N`, `print_note = 'Failed to print'`), tetapi agent lama tidak pernah mengirimnya.

**Fase 3: API antrean v3 (perubahan di opsifin-main, opsional tetapi direkomendasikan)**

| Endpoint baru | Tujuan |
| --- | --- |
| `GET /api/print/v3/queue?since=<cursor>` | Daftar item siap cetak dengan ID stabil, `customer_id`, `branch_id`, `implant_code`, `doc_type`, `version`, `pdf_url`, dan `updated_at`. Tidak ada efek samping saat dibaca |
| `POST /api/print/v3/jobs/{id}/status` | Status granular (`queued`, `printing`, `printed`, `failed` + alasan) dengan header `Idempotency-Key` |
| Webhook keluar ke Hub (FR-39) | Opsional: memberi tahu Hub saat item baru siap, supaya latensi tidak lagi bergantung interval pull |

Dengan v3, keputusan **ke printer mana** sebuah dokumen dikirim bisa pindah dari join `mst_customer.Printer/PrinterKOP` ke aturan routing di Hub (per customer, cabang, implant, jenis dokumen). Sampai saat itu, sumber kebenaran routing tetap opsifin-main.

**Catatan:** link PDF (`Link`) saat ini diunduh tanpa autentikasi. Hub yang mengunduhnya, lalu agent hanya menerima signed URL dari storage Hub. Dengan begitu agent tidak perlu akses jaringan ke opsifin-main.

## Data model

Inti skemanya adalah tiga tabel: `print_jobs` (keadaan sekarang), `print_job_attempts` (setiap percobaan cetak), dan `print_job_events` (log append-only, sumber jejak hulu ke hilir). Semua tabel bisnis punya `tenant_id`, dan primary key memakai UUIDv7 (urut waktu, aman diekspos).

| Tabel | Isi | Kolom penting |
| --- | --- | --- |
| `tenants` | Satu baris per instance opsifin-main | `code`, `name`, `timezone`, `quiet_hours`, `retention_days`, `status` |
| `users`, `memberships` | User dan role per tenant/site | `role` (tenant\_admin, operator, viewer), `site_ids[]`, `totp_secret` |
| `source_connections` | Koneksi ke opsifin-main | `base_url`, `username`, `password_enc`, `api_version` (2 / 3), `pull_interval_s`, `last_ok_at`, `last_error` |
| `sites` | Cabang/lokasi fisik | `name`, `address`, `pic`, `timezone` |
| `agents` | Satu baris per PC terpasang | `site_id`, `hostname`, `version`, `secret_hash`, `last_seen_at`, `status`, `revoked_at` |
| `printers` | Printer Windows yang dilaporkan agent | `agent_id`, `windows_name`, `alias`, `status`, `status_detail`, `is_active`, `is_paused`, `print_settings json` |
| `printer_bindings` | Hubungan printer ke antrean opsifin-main | `printer_id`, `source_printer_name`, `source_identifier`, `printer_version` (1/2/3), `is_active` |
| `documents` | Snapshot PDF yang dicetak | `storage_key`, `sha256`, `size_bytes`, `page_count`, `source_url`, `fetched_at`, `expires_at` |
| `print_jobs` | Satu baris per dokumen yang harus dicetak | lihat DDL |
| `print_job_attempts` | Satu baris per percobaan cetak | lihat DDL |
| `print_job_events` | Log append-only, dipartisi per bulan | lihat DDL |
| `callback_outbox` | Panggilan ke opsifin-main yang belum terkirim | `job_id`, `endpoint`, `payload json`, `attempts`, `next_attempt_at`, `last_response`, `sent_at` |
| `printer_status_history` | Perubahan status printer (hanya saat berubah) | `printer_id`, `status`, `detail`, `at` |
| `audit_logs` | Perubahan konfigurasi oleh manusia | `actor_id`, `action`, `subject`, `before json`, `after json`, `ip` |

```sql
-- MySQL 9.7, InnoDB, utf8mb4. Semua waktu disimpan UTC di DATETIME(6).
CREATE TABLE print_jobs (
  id              CHAR(36) PRIMARY KEY,        -- UUIDv7
  tenant_id       CHAR(36) NOT NULL,
  binding_id      CHAR(36) NOT NULL,
  printer_id      CHAR(36) NOT NULL,
  dedupe_key      VARCHAR(191) NOT NULL,       -- InvNo|ItemType|VersionInv
  inv_no          VARCHAR(64) NOT NULL,
  item_type       VARCHAR(32) NOT NULL,        -- TICKET, PO/TO, INV, BILL, ...
  version_inv     SMALLINT,
  customer_id     VARCHAR(64), customer_name VARCHAR(255),
  branch_id       VARCHAR(64), implant_code VARCHAR(64),
  is_hardcopy     BOOLEAN NOT NULL,
  document_id     CHAR(36),
  state           VARCHAR(16) NOT NULL,        -- lihat state machine
  failure_code    VARCHAR(32),                 -- PDF_INVALID, PRINTER_OFFLINE, ...
  attempt_count   SMALLINT NOT NULL DEFAULT 0,
  lease_agent_id  CHAR(36), lease_until DATETIME(6),
  source_payload  JSON NOT NULL,               -- baris asli dari getData
  received_at     DATETIME(6) NOT NULL,
  printed_at      DATETIME(6),
  reported_at     DATETIME(6),
  UNIQUE KEY uq_dedupe (tenant_id, dedupe_key),
  KEY ix_claim (printer_id, state, received_at),   -- dipakai query klaim
  FOREIGN KEY (tenant_id)   REFERENCES tenants (id),
  FOREIGN KEY (binding_id)  REFERENCES printer_bindings (id),
  FOREIGN KEY (printer_id)  REFERENCES printers (id),
  FOREIGN KEY (document_id) REFERENCES documents (id)
);

CREATE TABLE print_job_attempts (
  id               CHAR(36) PRIMARY KEY,
  tenant_id        CHAR(36) NOT NULL,
  job_id           CHAR(36) NOT NULL,
  attempt_no       SMALLINT NOT NULL,
  agent_id         CHAR(36) NOT NULL,
  printer_id       CHAR(36) NOT NULL,
  trigger_type     VARCHAR(16) NOT NULL,       -- auto | retry | manual_reprint (TRIGGER kata kunci MySQL)
  requested_by     CHAR(36),                   -- user bila manual
  reason           VARCHAR(500),
  spooler_job_id   INT,
  pages_printed    INT,
  started_at       DATETIME(6), finished_at DATETIME(6),
  outcome          VARCHAR(16),                -- printed | failed | cancelled | lease_expired
  UNIQUE KEY uq_attempt (job_id, attempt_no),
  FOREIGN KEY (job_id) REFERENCES print_jobs (id)
);

-- Tabel terpartisi di MySQL tidak boleh punya foreign key,
-- dan setiap unique key wajib memuat kolom partisi.
CREATE TABLE print_job_events (
  id          CHAR(36) NOT NULL,               -- dibuat oleh pengirim (idempoten)
  tenant_id   CHAR(36) NOT NULL,
  job_id      CHAR(36) NOT NULL,
  attempt_id  CHAR(36),
  type        VARCHAR(32) NOT NULL,            -- received, validated, claimed, spooled, ...
  from_state  VARCHAR(16), to_state VARCHAR(16),
  actor_type  VARCHAR(16) NOT NULL,            -- system | agent | user | source
  actor_id    VARCHAR(64),
  data        JSON NOT NULL DEFAULT (JSON_OBJECT()),  -- status spooler, error, respons HTTP, dll
  occurred_at DATETIME(6) NOT NULL,            -- jam di sumber (agent)
  recorded_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id, occurred_at),
  KEY ix_timeline (tenant_id, job_id, occurred_at)
)
PARTITION BY RANGE COLUMNS (occurred_at) (
  PARTITION p2026_11 VALUES LESS THAN ('2026-12-01'),
  PARTITION pmax     VALUES LESS THAN (MAXVALUE)  -- scheduler memecah pmax tiap bulan
);
```

Retensi default: `print_job_events` 13 bulan (partisi lama di-drop), `documents` 90 hari (objek dihapus, metadata dan hash tetap). Keduanya bisa diatur per tenant.

## Kebutuhan non-fungsional

Target dirancang untuk asumsi 50 tenant, 300 agent, dan 1.000 printer. Angka ini perlu dikonfirmasi (lihat pertanyaan terbuka); arsitekturnya masih cukup sampai sekitar 10x tanpa perubahan desain.

| Kategori | ID | Kebutuhan | Target |
| --- | --- | --- | --- |
| Performa | NFR-01 | API agent (heartbeat, klaim, event) | p95 < 200 ms |
| Performa | NFR-02 | Latensi job siap di opsifin-main sampai `spooled` | p95 < 45 detik (pull 30 detik + klaim 5 detik) |
| Performa | NFR-03 | Dashboard dan daftar job (filter 30 hari) | p95 < 1,5 detik |
| Reliabilitas | NFR-04 | Ketersediaan Hub | 99,5% per bulan (jam 05.00 sampai 22.00 WIB) |
| Reliabilitas | NFR-05 | Agent tetap mencetak job yang sudah diklaim saat Hub tidak terjangkau; event disimpan di outbox lokal lalu dikirim saat tersambung | 0 event hilang |
| Reliabilitas | NFR-06 | Tidak ada cetak ganda karena retry jaringan | Klaim memakai lease + `SKIP LOCKED`, event idempoten per `event_id` |
| Reliabilitas | NFR-07 | Agent pulih sendiri setelah PC reboot atau crash | Windows service start otomatis + recovery action restart |
| Observability | NFR-08 | Setiap request membawa `trace_id` yang ikut ke event job dan log agent | 100% |
| Observability | NFR-09 | Metrik: kedalaman antrean per printer, umur job tertua, rasio gagal, agent offline, latensi callback | Tampil di Pulse/Grafana + alert |
| Keamanan | NFR-10 | Isolasi tenant diuji otomatis di CI | Test lintas tenant di setiap endpoint |
| Keamanan | NFR-11 | Secret tidak pernah muncul di log, UI, atau audit | Disamarkan di semua tempat |
| Keamanan | NFR-12 | TLS 1.2+ untuk semua koneksi; paket OTA harus cocok dengan SHA-256 yang dihitung Hub | Wajib |
| Agent | NFR-13 | Jejak di PC lokasi | RAM < 60 MB idle, CPU < 1% idle, satu service, satu folder |
| Agent | NFR-14 | Kompatibilitas OS | Windows 10/11 dan Windows Server 2016+ (x64) |
| Maintainability | NFR-15 | Cakupan test pada domain inti (state machine, klaim, dedupe, adapter) | ≥ 80% baris; test kontrak untuk API agent |
| Maintainability | NFR-16 | Kontrak API agent terversi (`/agent/v1`) dan didokumentasikan dengan OpenAPI | Hub mendukung versi N dan N-1 agent |
| Data | NFR-17 | Backup MySQL | PITR, RPO ≤ 15 menit, RTO ≤ 4 jam |
| Data | NFR-18 | Semua waktu disimpan dalam UTC dan ditampilkan sesuai timezone tenant | Wajib |

## UX: menu dan alur layar

Hub dibuka di layar **Dashboard** yang menjawab satu pertanyaan: "apakah ada yang perlu saya tangani sekarang?" Semua layar lain bisa dicapai dalam satu klik dari kartu masalah.

| Menu | Isi | Aksi utama | Peran |
| --- | --- | --- | --- |
| Dashboard | Kartu: printer bermasalah, agent offline, job gagal 24 jam, job tertua di antrean. Grafik volume hari ini per jam | Klik kartu untuk membuka daftar yang sudah terfilter | Semua |
| Print Jobs | Tabel job dengan filter: state, printer, site, item type, rentang tanggal, InvNo | Cetak ulang, batalkan, ekspor CSV | Operator+ |
| Detail Job | Header (InvNo, customer, state) + timeline event + tab Attempts, Dokumen (pratinjau PDF + SHA-256), Callback (request/response) | Cetak ulang ke printer yang sama/lain, unduh PDF | Operator+ |
| Printers | Kartu per printer: status, antrean, terakhir cetak, binding | Aktifkan, pause/resume, test page, atur binding & pengaturan cetak | Tenant Admin (Operator: pause/resume) |
| Agents | Tabel agent: site, hostname, versi, last seen, printer terdeteksi | Pairing code baru, revoke, minta log, jadwalkan update | Tenant Admin |
| Sites | Daftar lokasi + agent + printer di dalamnya | CRUD | Tenant Admin |
| Source | Koneksi opsifin-main, jam aktif, interval pull, status terakhir | Test connection, rotasi credential | Tenant Admin |
| Reports | Volume per hari/printer/site, rasio gagal, alasan gagal teratas, latensi p95 | Ekspor CSV | Viewer+ |
| Audit Log | Perubahan konfigurasi dengan diff | Filter, ekspor | Tenant Admin, Viewer |
| Settings | User & role, notifikasi, retensi | Undang user, atur kanal notifikasi | Tenant Admin |
| Platform (khusus Platform Admin) | Tenant, semua agent lintas tenant, rilis agent | Buat tenant, impersonate, kelola rollout versi | Platform Admin |

**Alur 1: pasang PC + printer baru (target < 5 menit)**

1. Agents, lalu "Tambah agent": pilih site, dan Hub menampilkan pairing code + link unduh MSI.
2. Di PC lokasi: jalankan MSI dan masukkan kode. Agent muncul *Online* di Hub dalam 30 detik beserta daftar printer Windows-nya.
3. Printers, lalu pilih printer baru dan "Aktifkan": isi alias dan tambahkan binding (pilih versi 1/2/3; `PrinterName` sumber terisi otomatis).
4. Klik "Test page". Bila kertas keluar, status printer berubah *Ready*.

**Alur 2: menangani job gagal**

1. Kartu "3 job gagal" di Dashboard membuka Print Jobs dengan filter `state = failed`.
2. Detail job memperlihatkan alasannya di timeline, misalnya `PAPER_OUT` di attempt 3.
3. Setelah masalah fisik beres, operator klik "Cetak ulang" dan mengisi alasan. Attempt baru dibuat, dan callback `Success` dikirim setelah tercetak.

**Prinsip UI:** status selalu memakai kata + warna (bukan warna saja), waktu ditampilkan relatif dengan tooltip absolut, setiap aksi destruktif meminta konfirmasi + alasan, dan tabel bisa dibagikan lewat URL berisi filter.

## Rencana rilis dan migrasi

Migrasi dilakukan per printer, bukan big bang. Pada Fase 1-3, opsifin-main tidak perlu diubah sama sekali, sehingga rollback cukup dengan menyalakan kembali `.exe` lama di printer yang bermasalah.

&#91;embedded content: roadmap · 5 fase, 4 gate (estimasi)\]

Durasi di atas adalah estimasi awal dan perlu dikalibrasi ulang di Gate 1 setelah spike.

**Runbook cutover per printer** (dijalankan Tenant Admin dengan pendampingan tim Opsigo di gelombang pertama)

1. Pasang Print Agent di PC (MSI + pairing code). Agent dan `.exe` lama boleh hidup bersamaan karena agent belum memegang binding.
2. Di Hub, aktifkan printer dan buat binding dengan `PrinterName`/`Identifier` yang **sama persis** dengan `data.db` lama (`config.printerUsed`, `config.identifier`, `config.versionInv`). Binding dibiarkan nonaktif.
3. Hentikan dan uninstall `autoprint.exe` lama. Simpan `data.db` lama sebagai arsip.
4. Aktifkan binding. Hub mulai memanggil `getData` untuk printer tersebut.
5. Kirim test page, lalu pantau 1 jam: job pertama harus mencapai `printed` dan callback `Success` harus terlihat di `log_print_inv`.
6. Rollback bila perlu: nonaktifkan binding, lalu install ulang `.exe` lama dengan `data.db` arsip.

**Urutan gelombang rollout:** tenant pilot, lalu tenant dengan printer paling sedikit, lalu tenant besar. Setiap gelombang minimal berjalan 3 hari kerja sebelum gelombang berikutnya dimulai.

## Memulai proyek

Proyek dimulai dari 2 repo baru dan Fase 0, bukan langsung MVP. Repo `opsifin-autoprint` tidak diubah, karena `.exe` lama adalah jalur rollback selama rollout.

| Repo | Isi | Setup lokal |
| --- | --- | --- |
| `opsifin-print-hub` | Laravel 13 + PHP 8.5, Filament 5, MySQL 9.7 LTS, Redis 8 + Horizon. Modul di `app/Domain/*`, kontrak di `contracts/agent-v1.yaml` | Docker Compose (app, worker Horizon, scheduler, MySQL, Redis); bisa dijalankan di Linux/WSL |
| `opsifin-print-agent` | .NET 10 Worker Service: `src/Agent.Service` (host + loop), `src/Agent.Printing` (winspool, PDFium, monitor spooler), `src/Agent.Hub` (klien API dari OpenAPI), `installer/` (WiX), `tests/` | Wajib Windows 10/11 (fisik atau VM) dengan minimal 1 printer nyata untuk uji cetak |

Kedua repo menyimpan `docs/PRD.md` (ekspor dokumen ini) dan `CLAUDE.md` yang merujuk ke PRD, supaya setiap sesi coding memakai konteks yang sama. Dokumen ini tetap menjadi tempat diskusi dan keputusan; ekspor diperbarui setiap Gate.

**Checklist Fase 0** (urut berdasarkan lama tunggu, yang paling lama dimulai duluan)

- [x] Sederhanakan release menjadi satu MSI melalui HTTPS + SHA-256; Authenticode publik bersifat opsional
- [ ] Jawab pertanyaan terbuka: jumlah tenant/printer, Hub di cloud atau on-premise, akses `Link` PDF
- [ ] Kumpulkan `Link` PDF semua tenant dan uji dari jaringan calon Hub (S5)
- [ ] Survei IT klien: hak admin, proxy, antivirus, cara instal massal (S10)
- [ ] Spike agent: Windows service yang membaca printer lewat winspool, mencetak lewat PDFium, dan memantau `JobId`; jalankan B1, B2, B3, B4 di 3 PC klien nyata, ditambah 1 printer Bluetooth A4 di kantor (S14)
- [ ] Ukur `getData` + render PDF di tenant terbesar pada interval 30 detik (B6)
- [ ] Kerangka Hub: Docker Compose, endpoint `/agent/v1` sesuai OpenAPI (boleh berisi data dummy), uji beban k6 (B5)
- [ ] Uji cutover app lama + Hub berdampingan di staging (B7)
- [ ] Gate 1: review hasil benchmark, putuskan mitigasi S1/S5, kalibrasi ulang durasi fase

## Definition of Done

DoD berlaku di tiga tingkat: setiap item kerja (story/PR), setiap modul fitur, dan setiap rilis. Sebuah item dianggap selesai hanya jika semua kotak di tingkatnya dan tingkat di bawahnya tercentang.

### DoD global (setiap story / PR)

- [ ] Acceptance criteria di tiket terpenuhi dan didemokan ke PO/QA
- [ ] Code review oleh minimal 1 engineer lain; tidak ada komentar blocking terbuka
- [ ] Lint, static analysis (PHPStan level 6+ / .NET analyzers + `dotnet format`), dan semua test CI hijau
- [ ] Unit test untuk logika baru; test lintas tenant untuk setiap endpoint atau query baru
- [ ] Migrasi DB bisa di-rollback dan sudah dijalankan di staging
- [ ] Tidak ada secret di kode, log, atau fixture
- [ ] Event / log / metrik yang relevan ditambahkan (perubahan state job wajib menulis `print_job_events`)
- [ ] Dokumentasi diperbarui: OpenAPI (bila API berubah), README, dan runbook (bila ada perilaku operasional baru)
- [ ] Di-deploy ke staging dan diverifikasi QA dengan skema testing

### DoD per modul fitur

| Modul | Selesai bila |
| --- | --- |
| Tenant & user | Tenant baru bisa dibuat dan user diundang end-to-end; 2FA wajib untuk admin aktif; test lintas tenant membuktikan tenant A tidak bisa membaca data B di semua endpoint, dan test arsitektur memastikan setiap model bisnis memakai BelongsToTenant |
| Source connection | Test connection memberi pesan jelas untuk 401, timeout, dan URL salah; credential tersimpan terenkripsi dan tidak pernah tampil ulang; quiet hours dihormati |
| Pairing & agent | MSI terpasang diam (`msiexec /qn PAIRING_CODE=...`) di Win10, Win11, dan Server 2019; agent muncul Online < 30 detik; revoke memutus agent < 60 detik; agent hidup lagi setelah reboot dan kill proses |
| Printer & binding | Printer terdeteksi otomatis; binding tersinkron ke `mst_customer_printer_name` (diverifikasi di DB opsifin-main); status PAPER\_OUT/OFFLINE muncul di Hub dan di layar `customer_printer` opsifin-main < 60 detik |
| Print job pipeline | Semua transisi state machine punya test; skenario chaos lolos: Hub mati saat printing, agent mati setelah spooled, jaringan putus saat callback, PDF rusak, printer kehabisan kertas; tidak ada cetak ganda dalam 1.000 job uji beban |
| Callback | Callback idempoten (kirim dua kali, efeknya sama); kegagalan opsifin-main di-retry dengan backoff dan terlihat di detail job; `Failed` terkirim untuk kegagalan permanen |
| Dashboard & laporan | Angka dashboard cocok dengan query langsung ke DB (rekonsiliasi harian di staging); filter bisa dibagikan via URL |
| Notifikasi | Setiap jenis alert dipicu di staging dan diterima di kanal yang diatur; ada dedup/throttle (maksimal 1 alert sama per 15 menit) |
| Update agent | Rollout canary 10% lalu 100% berhasil; rollback ke versi sebelumnya berhasil; paket dengan SHA-256 berbeda ditolak |

### DoD rilis

**MVP siap pilot**

- [ ] Semua FR berprioritas P0 selesai sesuai DoD modul
- [ ] NFR-01, 02, 05, 06, 07, 10, 11, 12 terverifikasi di staging
- [ ] Uji beban: 1.000 printer simulasi, 20 job/menit/tenant selama 1 jam, error rate < 0,1%
- [ ] Pen-test internal (OWASP ASVS L1) untuk Hub dan API agent; temuan high/critical ditutup
- [ ] Runbook: agent offline, printer error, callback macet, rotasi credential, restore DB
- [ ] Backup + restore PITR diuji sekali

**Pilot selesai (siap rollout)**

- [ ] 1 tenant pilot, minimal 3 site dan 5 printer, berjalan 2 minggu
- [ ] Rekonsiliasi harian: setiap invoice `print_status = Y` di opsifin-main punya job `printed` atau `skipped` di Hub, tanpa selisih
- [ ] 0 laporan cetak ganda atau `Success` palsu
- [ ] Operator pilot bisa menangani job gagal tanpa bantuan tim dev
- [ ] Agent lama di-uninstall dari semua PC pilot

**GA (semua tenant)**

- [ ] Semua tenant dimigrasi; inventaris `.exe` lama = 0
- [ ] Metrik keberhasilan di bagian Tujuan tercapai 30 hari berturut-turut
- [ ] Dokumentasi pengguna (Tenant Admin & Operator) dan materi training tersedia
- [ ] On-call dan SLA support disepakati

## Risiko dan pertanyaan terbuka

Risiko terbesar adalah cetak ganda atau terlewat selama masa transisi, saat agent lama dan Hub sama-sama memanggil `getData` untuk printer yang sama.

| Risiko | Dampak | Mitigasi |
| --- | --- | --- |
| Agent lama dan Hub aktif bersamaan untuk binding yang sama | Invoice tercetak dua kali | Cutover per printer: uninstall `.exe` lama dulu, baru aktifkan binding. Hub memperingatkan bila `LastConnected` di opsifin-main diperbarui oleh pihak lain |
| Spooler "selesai" belum tentu berarti kertas keluar | `printed` palsu saat printer macet setelah menerima data | Status printer dipantau 60 detik setelah spooled; error di jendela itu ditandai `printed_unverified` dan muncul di dashboard. Keterbatasan ini didokumentasikan |
| Hub crash setelah `getData` menandai `Process`, sebelum job tersimpan | Invoice tersangkut | `getData` tetap mengembalikan baris `Process`, jadi pull berikutnya mengambilnya lagi; dedupe mencegah duplikat |
| PC lokasi mati atau tidak ada di lokasi | Antrean menumpuk diam-diam | Alert agent offline > 3 menit + job tertua > 10 menit; printer cadangan (FR-21) di v2 |
| SmartScreen/antivirus memblokir agent | Instalasi pertama perlu konfirmasi atau whitelist | Panduan `More info → Run anyway`, publikasi SHA-256, dan whitelist hash oleh IT customer; Authenticode publik dapat ditambahkan saat skala membenarkan biayanya |
| Kebocoran data antar tenant | Insiden keamanan serius | Global scope + test arsitektur + static analysis + test lintas tenant, semuanya wajib lulus di CI |
| Lisensi SumatraPDF (GPLv3) | Risiko hukum bila salah dibundel | Didistribusikan sebagai executable terpisah tanpa modifikasi, dengan teks lisensi. Opsi PDFium (BSD) bila legal keberatan |
| Tim belum familiar dengan C#/.NET | Kecepatan pengembangan agent | Cakupan agent kecil (\~3 rb baris); template proyek + reviewer .NET berpengalaman |
| Perilaku `getData` berubah di opsifin-main | Adapter rusak | Test kontrak terhadap staging opsifin-main di CI Hub; Fase 3 memindahkan ke API v3 yang terversi |

Daftar stopper lengkap (S1-S13) dan benchmark Fase 0 beserta kriteria lulusnya ada di Ringkasan & Keputusan.

**Pertanyaan terbuka**

- [ ] Berapa jumlah nyata tenant, site, dan printer saat ini, serta proyeksi 2 tahun?
- [ ] Hub di-host di cloud milik Opsigo (satu Hub untuk semua) atau ada tenant yang mewajibkan on-premise?
- [ ] Apakah `Link` PDF dari opsifin-main bisa diakses publik dari internet, atau hanya dari jaringan tertentu?
- [ ] Apakah quiet hours 22.00 sampai 05.00 berlaku untuk semua tenant, atau perlu per tenant?
- [ ] Siapa Tenant Admin di sisi customer: tim IT customer atau tim support Opsigo?
- [ ] Kanal notifikasi yang dipakai tim: email, Telegram, Slack, atau WhatsApp?
- [ ] Retensi bukti cetak (snapshot PDF) yang dibutuhkan audit/finance: 90 hari cukup?
- [ ] Apakah mode `UseSharedFolder` dan `UseAutoPrintBackend` di opsifin-main masih dipakai tenant aktif, sehingga adapter harus mendukungnya sejak MVP?
- [ ] Agent: diputuskan .NET 10 (lihat tab Ringkasan & Keputusan), dikonfirmasi lewat benchmark B1-B4 di Fase 0
