# Product Blueprint

## 1. Product definition

QRtoPrint is a commercial QR-based printing platform. Independent print shops subscribe to Smart Print SaaS; their walk-in and remote customers use a mobile-first web experience to submit and pay for print jobs. A local Windows Print Agent executes authorised work through the shop's installed printer drivers and supported network protocols.

## 2. Commercial model

- Primary paying customer: an individual printing shop subscribing monthly or annually.
- Standard edition: Smart Print platform identity plus the shop's own name, logo, colours, and contact information.
- White-label edition: paid entitlement that removes or replaces platform branding, subject to plan and contract.
- Shop printing revenue: customer print payments settle to the shop's own payment account.
- Platform revenue: SaaS subscriptions, white-label upgrades, additional printer/device entitlements, custom domains, integrations, onboarding, premium support, and agreed enterprise services settle to the platform owner.
- These two payment flows, ledgers, invoices, refunds, and reconciliation processes must remain separate.
- Previously discussed plan prices are provisional only. Pricing and entitlements must be configurable by the platform owner, not hard-coded.

## 3. Product surfaces

1. Customer PWA: QR session, upload, document validation/preview, print configuration, quote, payment, tracking, receipt, collection and issue reporting.
2. Shop portal/desktop: dashboard, print queue, printers, QR stations, pricing, payment configuration, customers, reports, users, settings and diagnostics.
3. Windows Print Agent: Windows service plus tray companion, local durable queue, printer discovery, job execution, spooler monitoring, restart recovery, secure cloud connection and diagnostics.
4. Platform administration: tenant lifecycle, plans, subscriptions, entitlements, platform billing, licences/devices, releases, support, audit and aggregate analytics.
5. Shared backend: tenant-aware identity, policy, pricing, job orchestration, payment records, document metadata, secure cloud transfer and audit.

## Android companion

The native Android app complements the customer web experience and uses the same shop QR session, quote, payment and print-job lifecycle. Implemented tools include on-device QR scanning, an app-private My Files library with folder import/export, Google Play Services document scanning, PDF copy editing, Photo Studio and ID Studio. ID Studio stores prepared sheets in a separate Android Keystore-encrypted local vault; prepared ID sheets always require Private Printing when submitted to a shop. The app does not replace the Windows Print Agent or provide offline printing. Public production/Play Store availability remains subject to real-device release validation.

## 4. Customer access modes

### Local Wi-Fi

The intended end-state local mode keeps document transfer and print execution on the shop network. **Currently implemented slice:** the phone obtains a one-use grant from the cloud API using the live QR session, sends the document directly to the paired Agent, and the Agent validates and stores the file locally. The cloud retains job/document metadata and remains authoritative for quote, approval, payment verification and explicit dispatch. Therefore current LAN upload avoids cloud document storage but does not provide offline quote/payment/dispatch.

### Cloud-assisted

A customer scans a QR or opens the shop portal over mobile data or any internet connection. Files are uploaded to a protected cloud transfer area. The shop's authenticated agent retrieves only jobs authorised for its tenant and station. Cloud transfer/storage must have explicit quotas, retention, encryption, and metering.

Both modes share one job model, server-side price engine, approval policy, payment state machine and audit trail. The local mode must not become a separate product.

## 5. Document intelligence

Use deterministic processing first; no paid AI service is required for core printing.

- Validate extension, MIME type, file signature, integrity, size, page count and malware-scanning result where configured.
- Convert supported Office/image documents to a stable printable representation before final approval.
- Render page previews and inspect page dimensions, orientation, blank-page likelihood and colour content.
- Detect likely duplicate uploads but never silently delete.
- Recommend paper, orientation, scaling, duplex and colour mode based on document and printer capabilities.
- Preserve source bytes and page order. Any derived print rendition must be traceable to the original.
- Show the customer the final settings and price before authorisation.
- Do not silently alter content or increase the quote.
- Mixed colour documents require a deliberate page/sub-job strategy; preserve ordering, collation and copy semantics.

Initial candidate formats: PDF, JPG, PNG, and common Office formats, subject to tested conversion support.

