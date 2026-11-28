# Android Native Printer Management

## Status

Product and implementation design for additive Android self-service printing for eligible customers and shop owners. This document is a design baseline; it does not claim that the feature is implemented.

## Product boundary

- Eligible customers who have purchased an applicable plan can independently discover, connect, configure, test, and print to supported printers.
- Shop owners retain their existing printer-management and printing workflow; the Android capability must not replace or regress it.
- Customer self-service printer setup and shop-owned printer management are separate experiences with separate authorization and ownership boundaries.
- Keep customer QR scanning, document library, checkout, and print-job lifecycle intact unless a deliberate integration is required and regression-tested.
- Do not make customer printer setup dependent on a shop owner.
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

- Reuse existing account, plan-entitlement, shop tenant, printer registry, and print-job contracts where appropriate; audit before changing them.
- Model customer-owned/saved printer associations separately from shop-owned printer registrations. Do not create a parallel physical-device inventory or job system.
- Customer registration is authenticated, customer-scoped, server-entitlement-checked, auditable, idempotent, and revocable. Shop registration remains shop-tenant-scoped and role-authorized.
- A physical printer may have separate authorized customer and shop associations. A matching IPP UUID or endpoint must never expose another account's metadata, credentials, or jobs.
- Discovery remains local until the user explicitly saves/connects the printer.
- Derive principal, ownership, and plan entitlement from authenticated server-side context; never trust client-supplied IDs or plan status.
- Keep Android device identity distinct from physical printer identity and from Windows Agent identity. Preserve the existing Windows Agent execution path.
- Customer/QR sessions must not manage shop inventory; anonymous or expired-plan sessions must not bypass paid printing entitlement.
- Protect stored credentials with platform-backed secure storage and redact credentials/documents from logs.

## UX acceptance

- A customer-facing `My Printers` area, clearly separate from `Scan & Print` and shop-owner administration.
- Check plan eligibility before enabling customer setup/printing; ineligible customers receive a clear upgrade/renewal path.
- One primary `Find printers` action; discovery progress, elapsed time, stop/rescan, and actionable permission/network guidance.
- Results grouped by `Ready to connect`, `Needs setup`, and `Unsupported`, with model, connection type, protocol, address disclosure only where useful, and confidence/verification status.
- Printer details show capability source and last-checked time; unsupported settings are hidden or disabled with a concise explanation.
- Manual IPP/IPPS add flow with validation and a safe connection test.
- Explicit save/connect confirmation with clear personal-printer versus shop-printer ownership wording; explain shared-device matches without leaking another account's data.
- Guided one-copy diagnostic print with preview, explicit confirmation, cancellation, progress, and truthful outcomes (`Accepted`, `Printing`, `Completed/verified` only when supported, `Unknown`).
- Normal customer checkout lists only printers saved by or explicitly authorized for that customer, with settings actually supported by the selected printer.
- Customer and shop-owner controls must not be conflated.
- Accessible light/dark themes, large touch targets, TalkBack labels, responsive small-screen layout, and no blocking full-screen spinner.
- No silent network scanning in background; scanning is user initiated and foreground scoped.

## Required test programme

- Unit tests: DNS-SD service normalization, IPP attribute parsing, capability normalization, scoped device matching, endpoint validation, plan-entitlement decisions, ownership authorization, state machine and unsupported-feature policy.
- Protocol tests: IPP Get-Printer-Attributes, Validate-Job, Print-Job, Get-Job-Attributes, IPPS certificate validation, timeouts, malformed packets, redirects, partial responses, and printer disappearance mid-job.
- Discovery tests: mDNS found/lost, duplicate advertisements, multi-interface/network changes, Wi-Fi multicast behaviour, no mDNS, manually entered endpoint, permission denied and Android local-network permission changes.
- USB tests: device attach/detach, permission denial/grant, unsupported class/protocol, device reset and USB IPP where available.
- Security tests: private/local address validation, SSRF-resistant endpoint policy, DNS rebinding/re-resolution, TLS certificate validation, secure credential storage, cross-customer isolation, customer/shop isolation, plan expiry/revocation, replay/idempotency, and no credential/document leakage in logs.
- Document tests: PDF, JPEG, PNG, supported raster/PCLm formats, large files, multi-page, copies, color, duplex, paper sizes, rotation, scaling and memory pressure.
- UI tests: loading/empty/error/offline states, eligible/ineligible/expired plan states, permission education, discovery cancellation, manual add, duplicate/shared-device warning, capability editing, test-print confirmation, customer checkout printer selection, dark/light themes, accessibility, rotation and process recreation.
- Regression tests: existing customer QR/checkout/job flows, Shop Owner portal, Windows Agent discovery/execution, and shop tenant isolation.
- Hardware matrix: representative printers from HP, Canon, Epson, Brother, Xerox and other locally common brands, including both IPP-capable and legacy devices; network and supported USB cases. Record exact model, firmware, Android version, router/network conditions and results.

## Rollout gates

1. Complete codebase/API/auth audit and dependency/license review.
2. Implement discovery and protocol capability probing behind testable interfaces.
3. Implement customer saved-printer ownership and server-enforced plan entitlement; separately preserve shop-owner authorization and registration.
4. Implement document preparation and real print submission, with idempotent job tracking and safe unknown-outcome handling.
5. Deliver customer and shop-owner UX without merging their permission or ownership models.
6. Run JVM, instrumentation, emulator, virtual-printer and physical-printer matrix, plus customer/shop/Windows regression suites.
7. Ship only verified protocol/model combinations; retain feature-level kill switch and existing Windows path.

## Known limitation

No software stack can guarantee printing to every printer ever manufactured. Compatibility depends on the printer exposing an interoperable protocol or a legally usable vendor adapter, supported document language, network/USB topology, firmware, and Android device capabilities. The product must communicate this honestly rather than marking every discovered device as supported.
