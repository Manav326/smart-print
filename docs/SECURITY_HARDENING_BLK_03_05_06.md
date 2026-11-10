# SmartPrint security hardening: document processing, concurrency and recovery

This document describes the security controls introduced on
`security/blk-03-05-06-hardening`. It does not mark any release gate closed by
itself. CI evidence and environment-specific acceptance are still required.

## BLK-03 — Malware scanning and parser isolation

### Cloud uploads

Cloud document uploads must pass these checks in order:

1. Extension, declared size and streamed byte count validation.
2. ClamAV INSTREAM scan. Only an explicit clean response is accepted.
3. Document analysis in the separate `smart-print-document-worker` container.
4. AES-256-GCM document encryption at rest and tenant/document-scoped persistence.

A ClamAV detection returns a document-security rejection. Scanner errors, timeouts,
malformed replies and unavailable ClamAV fail closed with HTTP 503. They must never
fall back to unscanned parsing. The scanner is reached only over the private Docker
network; no ClamAV port is published to the host or public network.

The parser worker is on an internal-only Docker network shared with the API. It runs
as the non-root `app` user, has a read-only root filesystem, a small no-exec tmpfs,
no added Linux capabilities, `no-new-privileges`, and explicit memory, CPU and PID
limits. API-to-worker requests use a persistent random token stored in a named Docker
volume and mounted read-only into the API and worker. The worker returns analysis
metadata only, not document bytes. Worker absence, invalid authentication, errors or
timeouts reject the upload.

### Direct shop-LAN uploads

