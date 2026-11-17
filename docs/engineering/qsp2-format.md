# QSP2 authenticated streaming document format

Status: implementation foundation; not enabled for production storage yet.

## Scope

QSP2 replaces whole-file encryption with independently authenticated records. The data-key envelope is deliberately outside the encrypted record body so an authorised service can re-wrap the small data key for another approved recipient without decrypting or rewriting document records.

The caller owns temporary-file creation, key generation, key wrapping, atomic publication and cleanup. A writer or reader must never publish a partial object as a completed document.

## Cryptographic parameters

- Data key: 32 random bytes per document.
- KDF: HKDF-SHA-256, with a fresh 32-byte random salt per document.
- Record cipher: AES-256-GCM, 128-bit authentication tag.
- Default record size: 1 MiB; supported range: 64 KiB to 8 MiB.
- Nonce: 4-byte random per-file prefix followed by an unsigned 64-bit big-endian record index.
- Header authentication: HMAC-SHA-256 using a separately derived header key.
- Whole-document digest: incremental SHA-256.
- Compression: disabled.

The writer generates a fresh salt and nonce prefix. The caller must generate a new data key for each new document encryption. Reusing a key is not a supported operation.

## Key derivation

Derive two independent 32-byte keys from the document data key and the header salt:

- HKDF-SHA-256 with info SmartPrint.QSP2.HeaderKey.v1.
- HKDF-SHA-256 with info SmartPrint.QSP2.RecordKey.v1.

The content records do not contain the wrapped data key. Cloud key wrapping, RSA-OAEP wrapping for a paired Agent, and Windows DPAPI wrapping are external envelope responsibilities. Key-wrap metadata must be stored with the authorised document metadata and versioned independently from the content records.

## Binary layout

All integers are big-endian as described below. GUIDs use RFC 4122/network byte order, not the legacy .NET mixed-endian byte layout.

### Fixed 100-byte content header

| Offset | Length | Field |
|---:|---:|---|
| 0 | 4 | ASCII QSP2 |
| 4 | 1 | Format version (2) |
| 5 | 1 | Cipher suite (1 = AES-256-GCM) |
| 6 | 2 | Flags (must be zero) |
| 8 | 4 | Header length (100) |
| 12 | 8 | Positive plaintext length, signed int64 |
| 20 | 4 | Record size, uint32 |
| 24 | 8 | Record count, uint64 |
| 32 | 32 | Random HKDF salt |
| 64 | 4 | Random nonce prefix |
| 68 | 16 | Tenant GUID, RFC 4122 order |
| 84 | 16 | Document GUID, RFC 4122 order |

The 100-byte header is followed by a 32-byte HMAC-SHA-256 header authenticator.

### Data records

Records appear in ascending contiguous index order. Each record is:

| Length | Field |
|---:|---|
| 4 | Plaintext length, uint32 |
| Same as plaintext length | AES-GCM ciphertext |
| 16 | AES-GCM tag |

The nonce is derived from the header prefix and record index. The record AAD is:

SHA256(header) || 0x01 || uint64(recordIndex) || uint32(plaintextLength)

All records except the last contain exactly the configured record size. The final record contains the remaining positive byte count.

### 68-byte final record

The final record consists of a 52-byte authenticated core followed by a 16-byte AES-GCM tag:

| Offset | Length | Field |
|---:|---:|---|
| 0 | 4 | ASCII QEND |
| 4 | 8 | Record count, uint64 |
| 12 | 8 | Plaintext length, int64 |
| 20 | 32 | Final SHA-256 digest |
| 52 | 16 | AES-GCM authentication tag |

The final-record nonce uses the next record index (recordCount). Its AAD is SHA256(header) || 0x02 || finalRecordCore; ciphertext and plaintext are empty.

## Reader and writer invariants

1. Check expected tenant and document identities before accepting plaintext.
2. Validate header HMAC before reading or decrypting any record.
3. Check record order, exact record length and GCM tag for every record.
4. Validate the final record's count and length and compare its digest to the incremental SHA-256.
5. Reject truncated and trailing data.
6. Do not acknowledge or publish an object until the writer/reader completes successfully.
7. Decryption output is a caller-owned staging stream. If decryption fails at a later record or at the final digest, discard that staging output.
8. The caller must protect and persist the data-key envelope separately. Do not log or persist raw keys.

## Nonce and retry safety

The writer generates a new random salt and nonce prefix. The nonce for a given record is the four-byte prefix followed by its big-endian record index. The authenticated header fixes the prefix and file identity.

A retry must retransmit the exact previously encrypted bytes or query durable receiver state before creating ciphertext. Never re-encrypt different plaintext under the same data key, salt and record index. A new encryption session must use a new data key, salt and nonce prefix.

## Compatibility and rollout

QSP1 remains readable. New production writes are not enabled by this foundation alone. Before rollout, add versioned key-envelope persistence, QSP2 storage/read integration in the API and Agent, browser interoperability tests, migration coverage and performance benchmarks. A reader for both QSP1 and QSP2 must be deployed before any QSP2 document is written in production.
