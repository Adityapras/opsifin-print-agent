# Opsifin Print Agent — Developer Build & Release Guide

Panduan ini menjelaskan cara menyiapkan komputer developer, menjalankan aplikasi dari source,
membuat installer MSI, dan merilis update melalui Opsifin Print Hub.

## Ringkasan alur

```text
Developer branch
    → coding dan automated test
    → pull request
    → CI build dan test
    → merge ke master
    → build/download MSI
    → upload ke Print Hub
    → schedule update
    → verifikasi heartbeat versi baru
```

Semua developer dapat melakukan build dari PC Windows masing-masing. Proses ini tidak membutuhkan
certificate, private key, PFX, atau signing workstation khusus.

## 1. Prasyarat komputer developer

Gunakan komputer Windows x64 dan install:

- Git;
- .NET 10 SDK;
- Visual Studio Build Tools;
- workload **Desktop development with C++**;
- Windows SDK;
- PowerShell.

Versi SDK repository dipin di `global.json`. Saat panduan ini ditulis, versi yang digunakan adalah
`.NET SDK 10.0.402` dengan `rollForward: latestFeature`.

Verifikasi instalasi:

```powershell
git --version
dotnet --version
dotnet --info
```

Publish Native AOT harus dilakukan di Windows karena membutuhkan toolchain C++ dan Windows SDK.

## 2. Clone repository

Menggunakan SSH:

```powershell
git clone git@github.com:Adityapras/opsifin-print-agent.git
cd opsifin-print-agent
git checkout master
```

Menggunakan HTTPS:

```powershell
git clone https://github.com/Adityapras/opsifin-print-agent.git
cd opsifin-print-agent
git checkout master
```

## 3. Memulai pekerjaan baru

Jangan membuat perubahan langsung pada `master`. Tarik source terbaru dan buat branch:

```powershell
git checkout master
git pull --ff-only
git checkout -b fix/nama-perubahan
```

Gunakan prefix branch yang sesuai:

- `feat/` untuk fitur;
- `fix/` untuk bug;
- `refactor/` untuk perubahan struktur tanpa perubahan perilaku;
- `docs/` untuk dokumentasi;
- `chore/` untuk build, dependency, atau tooling.

## 4. Build dan test selama development

Jalankan pemeriksaan dasar:

```powershell
dotnet build
dotnet test
```

Semua warning diperlakukan sebagai error. Jangan lanjut membuat release apabila salah satu perintah
gagal.

Menjalankan Agent Control dari source:

```powershell
dotnet run --project src/Agent.Desktop
```

Menjalankan service sebagai aplikasi console dalam simulation mode:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src/Agent.Service
```

Simulation mode menjalankan pipeline tanpa mengirim dokumen ke printer fisik. Jangan menjalankan
source development yang terhubung ke tenant production tanpa koordinasi.

## 5. Menentukan versi release

Versi produk berada di `Directory.Build.props`:

```xml
<Version>1.0.4</Version>
```

Aturan versioning:

| Jenis perubahan | Contoh |
| --- | --- |
| Bug fix atau perubahan kecil | `1.0.3 → 1.0.4` |
| Fitur baru yang kompatibel | `1.0.4 → 1.1.0` |
| Perubahan tidak kompatibel | `1.1.0 → 2.0.0` |

Setiap release wajib memiliki versi lebih tinggi daripada Agent yang terpasang. Jangan menggunakan
ulang versi MSI yang pernah di-upload atau dipublish di Hub.

Untuk release resmi, ubah versi di `Directory.Build.props` dan commit perubahan tersebut. Parameter
`-Version` pada script hanya cocok untuk build sementara karena tidak mengubah source version.

## 6. Membuat installer MSI

Dari root repository, jalankan:

```powershell
.\tools\build-installer.ps1
```

Script akan:

1. menjalankan seluruh automated test;
2. publish Windows Service Native AOT;
3. publish Agent Control;
4. publish updater;
5. build installer WiX;
6. menyalin satu MSI ke folder `dist`;
7. menghitung SHA-256 MSI.

Hasil build:

```text
dist\OpsifinPrintAgent-Setup-<version>-win-x64.msi
```

Contoh output sukses:

```text
Build succeeded.
0 Warning(s)
0 Error(s)

Installer is ready:
D:\Dev\opsifin-print-agent\dist\OpsifinPrintAgent-Setup-1.0.4-win-x64.msi
SHA-256: <hash>
```

Jika PowerShell memblokir script lokal:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\tools\build-installer.ps1
```

Perubahan execution policy tersebut hanya berlaku untuk proses PowerShell yang sedang dibuka.

Build sementara dengan version override:

```powershell
.\tools\build-installer.ps1 -Version 1.0.4
```

Jangan memakai `-SkipTests` untuk release lokal. Opsi tersebut disediakan untuk CI karena CI sudah
menjalankan test pada langkah terpisah.

## 7. Memverifikasi artefak

Hitung ulang SHA-256 bila diperlukan:

```powershell
Get-FileHash `
  .\dist\OpsifinPrintAgent-Setup-1.0.4-win-x64.msi `
  -Algorithm SHA256
```

Pastikan:

- nama MSI membawa versi yang benar;
- build selesai tanpa warning dan error;
- seluruh test lulus;
- SHA-256 sama dengan nilai yang ditampilkan script;
- installer dapat dibuka pada Windows test machine.

## 8. File yang masuk dan tidak masuk Git

Pertahankan di repository:

