# Hub Handoff: Active Print Jobs in Agent Control

> Status 5 November 2026: **implemented on Hub and Agent**. Canonical contract Hub already
> contains `display` and `source_poll_interval_s`; Agent `0.3.3` has synchronized the contract,
> maps invoice metadata into the local activity snapshot, and retains the short Job ID fallback.

## Purpose

Opsifin Agent Control needs to show which invoice is currently being processed on each local
printer. The Agent must remain operationally simple: Hub owns invoice identity, routing,
queueing, retries, cancellation, and history; Agent only displays the active work it has
already claimed.

The same screen also needs to explain two different polling intervals accurately:

- Agent checks Print Hub for available print jobs using `claim_interval_s` (default 5 seconds).
- Print Hub pulls invoice data from opsifin-main using `source_connections.pull_interval_s`
  (default 30 seconds).

These values must not be presented as one interval because they represent different systems.

## Original Limitation (resolved)

Before this handoff, `ClaimedJob` contained only `job_id`, `attempt_id`, `printer_id`, `lease_until`,
document metadata, and print settings. Agent therefore cannot identify the invoice beyond its
internal Job UUID.

`AgentConfig` carried `claim_interval_s`, but it did not carry the Hub source refresh
interval. Agent must not hard-code the 30-second source interval because Hub may configure it
per tenant or source connection.

## Required Hub Contract Changes

Update the source-of-truth contract in `opsifin-print-hub/contracts/agent-v1.yaml` first.
After Hub implementation and tests pass, copy the contract to this repository.

Add an optional `display` object to `ClaimedJob`:

```yaml
ClaimedJob:
  type: object
  required: [job_id, attempt_id, printer_id, lease_until, document, print]
  properties:
    # Existing properties remain unchanged.
    display:
      $ref: '#/components/schemas/JobDisplay'

JobDisplay:
  type: object
  required: [invoice_no, item_type]
  properties:
    invoice_no:
      type: string
      maxLength: 100
    item_type:
      type: string
      maxLength: 50
    received_at:
      type: [string, 'null']
      format: date-time
```

Add an optional informational field to `AgentConfig`:

```yaml
AgentConfig:
  type: object
  properties:
    # Existing properties remain unchanged.
    source_poll_interval_s:
      type: [integer, 'null']
      minimum: 1
      description: >-
        Informational interval used by Hub to pull invoices from opsifin-main for this
        agent's source. Agent must not use this value for scheduling.
```

If more than one source interval applies to an Agent in the future, Hub should send `null`
rather than an inaccurate number. Agent will then omit the source interval from its UI.

## Example Payload

```json
{
  "jobs": [
    {
      "job_id": "019a4d3d-19af-7c61-b4cf-f949fd97a22d",
      "attempt_id": "019a4d3d-1b1a-73f8-9578-aab0958a3f38",
      "printer_id": "019a4d3d-1d02-7f8a-a034-312f7136d40f",
      "lease_until": "2026-11-04T05:02:00Z",
      "display": {
        "invoice_no": "INV-2026-001238",
        "item_type": "INV",
        "received_at": "2026-11-04T04:59:30Z"
      },
      "document": {
        "url": "https://signed-storage.example/document",
        "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "pages": 2,
        "size_bytes": 182340
      },
      "print": {
        "engine": "pdfium",
        "copies": 1,
        "duplex": false,
        "tray": null,
        "scale": "noscale"
      }
    }
  ]
}
```

Heartbeat config example:

```json
{
  "server_time": "2026-11-04T05:00:00Z",
  "config_version": 12,
  "config": {
    "claim_interval_s": 5,
    "heartbeat_interval_s": 30,
    "source_poll_interval_s": 30,
    "printers": []
  }
}
```

## Compatibility Rules

- Changes must remain additive within `/agent/v1`.
- `display` and `source_poll_interval_s` must be optional.
- Hub must continue accepting older Agents that ignore these fields.
- New Agents must use a shortened Job ID as fallback when `display` is absent.
- Hub must never reject a claim solely because display metadata cannot be produced.
- Existing lease, idempotency, ordering, and one-job-per-printer behaviour must remain unchanged.

