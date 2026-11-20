# Agent private documents module

## Ownership
Owns Agent-side private-relay queue discovery/long polling and the pending-purge queue/acknowledgement that a local-only private source has been erased.

## Safety invariants
- Relay and purge queue results are scoped to the verified tenant and the exact Agent that owns or claimed the job.
- The relay endpoint subscribes before its first durable database read, then re-reads after a notification or bounded timeout; PostgreSQL remains the source of truth.
- Relay pickup is bounded to 50 oldest eligible tickets, a 2-minute fresh/ready printer queue, and a 25-second listener / 10-second fallback wait. Caller-supplied wait values are clamped to those bounds.
- PDF virtual output is independently disabled in Production even when the operational setting is enabled; non-production requires the explicit operational setting.
- Agent stage updates are tenant- and Agent-scoped, require an actually received cloud payload or an unexpired direct-LAN claim, and preserve monotonic transitions, duplicate success and terminal-state rejection.
- Agent rejection reports accept only the existing five document failure codes and a message of at most 400 characters; the same Agent/tenant owns the ticket, rejected encrypted relay bytes are deleted, encryption metadata is cleared and repeated rejection is idempotent.
- A purge is offered only after confirmed physical output or a separately recorded non-production test output, with an explicit purge request.
- A duplicate acknowledgement is idempotent and returns the original purge timestamp.
- The API accepts acknowledgements only from the Agent that owns the `LOCAL_AGENT` document record.
- On acknowledgement, document metadata and the matching private upload-ticket metadata are redacted exactly as before; the one-way grant digest is retained for reconciliation, but retrieval/cloud recovery are not re-enabled.
- A PrintJobEvent with the existing reason code is recorded; job status is unchanged.
- No file is deleted by the API. The Agent confirms local deletion and the API only records the acknowledgement.
- All reads/writes stay tenant-scoped. No schema or migration changes are introduced.

## Dependencies
Uses the existing Agent authenticated-device context, shared EF entities and `PrivatePrintingOutputPolicy`.