- source di `src/`;
- automated test di `tests/`;
- source installer di `installer/`;
- script developer di `tools/`;
- asset branding di `assets/`;
- kontrak API dan dokumentasi;
- workflow GitHub Actions.

Jangan commit:

- `bin/`, `obj/`, `artifacts/`, dan `dist/`;
- file MSI dan cabinet;
- log dan database runtime;
- diagnostic ZIP;
- `.env` atau konfigurasi developer lokal;
- certificate, PFX, PEM, dan private key;
- device identity, device secret, atau bearer token.

File tersebut sudah dicakup oleh `.gitignore`. Tetap periksa `git status` sebelum commit.

## 9. Commit dan Pull Request

Periksa perubahan:

```powershell
git status
git diff
```

Commit menggunakan Conventional Commits:

```powershell
git add .
git commit -m "fix(updater): describe the change"
git push -u origin fix/nama-perubahan
```

Contoh tipe commit:

- `feat(agent): add printer capability`;
- `fix(updater): authenticate package download`;
- `test(printing): cover spooler error state`;
- `docs(build): update release guide`;
- `chore(release): prepare version 1.0.4`.

Buat Pull Request ke `master`. Pull Request harus lulus CI sebelum merge.

## 10. Build melalui GitHub Actions

Workflow `.github/workflows/ci.yml` berjalan pada Pull Request dan push ke `master` menggunakan
Windows runner. CI menjalankan build, test, Native AOT publish, dan MSI packaging.

Setelah workflow selesai:

1. buka repository GitHub;
2. buka tab **Actions**;
3. pilih workflow run yang berhasil;
4. download artifact **opsifin-print-agent-msi**;
5. ekstrak ZIP artifact;
6. gunakan MSI di dalamnya untuk release Hub.

Artifact CI adalah output sementara. MSI production tetap dikelola melalui Print Hub, bukan
disimpan di Git repository.

## 11. Upload release ke Print Hub

1. Buka **Agent Releases**.
2. Upload satu MSI.
3. Masukkan versi yang sama dengan `Directory.Build.props`.
4. Isi release notes.
5. Gunakan installation policy **Manual** untuk pengujian awal.
6. Publish release.
7. Buka tenant → **Agents**.
8. Pada Agent target, pilih **Actions → Schedule update**.
9. Pilih target release.
10. Pantau deployment sampai `Succeeded`.

Hub menghitung SHA-256 server-side. Bandingkan nilainya dengan output build untuk memastikan file
yang di-upload adalah MSI yang benar.

Status normal deployment:

```text
Pending
→ Delivered
→ Accepted
→ Downloading
→ Ready
→ Waiting for idle
→ Installing
→ Succeeded
```

`Succeeded` hanya valid setelah heartbeat melaporkan target version. Command yang diterima belum
berarti installer sudah berhasil dipasang.

## 12. Pilot dan rollout production

Untuk satu Agent test, gunakan **Schedule update** langsung pada Agent.

Untuk banyak Agent production:

1. publish release;
2. jalankan **Pilot 10%**;
3. pantau error, versi, service health, dan print activity;
4. lanjutkan **Roll out remaining** setelah pilot stabil;
5. retire release lama yang tidak boleh dijadwalkan lagi.

Policy **Automatic** tetap menunggu print aktif selesai dan event outbox terkirim sebelum service
berhenti untuk instalasi.

## 13. Checklist release developer

- [ ] Source terbaru sudah ditarik dari `master`.
- [ ] Pekerjaan dilakukan di feature/fix branch.
- [ ] Versi dinaikkan dan belum pernah digunakan.
- [ ] `dotnet build` berhasil.
- [ ] `dotnet test` berhasil.
- [ ] `build-installer.ps1` berhasil tanpa warning/error.
- [ ] Nama MSI dan SHA-256 sudah diperiksa.
- [ ] Tidak ada secret atau build output di `git status`.
- [ ] Pull Request dan CI berhasil.
- [ ] MSI yang benar sudah di-upload ke Hub.
- [ ] Release diuji pada satu Agent sebelum rollout luas.
- [ ] Heartbeat Hub mengonfirmasi target version.

## 14. Troubleshooting singkat

### Native AOT atau linker gagal

Pastikan Visual Studio Build Tools, workload **Desktop development with C++**, dan Windows SDK sudah
terpasang. Tutup lalu buka kembali terminal setelah instalasi workload.

### PowerShell menolak script

```powershell
Set-ExecutionPolicy -Scope Process Bypass
```

### Hub tidak mengirim notifikasi update

Status **Published** hanya membuat release tersedia. Jadwalkan update melalui Agent
**Actions → Schedule update** atau gunakan **Pilot 10%** untuk rollout bertahap.

### Update gagal pada tahap download

Periksa deployment message di Hub. HTTP 401 berarti autentikasi Agent ditolak; HTTP 404 biasanya
berarti package/deployment tidak ditemukan; SHA-256 mismatch berarti file yang diunduh berbeda dari
release yang disimpan Hub.

### Versi tidak berubah setelah instalasi

Pastikan versi dinaikkan sebelum build, service kembali Running, dan heartbeat terbaru melaporkan
target version. Tutup dan buka kembali Agent Control agar GUI memuat executable terbaru.

## Batas kompatibilitas

Build saat ini ditujukan untuk Windows 10/11 dan Windows Server 2016+ x64. Windows 7 belum didukung
oleh runtime `.NET 10` yang digunakan Agent ini.
