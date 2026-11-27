# Printer onboarding and diagnostics — implementation audit

**Audit baseline:** `main` / `feature/unified-printer-onboarding` at `69d34714c75502666fa5c69c1f2aacb9e61c4a37`  
**Scope:** Wi-Fi/network, IPP/IPPS, USB and virtual printers. Bluetooth, BLE, thermal/label/portable and ESC/POS/ZPL/TSPL/CPCL are excluded.

## Executive finding

SmartPrint already has a mature Windows-queue discovery and print-execution foundation. It is not yet a cross-protocol printer discovery platform. The current product discovers queues installed in Windows, identifies a transport hint from the queue port, reads capabilities from the installed Windows driver, and sends output through the Windows spooler. The portal explicitly instructs an owner to add a printer in Windows Settings; there is no in-app discovery/installation of network IPP endpoints.

The safest next step is to extend the existing Windows queue pathway and its owner experience first. Do not add a second renderer, job queue, local database, print worker or printer classification enum. Native IPP discovery/direct submission requires a separate, evidence-backed phase because it is not present in the current call path.

## 1. Baseline and repository activity

- The feature branch was created from the current `main` SHA above; both refs were identical when the audit was rechecked.
- Main CI run `46026943046` was in progress at audit time. The latest earlier main push CI run `45991827541` completed successfully. Do not treat the separate MSIX validation failure on PR #128 as a printer-code failure; keep printer work away from MSIX packaging changes.
- Open printer-related work already exists:
  - PR #47 is a draft driver qualification matrix. Its hosted Windows fixture validates a Microsoft Print to PDF virtual driver and Windows spooler rendering, not a physical printer or an IPP endpoint. Its vendor-driver package execution remains gated.
  - PR #116 is a draft shop-floor acceptance checklist. It explicitly leaves Epson L360, USB, Wi-Fi/LAN, shared queue and Android IPP physical tests outstanding.
- Existing product work is tracked in issue #131. This audit is the code-level refinement of that scope, not a replacement for those acceptance gates.

## 2. End-to-end code trace

### Discovery on the Windows Agent

- `WindowsPrinterDiscovery.TryDiscoverMetadataAsync` runs queue enumeration in a disposable child process using WMI; if the result is null/empty it tries isolated WinSpool enumeration.
- Queue metadata is normalized by **Windows queue name**. Different queues sharing the same port are intentionally retained as separate queues.
- When a queue status is unknown, the Agent asks WinSpool for a fresher status and merges by queue name.
- Capability enrichment is separate from metadata publication. `WindowsPrinterCapabilityProbeRunner` executes driver probing in a disposable child process, has a two-probe concurrency limit, an eight-second default probe timeout (clamped 2–30 seconds), short success/unknown caching and failure backoff. A failed capability query leaves the queue visible with unknown capabilities.
- `Worker.SyncPrinterInventoryAsync` publishes queue metadata first to `api/v1/agent/printers/sync`; capability enrichment runs separately and refreshes queue metadata before publishing capability snapshots. A slow or unstable driver therefore does not hide unrelated printers.

### API persistence and classification

- `AgentPrinterSyncRules.Validate` bounds an inventory to 100 queues and validates queue, driver, port, status and capabilities payload lengths. Malformed individual entries are rejected without discarding valid entries.
- `Program.cs` persists the discovered queue under the authenticated tenant and paired Agent. The identity fingerprint is derived from Agent ID + normalized queue name + driver + port. A queue/driver/port/Agent identity change invalidates old virtual test grants.
- Connection type and output profile are separate dimensions and must remain so:
  - Connection types: `USB`, `LAN`, `IPP`, `WindowsQueue`.
  - Output profiles: `PHYSICAL`, `PDF_EXPORT`, `UNSUPPORTED_VIRTUAL`.
- The output profile does not become physical because a transport is IPP or USB. Virtual identity remains virtual and requires a non-production shop-scoped grant for the existing test-only spooler path.
- Printer records currently hold one Windows queue/port identity. The data model is not yet a logical-device-with-many-endpoints model.

### Capability reporting

- `WindowsPrinterCapabilityDiscovery` reads Windows `DeviceCapabilities` and Windows Print Schema through the installed driver. The payload identifies its source as `WINDOWS_DRIVER` and reports a discovery timestamp.
- Missing driver values are represented as unknown rather than unsupported.
- This is not a native IPP `Get-Printer-Attributes` exchange. An IPP/HTTP port label is not evidence that SmartPrint directly queried printer attributes, negotiated IPP formats, or handled IPPS certificate policy.

### Owner onboarding UI

