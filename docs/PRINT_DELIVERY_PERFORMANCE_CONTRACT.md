# Print-delivery performance and reliability contract

This contract applies to every security, storage, payment, queue, Agent, and backup change. Security hardening is not accepted if it makes the normal customer-to-printer journey slower, less predictable, or less recoverable.

## User-visible invariants

- A customer must always receive an explicit state for upload validation, payment, shop acceptance, queueing, printer handoff, and completion. A pending operation must not be represented by an indefinite spinner.
- A shop operator must be able to distinguish a queued job, a job being prepared, a printer-offline job, a submitted job, and output that is not yet confirmed. Every blocked state must have a reason and a safe next action.
- A paid job must remain durably recoverable across API, Agent, process, and temporary network restarts.
- Retrying a payment callback, queue delivery, Agent claim, or acknowledgement must be idempotent. It must not create another charge or silently print a second copy.
- Physical completion must never be inferred solely from API acceptance or Windows spooler acceptance.

## Critical-path boundaries

- Keep payment confirmation, durable job state transition, and dispatch eligibility small and bounded. Do not run backups, malware signature updates, report generation, or unrelated maintenance in this path.
- Do not run unbounded document parsing, image decoding, malware scanning, or archive expansion on the API request thread or printer-dispatch loop.
- A document must not become printable until its required validation and malware checks have completed successfully. Moving work to a background worker must not weaken this gate.
- Security processing may be asynchronous from the customer's perspective only when the product exposes a durable, recoverable processing state and the printer cannot access the document prematurely.
- Use bounded queues, bounded memory, explicit CPU/time/page/pixel/decompression limits, and per-tenant concurrency controls. A backlog in one tenant must not starve unrelated shops.
- Keep printer dispatch independent from scanning, backup, and document-conversion worker pools. Apply backpressure to upload admission rather than exhausting resources used by active print jobs.
- Preserve streaming encryption and bounded-memory file transfer. Do not solve latency or parser isolation by buffering whole uploads in API memory.

## Document retention policy

- Standard completed customer documents and their local Agent source copies are retained for 72 hours (3 days) after confirmed output, then securely deleted by their respective retention workers.
- Upload policy: maximum 50 MiB per file, 20 files per batch, and 500 MiB aggregate per batch. The 500 MiB batch is a logical group of individual file uploads, not one 500 MiB multipart request; each file request is independently capped (including multipart overhead).
- Ordinary JPEG/PNG validation checks file signature, dimensions and bounded container framing only; it does not inflate/decode raster pixels. The Windows print driver performs raster decoding at print time. PDF page count and page geometry remain validated; embedded PDF image geometry is inspected only for Photo Sheet/Photo Studio workflows that require it.
- QSP2 private relay and normal document storage use bounded streaming. Legacy QSP1 private relay remains a compatibility fallback and still has bounded-size whole-payload buffering/decryption; do not mistake that legacy path for the steady-state resource profile.
- Private Printing source documents are purged immediately after confirmed physical output and successful local purge acknowledgement. If output is uncertain or the Agent acknowledgement is unavailable, retain the protected source only as long as needed for safe reconciliation; never delete the only recoverable copy prematurely.
- Keep financial records, invoices, payment/refund history, job status, and required audit metadata independently of document-source retention. Retention must not erase evidence needed for payment reconciliation or legal/accounting obligations.
- Retention sweeps are bounded and must not compete with active print dispatch.

## Failure and recovery behavior

- A temporary scanner, parser, cloud, LAN, or Agent outage must produce a visible retryable or operator-actionable state; it must not silently drop a job.
- Retries must use stable job/document identities and idempotency keys. Expired Agent leases may be reclaimed only through the existing conditional state transition rules.
- If printer output is uncertain, do not automatically resubmit. Show output-unconfirmed status and require the supported reconciliation or explicit duplicate-risk confirmation.
- A printer being offline must not block unrelated printer lanes or other shops.
- Background backups and retention sweeps must use separate resource budgets and must not contend unboundedly with active printing.

## Required verification before merge

- Record baseline and post-change p50/p95 timings for upload validation, payment confirmation, queue visibility, Agent claim, and printer handoff using the same fixture and environment. Report the payload size, page count, concurrency, and hardware/runtime profile.
- Demonstrate that concurrent uploads and background scanning do not starve dispatch or heartbeat loops.
- Exercise API restart, Agent restart, LAN interruption, scanner/parser timeout, printer offline, expired lease, duplicate webhook, and duplicate dispatch/acknowledgement scenarios.
- Prove that a rejected or still-scanning document cannot be claimed or printed.
- Prove that one tenant's upload/scan backlog does not prevent another tenant's paid job from reaching its Agent.
- Keep the normal CI suite green and add targeted regression tests for every changed state transition.
- Do not claim a latency improvement without measured before/after results. Do not claim a print is complete without a physical-output signal.

## Rollout rule

Changes must be introduced behind compatible state transitions and safe defaults. Keep production deployment and the main branch unchanged until tests, review, and staged operational verification are complete. If measurements show a regression in the normal print path, stop rollout and correct the design rather than accepting slower printing as the cost of security.

## Machine-resource budget and smoothness priority

Customer and shop-owner smoothness is the top product priority. Keep the shop-side footprint small enough for ordinary Windows shop PCs and modest networks; do not assume dedicated high-memory servers or GPU hardware.

- Prefer one lightweight, bounded worker process or an existing service over introducing several always-on daemons. Do not add Redis, Kafka, Elasticsearch, a separate database, or a heavyweight scanner stack unless measured requirements prove they are necessary.
- Reuse existing API, Agent, storage, and queue infrastructure where safe. Avoid parallel pipelines and duplicate copies of documents.
- Use streaming I/O, bounded buffers, backpressure, and explicit per-process concurrency limits. Never buffer an entire upload in RAM merely to scan or parse it.
- Keep CPU-intensive scanning, rendering, OCR, conversion, and thumbnail work off the Agent heartbeat and printer-dispatch threads. Run them with low priority and bounded concurrency; pause or throttle them when printing is active or the machine is under pressure.
- Prefer remote/cloud scanning or parsing when an approved service already exists and the privacy, encryption, latency, and availability requirements are met. Do not make a new cloud dependency mandatory for basic local printing without a documented fallback.
- Avoid polling loops and frequent status writes. Prefer event-driven wakeups, bounded retry backoff, and compact status updates.
- Set measurable RAM, CPU, disk, and queue limits for API and Agent processes. Expose health/queue depth and resource pressure without high-frequency telemetry.
- When a shop machine is resource constrained, prioritize active print jobs and Agent connectivity; defer non-urgent previews, indexing, backups, and maintenance.
- If capacity is exhausted, reject or defer new work with a clear explanation and retry estimate rather than letting the entire application freeze.

## Resource and experience acceptance tests

- Measure idle and active CPU, resident memory, peak memory during a maximum-size upload, disk I/O, and network use on a documented modest shop-PC profile.
- Run simultaneous print dispatch while uploads are being scanned/parsed and while backup/maintenance is active; prove printer dispatch and Agent heartbeat latency remain within agreed budgets.
- Test low-memory and high-CPU conditions. The UI must remain responsive, queued work must remain durable, and background work must throttle or pause safely.
- Compare customer-visible time-to-queue and shop-visible time-to-first-action before and after changes. Report p50/p95, not only averages.
- Do not introduce a new always-on service or infrastructure dependency without a measured resource budget, failure-mode review, and explicit justification.
