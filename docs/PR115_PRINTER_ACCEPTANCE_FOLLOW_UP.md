# PR #115 — Printer isolation follow-up acceptance

**Parent change:** PR #115, merged to `main` as commit `8faed192162725fa5f70bd0a32b3d298a00f6bb1`.

**Status:** Follow-up acceptance is pending. A green CI run proves the checked build and automated scenarios for that commit; it does not certify a shop's installed printer drivers or physical paper output.

This checklist keeps the deferred qualification work explicit. Record each result against the exact candidate SHA and retain sanitised logs, screenshots/video, spooler observations and labelled physical samples. Use synthetic documents and a disposable QA shop/computer. Do not change or reset a shop's existing printer configuration, pairing, queue or customer data during qualification.

## 1. Windows restart and isolated discovery

- [ ] Reboot the Windows shop computer and verify the paired Agent service starts under the configured LocalService identity without manual repair or re-pairing.
- [ ] Confirm all installed Windows queues are discovered and displayed independently of whether the shop has selected them.
- [ ] With a plan quota smaller than the discovered queue count, select up to the quota and replace any selected printer with another eligible discovered queue. Verify the quota is respected without hiding the other queues.
- [ ] Confirm a slow, crashing or unsupported driver's capability probe cannot delay or remove unrelated queue inventory.
- [ ] Change one queue's driver and confirm only that queue's stale capability snapshot is invalidated; a metadata-only refresh with the same driver preserves its last-known-good snapshot.
- [ ] Disconnect or pause one queue while another selected queue is processing. Verify the healthy queue continues and per-queue FIFO is preserved.
- [ ] Restart the Agent before spooler submission and during submission. Verify durable recovery and that uncertain output is never blindly resubmitted.

## 2. Shop-owner printer replacement and customer continuity

- [ ] Replace a selected printer with another discovered, ready printer from the shop UI. The owner must not need to delete/re-register the shop, customer, station, documents or payment records.
- [ ] Verify QR station defaults and eligible, not-yet-claimed orders follow the documented replacement policy. Do not silently redirect work already claimed or showing spooler evidence.
- [ ] Confirm private/local-only documents remain on their originating Agent and are not silently moved to a different computer.
- [ ] Confirm waiting files, customer history, quotes, payment records and completed output records remain intact after replacement and Agent restart.
- [ ] Verify readiness is consistent in printer selection, QR station defaults, order assignment and availability summaries, including stale Agent/queue heartbeats and Ready/Busy/Offline states.

## 3. Required private 20-file interruption scenario

Use one paid, authorised private batch with 20 separate synthetic documents and a named physical QA printer. Disconnect the printer after exactly eight documents have been physically verified as printed.

- [ ] The eight completed documents are never resubmitted.
- [ ] The other twelve documents still retained by the original Windows queue resume through that same queue after reconnection, without creating duplicate spooler submissions.
- [ ] For a spooler entry that disappeared, the normal retry path remains blocked. The owner can use the explicit recovery action only after checking the original queue and paper tray and confirming the API's fresh queue-absence evidence.
- [ ] The recovery API accepts only a selected file with known total pages, exactly zero pages printed, fresh verified absence, verified payment, an online/ready original Agent and queue, and the durable protected source on that original Agent.
- [ ] Each explicitly re-authorised file receives a new spooler document name; its protected source is reused and is not uploaded again.
- [ ] Any file with partial page progress stays `OUTPUT_UNCONFIRMED`; whole-file retry and normal completion remain blocked.
- [ ] A file with unknown page count or uncertain paper output cannot enter the zero-page recovery path.
- [ ] Repeat with an Agent restart around spooler submission and verify no duplicate physical output.

Record the per-file expected/observed result for all 20 files, page counts, spooler IDs/names, timestamps, queue and Agent identity, and physical-paper inspection. A browser simulation or virtual queue alone is not a pass for this physical interruption scenario.

## 4. Named hardware and connection qualification

For every configuration claimed as supported, record the exact Windows version, printer model, driver name/version, connection/port, firmware, paper/tray and output settings.

- [ ] Epson L360 with the supplied/official driver: install in an isolated QA environment, discover capabilities, assign from the shop-owner UI, print a controlled document, disconnect/reconnect and verify output.
- [ ] USB-connected physical printer: discovery, selection, printing, restart and reconnection.
- [ ] Wi-Fi/LAN-connected physical printer: discovery, selection, printing, restart and reconnection.
- [ ] Windows shared queue: verify the actual LocalService access and documented credential/permission boundaries.
- [ ] Android IPP queue: verify discovery and physical output from a real device/queue; emulator success is not physical qualification.
- [ ] Multiple physical queues: verify independent progress, per-queue FIFO, and that a failed queue does not block a healthy one.
- [ ] Capture actual printed pages and verify page count, orientation, copies, colour/mono and duplex where the selected driver reports those capabilities.

Do not generalise the result to untested printer models or drivers. SmartPrint must not install, replace, rename or reconfigure a shop's vendor driver, Windows port or default printer as part of these tests.

## 5. Truthful dispatch and output status

- [ ] After authorisation but before Agent receipt, customer and shop see **Waiting for shop computer**.
- [ ] During Windows submission, they see **Sending to printer**.
- [ ] Spooler acceptance is not presented as proof that paper physically exited.
- [ ] Printing/collection-ready/completed status is shown only from the corresponding observed and owner-confirmed lifecycle state.
- [ ] A worker failure before the pre-`StartDoc` boundary is handled as blocked; a failure after that boundary is treated as uncertain and does not trigger automatic duplicate submission.

## 6. Known deferred product workflow

Page-by-page continuation or partial settlement for a document where some pages printed and others did not is **not implemented by PR #115**. Until designed, implemented and separately tested:

- Keep partial-page jobs in `OUTPUT_UNCONFIRMED`.
- Do not offer whole-file retry for any file with pages printed.
- Do not mark the file complete or settle it as fully printed based only on queue absence.
- Resolve only through a separately designed, auditable page-level continuation/settlement workflow or explicit physical inspection policy.

## Evidence and sign-off

For each case record: candidate commit SHA, Windows build, Agent version and service identity, shop/Agent/queue identifiers (redacted where needed), printer and driver versions, connection, start/end time, exact steps, expected/observed result, spooler evidence, physical-paper evidence, logs and defect/retest link.

A case is **PASS** only when the expected result was observed and its evidence retained. Mark unavailable hardware cases **BLOCKED/NOT RUN**, never PASS. Do not treat this checklist or a green CI run as production sign-off; complete the existing master QA plan and final pre-production release gate before any production compatibility claim.
