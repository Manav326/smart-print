# Printer compatibility and qualification

**Baseline:** `main` after PR #132, commit `9740ba641f5ccd8a4694c4e3cd147bc70cf0699f`.
**Scope:** physical USB printers, physical Wi-Fi/Ethernet printers, Windows shared queues, Windows-discovered WSD/IPP queues, IPP/IPPS endpoints, and the existing supported virtual/PDF workflows.
**Explicit exclusions:** Bluetooth/BLE and thermal, label, portable, ESC/POS, ZPL, TSPL and CPCL devices/profiles. These must not appear as supported onboarding choices or qualification targets in this workstream.

## Reuse before building

The product already has the following implementation and it remains authoritative:

- Windows queue inventory through isolated WMI discovery with WinSpool fallback.
- Separate capability enrichment through isolated driver probes, bounded concurrency, timeouts, caching and failure backoff.
- Existing API inventory sync, tenant/Agent ownership and queue/driver/port identity fingerprint.
- Existing Windows spooler execution, protected Agent queue, authorization immediately before spooler submission, reconciliation and owner confirmation.
- Existing local network adapter selection for phone-to-Agent upload. This is **not printer discovery** and must not be reused or renamed as such.
- Existing printer type readiness fixture and policy tests.
- PR #47's driver qualification harness and PR #116's shop-floor checklist are existing work to reconcile/reuse, not duplicate implementations. Their current draft status and their evidence limitations must be checked before adopting files or claims.

## Truthful support matrix

| Family | Current route | Automated evidence | Additional required evidence |
|---|---|---|---|
| USB office printers | Installed Windows queue and vendor driver; existing Agent/spooler path | Queue discovery, capability probe, executor and reconciliation tests; isolated virtual queue fixtures | Named physical model + driver/version on supported Windows build; actual page output |
| Wi-Fi / Ethernet office printers | Installed Windows queue (WSD, Standard TCP/IP, IPP-backed queue, or vendor queue) | Port classification, queue discovery, spooler execution and failure/recovery tests | Real device on controlled LAN/Wi-Fi; sleep, reconnect and address-change cases |
| Shared Windows queues | Existing connected Windows queue and spooler path | UNC queue metadata, identity, permissions and executor tests | Real print-server share, user/service permissions, server outage and reconnect |
| WSD queues | Windows-discovered WSD queue and spooler path | WSD queue/port fixture and identity tests | Physical WSD printer discovery and print on supported Windows builds |
| IPP / IPPS | Existing Windows-installed queue first; native protocol adapter only after a separately reviewed design | IPP virtual receiver and exact endpoint integration; scheme/identity and capability tests | Physical IPP/IPPS endpoint, attribute negotiation, document-format acceptance, TLS hostname/certificate and job reconciliation |
| PDF export / supported virtual test output | Existing restricted output-profile-specific route | File artifact, grant and policy tests | Virtual output must never be reported as physical paper output |

A connection medium is not a print protocol. Wi-Fi devices may expose IPP, WSD, RAW TCP, LPR or a vendor-managed Windows queue. A reachable HTTP(S) URL is not sufficient evidence of IPP. Only explicit `ipp://` or `ipps://` URI schemes identify IPP from the URI alone; WSD and Windows queue metadata remain separately classified.

## Common acceptance contract

All physical office-printer families reuse the same existing job, rendering, authorization, spooler and reconciliation pipeline. Tests must exercise:

1. **Discovery:** real Windows queue enumeration; bounded input and output; duplicate queue observations collapse only by the existing queue identity rule; different queues sharing a port remain distinct.
2. **Identity:** retain Agent + tenant boundaries and queue/driver/port fingerprint. Never merge on display name alone. A changed driver or port must invalidate stale grants/qualification as already required by existing rules.
3. **Capabilities:** driver-reported supported values only; missing, timed-out or contradictory values remain unknown/conservative. A failed probe must not hide the discovered queue or delay unrelated queues.
4. **Selection:** only eligible output profiles can be selected; virtual queues never become physical merely because their port or driver resembles IPP.
5. **Submission:** one controlled diagnostic/customer document through the existing secure Agent queue and existing isolated Windows executor; authorization is checked immediately before spooler side effects.
6. **Reconciliation:** correlate submission attempts, preserve terminal evidence, classify post-submission uncertainty, and reconcile before retry. Never blindly replay an uncertain physical job.
7. **Physical verification:** spooler completion is not paper-output proof. Require shop-owner confirmation and retain the diagnostic attempt and evidence.
8. **Recovery:** queue offline/paused, Agent restart, network interruption, unavailable print server, capability timeout, stale inventory, duplicate request and cleanup paths.
9. **Security:** no broad subnet scanning, driver downloads/installation, weakened TLS validation, tenant crossing or virtual-grant bypass.

## Test layers and evidence labels

- **L1 — policy/unit:** classification, identity, capability and state-transition rules.
- **L2 — Windows spooler fixture:** real Windows APIs and isolated queue/driver fixture; not physical hardware.
- **L3 — live endpoint:** actual protocol receiver and job acceptance/reconciliation (the Android IPP test is one such endpoint).
- **L4 — physical hardware:** named model, firmware (when available), driver/version, Windows build, connection, media/settings, sample output and owner result.

Never promote L1–L3 evidence to L4. Every matrix row must retain `NOT_RUN`, `BLOCKED`, `PASS` or `FAIL` with the evidence artifact and exact environment. Do not infer support for all models in a vendor family from one model.