- `PrintersPage` lists Windows queues reported by the paired Agent, shows the connection/output distinction and capabilities, and supports selection/replacement within the shop plan.
- Its current onboarding guidance tells the owner to connect over USB/network, use **Windows Settings → Printers & scanners → Add device**, install a manufacturer driver when Windows asks, and leave the Agent running.
- Therefore, current discovery is **Windows queue discovery**, not an in-app local-network printer search/installation experience.
- The existing local-network discovery code serves a different purpose: selecting the shop computer's private Wi-Fi/Ethernet interface for phone-to-Agent document upload on TCP 17842. It does not discover printers on that network.

### Print submission, reconciliation and output confirmation

- Customer documents follow the existing server-owned quote/review/payment/assignment flow. The Agent first secures the paid document in its DPAPI-protected local queue.
- Explicit dispatch is a separate API operation. `/jobs/print-claim` claims only jobs with verified payment, a valid Agent/queue relationship, a fresh usable physical queue (or the specific permitted test/PDF route), and a current dispatch lease.
- The Agent uses the existing isolated Windows print child. It prepares and validates the document/settings, then calls the server authorization callback immediately before `StartDoc`. A failed/expired authorization does not reach the spooler.
- `WindowsPrintExecutor` expands copies in software exactly once; the driver copy count stays at one. The current PrintJob contract and settings validation must remain authoritative.
- Before spooler acceptance a failure is blocked; after `StartDoc` a failure is uncertain. The existing reconciliation and retry guards prevent a blind duplicate print.
- Spooler acceptance/status is not treated as proof that paper physically exited. Shop-owner output confirmation remains a separate lifecycle step.

### Branding and virtual output

- `PdfBrandingOverlay` is an opt-in overlay for eligible customer PDFs, not a dedicated diagnostic template. It only modifies PDFs when the job explicitly enables branding and strict safety checks pass; signatures/forms and unsafe page layouts are preserved unchanged.
- Phase A now provides a dedicated one-page diagnostic generator and stable template version. It does not reuse the customer-PDF branding toggle or brand ordinary customer documents by default.
- Shop-scoped virtual test grants already bind tenant + printer + Agent + queue/driver/port fingerprint and are checked again at the pre-spooler boundary. Production rejects virtual test output. Preserve these invariants.
- `PDF_EXPORT` is an Agent-side protected file output, not a generic virtual printer and not a physical-output confirmation.
- OS-level inbound print capture (for example, a virtual printer that receives print data from other applications) is distinct from SmartPrint's outbound print/export path and is not implemented by the current virtual test-grant feature.

## 3. Support gap matrix

| Area | Current implementation | Gap / truthful boundary |
|---|---|---|
| USB physical | Windows-installed queue, driver capability probe, Windows spooler execution | No direct USB stack; model/driver/hardware combinations still need qualification |
| Wi-Fi / LAN physical | Works when Windows has installed a usable queue; port may be classified LAN | Wi-Fi radio/SSID does not define a print protocol; no in-app printer service browse or queue installation |
| IPP / IPPS | IPP-like Windows queue ports are classified as IPP; print uses the Windows spooler/installed queue | No direct IPP attribute query, format negotiation, TLS policy or driverless adapter contract |
| Virtual output | Windows queues retain `UNSUPPORTED_VIRTUAL`; non-production shop-scoped grant can exercise the shared spooler path; PDF export is separate | No general production virtual-printer support; no OS inbound print-capture workflow |
| Diagnostic test | Dedicated one-copy diagnostic job purpose, branded template, attempt history, Agent/spooler reconciliation and owner verification are implemented on installed Windows queues | Real physical paper-output qualification remains device/driver-specific; Bluetooth/BLE and native protocol discovery are out of scope |

## 4. Architecture decisions

1. **Preserve the existing two-dimensional model.** Connection type describes how Windows reaches a queue; output profile describes what SmartPrint is allowed to do with it.
2. **Keep the Windows spooler as the first adapter.** USB and network printers already installed in Windows remain on the existing queue, capability-probe, renderer, isolated-executor and reconciliation path.
3. **Treat Wi-Fi as a network medium, not a protocol.** Discover only protocol services that can be meaningfully identified; do not scan broad subnets or infer printability from a reachable IP address.
4. **Do not equate IPP labels with native IPP support.** First verify Windows IPP Class Driver/queue behaviour on supported Windows versions and endpoints. Add native IPP/IPPS only behind a protocol adapter with bounded discovery, printer-attribute parsing, supported-format negotiation, strict endpoint validation, certificate/hostname validation and submission/reconciliation tests.
5. **Do not silently merge queues.** Queue name alone is not a global device ID. A future multi-endpoint model must retain endpoint identity and matching confidence and must not merge same-named devices across tenant/Agent boundaries.
6. **Create diagnostics as a first-class purpose, not a fake customer order.** Do not mint a zero-value verified payment or synthetic customer settlement record. The diagnostic should have a separate audit/history record while reusing the same Agent renderer, isolated spooler executor, pre-`StartDoc` authorization, receipt/reconciliation and owner confirmation safety contracts.
7. **Use the repository's official SmartPrint branding assets.** Keep the diagnostic template independent from optional customer PDF branding and print legibly in monochrome.
8. **Keep evidence tiers explicit.** Unit/CI spooler tests, Windows virtual fixtures, installed IPP queue tests and physical paper-output tests are separate evidence levels.

