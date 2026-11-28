# Android Native Printer Management

## Status

Implementation design for the additive Android shop-owner printer-management capability. The existing Shop Owner portal and Windows Agent printer onboarding/execution paths remain unchanged.

## Product boundary

- Add a separate shop-owner entry point in the native SmartPrint Android app.
- Keep customer QR scanning, document library, customer print checkout, and customer print-job lifecycle unchanged.
- Keep the existing Shop Owner portal printer workflow unchanged.
- Keep the Windows Agent as an independent, supported execution path.
- Exclude Bluetooth and thermal printers.
- Do not claim universal compatibility. Publish compatibility only for tested printer/transport/protocol combinations.

## Recommended technology

1. Android `NsdManager` for DNS-SD/mDNS discovery of `_ipp._tcp` and `_ipps._tcp` endpoints. Use lifecycle-bound discovery, bounded scan windows, network-change handling, and permission-aware diagnostics.
2. HP Inc. JIPP (`com.hp.jipp:jipp-core`) as the candidate IPP packet codec/client foundation, subject to dependency, license, Android compatibility, maintenance and API review before adoption. Evaluate `jipp-pdl` only if document conversion is needed.
3. Printer Working Group IPP Everywhere (PWG 5100.14) as the network-printer interoperability baseline: query real printer attributes, supported document formats, media, color, duplex and job state; never infer capabilities from model names.
4. Android Print Framework `PrintService` as a system-level integration adapter where it improves native Android print entry points. It is not a replacement for the in-app shop-owner printer manager or its shop registration flow.
5. Android USB Host APIs as a separately permissioned adapter for USB printers. USB detection alone is not evidence that a device can accept a supported print language. Start with IPP-USB where the device advertises it; any additional USB class/transport requires explicit protocol support and tests.
6. Keep transport, discovery, capability probing, document rendering, job submission, and shop registration behind separate interfaces so future protocols do not leak into UI or cloud contracts.

## Compatibility policy

- Tier 1: IPP Everywhere / IPPS over Wi-Fi or Ethernet with DNS-SD discovery.
- Tier 2: manually added IPP/IPPS endpoint where DNS-SD is unavailable.
- Tier 3: USB IPP-USB devices, after device permission and protocol verification.
- Tier 4: other documented network/USB printer protocols only through separately tested adapters.
- Vendor-specific protocols that require closed SDKs, credentials, or licensing remain unsupported until rights and test hardware are available.
- Bluetooth and thermal printers are explicitly excluded.

A printer can be discovered but remain `Not yet verified` until capability negotiation and a controlled test print succeed. A print submission acknowledgement must not be displayed as confirmed physical output.

## Identity and backend integration

- Reuse existing shop tenant identity and printer registry contracts where possible; do not create a second printer inventory.
- Android-discovered devices must be deduplicated against Windows-discovered devices using stable protocol identifiers (IPP `printer-uuid` / device UUID when available), normalized endpoint identity, and shop scope. MAC address is optional evidence only, not the sole identity.
- Registration must be authenticated and authorized by the shop owner, tenant-scoped, auditable, idempotent, and revocable.
- Discovery results remain local until an explicit owner action registers a printer.
- Keep Android device identity distinct from physical printer identity and from the Windows Agent identity.
- Preserve Windows Agent authority and current execution path; Android printing is an additional execution origin with explicit capability and health reporting.
- Do not allow a customer session or QR station token to register or manage printers.

## UX acceptance

- Dedicated `Printers` area with clear separation from customer `Scan & Print`.
- One primary `Find printers` action; discovery progress, elapsed time, stop/rescan, and actionable permission/network guidance.
- Results grouped by `Ready to connect`, `Needs setup`, and `Unsupported`, with model, connection type, protocol, address disclosure only where useful, and confidence/verification status.
- Printer details show capability source and last-checked time; unsupported settings are hidden or disabled with a concise explanation.
- Manual IPP/IPPS add flow with validation and a safe connection test.
- Explicit `Register to this shop` confirmation; explain duplicate matches and existing Windows registration before creating anything.
- Guided one-copy diagnostic print with preview, explicit confirmation, cancellation, progress, and truthful outcomes (`Accepted`, `Printing`, `Completed/verified` only when supported, `Unknown`).
- Accessible light/dark themes, large touch targets, TalkBack labels, responsive small-screen layout, and no blocking full-screen spinner.
- No silent network scanning in background; scanning is user initiated and foreground scoped.

## Required test programme

- Unit tests: DNS-SD service normalization, IPP attribute parsing, capability normalization, duplicate identity matching, endpoint validation, state machine and unsupported-feature policy.
- Protocol tests: IPP Get-Printer-Attributes, Validate-Job, Print-Job, Get-Job-Attributes, IPPS certificate validation, timeouts, malformed packets, redirects, partial responses, and printer disappearance mid-job.
- Discovery tests: mDNS found/lost, duplicate advertisements, multi-interface/network changes, Wi-Fi multicast behaviour, no mDNS, manually entered endpoint, permission denied and Android local-network permission changes.
- USB tests: device attach/detach, permission denial/grant, unsupported class/protocol, device reset and USB IPP where available.
- Security tests: private/local address validation, SSRF-resistant endpoint policy, DNS rebinding/re-resolution, TLS certificate validation, auth secret storage, tenant isolation, replay/idempotency, and no credential/document leakage in logs.
- Document tests: PDF, JPEG, PNG, supported raster/PCLm formats, large files, multi-page, copies, color, duplex, paper sizes, rotation, scaling and memory pressure.
- UI tests: loading/empty/error/offline states, permission education, discovery cancellation, manual add, duplicate warning, capability editing, test-print confirmation, dark/light themes, accessibility, rotation and process recreation.
- Hardware matrix: representative printers from HP, Canon, Epson, Brother, Xerox and other locally common brands, including both IPP-capable and legacy devices; network and supported USB cases. Record exact model, firmware, Android version, router/network conditions and results.

## Rollout gates

1. Complete codebase/API/auth audit and dependency/license review.
2. Implement discovery and protocol capability probing behind testable interfaces.
3. Implement shop-owner authentication and explicit shop-scoped registration without touching existing portal flows.
4. Implement document preparation and real print submission, with idempotent job tracking and safe unknown-outcome handling.
5. Deliver the native printer-management UX and accessibility coverage.
6. Run JVM, instrumentation, emulator, virtual-printer and physical-printer matrix.
7. Ship only verified protocol/model combinations; retain feature-level kill switch and existing Windows path.

## Known limitation

No software stack can guarantee printing to every printer ever manufactured. Compatibility depends on the printer exposing an interoperable protocol or a legally usable vendor adapter, supported document language, network/USB topology, firmware, and Android device capabilities. The product must communicate this honestly rather than marking every discovered device as supported.
