> Ekspor dari https://claude.ai/artifact/Eg6irNPQwFVvrc2i1eB8zJ pada 2026-10-23. Dokumen itu sumber kebenaran; diagram hanya tampil di sana. Perbarui ekspor ini setiap Gate.

# Ringkasan dan Keputusan — Opsifin Print Hub

Oct 5, 2026 · @Aditya

Rekomendasinya tetap **Hub terpusat + agent ringan**, dengan satu koreksi dari PRD v0.1: agent dibangun dengan **.NET 10 (C#)**, bukan Go. Tab ini menjawab 8 pertanyaan review; detail spesifikasinya ada di tab *PRD detail*.

| # | Pertanyaan | Jawaban singkat |
| --- | --- | --- |
| 1 | Agent pakai Go atau .NET? | **.NET 10 LTS.** Akses Win32 printer resmi lewat CsWin32, cetak PDF vektor lewat PDFium ke GDI, service host dan WMI resmi Microsoft. Go bisa, tetapi library printernya tidak aktif sejak 2020 dan cetak vektor butuh cgo |
| 2 | Multi-tenant: agent dipasang di mana? | Tenant = satu perusahaan klien (satu opsifin-main). Agent dipasang di **setiap komputer yang punya akses ke printer**, sebanyak yang dibutuhkan. Untuk printer jaringan cukup 1 PC per lokasi; printer USB butuh agent di PC tempat printer itu dicolok |
| 3 | Cara kerja Hub, agent, opsifin-main | Hub menarik antrean dari opsifin-main, agent menarik job dari Hub lalu mencetak, Hub melapor balik ke opsifin-main. Agent tidak pernah berbicara dengan opsifin-main. Lihat diagram alur di bawah |
| 4 | Tepat untuk menggantikan app lama? | Ya. Masalah app lama ada di sisi operasional, bukan fungsi, jadi perilaku ke opsifin-main dipertahankan 1:1 sementara konfigurasi, logika, dan update dipindah ke server |
| 5 | Tidak over-engineering tapi skalabel? | Satu VM dengan Docker Compose (Laravel + MySQL + Redis) cukup untuk sekitar 1.000 printer. Setiap komponen punya jalur upgrade yang baru dipakai saat angkanya tercapai |
| 6 | Stopper | Ada 13 titik. Yang paling kritis: printer tidak terlihat dari Windows service, presisi cetak kertas kop (v2), cetak ganda saat cutover, dan Hub sebagai satu titik gagal |
| 7 | Riset & benchmark | Riset dokumentasi Microsoft, PDFium, dan SumatraPDF sudah dilakukan. Tujuh benchmark dengan kriteria lulus dijalankan di Fase 0, sebelum kode produksi ditulis |
| 8 | Lebih baik & mudah dikembangkan | Sebagian besar fitur baru cukup di-deploy di Hub (sekali untuk semua tenant). Pengaturan agent dikirim lewat remote config, dan perubahan kode agent dikirim lewat auto-update bertahap. Tidak ada lagi build ulang lalu salin `.exe` ke setiap PC |

## Alur proses end-to-end

Satu invoice berpindah antara empat pihak dalam 11 langkah. Agent hanya berbicara dengan Hub dan spooler di PC-nya; semua komunikasi dengan opsifin-main dilakukan Hub.

&#91;embedded content: alur proses end-to-end · 4 lane, 11 langkah + heartbeat\]

Invoice dengan `IsHardCopyInv = N` berhenti di langkah 3 dan langsung lompat ke langkah 10-11 tanpa dicetak. Penjelasan setiap langkah ada di bagian 3.

## 1. Agent: .NET atau Go?

Pilih **.NET 10 LTS** (didukung sampai 10 November 2028). Untuk program yang kerjanya hampir seluruhnya bicara dengan spooler dan driver printer Windows, tooling resmi Microsoft lebih berharga daripada biner kecil dari Go.

**Fakta dari riset yang menentukan desain**

- Microsoft menyatakan `System.Drawing.Printing` dan `System.Printing` (API printer "bawaan" .NET) **tidak didukung di Windows service**. Yang didukung adalah **Win32 GDI + winspool**, yaitu `CreateDC`/`StartDoc` dan `OpenPrinter`/`EnumJobs`. Artinya, .NET maupun Go sama-sama memanggil Win32 secara langsung; keunggulan .NET ada di kualitas tooling untuk memanggilnya.
- Akses printer dari service bergantung pada **akun service**. Printer jaringan yang dipasang per user tidak terlihat oleh LocalSystem, kecuali dipasang per mesin (`printui /ga`) atau service dijalankan dengan akun user tersebut.
- PDFium punya `FPDF_RenderPage` yang menggambar langsung ke HDC printer (cetak **vektor**, tajam, ukuran spool kecil). Di .NET cukup dua deklarasi `LibraryImport`; di Go (go-pdfium) fitur ini hanya ada di mode **cgo single-thread**, sehingga cross-compile dari Linux tidak lagi mudah.

| Kriteria | .NET 10 (C#) | Go 1.2x | Unggul |
| --- | --- | --- | --- |
| Akses spooler (OpenPrinter, EnumJobs, GetJob, notifikasi perubahan) | CsWin32 (generator resmi Microsoft) membuat binding bertipe + SafeHandle otomatis | `x/sys/windows` + syscall tulis tangan; `alexbrainman/printer` rilis terakhir September 2020 | .NET |
| Cetak PDF dari service (vektor ke GDI) | PDFium via `LibraryImport`, AOT-compatible; contoh nyata: Print-It (Windows service .NET + PDFium) | go-pdfium `FPDF_RenderPage` butuh cgo + MinGW | .NET |
| Status printer (paper out, jam, offline) | `System.Management` (WMI `Win32_Printer.DetectedErrorState`) resmi + `PRINTER_INFO_2.Status` | Library WMI komunitas (go-ole) + syscall | .NET |
| Windows service host, Event Log, auto-restart | `Microsoft.Extensions.Hosting.WindowsServices` resmi | `x/sys/windows/svc` atau kardianos/service | Seri |
| Simpan secret (DPAPI), proxy kantor | `ProtectedData` resmi; `HttpClient` otomatis memakai proxy sistem Windows | Syscall `CryptProtectData`; proxy harus diatur manual | .NET |
| Ukuran dan memori | Native AOT: sekitar 15-25 MB, working set sekitar 18-30 MB; self-contained biasa sekitar 70 MB | Sekitar 10-15 MB, memori sekitar 10-20 MB | Go (tidak signifikan untuk PC kantor) |
| Siklus dukungan | LTS 3 tahun, rilis November tiap tahun genap | Tiap versi didukung sampai dua rilis berikutnya (\~1 tahun) | .NET |
| Kecocokan dengan tim PHP/Laravel | DI, OOP, async mirip pola Laravel; IDE gratis (VS/Rider) | Bahasa sederhana, tetapi idiom berbeda | Sedikit .NET |

**Keputusan desain agent**

- .NET 10 Worker Service, dipublikasikan sebagai Native AOT (fallback: self-contained single-file bila ada library yang belum AOT).
- **Mesin cetak**: PDFium ke GDI (default) dan SumatraPDF CLI sebagai mesin cadangan. Mesin bisa dipilih per printer dari Hub, untuk driver yang bermasalah dengan salah satunya.
- **Mode jalan**: `service` (default, berjalan meski tidak ada user login) atau `user` (berjalan di sesi user seperti app lama). Mode `user` hanya untuk PC yang printernya terpasang per user dan tidak boleh diubah IT klien.
- Agent melaporkan **printer yang terlihat dari akun service**, sehingga printer yang "hilang" langsung tampak di Hub saat instalasi.

## 2. Konsep multi-tenant dan penempatan agent

Multi-tenant di sini berarti **satu Hub melayani banyak perusahaan klien sekaligus**, masing-masing dengan opsifin-main sendiri, dan datanya terpisah penuh. Agent tidak terkait dengan tenant secara langsung: agent dipasang di **komputer mana pun yang bisa mengakses printer**, sebanyak yang dibutuhkan.

&#91;embedded content: topologi multi-tenant · tenant, site, PC, printer\]

| Istilah | Artinya | Contoh |
| --- | --- | --- |
| Tenant | Satu perusahaan klien Opsifin = satu instance opsifin-main | PT Klien A |
| Site | Lokasi fisik milik tenant | Kantor Jakarta, cabang Surabaya |
| Agent | Satu instalasi service di satu PC; melayani semua printer yang terlihat dari PC itu | PC-FIN-01 |
| Printer | Printer Windows yang dilaporkan agent dan diaktifkan admin | Printer kop lantai 2 |
| Binding | Hubungan printer dengan antrean di opsifin-main (nama + identifier + versi 1/2/3). Satu printer boleh punya beberapa binding | Printer A melayani versi 1 dan 3 |

**Jadi, dipasang di mana?**

- **Printer jaringan (LAN/Wi-Fi):** cukup **1 agent per site**, di PC yang selalu menyala (PC admin atau mini PC). Satu agent bisa melayani puluhan printer jaringan.
- **Printer USB:** printer hanya terlihat dari PC tempat ia dicolok, jadi **agent dipasang di PC itu**. Lima kasir dengan printer USB berarti lima agent.
- **Campuran:** keduanya bisa ada di site yang sama. Hub menampilkan semuanya di satu daftar per site.
- **PC user biasa tanpa printer tidak perlu agent.** Agent bukan aplikasi per user.
- **Satu printer, satu pemilik.** Bila dua agent sama-sama melihat printer jaringan yang sama, admin memilih satu sebagai pemilik. Agent kedua bisa dijadikan cadangan otomatis (FR-21, P2).

Dibandingkan app lama (1 instalasi per printer), contoh di gambar turun dari 7 instalasi menjadi 4 agent. Untuk site dengan printer jaringan, turunnya lebih besar lagi: 1 agent untuk semua printernya.

## 3. Cara kerja Agent, Hub, dan opsifin-main

Pembagian perannya sederhana:

- **opsifin-main** tetap memutuskan apa yang perlu dicetak.
- **Hub** memutuskan printer mana yang mencetak, kapan, dan mencatat semuanya.
- **Agent** hanya mengerjakan pekerjaan fisik di PC.

Nomor langkah di bawah sama dengan nomor di diagram alur.

| Komponen | Tanggung jawab | Tidak boleh dilakukan |
| --- | --- | --- |
| opsifin-main (per klien) | Menentukan invoice siap cetak (`log_print_inv`), merender PDF lewat `Link`, menyimpan hasil cetak ke `trx_*_invoice` | Tidak perlu diubah sampai Fase 4 |
| Hub (Opsigo, satu untuk semua) | Menyimpan credential dan konfigurasi, menarik antrean, validasi PDF, mengatur antrean per printer, retry, callback, dashboard, audit | Tidak menyentuh printer secara langsung |
| Agent (PC klien) | Melaporkan printer dan statusnya, mengklaim job, mencetak, memantau spooler, mengirim event | Tidak menyimpan credential Opsifin, tidak memanggil opsifin-main, tidak punya logika bisnis |

**A. Sekali di awal (setup)**

1. Platform Admin membuat tenant dan mengisi URL + credential REST opsifin-main klien.
2. Tenant Admin membuat site dan pairing code, lalu memasang MSI agent di PC lokasi.
3. Agent melakukan pairing, menerima device token, lalu mengirim daftar printer yang terlihat.
4. Admin mengaktifkan printer dan membuat binding (nama printer + identifier + versi 1/2/3). Hub memanggil `/printerName` sehingga `mst_customer_printer_name` di opsifin-main terisi seperti biasa.

**B. Siklus cetak (setiap invoice)**

1. **Pull** — setiap 30 detik per binding, Hub memanggil `POST /getData` dengan payload yang sama dengan app lama.
2. **getData** — opsifin-main mengembalikan maksimal 50 invoice dan menandainya `Process`.
3. **Simpan job** — Hub menyimpan satu job per invoice dengan kunci unik (tenant, InvNo, ItemType, versi), sehingga data ganda diabaikan. Bila `IsHardCopyInv = N`, job langsung `skipped` dan masuk langkah 10.
4. **Render PDF** — Hub mengunduh `Link`; opsifin-main merender PDF seperti sekarang.
5. **Validasi** — Hub memastikan PDF bisa dibuka, menghitung SHA-256 dan jumlah halaman, menyimpan snapshot, lalu mengubah job menjadi `queued`.
6. **Klaim** — agent bertanya ke Hub setiap 5 detik. Hub memberi job tertua per printer dengan lease 120 detik, satu job per printer sekaligus agar urutan terjaga.
7. **Unduh** — agent mengunduh PDF dari signed URL Hub (berlaku 5 menit) dan mencocokkan SHA-256-nya.
8. **Cetak** — agent merender PDF ke printer lewat PDFium + GDI. Spooler Windows memberi `JobId`.
9. **Pantau** — agent memantau `JobId` di spooler dan mengirim setiap perubahan (spooled, error kertas habis, selesai) sebagai event ke Hub. Bila Hub tidak terjangkau, event disimpan dulu di outbox lokal.
10. **Printed** — setelah spooler selesai tanpa error, Hub menandai job `printed` dan memasukkannya ke callback outbox.
11. **Callback** — Hub memanggil `POST /` opsifin-main dengan `Success` (atau `Failed` bila attempt habis). opsifin-main mengisi `print_status = Y` seperti dulu. Bila gagal, Hub mengulang dengan backoff sampai 24 jam.

**C. Paralel setiap 30 detik (heartbeat)**

Agent mengirim heartbeat berisi versi, status setiap printer, dan isi antrean spooler. Hub menyimpan perubahan status, memicu alert bila perlu, dan meneruskan status ke `POST /PrinterStatus`, sehingga layar *Customer Printer* di opsifin-main tetap akurat. Balasan heartbeat membawa **remote config** (interval, mesin cetak, pengaturan tray/duplex) dan perintah (test page, kirim log, update versi).

**D. Saat terjadi masalah**

| Kejadian | Yang terjadi otomatis | Yang dilihat user |
| --- | --- | --- |
| Kertas habis di tengah cetak | Spooler melaporkan `PAPEROUT`; job tetap `printing` dan menunggu | Kartu printer merah "Kertas habis" + notifikasi setelah 5 menit |
| PC dimatikan saat job `claimed` | Lease habis setelah 120 detik, job kembali `queued` | Agent *Offline* dan job menunggu |
| opsifin-main down saat callback | Outbox retry 30 detik, 2 menit, 10 menit, dan seterusnya sampai 24 jam | Job `printed`, callback "tertunda" |
| PDF rusak | Job `failed` (`PDF_INVALID`), callback `Failed` | Job gagal dengan alasan yang jelas, bisa dicetak ulang setelah diperbaiki di opsifin-main |
| Hub down | Agent menyelesaikan job yang sudah diklaim; job baru menunggu Hub hidup lagi | Status di opsifin-main tidak bergerak; alert ke tim Opsigo |

## 4. Apakah tepat untuk menggantikan aplikasi lama?

Ya, dengan satu prinsip: **fungsinya dipertahankan, operasionalnya yang diganti.** App lama sudah benar secara fungsi (endpoint, payload, status). Yang mahal adalah merawatnya, jadi kontrak ke opsifin-main dipertahankan 1:1, dan yang dipindah adalah konfigurasi, logika, dan cara update.

| Aspek | App lama (opsifin-autoprint) | Print Hub + Agent |
| --- | --- | --- |
| Instalasi | 1 `.exe` + `data.db` per printer, diatur per PC | 1 agent per PC untuk semua printernya; konfigurasi di Hub |
| Credential REST | Di `data.db` setiap PC (kunci Fernet di sebelahnya) | Hanya di Hub, terenkripsi; agent memegang device token yang bisa dicabut |
| Menambah fitur | Ubah Python, build PyInstaller, salin `.exe` ke setiap PC | Kebanyakan cukup deploy Hub sekali; perubahan agent lewat auto-update bertahap |
| Ubah pengaturan (printer, versi) | Buka app settings di PC itu (password `opsifin1`) | Dari dashboard, lengkap dengan audit |
| Visibilitas | Log file di setiap PC | Timeline per invoice, dashboard semua tenant |
| Konfirmasi tercetak | Hanya cek job pertama di antrean (bisa `Success` palsu) | Pantau `JobId` spesifik + jendela verifikasi |
| Kegagalan | Retry tanpa batas; PDF rusak berulang diam-diam | Maksimal 3 attempt, lalu `Failed` + notifikasi + cetak ulang |
| Ketergantungan PC | Python runtime via PyInstaller, VC++ Redist, aplikasi PDF default | Satu service .NET AOT + PDFium yang dibundel |

**Opsi yang dipertimbangkan**

| Opsi | Biaya | Memenuhi tujuan? | Catatan |
| --- | --- | --- | --- |
| A. Rapikan app Python (multi-printer + auto-update) | Rendah | Sebagian | Credential dan konfigurasi tetap di PC; tidak ada pandangan terpusat |
| B. Agent baru, konfigurasi di opsifin-main tiap klien | Sedang | Sebagian | Terpusat per klien saja; fitur baru harus masuk ke codebase CI3 lama di setiap instance |
| **C. Hub + Agent (direkomendasikan)** | Paling tinggi di awal | Ya | Satu tempat untuk semua klien; opsifin-main tidak berubah sampai Fase 4 |

Biaya awal opsi C bisa ditekan dengan membangunnya bertahap. MVP Hub hanya berisi fitur P0 (tanpa laporan dan notifikasi lanjutan), sehingga investasi awalnya mendekati opsi B, tetapi arahnya sudah benar untuk jangka panjang.

## 5. Skalabel tanpa over-engineering

Aturannya: mulai dari bentuk paling sederhana yang lolos benchmark, lalu naik kelas hanya saat ambang tertentu tercapai. Dengan asumsi 50 tenant, 300 agent, 1.000 printer, dan 20.000 job per hari, beban Hub tergolong kecil.

**Hitungan beban** (asumsi, diverifikasi di benchmark B5 dan B6)

| Sumber beban | Rumus | Hasil |
| --- | --- | --- |
| Heartbeat agent | 300 agent / 30 detik | 10 req/detik |
| Klaim job (adaptif: 5 detik saat ada antrean, 15 detik saat sepi) | 300 / 5 detik (puncak) | 20-60 req/detik |
| Event job | 20.000 job × \~12 event / 86.400 detik | \~3 insert/detik (\~7 juta baris/bulan) |
| Pull ke opsifin-main | 1.200 binding / 30 detik, tersebar di 50 tenant | \~0,8 req/detik per tenant (app lama: 0,4 req/detik per tenant di interval 60 detik) |

Puncak sekitar 70 req/detik ke endpoint ringan masih jauh di bawah kapasitas satu server PHP-FPM 2-4 vCPU. Batas yang lebih dulu tercapai justru di **opsifin-main**: `getData` berat (`memory_limit 1024M`) dan PDF dirender saat `Link` dibuka. Karena itu interval pull default 30 detik, dibatasi maksimal 2 request paralel per tenant, dan diberi jitter.

**Mulai dari sini, naik kelas bila perlu**

| Komponen | MVP (hari pertama) | Ambang naik kelas | Naik ke |
| --- | --- | --- | --- |
| Hosting Hub | 1 VM, Docker Compose: app, worker, scheduler, MySQL, Redis | CPU > 60% selama 1 jam di jam sibuk | 2+ container app di belakang load balancer |
| Database | MySQL di VM yang sama + backup harian + binlog | > 2.000 printer atau RPO < 15 menit wajib | MySQL managed (HA + PITR) |
| Penyimpanan PDF | Volume disk lokal | Hub lebih dari 1 instance | Object storage S3-compatible |
| Pengiriman job | Poll klaim adaptif | Latensi p95 > 60 detik atau > 100 req/detik dari poll | Reverb WebSocket sebagai sinyal "ada job" |
| Tabel event | Satu tabel dengan index | > 50 juta baris | Partisi bulanan (DDL sudah disiapkan) |
| Laporan | Query langsung | Query laporan > 2 detik | Tabel ringkasan harian / read replica |
| Sumber antrean | Adapter `getData` per binding | Beban opsifin-main naik > 10% | API v3 per tenant (Fase 4) |

**Yang sengaja tidak dibuat:** microservices, Kafka/RabbitMQ, Kubernetes, event sourcing penuh, DB per tenant, dan aplikasi frontend SPA terpisah. Semuanya menambah biaya operasional tanpa menyelesaikan masalah yang ada sekarang.

## 6. Stopper dan pin point

Empat stopper kritis harus terbukti aman di **Fase 0 (spike 2 minggu)** sebelum MVP dimulai. Bila salah satunya gagal, desain agent atau rencana migrasi diubah dulu, bukan ditambal belakangan.

| # | Stopper | Dampak | Kenapa berbahaya | Dicek kapan / bagaimana | Mitigasi |
| --- | --- | --- | --- | --- | --- |
| S1 | Printer tidak terlihat dari Windows service | Kritis | App lama berjalan di sesi user, jadi printer jaringan yang dipasang per user terlihat. Service (LocalSystem) tidak melihatnya | Fase 0, benchmark B2 di 3 PC klien nyata | Pasang printer per mesin (`printui /ga` atau port TCP/IP lokal); service berjalan dengan akun khusus; atau agent mode `user` |
| S2 | Presisi cetak kertas kop (versi 2) | Kritis | Kertas kop sudah tercetak; selisih skala atau margin beberapa mm membuat invoice tidak terpakai | Fase 0, benchmark B1: overlay hasil cetak lama vs baru | Mode skala `noscale` per printer; profil cetak per printer; SumatraPDF sebagai mesin cadangan |
| S3 | Cetak ganda saat cutover | Kritis | `getData` dipanggil dua pihak untuk printer yang sama; item `Process` diambil ulang | Uji di staging dengan dua klien paralel | Runbook cutover per printer; dedupe di Hub; Hub memberi peringatan bila `LastConnected` diperbarui pihak lain |
| S4 | Hub menjadi satu titik gagal untuk semua klien | Kritis | Sekarang tiap PC mandiri; nanti Hub down = semua klien berhenti mencetak job baru | Desain + uji restore (DoD MVP) | Monitoring uptime, backup + restore teruji, RTO 4 jam; `.exe` lama disimpan sebagai fallback dingin selama rollout |
| S5 | Hub tidak bisa menjangkau opsifin-main atau `Link` | Tinggi | `Link` dibentuk dari `base_url` (`http://` + `HTTP_HOST`), sehingga host-nya bisa host internal | Fase 0: daftar URL semua tenant + uji dari jaringan Hub | Whitelist IP Hub; mode unduh per tenant: `hub` (default) atau `agent` (agent unduh langsung seperti sekarang) |
| S6 | Beban `getData` dan render PDF di opsifin-main | Tinggi | Query berat dengan `memory_limit 1024M`; PDF dirender saat diminta | Benchmark B6 di tenant terbesar | Interval 30 detik, maksimal 2 paralel per tenant, jitter; API v3 di Fase 4 |
| S7 | Versi `opsifin_api_print` berbeda antar tenant | Tinggi | Tiap klien punya instance sendiri; ada yang belum di-update | Saat onboarding tenant | Test kontrak per tenant; adapter mendeteksi versi; daftar versi minimum |
| S8 | Driver printer memunculkan dialog di session 0 | Tinggi | Proses cetak menggantung tanpa terlihat | Benchmark B1/B2 | Timeout per job + kill proses; mesin cadangan; daftar driver bermasalah |
| S9 | SmartScreen dan antivirus klien | Sedang | Installer tanpa publisher publik memerlukan konfirmasi atau whitelist saat instalasi pertama | Sediakan panduan onboarding dan SHA-256 sejak pilot | `More info → Run anyway` atau whitelist hash oleh IT klien; Authenticode publik opsional bila skala membenarkan biaya |
| S10 | Hak admin dan proxy di PC klien | Sedang | Instalasi service butuh admin; banyak kantor memakai proxy | Survei IT klien di Fase 0 | MSI diam lewat RMM/GPO; `HttpClient` memakai proxy sistem + konfigurasi proxy manual |
| S11 | Spooler "selesai" belum tentu kertas keluar | Sedang | Status `printed` bisa palsu bila printer macet setelah menerima data | Benchmark B3 | Jendela verifikasi 60 detik, event `printed_unverified`, didokumentasikan sebagai batasan |
| S12 | Data invoice klien tersimpan terpusat | Sedang | Isu kontrak dan privasi dengan klien | Review legal sebelum pilot | Retensi 90 hari per tenant; opsi simpan hash saja; enkripsi storage |
| S13 | Keahlian C#/.NET di tim | Sedang | Tim utama PHP | Fase 0 | Agent kecil (\~3.000 baris), template proyek, code review oleh engineer .NET berpengalaman (internal atau kontrak) |
| S14 | Printer Bluetooth untuk invoice A4 | Sedang | Koneksi Bluetooth bisa tidur atau putus, dan status balik (kertas habis, offline) sering tidak sampai ke spooler. Job bisa menggantung atau tercatat printed padahal kertas tidak keluar | Fase 0, benchmark B1, B2, B3 dengan printer Bluetooth A4 di kantor | Hanya printer yang terpasang dengan driver Windows (bukan sekadar virtual COM port); status ditandai best-effort + timeout per job; sarankan Wi-Fi atau USB bila printer mendukung |

## 7. Riset dan rencana benchmark

Riset dokumen sudah menjawab pilihan teknologi. Yang belum bisa dijawab dari dokumen adalah perilaku di **PC, printer, dan driver klien yang nyata**, jadi tujuh benchmark berikut dijalankan di Fase 0. Hasilnya menentukan Gate 1.

**Hasil riset yang sudah dipakai di desain**

| Temuan | Dampak ke desain | Sumber |
| --- | --- | --- |
| `System.Drawing.Printing` dan `System.Printing` tidak didukung di Windows service; Win32 GDI didukung, dengan catatan akun service | Agent memakai GDI + winspool via CsWin32, bukan API printer .NET tingkat tinggi | [Microsoft: System.Drawing.Printing](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.printing?view=windowsdesktop-8.0), [Microsoft: Printing from a Windows Service](https://learn.microsoft.com/en-us/archive/blogs/dsui_team/printing-from-a-windows-service) |
| `printui /ga` membuat koneksi printer per mesin | Solusi utama S1 | [Microsoft: rundll32 printui](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/rundll32-printui) |
| PDFium `FPDF_RenderPage` bisa menggambar ke HDC printer (Windows) | Cetak vektor tanpa aplikasi PDF eksternal | [dotnet-devices #26](https://github.com/adaptive-architecture/dotnet-devices/issues/26), [go-pdfium PR #370](https://github.com/klippa-app/go-pdfium/pull/370) |
| Ada Windows service .NET + PDFium yang sudah dipakai untuk cetak PDF di background | Pola terbukti, bukan eksperimen | [Print-It](https://github.com/huysentruitw/print-it) |
| SumatraPDF CLI punya `-print-to`, `-print-settings`, `-silent`, dan exit code per jenis error | Mesin cadangan dengan error yang bisa dipetakan | [SumatraPDF command line](https://www.sumatrapdfreader.org/docs/Command-line-arguments) |
| `Win32_Printer.DetectedErrorState` memberi paper out, jam, offline, tetapi akurasinya tergantung driver | Status digabung dari spooler + WMI; diuji per model printer (B3) | [Microsoft: Win32\_Printer](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-printer) |
| .NET 10 LTS didukung sampai November 2028 | Versi target agent; upgrade ke LTS berikutnya terjadwal | [.NET Blog: Announcing .NET 10](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/) |

**Benchmark Fase 0**

| ID | Yang diuji | Cara | Lulus bila |
| --- | --- | --- | --- |
| B1 | Kualitas cetak | 3 model printer klien × 3 versi (normal, kop, billing) × 10 invoice; bandingkan PDFium-GDI, SumatraPDF, dan app lama dengan overlay | Posisi teks selisih ≤ 1 mm vs app lama; 0 dialog muncul |
| B2 | Printer terlihat dari service | Win10, Win11, Server 2019; akun LocalSystem vs akun service vs mode user; printer lokal (USB dan Bluetooth), jaringan per user, dan per mesin | Semua printer produksi terlihat di minimal satu konfigurasi yang bisa distandarkan |
| B3 | Akurasi status | 20 kali cabut kertas, matikan printer, buka pintu di tengah job; untuk printer Bluetooth juga putuskan koneksi (matikan Bluetooth atau keluar jangkauan) di tengah job | 100% terdeteksi sebagai error, tidak ada `printed` palsu |
| B4 | Jejak agent | Jalan 24 jam idle + 500 job; Native AOT vs self-contained | RAM < 60 MB, CPU idle < 1%, tidak ada kebocoran handle |
| B5 | Kapasitas Hub | k6 mensimulasikan 1.000 agent (heartbeat 30 detik, klaim 5 detik) + 20 job/menit/tenant di VM 2 vCPU / 4 GB | p95 API < 200 ms, error < 0,1% |
| B6 | Beban opsifin-main | Ukur `getData` + render `Link` di tenant terbesar pada interval 30 detik | p95 `getData` < 2 detik, CPU opsifin-main naik < 10% |
| B7 | Cutover | Staging: app lama dan Hub hidup bersamaan, lalu jalankan runbook | 0 invoice tercetak ganda, 0 invoice terlewat |

## 8. Lebih baik dan mudah dikembangkan

Kuncinya adalah **agent dibuat "bodoh" dan stabil, Hub dibuat pintar.** Semakin sedikit logika di agent, semakin jarang agent perlu di-build ulang.

**Di mana sebuah perubahan dikerjakan**

| Contoh kebutuhan baru | Dikerjakan di | Perlu build agent? |
| --- | --- | --- |
| Aturan routing baru (customer X ke printer Y) | Hub, modul `Jobs` | Tidak |
| Jenis dokumen baru dari opsifin-main (mis. e-invoice) | Hub, adapter baru di modul `Sources` | Tidak |
| Jadwal cetak, quiet hours, prioritas | Hub (konfigurasi tenant) | Tidak |
| Notifikasi ke kanal baru (WhatsApp, Teams) | Hub, modul `Notifications` | Tidak |
| Laporan atau dashboard baru | Hub, modul `Reporting` | Tidak |
| Pengaturan cetak (tray, duplex, copies, fit/noscale, mesin cetak) | Hub mengirim lewat remote config di balasan heartbeat | Tidak |
| Interval, level log, feature flag agent | Remote config | Tidak |
| Kemampuan baru di PC (mis. scan, label printer ZPL) | Agent: modul baru + capability flag | Ya, dikirim lewat auto-update bertahap |

**Mekanisme yang membuat ini aman**

- **Remote config versi.** Setiap balasan heartbeat membawa `config_version`; agent menerapkan config baru tanpa restart dan melaporkan bila gagal.
- **Capability flags.** Agent melaporkan kemampuannya (`engines: [pdfium, sumatra]`, `agent_version`). Hub hanya mengirim fitur yang didukung agent itu, sehingga agent lama dan baru bisa hidup bersamaan.
- **Kontrak terversi.** `/agent/v1` diberi spesifikasi OpenAPI; Hub mendukung versi N dan N-1 agent.
- **Auto-update bertahap.** CI membangun dan menandatangani paket. Hub merilisnya ke 10% agent (canary), lalu 100% bila error rate tidak naik dalam 24 jam. Rollback cukup satu klik.
- **Adapter sumber sebagai interface.** `SourceAdapter` punya method `fetch()`, `document()`, `report()`. Adapter v2 (getData lama) dan v3 hidup berdampingan, dan tenant dipindah satu per satu.
- **Modul Hub dipisah per domain** (`app/Domain/*`) dengan test di setiap modul, sehingga engineer baru bisa mengubah satu modul tanpa memahami seluruh sistem.

**Hasilnya dibandingkan app lama:** perubahan konfigurasi butuh 0 kunjungan ke PC; sebagian besar fitur cukup 1 deploy server; perubahan agent butuh 1 rilis yang menyebar sendiri. Sebelumnya, setiap perubahan berarti build ulang lalu menyalin `.exe` ke N PC.

## Sumber

Diakses 5 Oktober 2026.

- [Microsoft Learn: System.Drawing.Printing namespace (Caution untuk Windows service)](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.printing?view=windowsdesktop-8.0)
- [Microsoft Learn (arsip): Printing from a Windows Service](https://learn.microsoft.com/en-us/archive/blogs/dsui_team/printing-from-a-windows-service)
- [Microsoft Learn: rundll32 printui.dll,PrintUIEntry](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/rundll32-printui)
- [Microsoft Learn: Win32\_Printer class](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-printer)
- [Microsoft: CsWin32 source generator](https://github.com/microsoft/CsWin32)
- [.NET Blog: Announcing .NET 10](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/)
- [Print-It: Windows service .NET + PDFium](https://github.com/huysentruitw/print-it)
- [adaptive-architecture/dotnet-devices #26: cetak PDF vektor via PDFium ke spooler](https://github.com/adaptive-architecture/dotnet-devices/issues/26)
- [klippa-app/go-pdfium PR #370: FPDF\_RenderPage dengan HDC (cgo)](https://github.com/klippa-app/go-pdfium/pull/370)
- [alexbrainman/printer (rilis terakhir September 2020)](https://pkg.go.dev/github.com/alexbrainman/printer)
- [SumatraPDF: Command-line arguments](https://www.sumatrapdfreader.org/docs/Command-line-arguments)
- Kode `opsifin-main`: `opsifin_api_print` (GetData, PrinterStatus, PrinterName, root callback), `ValidateServices.php` (pembentukan `Link`), `config/config.php` (`base_url` dari `HTTP_HOST`)
