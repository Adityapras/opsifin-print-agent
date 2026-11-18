# Installer EXE dan MSI

## Build satu perintah

Jalankan dari PowerShell pada Windows:

```powershell
.\tools\build-installer.ps1
```

Script menjalankan test, publish Service/Desktop/Updater, lalu membuat dua installer:

```text
dist\OpsifinPrintAgent-Setup-<version>-win-x64.exe
dist\OpsifinPrintAgent-Setup-<version>-win-x64.msi
```

- Berikan EXE kepada client untuk fresh install atau upgrade manual.
- Upload MSI ke Print Hub untuk update OTA.
- Folder `artifacts\package` hanya input build dan tidak perlu didistribusikan.

Setup EXE adalah WiX Burn bundle yang menanam MSI yang sama. EXE menyediakan UI branded,
pemeriksaan Windows x64 yang didukung, UAC, progress instalasi, serta membuka Agent Control
setelah instalasi interaktif. Lifecycle file, service, rollback, upgrade, dan uninstall tetap
ditangani MSI.

Silent install untuk deployment IT:

```powershell
.\OpsifinPrintAgent-Setup-<version>-win-x64.exe /quiet /norestart
```

## Release update

Tidak ada certificate, PFX, signing PC, atau release manifest. Pada saat MSI diunggah, Hub:

1. membaca versi release;
2. menghitung SHA-256 MSI;
3. menyimpan MSI pada URL HTTPS;
4. mengirim `version`, `url`, dan `sha256` kepada Agent.

Agent mengunduh MSI, mencocokkan SHA-256, menunggu aktivitas print selesai, memasang MSI secara
silent, lalu menyalakan service kembali. `install_policy` dapat berupa `manual` atau `automatic`.

EXE dan MSI belum memiliki Authenticode sehingga Windows dapat menampilkan peringatan publisher.
Ini tidak memengaruhi update OTA dari Agent Control.

## Hasil instalasi

MSI memasang service `OpsifinPrintAgent`, Opsifin Agent Control, system tray, shortcut Start Menu,
dan shortcut Desktop. Pairing dan pengujian dilakukan dari GUI; CLI hanya tersedia untuk diagnosis
developer.
