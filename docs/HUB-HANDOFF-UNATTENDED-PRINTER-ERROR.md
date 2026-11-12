# Hub Handoff: Treat Interactive Virtual Printers as a Permanent Error

## Status

Agent `0.3.4` rejects printers that require an interactive desktop or output filename before
calling GDI `StartDoc`. This prevents Windows Service Session 0 from hanging indefinitely on
Microsoft Print to PDF, XPS Document Writer, Fax, OneNote, and similar virtual drivers.

## Agent Event

The Agent emits a durable `print_error` with:

```json
{
  "error_code": "PRINTER_REQUIRES_INTERACTION"
}
```

## Required Hub Change

Add `PRINTER_REQUIRES_INTERACTION` to `print_hub.permanent_print_errors`. Retrying the same job
cannot succeed until the binding points to an unattended physical/network printer, so automatic
retry only creates duplicate failed attempts.

The Hub UI should explain:

- Microsoft Print to PDF and other prompt-based virtual printers cannot be used by a Windows
  Service;
- select a physical/network printer for production;
- use Agent simulation mode for end-to-end testing without physical hardware.

Add a job-event test proving this error moves the job directly to failed/permanent rather than
scheduling another automatic retry.