## 6. Print configuration

Support only capabilities actually advertised by the selected printer/driver: page ranges, copies, paper size/type, orientation, colour/mono, single/duplex, pages per sheet, scaling, quality, and collation. The UI must hide or disable unsupported settings with an explanation.

## 7. Quote and payment

The server/local trusted pricing service calculates the quote from shop-configured rules, printer capabilities, pages/impressions, copies, paper, duplex and optional finishing. Never trust client-supplied totals. Persist an immutable quote snapshot with currency, tax treatment, rule version and expiry.

Payment methods are shop-configured. Gateway payment status must be verified server-side using signed webhooks/API verification. A screenshot or client callback is not proof of payment. Cash/counter payment is an explicit shop policy and must be audited.

## 8. Job lifecycle

Suggested state model:

UPLOADED → VALIDATING → ANALYSING → AWAITING_CUSTOMER_APPROVAL → AWAITING_PAYMENT / COUNTER_PAYMENT_PENDING → AUTHORISED → QUEUED → SUBMITTING_TO_SPOOLER → SPOOLER_ACCEPTED → PRINTING (only when reliably observable) → COMPLETED or OUTPUT_UNCONFIRMED.

Exceptional states include REJECTED, CANCELLED, PAUSED, FAILED, REFUND_PENDING, REFUNDED and PARTIALLY_COMPLETED. State transitions must be validated server-side and recorded in an append-only event history.

Spooler acceptance is not proof of physical output. If the printer cannot provide trustworthy completion status, show OUTPUT_UNCONFIRMED and require operator reconciliation. A restart must never automatically submit an uncertain physical job again.

## 9. Shop operations

- Dashboard: job counts, pages, revenue, pending/failed work, printer health and recent activity.
- Queue: search/filter, inspect, approve/reject, pause/resume, cancel, route to another compatible printer before submission, safely retry only when duplicate-print risk is ruled out, refund and mark collection.
- Printers: discover, register, name, inspect capabilities, set default, test, pause/resume and diagnose.
- QR stations: per counter/printer identity, printable QR artwork, enable/disable, rotate secret, session TTL and activity.
- Pricing: configurable rate cards, minimum charge, media, duplex, taxes/fees, payment policy, upload limits and retention.
- Reports: daily/weekly/monthly sales and volume, printer use, customer history, failures, refunds and operator activity.
- Roles: owner, manager, operator and read-only/reporting access, with audited privileged actions.

## 10. SaaS platform administration

Manage shops/tenants, plans, subscription lifecycle, invoices, platform payment reconciliation, entitlements, device/printer limits, white-label upgrades, coupons, support, release channels, aggregate usage and audit logs. Platform operators must not casually access customer documents; support access is time-limited, purpose-bound and audited.

## 11. Domain and branding

Preferred domain: thinkwithsujeet.in. Proposed subdomains, subject to DNS verification:

- print.thinkwithsujeet.in — customer portal
- shops.thinkwithsujeet.in — shop portal
- admin.thinkwithsujeet.in — platform administration
- api.thinkwithsujeet.in — API
- downloads.thinkwithsujeet.in — release delivery

The standard brand combines Smart Print identity and tenant identity. White-label is a separately controlled entitlement. Tenant themes must be constrained by accessibility and contrast rules.

## 12. Initial release boundaries

V1 includes multi-tenancy, SaaS billing foundation, customer flow, shop operations, Windows agent, multi-vendor printer abstraction, local and cloud-assisted access, shop payment integration boundary, platform admin, security, backups, installer/update flow and automated tests.

Defer public Play Store distribution until the existing Android companion passes its real-device release matrix; advanced AI/OCR, universal manufacturer-specific features, advanced finishing without confirmed hardware, franchise workflows and loyalty features for print customers remain outside the current verified scope.


## Implemented capability boundary (source audit: 12 November 2026)

The earlier blueprint describes some intended capabilities and has been superseded in places by the current implementation. The following is the code-verified implementation boundary as of this audit; it does not claim every deployment or physical device has passed production acceptance.

