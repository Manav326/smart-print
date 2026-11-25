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