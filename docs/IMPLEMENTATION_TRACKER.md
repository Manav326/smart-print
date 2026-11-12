# SmartPrint by QuantaEdge — implementation tracker

Integration target: `main`. The initial shop-review/refund work (PR #2), Android document studio/global customer account (PR #1), and native multi-file batch handoff (PR #3) are merged. The current shop customer-directory work is tracked in PR #4 and remains subject to CI and tenant-isolation validation.

## Implemented in source
- [x] Multi-file PDF/JPG/JPEG/PNG customer uploads share one batch identifier.
- [x] Every file starts accepted; the shopkeeper can reject files individually and undo selections before applying review.
- [x] Verified paid files support page-level partial refunds from immutable per-page allocations saved with the original quote (duplex sheet sharing, copies and minimum charge included).
- [x] Multiple partial refunds accumulate; the total cannot exceed the original payment.
- [x] Refund idempotency keys prevent duplicate records and cannot be reused across files.
- [x] Previously refunded pages cannot be restored.
- [x] The API previews the additional refund before the shopkeeper confirms that money has already been returned.
- [x] The Windows Agent persists rejected pages and omits them from physical print and PDF export.
- [x] Partially refunded jobs remain eligible for printing their accepted pages.
- [x] Shop collection reports subtract recorded refunds from net collections.
- [x] SmartPrint by QuantaEdge branding is applied to portal metadata, Android launcher and primary documentation.

## Phase 1 / Phase 2 security and data-integrity hardening

Implementation is on `feature/phase-1-2-security-data-hardening`; merge acceptance remains subject to the branch CI and review.

- [x] Backend tenant-active enforcement on shop, public customer and Agent operation groups; suspended shops cannot start new work while read-only history and narrowly scoped recovery actions remain available.
- [x] Platform-admin tenant suspension, reactivation and permanent deactivation with required reason, actor/timestamp audit history, and terminal Inactive state.
- [x] Server-side checks for active subscription access, Advanced Reports and White-label branding; portal-only feature hiding is not relied upon.
- [x] Partitioned API request limiting for authentication, public, QR-session, Agent and document-upload traffic, keyed by source IP to prevent header-based partition evasion.
- [x] Local and cloud batch upload capacity share one overflow-safe 20-file / 50-MB rule; local ticket reservation, idempotency and monthly quota checks are serialized with the tenant quota lock.
- [x] Monthly print-job quota counts unexpired local reservations and completed-but-not-yet-materialised tickets until their PrintJob exists.
- [x] Gross payment collections are reported by payment verification date; refunds are a separate event stream by the date the shop records sending the refund.
- [x] Cloud document retention purges standard source documents 7 days after confirmed completion by default; hourly cleanup, persistent purge timestamp and HTTP 410 for purged previews. Private Printing erases the paired-Agent working source immediately after confirmed physical output and then redacts server source metadata.
- [x] Windows Agent retention removes standard completed print/PDF source documents after the 7-day default window (hourly sweep); Private Printing securely erases the paired-computer working copy immediately after confirmed physical output. Output-unconfirmed documents are preserved for recovery.
- [x] Regression tests cover tenant status transitions, batch bounds, cloud retention eligibility and safe local-queue retention.
- [x] Android guest-account instrumentation test clears persistent customer-account state before asserting guest UI.

### Remaining production blockers / acceptance work

- [ ] Encrypt direct LAN document payloads end-to-end. Current local HTTP uses one-use grants, CORS, ownership checks and hashes, but those controls do not encrypt bytes on the shop network.
- [ ] Run the Windows Agent under a tested least-privilege service identity and confirm printer, DPAPI and queue permissions; current installer identity has not been changed without physical Windows validation.
- [ ] Add malware scanning and isolate document parsers/converters.
- [ ] Add email verification and secure account recovery.
- [ ] Add integration/concurrency tests against PostgreSQL for simultaneous local completion, cloud upload, quota checks, refunds and Agent claims.
- [ ] Encrypt backup artifacts before off-host transfer and rehearse database/document/secret restore as one recovery set.
- [ ] Add per-tenant cloud/local storage quotas and the local-Agent disk-loss re-upload recovery flow.
- [ ] Validate retention behavior against real shop operations, disputes, legal holds and customer support policy before selecting the production retention period.

## Acceptance still required
- [ ] Re-run portal typecheck/build and .NET Release build/tests after quote-allocation and row-lock changes.
- [ ] Apply the EF migration against PostgreSQL containing historical refund rows.
- [ ] Exercise partial/full refunds, cumulative amounts and refund-vs-Agent-claim concurrency against PostgreSQL.
- [ ] Validate filtered physical print and PDF output on Windows.
- [ ] Validate page-review UX on desktop and physical Android.
- [ ] Rehearse UPI/cash returns and verify the customer receives the money before recording it.

## Rules
- Refunds are not automatic: the shopkeeper returns money through their own UPI/bank or cash process and records the returned amount.
- Page numbers are 1-based. The Windows Agent filters those numbers against the original document.
- Page selections can be changed before recording. Refunded pages are then permanently excluded.
- Refunds are blocked after local handoff, Agent claim, spooler activity or output confirmation.
- Rejecting all pages returns the remaining payment and marks the job `REFUNDED`.

## Shop customer directory — implementation in progress

- [x] Data source is the existing global customer identity linked to a QR print session; no shop-owned duplicate customer account is created.
- [x] API query filters by the authenticated shop tenant and includes only signed-in global customer accounts linked through that shop's sessions.
- [x] Guest sessions remain anonymous; historic guest jobs are not silently linked to customer accounts.
- [x] Shop responses mask the global account email; the full email is not returned to shop users.
- [x] Customer list supports bounded search and pagination; customer history is limited to jobs from the requesting shop.
- [ ] CI, API query translation and portal typecheck/build for PR #4.
- [ ] Exercise two shops with one shared global customer and verify that each shop sees only its own sessions/jobs.
- [ ] Physical shop operator/accessibility acceptance.
