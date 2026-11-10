# Private Printing — no source-document retention

Private Printing is a customer-selected local-Agent retention mode. It is not an
anonymous-payment mode: the existing shop approval, quote, payment, refund and
job audit lifecycle remains in force.

## Customer promise

- The customer explicitly selects **Private Printing** before upload.
- The API creates only a local-Agent upload ticket; cloud document upload and
  HTTPS recovery are forbidden for that ticket.
- The browser encrypts the document with a fresh AES-256-GCM key. It wraps that
  key with the paired Agent's RSA-OAEP/SHA-256 public key, which is advertised
  to the customer only through the authenticated HTTPS API heartbeat/session.
- The paired Agent decrypts in memory, validates the original hash and document
  structure, then stores the file in the existing DPAPI-protected queue.
- The shop must use a physical printer. Managed PDF export, source preview,
  source download and shop-side photo-revision operations are blocked.
- Only after the server records confirmed physical output does the Agent remove
  the encrypted source file. The Agent retains only a minimal purge-pending
  manifest until the cloud acknowledges that source metadata was redacted.
- If output is uncertain, the source remains in the protected queue for safe
  reconciliation. Private mode never trades reliability for premature deletion.

## Metadata and limits

The print/payment/audit record remains available for reconciliation and legal
accounting. Private file names are replaced with "Private document"; after
confirmed output the API redacts source hash, byte count, MIME/file metadata,
storage key and page-size details. SmartPrint does not promise forensic erasure
of filesystem, SSD, Windows spooler, printer firmware, screenshots or external
copies created by another person.

The local Agent endpoint is still an HTTP endpoint on the shop LAN. For Private
Printing, document bytes and the per-upload AES key are protected end-to-end;
the wrapping public key comes from the authenticated API, not from an
unauthenticated local endpoint. Existing non-private LAN uploads are unchanged.
