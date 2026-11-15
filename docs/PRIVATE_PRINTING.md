# Private Printing — source protection and retention controls

Private Printing is a customer-selected local-Agent processing mode. It is not an
anonymous-payment mode: the existing shop approval, quote, payment, refund and
job-audit lifecycle remains in force.

## Customer-visible behaviour

The customer and shop owner use the existing upload, quote, approval, payment and
print workflow. Security controls run inside the browser, API and Agent; they do
not add a second upload, extra confirmation, or a separate print screen.

## Source-document protection

- The customer selects **Private Printing** before upload.
- The API creates a local-Agent upload ticket. Cloud document upload and HTTPS
  recovery are forbidden for that ticket.
- The browser encrypts the document with a fresh AES-256-GCM key and wraps that
  key with the paired Agent's RSA-OAEP/SHA-256 public key. The public key is
  supplied through the authenticated API heartbeat/session.
- The paired Agent decrypts in memory, validates the original hash and document
  structure, then stores the encrypted queue copy. Production queue files and
  the Agent's private document key use DPAPI LocalMachine protection, with the
  queue and key directories ACL-restricted to SYSTEM. The DPAPI scope alone is
  not user-isolated: the NTFS ACL is what prevents ordinary interactive accounts
  from reading the protected files through normal Windows permissions.
- This is defence in depth, not protection against a local administrator who
  can take ownership, execute as SYSTEM, inspect process memory, or modify the
  OS. The Agent remains compatible with its existing LocalSystem service
  identity and LocalMachine-protected queue data.
- Managed PDF export, source preview, source download and shop-side photo
  revision operations are blocked for Private Printing.
- After confirmed physical output, the Agent removes its encrypted source and
  retained source sidecars, then asks the API to redact the matching source
  metadata. Once the API acknowledges that redaction, the normal physical-job
  reconciliation manifest is removed from the Agent.
- Expired staged and terminal pending-payment files are removed by the local
  cleanup worker. Cleanup attempts overwrite-before-delete for managed queue
  files, but this is not a guarantee of forensic erasure on SSDs, snapshots,
  backups or other storage layers.
- The Agent detects Windows jobs marked as retained and attempts to delete
  those retained private jobs. It verifies that the job is no longer visible in
  the Windows queue and retries deletion in the background if necessary. This
  does not prove that a third-party driver or printer firmware has not retained
  another copy.

## Metadata and limits

The print/payment/audit record remains available for reconciliation and legal
accounting. Private file names are replaced with "Private document"; after
confirmed output the API redacts source hash, byte count, MIME/file metadata,
storage key and page-size details. Minimal job, payment and audit metadata remains
as required for order processing and reconciliation.

The paired Windows computer and printer must process readable document data to
produce paper output. A shop owner or other person with local administrator or
SYSTEM-level control can potentially capture that data while it is being
processed, change Windows or printer configuration, or intercept the print
pipeline. The application cannot guarantee protection against such a privileged
local operator. Arbitrary printer drivers and firmware may also retain data,
and no software can prevent someone from keeping, photographing or scanning the
physical paper.

The local Agent API remains available on the shop LAN. Private document bytes
and the per-upload AES key are protected end-to-end; the wrapping public key
comes from the authenticated API, not from an unauthenticated local endpoint.
Existing non-private LAN uploads and the normal customer/shop workflow are
unchanged.
