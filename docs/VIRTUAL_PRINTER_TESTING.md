# Shop-scoped virtual printer testing

## Purpose

Allow a platform administrator to authorise a specific shop's discovered Windows virtual printer queue to exercise the existing physical print pipeline. A virtual queue must retain its actual virtual identity; authorisation must never rewrite its output type to `PHYSICAL`.

## Non-negotiable invariants

- Physical printers continue through the current Agent and Windows spooler implementation.
- An authorised virtual queue uses that same Agent, document preparation, Windows spooler submission, receipt, reconciliation, and retention path.
- No second renderer, print worker, queue, retry policy, or virtual-only dispatch state machine is introduced.
- Authorisation is scoped to tenant + printer + paired Agent + queue/driver/port identity.
- Grants are created and revoked only by platform administrators, are time-bounded, and retain reason/actor/timestamps for audit.
- A queue identity change invalidates its grant. Revocation and expiry are checked server-side at every dispatch boundary.
- A grant in one tenant never authorises a queue in another tenant.
- Production always rejects virtual test output, regardless of grant, client flags, or stale settings.
- Test output is marked as test output and cannot trigger physical fulfilment or production settlement.
- PDF Export remains a separate output mode and workflow.
- Private Printing keeps its protected relay, acknowledgement, and purge guarantees.

## Implemented enforcement points

- Discovery stores `UNSUPPORTED_VIRTUAL` and a SHA-256 identity fingerprint; it never upgrades the queue to `PHYSICAL`.
- Platform administrators approve/revoke time-bounded grants for one tenant, printer and paired Agent. Reasons and actor/timestamps are retained.
- Shop selection, QR-station assignment, job assignment, Agent claim, Private Printing relay and the final pre-spooler authorization all check the current grant and identity.
- The Agent uses its existing `WindowsPrintExecutor.PrintAsync` path for both physical and authorised virtual queues. Only `PDF_EXPORT` uses the separate PDF-export path.
- Virtual test output is recorded as `TEST_VIRTUAL` / `TEST_OUTPUT_READY`, never as physical fulfilment. The ordinary physical-output confirmation endpoint rejects it.
- Standard and Private Printing retain the existing verified spooler receipt, retry/idempotency and protected-source cleanup rules. PDF export remains separate.

## Required verification

1. Physical printer regression: unchanged selection, dispatch, spooler submission, and receipt.
2. Three-shop isolation: A approved, B not approved, C approved then revoked.
3. Grant expiry, explicit revocation, changed queue name, driver, port, and Agent pairing.
4. Non-platform-admin approval attempts are forbidden.
5. Production fail-closed checks at selection, assignment, claim, and immediately before spooler submission.
6. Duplicate claims, retry, lost acknowledgement, and reconciliation remain idempotent.
7. Private Printing cannot purge protected content before the existing required acknowledgement.
8. Real Windows acceptance with the actual installed IPP virtual queue. CI/unit tests are not a substitute for this hardware/driver check.
