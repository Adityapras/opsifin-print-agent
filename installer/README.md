# Installer MSI

Build setelah `dotnet publish` menghasilkan folder `artifacts/agent-next`:

```powershell
dotnet build installer\Agent.Installer.wixproj -c Release
```

MSI memasang service `OpsifinPrintAgent` sebagai `LocalSystem`, bergantung pada Windows
Print Spooler, dan menjalankannya otomatis. Enrollment dilakukan setelah instalasi dari
terminal Administrator karena secret harus dilindungi DPAPI scope mesin:

```powershell
& "C:\Program Files\Opsigo\Opsifin Print Agent\Agent.Service.exe" enroll `
  --hub-url https://print-hub.example `
  --pairing-code ABCD1234
Restart-Service OpsifinPrintAgent
```
