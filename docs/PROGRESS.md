# Opsifin Print Agent — current baseline

Last updated: 12 November 2026

## Product baseline

- Version `1.0.0` is treated as a fresh installation. Compatibility and key migration from the
  previous local installations are intentionally removed.
- The Windows Service performs pairing, heartbeat, job claims, PDF download, printing, spooler
  monitoring, durable event delivery, remote commands, and safe updates.
- Opsifin Agent Control provides pairing, service/Hub health, printer inventory, active print jobs,
  simulation mode, diagnostics, software update status, and system tray access.
- The installer is one self-contained MSI. Users do not receive a publish folder and do not need a
  command line.

## Simplified release flow

1. Any developer or CI runner runs `.\tools\build-installer.ps1` on Windows.
2. The output is `dist\OpsifinPrintAgent-Setup-<version>-win-x64.msi`.
3. The operator uploads that single MSI to Print Hub.
4. Hub calculates SHA-256 and publishes the update.
5. Agent downloads over HTTPS, checks SHA-256, waits until printing is idle, installs silently, and
   reports the installed version through heartbeat.

There is no Authenticode requirement, PFX, internal signing key, recovery key, signing workstation,
or separate release manifest. Windows may show an unknown-publisher warning during first install.

## Verification completed

- `dotnet build -c Release`: passed with zero warnings and zero errors.
- `dotnet test -c Release --no-restore`: 103 tests passed.
- `.\tools\build-installer.ps1 -SkipTests`: produced the `1.0.0` MSI successfully.
- MSI SHA-256: `cf15fdb5f893667c187ca17ce2bf3a0fb9715241e0e9643050bb9515713bd8d5`.

## Next integration step

Update Print Hub according to `docs/HUB-HANDOFF-OTA-UPDATES.md`, then perform one clean end-to-end
test: uninstall the old Agent, clear its old local state, delete/revoke the old Hub Agent record,
install `1.0.0`, pair from the GUI, print in simulation mode, and publish a small `1.0.1` update.

## OTA verification build

- Version `1.0.1` changes the Agent Control subtitle to **Windows print control center with safe
  updates**, providing a visible confirmation after OTA succeeds.
- All 103 automated tests passed before packaging.
- MSI SHA-256: `56074974c70316b6f6128371d8b8b8a30f7b79a0cc87046baf31f482ca3f4efe`.
