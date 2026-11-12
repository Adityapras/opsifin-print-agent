# Installer MSI

## Build satu perintah

Jalankan dari PowerShell pada Windows:

```powershell
.\tools\build-installer.ps1
```

Script menjalankan test, publish Service/Desktop/Updater, lalu membuat satu installer:

```text
dist\OpsifinPrintAgent-Setup-<version>-win-x64.msi
```

Hanya file MSI tersebut yang diberikan kepada user atau diunggah ke Print Hub. Folder
`artifacts\package` hanya input build dan tidak perlu didistribusikan.

## Release update

Tidak ada certificate, PFX, signing PC, atau release manifest. Pada saat MSI diunggah, Hub:

1. membaca versi release;
2. menghitung SHA-256 MSI;
3. menyimpan MSI pada URL HTTPS;
4. mengirim `version`, `url`, dan `sha256` kepada Agent.

Agent mengunduh MSI, mencocokkan SHA-256, menunggu aktivitas print selesai, memasang MSI secara
silent, lalu menyalakan service kembali. `install_policy` dapat berupa `manual` atau `automatic`.

Installer pertama belum memiliki Authenticode sehingga Windows dapat menampilkan peringatan
publisher. Ini tidak memengaruhi update OTA dari Agent Control.

## Hasil instalasi

MSI memasang service `OpsifinPrintAgent`, Opsifin Agent Control, system tray, shortcut Start Menu,
dan shortcut Desktop. Pairing dan pengujian dilakukan dari GUI; CLI hanya tersedia untuk diagnosis
developer.