## Hub Implementation Requirements

1. Claim query must read `print_jobs.inv_no`, `print_jobs.item_type`, and
   `print_jobs.received_at` together with the existing claimed job.
2. Map those fields into `display` without adding another query per job.
3. Populate `source_poll_interval_s` from the applicable source connection or effective tenant
   configuration.
4. Continue ordering claims oldest-first and returning at most one job per requested idle
   printer.
5. Add contract and endpoint tests for:
   - display metadata present;
   - display metadata omitted without breaking the response;
   - source interval present and `null` variants;
   - older Agent version compatibility;
   - no customer or financial data leakage.
6. Update the canonical contract before copying it to the Agent repository.

## Agent Responsibilities

Agent will:

- show active jobs only, one row per printer;
- display invoice number and item type when Hub supplies them;
- fall back to a shortened Job ID when metadata is absent;
- display actual `claim_interval_s` from remote config;
- display `source_poll_interval_s` only when Hub supplies an unambiguous value;
- track local stages: Claimed, Downloading PDF, Sending to printer, Printing, and Verifying
  completion;
- clear active rows when processing finishes or the service restarts;
- keep full history, retry controls, routing, reprint, and cancellation in Hub.

## User Experience Contract

Agent Dashboard copy distinguishes local progress and the two intervals:

```text
Live progress from this PC's Windows print spooler
New print jobs are checked every 5 seconds
Print Hub refreshes invoice data every 30 seconds
```

The active table contains:

| Field | Source | Fallback |
| --- | --- | --- |
| Invoice | `display.invoice_no` | Short Job ID |
| Type | `display.item_type` | `Print job` |
| Printer | Local binding `windows_name` | Required; no fallback |
| Progress | Local processing stage | `Processing` |
| Pages | `document.pages` | `-` |
| Elapsed | Local start time | `-` |

`Verifying completion` must be visible because Agent intentionally waits up to 60 seconds
after a spooler job disappears before reporting success.

## Security and Privacy

- Do not send invoice amount, document contents, `source_payload`, signed URL, SHA-256, device
  secret, or bearer token as display metadata.
- Do not send `customer_name` in the first release. Invoice number and item type are sufficient
  for local reconciliation.
- Agent activity snapshots remain under the existing protected ProgramData ACL.
- Diagnostic export redaction rules must continue to exclude credentials and PDF contents.
- Job metadata is operational display data, not a second source of business history.

## Acceptance Criteria

- A claimed job with display metadata appears in Agent Control within the next local refresh
  cycle.
- Multiple printers can show multiple active invoices concurrently.
- Each printer still processes only one job at a time.
- Progress changes are visible without waiting for the 30-second heartbeat.
- Invoice number never appears in service log messages unless explicitly approved later.
- Agent with a new Hub shows invoice number and type.
- Agent with an older Hub remains functional and shows a shortened Job ID.
- UI shows the actual claim interval, not a hard-coded value.
- UI never describes Hub source polling as an Agent operation.
- Completion, retry, cancellation, and reprint behaviour remain unchanged.

## Recommended Delivery Order

1. Hub updates its canonical `agent-v1.yaml` contract.
2. Hub implements display metadata and effective source interval.
3. Hub adds contract and endpoint tests.
4. Copy the canonical contract into this repository.
5. Agent maps the optional Hub metadata into its local activity snapshot.
6. Run Hub–Agent compatibility tests with metadata present and absent.
7. Pilot on one Windows PC before wider rollout.

## Out of Scope

- Viewing the Hub queue from Agent Control.
- Invoice preview or PDF download from the GUI.
- Customer name and financial values.
- Local cancel, retry, reprint, printer routing, or schedule controls.
- Long-term print history on the client PC.
- Changing source-pull or claim intervals from Agent Control.
