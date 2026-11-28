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
- The isolated print child pauses after document preparation at the `StartDoc` boundary. It asks the parent Agent for a fresh server authorization and proceeds only after an explicit `ALLOW`; denial, timeout or transport failure sends `DENY` and does not call the Windows spooler.
- Virtual test output is recorded as `TEST_VIRTUAL` / `TEST_OUTPUT_READY`, never as physical fulfilment. The ordinary physical-output confirmation endpoint rejects it.
- Standard and Private Printing retain the existing verified spooler receipt, retry/idempotency and protected-source cleanup rules. PDF export remains separate.

## Required verification

Automated coverage now includes a real child-process stdin/stdout pipe test for the one-shot AUTHORIZE_SPOOLER_SUBMISSION handshake (ALLOW, DENY and callback/transport failure), plus policy tests for grant expiry/revocation during document preparation and strict output-type/environment matching. Platform Operations also requires a fresh queue inventory, an online paired Agent and an enabled queue before approval; revocation requires an explicit confirmation and audit reason.

1. Physical printer regression: unchanged selection, dispatch, spooler submission, and receipt.
2. Three-shop isolation: A approved, B not approved, C approved then revoked.
3. Grant expiry, explicit revocation, changed queue name, driver, port, and Agent pairing.
4. Non-platform-admin approval attempts are forbidden.
5. Production fail-closed checks at selection, assignment, claim, and the isolated child's pre-`StartDoc` authorization handshake.
6. A revoked/expired grant or failed authorization response after document preparation produces no spooler job.
7. Duplicate claims, retry, lost acknowledgement, and reconciliation remain idempotent.
7. Private Printing cannot purge protected content before the existing required acknowledgement.
8. Real Windows acceptance with the actual installed IPP virtual queue. CI/unit tests are not a substitute for this hardware/driver check.

## Real Windows IPP acceptance (shop PC)

Hosted CI can validate the policy, API, Agent protocol and Windows spooler integration fixtures, but it cannot reach a printer installed on a shop PC or an Android device on the shop LAN. Do not mark this acceptance complete from unit tests alone.

1. On the shop PC, install/connect the Android IPP virtual queue and confirm Windows can see it as Ready or Busy. Give the test queue an explicit name containing `Virtual` (for example, `Android Virtual IPP Test`) so discovery classifies it as `UNSUPPORTED_VIRTUAL`; do not rename or reclassify a physical IPP printer.
2. Confirm the Agent inventory reports the exact queue name, Microsoft IPP driver, port/IPP endpoint and `UNSUPPORTED_VIRTUAL` output mode. Record the identity fingerprint.
3. In Platform Operations, approve that exact shop and queue with an audit reason. Verify the approval appears against the correct tenant and expires in seven days by default.
4. In the shop portal, select the approved virtual queue. Submit a small, known-good PDF through the ordinary customer upload, review, payment and dispatch journey.
5. Verify on Windows that the job reaches the named IPP queue through the normal Agent print worker. Verify the Android IPP receiver receives the document. The job must finish as test output (`TEST_VIRTUAL` / `TEST_OUTPUT_READY`), not physical fulfilment; no collection-ready or physical settlement transition may occur.
6. Repeat with Private Printing. Verify the protected source is retained until the normal verified spooler/output acknowledgement and then purged according to the existing Private Printing rules. The test artifact remains subject to the separate non-production test-output retention policy.
7. Revoke the grant and repeat. The queue may remain visible but must not be selectable or assigned. If revocation/expiry/identity change occurs after document preparation but before `StartDoc`, the child must receive `DENY` and Windows must show no new spooler job.
8. Change the queue port or driver, sync inventory and verify the old grant is revoked and cannot be reactivated by switching the identity back.
9. Print one small test document to a real physical printer and verify the existing physical output confirmation journey is unchanged.
10. Verify Microsoft Print to PDF still uses the separate `PDF_EXPORT` path and is not treated as this virtual IPP test.

Record the Windows queue/port/driver, Agent version, grant ID and expiry, job ID, spooler job ID, Windows/Android receiver evidence, output state and whether any physical fulfilment state changed. Keep customer documents and personal content out of evidence artifacts.

## Executable shop-PC Android Virtual Printer acceptance

Use the checked-in operator-assisted harness after the virtual queue grant is active:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\qa\Invoke-SmartPrintVirtualPrinterAcceptance.ps1 `
  -QueueName "Android Virtual IPP Test" `
  -GrantId "<approved-grant-id>" `
  -AgentVersion "<installed-agent-version>"
```

Run this on the Windows shop PC that has the paired SmartPrint Agent and Android IPP virtual queue installed. The script does **not** create a synthetic Windows print job. It snapshots existing queue job IDs, waits for the operator to start exactly one diagnostic from the SmartPrint owner UI, and requires a newly observed spooler job on the selected queue. It then requires an explicit operator confirmation that the complete diagnostic arrived and can be opened/rendered in the Android receiver. A JSON evidence report is written under `artifacts/virtual-printer-acceptance`.

The harness reports PASS only when both the new Windows spooler job and the Android receiver confirmation are present. A missing spooler job, timeout, or absent receiver confirmation fails. A spooler job disappearing from Windows is recorded as an observation only; it is not interpreted as proof of delivery. The report records queue/driver/port identity, Windows and Agent versions, grant ID, spooler job metadata, timestamps and receiver confirmation. It deliberately excludes document contents and customer data.

This is an operator-assisted **real-device acceptance test**, not a hosted-CI test: hosted runners cannot see the shop PC's installed queue or the Android device on the shop LAN. Keep the Android receiver visible during the test and retain the generated JSON with the matching SmartPrint diagnostic attempt/job ID. After a successful virtual test, revoke the grant and run the negative-path checks above. Do not mark the overall hardware qualification complete until the real shop-PC run and its evidence have been reviewed.
