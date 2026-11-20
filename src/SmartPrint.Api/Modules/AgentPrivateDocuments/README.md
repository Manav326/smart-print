# Agent private documents module

## Ownership
Owns the Agent-side pending-purge queue and acknowledgement that a local-only private source has been erased.

## Safety invariants
- Queue results are scoped to the verified tenant and the exact Agent that claimed the job.
- A purge is offered only after confirmed physical output or a separately recorded non-production test output, with an explicit purge request.
- A duplicate acknowledgement is idempotent and returns the original purge timestamp.
- The API accepts acknowledgements only from the Agent that owns the `LOCAL_AGENT` document record.
- On acknowledgement, document metadata and the matching private upload-ticket metadata are redacted exactly as before; the one-way grant digest is retained for reconciliation, but retrieval/cloud recovery are not re-enabled.
- A PrintJobEvent with the existing reason code is recorded; job status is unchanged.
- No file is deleted by the API. The Agent confirms local deletion and the API only records the acknowledgement.
- All reads/writes stay tenant-scoped. No schema or migration changes are introduced.

## Dependencies
Uses the existing Agent authenticated-device context, shared EF entities and `PrivatePrintingOutputPolicy`.
