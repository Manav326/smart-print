# Security and Operations

## 1. Tenant and identity security

- Derive tenant scope from verified user membership or registered device identity.
- Enforce tenant checks for every API operation, file, job, printer, payment and report.
- Use role-based permissions for owner, manager, operator and reporting roles.
- Require strong authentication for shop owners and platform administrators; support MFA.
- Apply rate limits and abuse controls to QR sessions, uploads, login, quotes and payment callbacks.
- Audit role changes, refunds, overrides, retries, data exports and support access.

## 1A. Tenant lifecycle and subscription enforcement

- Tenant state is controlled through the platform-only `PATCH /api/v1/platform/tenants/{tenantId}/status` endpoint. The platform administrator must provide a 5–500 character reason; the actor, previous status, new status and timestamp are persisted in `TenantStatusAudits`.
- Supported transitions are Active → Suspended/Inactive and Suspended → Active/Inactive. Inactive is terminal; reactivation is not supported.
- Suspension blocks new QR sessions, print submissions, quotes, payment starts, new Agent claims and new authorisation/dispatch actions. Read-only history and limited recovery operations remain available so already-paid or physically submitted jobs can be reconciled, refunded, rejected or confirmed.
- The shop API enforces subscription access and feature entitlements server-side. Portal navigation is not an enforcement boundary.
- API rate limits use source-IP partitions so clients cannot bypass them by changing untrusted session or Agent headers. The current defaults are 20 authentication requests/minute, 120 public requests/minute, 30 Agent requests/minute, 30 QR session creations/minute and 8 document uploads/minute. Review these values against production traffic and trusted proxy configuration.
- In the provided Cloudflare Tunnel Compose deployment, portal Nginx overwrites `X-Forwarded-For` with Cloudflare's `CF-Connecting-IP` value before proxying to the API. Keep the API container private and do not publish port 8080 directly; this is the trust boundary for forwarded client addresses.

## 2. QR session security

A printed QR identifies a shop station; it does not authorise printing by itself. On scan, issue a short-lived, random, scoped session bound to tenant/station and validated by the local edge or cloud. Support expiry, revocation, rotation, abuse limits and replay protection. Do not embed long-lived secrets or customer data in static QR codes.

## 3. Document and file security

- Allowlist file formats and validate signatures, not only extensions.
- Enforce file size, page count, decompression and processing-time limits.
- Store files outside public web roots; use opaque identifiers and short-lived authorised access.
- Encrypt cloud documents at rest using AES-256-GCM with a deployment-specific 32-byte key; protect the key in the deployment secret store.
- For direct LAN uploads, issue a random one-use grant bound to the live QR session, tenant, station, assigned Agent/printer, filename, byte count and SHA-256. Persist only a hash of the grant in the API.
- Accept the document only at the exact private-LAN Agent, verify grant ownership with the cloud API, enforce a 20 MB transfer ceiling and analyse the actual document signature/content before storing.
- Store received Agent documents and the Windows Agent identity with DPAPI LocalMachine; protect the queue directory with SYSTEM/Administrators-only Windows ACLs. Never return a local-only document through cloud document-preview/download endpoints.
- The document storage format is versioned (`QSP1`); never reuse the development key in production.
- Cloud document uploads require a clean ClamAV result and then use the private, resource-limited document-worker container for parsing. Direct shop-LAN uploads require a Windows Defender scan on the paired Agent and then use a separate restricted LocalService upload-analysis worker. Both paths fail closed on scanner failure; print-time PDFium/Skia rendering still runs inside the Agent and needs its own process isolation and Windows acceptance evidence.
- Apply explicit retention and deletion policies. Never log document contents or public file URLs.
- Platform administrators configure print quota and document/database retention under **Platform Control Centre → Operations & retention**. The seeded trial quota is 3,000 print jobs per calendar month. Paid-plan quotas remain versioned in **Plans & entitlements**. Operations controls cloud-completed, Agent-completed, abandoned-unpaid, pending-local-upload, expired-ticket, empty-session and stale-refresh-token retention, plus API/Agent sweep intervals and whether already-paid work may continue during a shop suspension. Every update requires an audit reason and records before/after settings.
- Cloud source documents are purged only after confirmed physical output or a verified PDF export, or after a terminal rejection, quote expiry or full refund has passed the configured abandonment period without a financial hold. The database retains job, payment and refund metadata and marks purged documents with `PurgedUtc`; subsequent cloud preview/download returns HTTP 410.
- The Windows Agent receives its completed-document retention window and sweep interval from the platform heartbeat. It removes pending-payment local source documents only after the API confirms the job is terminal unpaid, financial holds are clear and the platform retention deadline has passed. Output-unconfirmed jobs and paid work remain recoverable.
- The API sweeps old expired local-upload tickets, empty customer sessions and expired/revoked refresh tokens at the platform-configured intervals. Historical print jobs, payment events, refund events and tenant audit history are retained.
- Both cloud and local customer upload paths share the 20-file / 50-MB batch limit. Local upload reservations are serialized with the tenant's monthly print-job quota lock; expired, uncompleted reservations no longer consume capacity.
- Collection reports record gross payments in the payment verification period and refunds in the period when the shop records that the refund was actually sent. A later refund does not rewrite the earlier gross collection period.