- Customer access: participating-shop QR sessions, guest printing and global customer accounts are implemented. Guest sessions remain anonymous in the shop's customer directory.
- Customer documents: PDF/JPG/JPEG/PNG, up to 20 MB per file and 500 PDF pages. Multi-file batches support up to 20 files and 50 MB total. Each file is its own PrintJob under a shared batch; this is not one merged multi-file print job.
- Customer configuration: copies (1–100), black-and-white/colour, single/double-sided, supported paper sizes and orientation. Folder/group defaults and per-file settings are supported.
- Quotes and print payments: server-side shop rate-card quotes; UPI or cash-at-counter configuration; manual shop verification of UPI references/cash before dispatch. The optional instant-payment-after-upload setting allows payment to start as soon as a quote is calculated, but does not bypass shop verification. SmartPrint does not collect or route customer print revenue.
- Shop control: the owner workspace includes Order Journey, Overview, Print Queue, Printers, QR Stations, Print Agent, Pricing, Subscription, Payments, Customers, Reports and Settings. The shop can review/accept/reject files, revise quotes before payment, assign printers, dispatch jobs and confirm actual paper output.
- Batch/rejection/refunds: per-file decisions in a batch and supported per-page/file refund calculations are implemented before printer handoff. The app records a shop-confirmed refund as sent; it does not itself transfer a bank/UPI refund.
- Privacy: Standard Printing follows the configurable retention policy (7 days by default after confirmed output or verified PDF export). Private Printing is customer-selected, uses end-to-end encryption to the paired Agent, bypasses normal cloud document storage/managed PDF export and deletes the Agent working copy only after confirmed physical output and Agent acknowledgement. Job/payment/audit metadata remains; the customer's original phone file is unchanged.
- Transfer: direct Shop Wi-Fi transfers use local HTTP and are not encrypted in transit; they are restricted to the paired local-network Agent and one-use grant. Cloud-assisted HTTPS is plan-entitlement dependent. The cloud service remains required for session, quote, payment and dispatch coordination; there is no full offline mode.
- Photo/ID preparation: Photo Studio prepares A4 sheets in 35×45 mm, 25×35 mm, 2×2 inch and 4×6 inch layouts with framing, basic appearance controls, background colour-key replacement, optional captions and custom text layers. Android ID Studio scans both card sides and creates an A4 physical-size sheet in an encrypted local vault; it always requires Private Printing. This is not AI photo segmentation, identity verification or facial retouching.
- Android companion: native QR scanner, local file/folder library, document scanner, PDF copy-editing tools, Photo Studio and ID Studio are implemented. The app uses the shared customer workflow; it does not provide offline printing. CI APK/unit tests do not replace real-device/public-release validation.
- Windows Agent: a paired Windows Service discovers installed queues, reports driver capabilities, keeps a protected local queue, claims only authorised jobs, submits through the Windows spooler and reports execution status. The shop confirms physical output; uncertain jobs are not automatically resubmitted. Microsoft Print to PDF is a separate output mode.
- SaaS subscription: INR Razorpay shop-subscription checkout, server-side checkout signature verification and signed/idempotent webhook processing are implemented. This is separate from customer print payments.
- Platform administration: shops, audited status changes, versioned plans/entitlements, subscription assignment/history and platform operational quota/retention controls are implemented.
- Plan-gated capabilities: Cloud-assisted Printing, Advanced Reports, White-label and capacity entitlements depend on a shop's published plan. The server enforces entitlements.
- Operational/release limitations: printer capability comes from the installed Windows driver and must be physically tested per model; the Windows beta installer is currently unsigned; no Agent tray UI or automatic updater is included. Malware scanning/isolated document conversion remain security hardening requirements. The standard direct LAN transfer is not encrypted in transit. Do not claim universal printer compatibility, fully offline printing, Office-file intake, automated customer UPI payments or automated customer print refunds.

See docs/SEO_PRODUCT_CAPABILITY_AUDIT.md for the detailed code-derived inventory and the source files reviewed.