## 5. Implementation sequence

### Phase A — diagnostic vertical slice on installed Windows queues

<badge>Implemented in PR #132; physical hardware qualification remains open</badge>

The Phase A code path is implemented as an owner-initiated, one-copy diagnostic on an already installed Windows queue. It adds a separate `PrinterDiagnosticAttempt` audit record and API start/confirm endpoints; produces a branded, versioned one-page PDF; uses the existing protected Agent queue, isolated Windows worker, and authorization immediately before `StartDoc`; records spooler evidence separately from the owner's physical-output decision; and excludes diagnostics from customer financial fulfilment, charging and monthly print-job usage.

Physical and non-production virtual output stay distinct. A virtual test can establish that its output artifact was received, but is never described as physical paper confirmation. A spooler job disappearing is uncertainty, not proof that nothing was printed. A completed `PRINTED` observation is retained if Windows later stops listing the job, and duplicate starts/confirmations are idempotent.

Focused policy, PDF-renderer and endpoint integration tests cover conservative capabilities, one-copy artifact generation, offline idempotent retry, tenant/owner authorization and concurrent owner confirmation. The Windows CI fixture and these tests validate code and spooler integration; they do not replace a real shop-floor sample from a named physical printer, driver and Windows build.

- Define a durable diagnostic attempt/state contract separate from customer financial orders.
- Generate a branded one-page diagnostic artifact from the official logo/brand assets, with stable template version and printer/endpoint/capability metadata.
- Enforce exactly one copy at the API and Agent boundaries; never honour a client-supplied copy count for diagnostics.
- Reuse the existing protected Agent queue and isolated Windows spooler executor, including server authorization immediately before spooler submission.
- Record spooler/Agent receipt and status observations, but do not call them physical proof.
- Ask the shop owner to confirm **printed correctly**, **printed with an issue**, or **nothing printed**. Keep a diagnostic failed/unconfirmed state separate from production order fulfilment.
- Add focused policy, endpoint, renderer/template, idempotency, retry/uncertain-state and API tenant-isolation tests.

### Phase B — richer discovery and endpoint identity

- Add bounded local service discovery only after selecting and validating the supported Windows implementation/permissions.
- Keep endpoint observations separate from the currently selected Windows queue until the owner explicitly connects/installs or chooses an endpoint.
- Preserve distinct endpoints for one logical device; do not collapse different physical printers due to display-name similarity.
- Record discovery source, endpoint freshness and evidence/confidence.

### Phase C — IPP/IPPS and network capability

- Validate Windows IPP Class Driver support against exact Windows build and real IPP/IPPS endpoints.
- Implement direct IPP/IPPS only if the shared adapter contract and reliable spooler-equivalent job correlation/reconciliation can be met.
- Validate IPP attributes and supported document formats; do not assume PDF, colour, duplex, trays or media options.
- Enforce TLS hostname/certificate validation; do not silently downgrade IPPS or bypass validation.

### Phase D — USB and virtual qualification

- Qualify actual USB driver/device pairs; keep installed Windows queue as the preferred route.
- Evaluate inbound virtual-printer capture separately from PDF export and outbound virtual test routing. Review Windows service/MSIX packaging constraints before implementation.
- Extend the existing PR #47 qualification registry and PR #116 shop-floor matrix; do not mark hosted virtual fixtures as physical qualification.

## 6. CI and physical acceptance gates

- Keep the current main CI baseline green; compare feature-branch checks against the same jobs rather than conflating PR #128's MSIX validation.
- Run Agent and API unit tests after each focused code slice; run portal typecheck/build when UI contracts change.
- Exercise the production Agent spooler path on a disposable Windows runner with a file-backed/Microsoft Print to PDF fixture. The output is virtual fixture evidence only.
- Require owner shop-floor evidence for USB and Wi-Fi/LAN devices, and real IPP/IPPS endpoint evidence for IPP claims.
- For each hardware claim, record Windows version/build, model, driver/version, firmware where available, connection/port, media/settings, Agent version, printed sample and observed result.
- Keep any untested combinations as **not yet qualified**, not "supported".

## 7. Explicit non-goals for this branch

Bluetooth/BLE, thermal/label/portable printers and ESC/POS/ZPL/TSPL/CPCL; broad subnet scans; vendor-driver downloads or installation; replacing a shop's existing Windows driver/default printer; universal compatibility claims; a second rendering/dispatch/queue service; or changes to the MSIX release pipeline unrelated to the printer product.
