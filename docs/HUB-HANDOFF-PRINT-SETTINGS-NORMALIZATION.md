# Hub Handoff: Normalize Printer Settings Before Agent Responses

## Status

Open in `opsifin-print-hub`. Agent `0.3.3` contains a defensive fallback, but Hub remains the
source of truth and must stop emitting values that violate its canonical Agent API contract.

## Reproduced Failure

After the **Fax** printer was activated, Hub stored:

```json
{
  "engine": "pdfium",
  "copies": null,
  "duplex": false,
  "tray": null,
  "scale": null
}
```

`AgentConfigBuilder` and `JobClaimService` use `array_replace(defaults, print_settings)`. A
present `null` therefore overwrites `copies = 1` and `scale = noscale`. Agent `0.3.0` rejected
the heartbeat response at `config.printers[0].print.copies`, entered backoff, and appeared stale
even though the Windows Service process was healthy.

The canonical contract requires `copies` to be an integer and `scale` to be `noscale` or `fit`.
Only `tray` is nullable.

## Required Hub Changes

1. Create one print-settings normalizer used by both `AgentConfigBuilder` and
   `JobClaimService`; do not duplicate response rules.
2. Treat missing or `null` required values as defaults:
   - `engine`: `pdfium`;
   - `copies`: `1`;
   - `duplex`: `false`;
   - `scale`: `noscale`;
   - `tray`: `null` is valid.
3. Reject or normalize values outside the supported contract before persisting them.
4. Make the Filament fields for engine, copies, and scale required so editing an existing
   printer cannot save `null` values.
5. Backfill existing printer JSON containing explicit `null` required fields.
6. Increment the affected Agent's `config_version` after backfill so corrected config is sent
   on its next heartbeat.

## Required Tests

- Heartbeat config with partial settings returns complete non-null defaults.
- Heartbeat config with explicit `copies: null` and `scale: null` returns `1` and `noscale`.
- Job claim applies the same normalization as heartbeat config.
- `tray: null` remains valid.
- Editing/activating a printer through Filament cannot persist null required values.
- Existing valid custom values remain unchanged.

## Agent Compatibility

Agent `0.3.3` accepts invalid/null values defensively, applies safe defaults, and logs a warning.
This prevents an individual printer configuration from taking down heartbeat or claim parsing.
The fallback is not a replacement for fixing Hub because other Agent versions still implement
the canonical non-null contract strictly.