The Windows Agent uses Microsoft Defender's on-demand file scan before PDF/image
analysis and before the document is accepted into the local queue. Its private scan
directory is ACL-restricted to SYSTEM, Administrators and the running Agent service
identity. A missing or failing Defender scanner blocks the upload; it does not
silently bypass scanning. The invoking \`MpCmdRun\` process is below-normal priority;
SmartPrint does not change the shop computer's global Defender CPU policy.

The upload parser is a separate Windows service running as
\`NT AUTHORITY\\LocalService\` with a restricted service SID and a local named-pipe
ACL. The Agent starts it only when a new upload or photo revision needs analysis.
The service stops itself after two minutes without a parsing request. Each parser
child has a 768 MB process-memory ceiling, a one-process job limit and a 90-second
request timeout. There is deliberately **no fixed CPU percentage cap**: the service
and parser run at below-normal process priority, allowing Windows to schedule the
normal-priority print Agent and spooler work first while analysis uses otherwise
available CPU. The parser service is not part of the Agent's print-dispatch dependency
graph. When it is unavailable, only new uploads/photo revisions are blocked; the
print Agent, GDI spooler path and already-accepted jobs remain available.

The Windows print path intentionally remains the existing direct
\`WindowsPrintExecutor\`/GDI implementation. It does not start, wait for, or call the
upload parser service. The Windows spooler integration tests execute that path
without installing the optional parser service, and the installer lifecycle test
asserts that the print Agent remains running while the parser is stopped and
Manual-start.

**Remaining BLK-03 work:** the existing Windows print executor still uses PDFium/Skia
inside the Agent process when rendering an accepted PDF for physical printing. The
upload-time analysis parser is now isolated, but the native print renderer is not.
Moving rendering out of the print execution path would add IPC and raster-transfer
cost and could compromise the shop computer's primary job. This candidate therefore
preserves the current print path and does not claim complete native parser isolation.
Run the supported-Windows Defender clean/malicious/scanner-outage acceptance suite,
verify the upload worker's service/ACL behavior on a shop-shaped Windows VM, and
obtain a security review of the in-process print renderer before BLK-03 sign-off.

## BLK-05 — PostgreSQL concurrency and financial-state integrity

The existing PostgreSQL integration suite already exercises quota reservations,
whole-batch printer assignment and tenant-scoped references. This branch adds:

- Refund-first versus Agent claim: a refund that commits first must prevent a later
  Agent claim.
- Agent-claim-first versus refund: the refund must re-read the locked row and reject
  after the claim/handoff wins.
- Two different Agent identities racing to claim one paid job: exactly one conditional
  update may succeed.
- Razorpay captured webhook versus browser checkout verification: both paths may
  reconcile the same capture, but only one subscription activation and platform audit
  record may be produced.

These tests must execute against isolated PostgreSQL, not EF's in-memory provider.
The tests do not replace the complete release matrix: upload completion, quota
boundaries, webhook replay/out-of-order delivery, tenant suspension and other state
transitions must remain in the recorded acceptance run.

## BLK-06 — Encrypted backup and isolated recovery

### Backup format

`scripts/docker-backup.ps1` now creates one `.spbackup` recovery bundle containing:

- PostgreSQL custom-format database dump.
- Archive of the encrypted cloud-document volume.
- Exact deployment `.env` file, including database, signing, document-encryption
  and Razorpay configuration.
- Cloudflare Tunnel token at `.secrets/cloudflare/tunnel-token`.
- Versioned manifest with component SHA-256 digests and the consistent snapshot time.

The complete archive is encrypted using streaming AES-256-GCM with independently
authenticated chunks and an authenticated footer. The key is derived from a
user-entered passphrase using PBKDF2-HMAC-SHA256 (600,000 iterations). The script
requires the passphrase to be entered twice and never writes it into the backup.
Keep the passphrase separately from both the application host and backup storage:
it cannot be recovered if lost.

By default, backup completion requires `SMARTPRINT_BACKUP_RCLONE_REMOTE` (or
`-OffsiteRemote`) and an off-site `rclone check --download` verification. The
explicit `-AllowLocalOnly` switch is for a local encrypted copy only; it is not
an off-site backup and is not a recovery sign-off.

### Restore and drill

`scripts/docker-restore.ps1` accepts only authenticated `.spbackup` bundles,
authenticates the full encrypted stream before parsing the manifest, verifies all
component hashes, validates the PostgreSQL/document archives and requires explicit
operator confirmation before replacing live data. Before destructive restore it
preserves the current `.env` and tunnel token. The backup and restore scripts stop
portal ingress first and drain API requests with a four-minute host/Compose shutdown
window before changing the database/document set. If restore fails after database
replacement starts, API and portal remain stopped rather than serving partial data.

`scripts/docker-restore-drill.ps1` restores the database into a disposable
PostgreSQL container on `--network none` and extracts documents into a disposable
Docker volume. It compares all active cloud-document references in the restored
database with the archive and verifies each QSP1 AES-GCM tag, decrypted byte count
and SHA-256. It records table/document counts, recovery-point age (RPO), restore
duration (RTO), and confirms that production containers/volumes were not touched.

CI runs both:
- `scripts/qa/backup-crypto.tests.ps1`: format, streaming archive, multi-chunk
  encryption/decryption, wrong passphrase, tampering and truncation tests.
- `scripts/qa/backup-restore-drill.tests.ps1`: creates synthetic PostgreSQL and
  encrypted-document inputs, builds a real encrypted bundle and invokes the same
  isolated restore-drill script used by operators.

The Windows Agent's local DPAPI-protected queue and printer state are not in the
Docker database/document backup. Shop-device recovery must preserve/re-enrol those
local queues using the separately documented Agent lifecycle procedures.

**Remaining BLK-06 evidence:** configure a real independent off-site storage
remote, execute a production-shaped backup with the owner-controlled passphrase,
verify the remote content, perform a scheduled restore drill from that remote copy,
and retain the evidence report with agreed RPO/RTO targets. CI synthetic restore is
necessary but not a substitute for this operational rehearsal.

## Operator commands

Create and verify an encrypted off-site backup:

```powershell
$env:SMARTPRINT_BACKUP_RCLONE_REMOTE = "remote:smartprint/backups"
pwsh ./scripts/docker-backup.ps1
```

Create a local encrypted bundle only (does not close the off-site gate):

```powershell
pwsh ./scripts/docker-backup.ps1 -AllowLocalOnly
```

Run a non-destructive isolated restore drill:

```powershell
pwsh ./scripts/docker-restore-drill.ps1 -BackupFile "D:\\backups\\SmartPrint-<timestamp>.spbackup"
```

A live restore is destructive and requires the explicit `-ConfirmRestore` switch
plus typing the requested confirmation phrase. Never use the restore script as a
routine health check; use the isolated restore-drill command instead.