- Prevent path traversal, archive bombs, malicious Office content and unsafe preview rendering.

## 4. Payments and financial integrity

- Verify automated gateway payment status server-side using signed webhooks and/or provider APIs.
- For a manually confirmed SaaS subscription payment, require a platform administrator to check the actual platform account receipt, enter the bank/UPI transaction reference, and grant the plan through the audited platform endpoint. The API snapshots the published amount/currency, operator and reference; never accept a shop customer's success screen or client-supplied payment flag as proof.
- Enforce idempotency for order creation, capture, refund and webhook processing.
- Store immutable quote, tax, currency and payment snapshots.
- Separate customer print payment flows from SaaS subscription billing.
- Reconcile gateway transactions and internal ledgers; never accept a client success screen as proof.
- Refunds and manual cash overrides require permission and audit trail.

## 5. Physical print safety

- Persist job intent and submission attempt before spooler side effects.
- Never automatically resubmit a job whose physical output is uncertain.
- Distinguish queued, submitted, spooler accepted, printing, completed and output unconfirmed.
- Operator retry must explain duplicate risk and require explicit confirmation where appropriate.
- Do not claim physical completion when the printer only confirms spooler acceptance.

## 6. Offline, backup and recovery

- Local authorised jobs should continue during temporary internet outages.
- Current limitation: direct LAN document upload and local Agent status/preview communication use private-network HTTP. CORS, one-use grants, ownership checks and hashes do not encrypt the document bytes in transit. Until end-to-end local payload encryption or trusted HTTPS is implemented and tested, treat the shop LAN as a trusted, segmented network and do not use this path on guest/public Wi-Fi.
- Current limitation: the Windows Agent installer does not yet configure a dedicated least-privilege service identity. Validate the service account and printer permissions before production deployment.
- Local queue and event journal must be durable across process and machine restart.
- Define recovery and reconciliation for interrupted uploads, conversion, payment, spooler submission and sync.
- Provide encrypted backup/restore for local configuration and metadata; document customer document retention separately.
- Subscription expiry must not delete tenant data or interrupt a physical job already in progress.
- Offline grace duration and restrictions are configurable product policy, not a hidden constant.

## 7. Privacy and support

Platform support access to tenant records must be least-privilege, time-bound, purpose-bound and audited. Document access should be exceptional and consent/contract governed. Retention, deletion, export and incident response must be documented before production launch.

## 8. Release operations

- Deploy PostgreSQL, API, portal and the Cloudflare Tunnel connector through the versioned Docker Compose stack. Keep database and encrypted document volumes persistent; store the remotely-managed tunnel token in the Git-ignored .secrets/cloudflare/tunnel-token Docker secret file. The public portal and API share PUBLIC_BASE_URL and do not require inbound host ports or a local TLS certificate.
- Keep the Windows Print Agent on the printer-connected shop computer; it is not run in the Linux API container.
- Preserve the exact `.env` secrets and back up PostgreSQL and cloud document storage together before upgrades/migrations; test restore procedures.
- Sign Windows installers and agent updates.
- Use staged release channels and rollback-capable updates.
- Verify update signatures before installation.
- Preserve queue state and local configuration during upgrade.
- Publish checksums and release notes.
- Provide diagnostics bundle export with secrets and document contents redacted.
- The current backup script creates one authenticated AES-256-GCM `.spbackup` containing the PostgreSQL dump, encrypted document archive, deployment `.env`, Cloudflare Tunnel token and a hash manifest. A PBKDF2-HMAC-SHA256 passphrase is entered interactively; off-site completion requires an rclone content check. The companion restore-drill restores a disposable PostgreSQL database and document volume and verifies active QSP1 document tags, sizes and hashes. A real off-site target and scheduled owner-run restore rehearsal are still required before BLK-06 can be signed off.
- Email verification and account recovery are not yet implemented. New accounts are not gated on confirmed email; complete verification and recovery workflows before relying on email ownership for sensitive account changes.
- Cloud uploads and direct shop-LAN uploads now require malware scanning; cloud parsing is isolated in a restricted Docker worker. The native Windows Agent PDFium/Skia parser and renderer still run in the Agent process and remain a BLK-03 residual. Do not treat file-signature validation or a clean scan alone as full document-sandbox certification.


## 8A. Security hardening evidence

See [Security hardening BLK-03/05/06](SECURITY_HARDENING_BLK_03_05_06.md) for scanner configuration, isolated worker boundaries, PostgreSQL race cases, encrypted backup format, restore drill, operator commands and residual acceptance requirements.