## Automated qualification coverage in CI

The Windows CI lane must exercise the real installed Windows spooler and the same Agent executor used by shops. The current qualification baseline includes:

| Family / route | Automated test that is possible in hosted CI | What the result proves | What it does not prove |
|---|---|---|---|
| USB office queue | Port/output classification tests; real Windows queue inventory and driver capability probe; shared spooler executor contract | SmartPrint handles an installed Windows queue and driver metadata without relying on the physical connector type | No USB cable, device firmware, paper feed or physical USB printer is present on the hosted runner |
| Wi-Fi / Ethernet (Standard TCP/IP) | IP_ port classification, physical queue identity and common Windows spooler execution tests | Network-port metadata is not confused with IPP and the existing Windows queue route is retained | No physical LAN printer, router, Wi-Fi roaming, sleep/wake or DHCP address-change behavior is certified |
| Windows shared queue | UNC path classification, queue identity and service-permission policy tests; hosted Windows queue fixtures | A shared queue is not mistaken for a file sink and remains a distinct Windows queue identity | A real remote print server, cross-machine share connection and shop-specific ACLs still need a controlled Windows test host |
| WSD | WSD port classification and physical-vs-virtual identity tests; Android IPP receiver integration where Windows installs the receiver behind a WSD port | WSD metadata is handled without automatically treating every WSD queue as virtual or physical | A physical WSD printer's discovery, sleep/wake and firmware behavior is not certified |
| IPP | Real Android IppVirtual receiver, Windows-installed IPP queue and production WindowsPrintExecutor submission; Windows spooler terminal job observation | A real IPP endpoint receives a document through Windows spooler and SmartPrint's production Agent executor | It is a virtual receiver, not physical paper output or broad model compatibility |
| IPPS | ipps:// classification and fail-closed identity policy tests | IPPS is distinguished from generic HTTPS and remains in the IPP family | No physical IPPS endpoint/certificate chain is validated by the current virtual receiver test |
| PDF / virtual | Real Microsoft Print to PDF queue, restricted virtual output policy, virtual IPP receiver and owner diagnostic workflow | PDF export and test-only virtual output stay separate from customer physical fulfilment | No physical output is implied |

The Agent test suite also covers isolated worker crashes, timeouts, capability-probe backoff, queue visibility when capability probing fails, spooler authorization immediately before submission, and uncertain submission without blind replay. These tests are transport-independent because all supported physical families converge on the same installed Windows queue and Agent execution path.

The Epson L360 vendor-driver qualification is an optional, stronger driver-specific lane. It runs only when an exact vendor package has been verified and supplied/downloaded; a skipped Epson lane must be reported as **not run**, never as a pass. Other physical model/driver combinations require their own evidence.

### Physical hardware gate

Hosted CI cannot truthfully simulate a USB cable, real paper path, ink/toner, tray, duplex unit, WSD device firmware, physical network outage or a remote shop print server. Those checks remain L4 and must be run on representative shop hardware. Until then, the product may say **Windows queue tested** or **IPP endpoint tested**, but must not say **physical printer verified**.

## Optimized discovery and background chores

- Keep inventory sync and capability probing separate. Publish queue visibility first; enrich capabilities asynchronously using the existing probe runner.
- Do not run network discovery on every UI refresh. Use an explicit owner-triggered scan or a bounded scheduled refresh with a cooldown, cancellation and per-interface limits.
- Cache endpoint observations by interface + service instance/endpoint identity with a short TTL; update last-seen rather than creating a new printer on every observation.
- Do not treat the shop computer's upload-interface selection as printer discovery.
- Prefer Windows' already-installed queue inventory for USB, shared, WSD and network queues. Do not download/install vendor drivers or modify the default printer.
- For future DNS-SD discovery, query only supported print service types on eligible local interfaces, cap result count/time, validate returned endpoints, and retain raw service identity separately from the Windows queue. Do not merge to a queue until identity evidence is sufficient and the owner confirms the association.
- Avoid repeated work when nothing changed: compare queue/driver/port fingerprints, reuse fresh capability snapshots, and only reprobe after driver/port change, expiry or explicit refresh.
- Make diagnostics and qualification artifacts opt-in, bounded, redacted and self-cleaning; never retain document contents when metadata/hashes are sufficient.

## Delivery sequence

1. Close gaps in the current Windows queue contract and regression tests (including precise IPP classification).
2. Reconcile PR #47 and PR #116 with this branch; cherry-pick or port only missing, compatible work after reviewing their full diffs and ancestry. Do not copy stale branches wholesale.
3. Complete the physical USB, network/WSD and shared-queue acceptance harness using existing Agent/spooler APIs.
4. Implement bounded DNS-SD printer-service discovery only if a supported Windows API/library and permission model are validated; keep it additive to Windows queue discovery.
5. Implement native IPP/IPPS only if the existing Windows queue route demonstrably cannot meet required device support. Require attribute negotiation, format negotiation, TLS validation, submission correlation, cancellation and uncertainty reconciliation before enabling it.
6. Add physical hardware evidence and update the owner onboarding copy to distinguish discovered, installed, tested and physically verified states.

## Non-goals

No second renderer, queue, database, Agent service or dispatch pipeline. No broad subnet scanning, automatic vendor-driver installation, default-printer changes, Bluetooth/BLE, thermal/label/portable profiles, or unsupported universal compatibility claims.
