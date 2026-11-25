# SmartPrint disaster-recovery and throughput fixture

This fixture is a release gate for the existing Docker backup/restore scripts. It uses synthetic records only. Run destructive restore checks only against a disposable staging Compose project and isolated database/document volume; never point a fixture at a shop's live project.

## Recovery boundary

- Standard document bytes may be restored only while their original 72-hour retention window (measured from confirmed physical output) is still open. The original output timestamp is authoritative; restore must never reset it.
- Private document bytes are never restored. This includes opaque Private relay ciphertext, wrapped document keys, and any private source payload. The backup archive excludes `private-relay/`; restore explicitly excludes that subtree (including in legacy archives) and clears stale relay references from restored tickets.
- The Windows Agent DPAPI-protected local queue is not part of the Docker backup. It remains on its original shop computer and is not copied to a replacement machine as part of server disaster recovery.
- Restore financial, payment, refund, invoice, job-state, and required audit metadata from PostgreSQL. Never replay a payment or print command automatically.
- A job whose physical output is uncertain remains output-unconfirmed and requires the existing reconciliation path. API acceptance or Windows spooler acceptance is not proof of physical completion.

## Synthetic fixture

Use a fixed fixture time T and synthetic tenant, Agent, printer, customer, payment, and job IDs. Seed the following rows and encrypted document objects in a disposable environment:

| Fixture | Mode/state at T | Expected after restore |
|---|---|---|
| STD-A | Standard; physical output confirmed at T-2h; source object exists | Restore source; preserve the original confirmation time and delete at its original T+70h deadline |
| STD-B | Standard; physical output confirmed at T-71h; source object exists | Restore only while its original 72h window remains open; retention worker removes it at the original deadline |
| STD-C | Standard; physical output confirmed at T-73h | Do not make the expired source available after restore; metadata may remain for accounting |
| PRIV-A | Private; relay ciphertext uploaded, Agent has not staged it | Never restore ciphertext or wrapped key; clear relay references and mark the incomplete relay ticket failed; require customer re-upload if printing is still wanted |
| PRIV-B | Private; Agent has confirmed a protected local copy, relay acknowledged/consumed | Do not restore any document bytes; retain essential job/payment/audit metadata. The original Agent's local protected queue follows its normal retry/deletion rules |
| PRIV-C | Private; physical output confirmed and source purged | Restore only permitted operational/financial metadata; no source, relay, or recovery copy |
| MIX-A | Standard and Private jobs in the same batch, with distinct documents and payment/job states | Restore eligible Standard content only; exclude all Private content; preserve per-job mode and financial allocation |
| PAY-A | Payment captured; webhook/checkout reconciliation already committed | Preserve the single captured payment and invoice; do not call the payment provider or create another charge |
| PRINT-A | Agent claim/dispatch acknowledged but physical output is uncertain | Preserve output-unconfirmed state; do not auto-dispatch again |

## Backup/restore exercise

1. Create the synthetic tenant and fixture rows above in staging. Record SHA-256, size, original output-confirmation timestamp, payment reference, job status, and privacy mode. Never place real customer content in this fixture.
2. Start a Standard upload and a Private relay upload while a different paid job is being dispatched. Run `scripts/docker-backup.ps1` during this activity.
3. Verify the API and portal remain running throughout backup. The PostgreSQL dump must validate with `pg_restore --list`; the encrypted-document archive must validate with `tar -tzf`.
4. Inspect the archive listing: Standard encrypted objects may be present; no path under `private-relay/`, no upload temporary file, and no Agent DPAPI queue file may be present in a newly created archive. A legacy archive may contain the path, but restore must never extract it.
5. In the disposable staging environment, restore the matching database, document archive, and secrets using the guarded restore procedure. The restore must exclude any `private-relay/` path during extraction, including from legacy backups, while still recovering eligible Standard documents.
6. Verify restored Standard files by decrypting through the application storage reader and comparing their original SHA-256 and byte length. Verify STD-C is not available for customer preview/reprint and is purged by the normal retention sweep.
7. Verify every Private fixture has no restored document object or relay key material. An incomplete relay ticket is failed with `RESTORE_CONTENT_EXCLUDED`; completed job/payment/audit metadata remains intact.
8. Verify payment counts, captured amounts, refunds, invoice identities, job identities, and dispatch attempt counts match the pre-backup fixture. No provider payment call or printer command is emitted by restore.
9. Restart the Agent in the fixture and verify existing local queue reconciliation remains idempotent. Do not re-claim or reprint PRINT-A while its output is uncertain.

## Throughput and user-experience gate

Run the same test once without backup and once with backup active, on the same documented modest shop-PC profile and network:

- 20 files per batch, 50 MiB maximum per file, 500 MiB aggregate; use separate file requests.
- At least three concurrent customer transfers while an existing paid job is dispatched and Agent heartbeats continue.
- Measure p50/p95 customer upload-to-queue visibility, shop-visible job availability, Agent heartbeat latency, claim latency, printer handoff latency, peak RAM, CPU, disk I/O, and network throughput.
- Compare against the no-backup baseline. Backup must not stop/restart API or portal, occupy Agent dispatch/heartbeat workers, create unbounded queues, or change payment/print state.
- A regression beyond the agreed baseline is a failed gate. Do not claim "no bottleneck" until measurements on the target hardware demonstrate it.

## Scope and safety

The backup is an online PostgreSQL snapshot followed by a read-only archive of atomically published encrypted Standard document objects. Extra unreferenced objects are safe to discard; missing eligible Standard content is not. Any archive/database mismatch, failed validation, or ambiguous restore result must fail closed and keep the restored API/portal offline until an operator resolves it. Never run this destructive exercise against production.
