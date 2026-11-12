# Hub handoff — simple Agent updates

## Decision

The Agent update flow intentionally has no Authenticode requirement, private signing key, recovery
key, signing workstation, or signed release manifest. This is a fresh-install baseline; migration
from older installed Agent versions is out of scope.

Transport and package integrity use:

- the existing authenticated Agent–Hub connection;
- HTTPS for production downloads;
- SHA-256 calculated by Hub when the MSI is uploaded and checked by Agent after download.

## Hub release screen

The operator only needs to:

1. select the MSI;
2. enter the version and optional release notes;
3. choose manual or automatic installation;
4. publish the release.

Hub calculates SHA-256. Do not ask the operator for a certificate, signature, key ID, channel,
manifest, or package size.

## Command sent to Agent

```json
{
  "id": "019aa000-0000-7000-8000-000000000001",
  "type": "update",
  "args": {
    "version": "1.0.1",
    "url": "https://print-hub.example/agent-updates/1.0.1/package",
    "sha256": "64-lowercase-hex-characters",
    "install_policy": "automatic",
    "release_notes": "Small reliability improvement"
  }
}
```

Required fields are `version`, `url`, and `sha256`. `install_policy` defaults to `manual` and can
be `manual` or `automatic`. `release_notes` is optional.

## Expected lifecycle

1. Agent accepts an update only when the target version is newer.
2. Agent downloads in the background while printing remains available.
3. Agent checks the SHA-256 supplied by Hub.
4. Manual policy waits for the user to select **Install update** in Agent Control.
5. Automatic policy continues without user action.
6. Both policies stop new claims and wait for active printing and event delivery to finish.
7. The updater installs the MSI silently, starts the service, and checks the new version health.
8. Heartbeat reports the update status and installed Agent version.

If download or SHA-256 validation fails, the installed version keeps running. An update must never
interrupt an active print operation.

## Hub acceptance checklist

- Uploading one MSI creates a release without another file.
- Hub calculates SHA-256 server-side.
- Production package URLs use HTTPS and require an authorized Agent request.
- Hub sends only the simplified command above.
- UI shows downloading, ready, waiting for idle, installing, succeeded, and failed.
- Hub considers installation complete only after heartbeat reports the target version.
