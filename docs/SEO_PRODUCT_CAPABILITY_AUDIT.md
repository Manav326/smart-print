# SmartPrint — Code-Verified Product Capability Audit for SEO

Last audited: 12 November 2026  
Repository: Manav326/smart-print  
Scope: main-branch implementation, customer portal, shop workspace, Android companion, .NET API, Windows Print Agent and product/operations documentation.

## Executive finding

SmartPrint is a connected print-shop workflow platform. Its value is the combination of an easier customer handoff, owner-controlled shop operations, printer execution through the shop's existing Windows queues, and explicit document-handling choices.

It should not be presented as only a QR upload tool, a generic online print fulfilment company, an automatic-print service, or a full cyber-cafe billing/PC management system.

The product has two primary audiences:

- Customers: submit documents from a phone, choose print options, see the shop's quote, pay using the shop's enabled method, follow progress and choose Private Printing when the paired shop computer supports it.
- Shop owners: manage QR stations, incoming jobs, prices, payment verification, printer queues, job decisions, refunds and output confirmation in one workspace.

This is a source-code audit. Code presence does not by itself prove production deployment, signed public distribution, physical printer compatibility or successful shop-floor acceptance.

## 1. Customer experience

### QR access and identity

- A customer scans a participating shop's QR station to open a shop-specific print session.
- QR sessions are bound to the shop/station and can be resumed, renewed and checked for status.
- Customers can use a guest session without registering.
- Customers can also register/sign in to a global SmartPrint customer account. A customer account can be used at participating shops.
- The Android app stores station-scoped guest session tokens encrypted with an Android Keystore AES-GCM key.
- A shop's customer directory shows signed-in customers connected to that shop only. Guest sessions remain anonymous and are not retrospectively linked to a person.

### Document intake and batches

- Customer upload types: PDF, JPG, JPEG and PNG.
- Per-file upload ceiling: 20 MB.
- PDF page ceiling: 500 pages.
- Multi-file batch: up to 20 files and 50 MB total. Each file remains a separate print job under a shared batch ID; it is not one merged document.
- Customers can select files or folders. Folder/group names are retained with the request.
- Folder-level settings can act as defaults, with per-file settings overriding them.
- Customers can see selected files, review each item and remove files before upload.
- Batch payment can cover the accepted files together; the shop still verifies payment.

### Print options

The customer/owner job settings visible in the current implementation include:

- Copies (1–100).
- Black-and-white or colour.
- Single-sided or double-sided.
- Paper size: A4, A3, A5, Letter or Legal.
- Orientation: Auto, portrait or landscape.
- Photo-specific size, photo count and photos-per-A4-page settings.

Only capabilities actually supported by the selected installed printer driver should be treated as available. The Windows Agent discovers paper, colour, duplex, orientation, media and resolution capabilities from the installed driver. The UI/server should not be described as offering every finishing option on every printer.

### Quote, payment and customer progress

- SmartPrint calculates quotes on the server using the shop's configured rate card and submitted document/print settings.
- Customers can review the quote and accept it.
- Depending on the shop's setting, the customer may pay after the shop review/approval step or be allowed to start payment as soon as the quote is calculated. Instant payment does not bypass payment verification or authorised dispatch.
- Shop-enabled customer print payment methods are UPI and cash at counter.
- Customers can submit a UPI transaction reference; a reference or screenshot is not proof of receipt.
- Customers can follow status changes: shop review, quote/payment, payment verification, local queue receipt, print authorisation, spooler submission, output unconfirmed, completed, rejection/refund or PDF export where applicable.
- The customer is told that Windows spooler acceptance is not proof that paper came out. The shop confirms the physical output.

## 2. Shop-owner workspace

The owner portal contains these main areas:

- Order Journey: groups customer work through review, payment and printer progress.
- Overview: setup checklist, shop operation snapshot, recent activity, QR stations, printers and Agent readiness.
- Print Queue: incoming jobs, batches, search/history, preview where allowed, approval/rejection, printer assignment, dispatch, output confirmation and recovery.
- Printers: register and enable installed printer queues discovered by the paired Windows Agent.
- QR Stations: create counter/station QR codes, assign a printer, enable/disable stations and rotate station codes.
- Print Agent: generate one-time enrollment codes, pair/reconnect/revoke shop computers and check local connectivity/readiness.
- Pricing: configure black-and-white and colour per-side rates, duplex per-sheet rates, minimum charge and photo rates by size.
- Payments: configure the shop's UPI ID/payee name and cash-at-counter availability; optionally allow customers to start payment immediately after quote calculation; verify/reject payment attempts.
- Customers: search/paginate linked signed-in SmartPrint customers and see that customer's activity at this shop. Guest sessions are deliberately not identified.
- Reports: plan-gated shop operations reports for 7, 30 or 90 days.
- Subscription: view entitlements/usage and purchase or renew the shop subscription.
- Settings: shop name/branding/profile and owner account details.

### Job and batch control

- The shop can inspect and review incoming files before dispatch.
- Shop review can accept/reject individual files in a batch.
- The shop can reject an entire file or specific pages where the refund workflow allows it.
- Page-level rejection/refunds must be completed before printer handoff. The system calculates proportional page allocations and cumulative refund amounts.
- The shop can change supported print settings before payment; the server recalculates a revised quote and requires customer acceptance.
- The shop can assign/reassign an eligible job to a compatible printer before output begins.
- The shop explicitly dispatches work after required checks.
- The shop confirms physical output after checking the pages.
- Job event/history is retained for operational review.
- A batch is assigned to one printer queue. Files in a local batch must stay with the same paired Agent/assigned computer.

### Pricing and customer payments

- Each shop controls its own customer-print rate card.
- The shop can configure monochrome/colour side rates, duplex sheet rates, minimum charges and per-photo rates for supported photo sizes.
- Customer print revenue goes to the shop's own UPI account or is collected at the shop counter; SmartPrint does not receive, hold or route that revenue.
- UPI references and cash receipts are manually verified by the shop before printing is authorised.
- Shop-recorded refunds capture method, reference where relevant, reason and confirmation that the shop sent the refund. The app records an already-sent refund; it does not itself transfer funds back through UPI/bank.
- Shop customer payments are separate from SmartPrint SaaS subscription payments.

### Shop reports

The current report UI shows:
- Print jobs created in the selected 7/30/90-day period.
- Jobs whose physical output was confirmed by the shop.
- Pages requested, calculated from source page count × copies (not a claim of actual pages printed).
- Payment attempts needing review.
- Job-status breakdown.
- Verified UPI and cash collections, grouped by currency.
- Refunds recorded as sent during the reporting period.
- Output-unconfirmed job warnings.

Advanced reports are restricted by plan entitlement. Do not imply every plan includes them.

## 3. Document privacy and storage modes

### Standard Printing

- Uses the shop's normal document workflow.
- Cloud-assisted documents use the protected HTTPS/API storage path; cloud document data is encrypted at rest using AES-256-GCM with a deployment-specific key.
- The default standard retention setting is 7 days after confirmed physical output or verified PDF export, with a cleanup sweep. Platform operators can configure the retention policy.
- Output-unconfirmed jobs, unfinished work and jobs under financial hold are not prematurely deleted.
- Job/payment/audit metadata is retained separately for reconciliation.

### Private Printing

Private Printing is a customer-selected document-retention mode, not anonymous payments or an untraceable transaction.

- The customer explicitly selects Private Printing before upload.
- It is available only when the paired shop computer is connected and has supplied its secure document-encryption public key.
- The browser encrypts the document with a fresh AES-256-GCM key. That key is wrapped for the paired Agent using RSA-OAEP/SHA-256.
- The normal cloud document store and managed PDF export are not used for the private source.
- The Agent decrypts in memory, validates the source hash/document structure and stores the protected local working copy in the DPAPI-protected queue.
- If the direct shop-network path is unavailable, the implementation can use a temporary encrypted relay; that temporary relay object is removed after the Agent verifies receipt.
- The shop cannot preview/download the private source or use the shop-side photo-revision flow on it. The filename is replaced with a generic private-document label.
- Private printing is physical-printer-only; managed PDF export is blocked.
- After the shop confirms actual physical output, the Agent securely overwrites/deletes its private working copy and acknowledges deletion. The API then redacts source metadata.
- If output is uncertain, the private source remains protected for reconciliation rather than being deleted too early.
- The original file on the customer's own phone/document library is never deleted.
- Job, payment and audit metadata remain for accounting/reconciliation. Private Printing does not hide the payment or make the order anonymous.
- The product does not promise forensic erasure of every possible copy in Windows spooler, printer firmware, SSD wear-leveling, screenshots or files copied by other software.

### Local Wi-Fi vs Cloud-assisted

These are transfer paths, not full offline operating modes:

- Direct Shop Wi-Fi sends standard document bytes from the phone/browser to the paired Windows Agent over the shop's private IPv4 network. It uses local HTTP and is explicitly not encrypted in transit. It is restricted to the selected local subnet, trusted portal origin, a one-use grant and the paired Agent identity. Do not advertise this standard local route as end-to-end encrypted or recommend it over guest/public Wi-Fi.
- Cloud-assisted transfer uses HTTPS and protected cloud storage before the paired Agent retrieves the authorised work. Availability is plan-entitlement dependent.
- Private Printing encrypts source content end-to-end even when the temporary encrypted relay is needed.
- Both transfer paths still rely on the SmartPrint service for QR-session validation, job metadata, quotes, payment status and dispatch authorisation. SmartPrint is not a fully offline quote/payment/printing system.

## 4. Photo and ID-document tools

### Photo Studio

Photo Studio is integrated into customer photo jobs and shop-created photo orders.

- Accepts JPG/JPEG/PNG originals up to 20 MB.
- Prepares A4 photo sheets in 35×45 mm, 25×35 mm, 2×2 inch and 4×6 inch portrait layouts.
- Provides crop/framing, zoom, focus positioning and preview.
- Offers Natural, Warm, Crisp and Black & White appearance presets, plus brightness/contrast/saturation adjustment.
- Can sample a background colour and replace similarly coloured pixels connected to the photo edges. This is colour-key replacement, not AI subject segmentation.
- Allows optional printed name/DOB captions and movable/customisable text layers.
- Supports 1–100 photos/copies and automatic or selected photos-per-A4-page layouts.
- Shows source-resolution guidance and preserves the original photo; it does not reshape/retouch facial features.
- The shop configures per-photo rates by size; the server calculates the quote.

### Shop-created photo orders

- A shop owner can create a photo-print job for an existing linked customer or a walk-in recipient.
- It uses the shop's configured photo rates and UPI/cash method; payment starts pending.
- The shop verifies actual payment and explicitly dispatches the job.
- This is a shop-originated photo order, separate from the customer QR upload flow.

### Android ID Studio

- Available in the native Android companion.
- Scans the front and back of a card/document using the Android document-scanner flow.
- Lets the user review/rotate both sides and adjust supported physical card dimensions.
- Composes one A4 portrait sheet containing both sides at the selected physical dimensions, with crop guides.
- Prepared ID PDFs are stored in a separate private, app-local encrypted vault using Android Keystore AES-GCM; they are separate from My Files.
- Prepared ID sheets are always marked as ID_DOCUMENT and automatically require Private Printing for the entire batch.
- This feature prepares an ID card sheet; it is not a government verification, identity-validation or document-authentication service.

## 5. Native Android companion

The Android app is more than a WebView wrapper:

- CameraX/ML Kit QR scanner; QR decoding happens on-device and camera frames are not uploaded for QR decoding.
- Guest and global customer account sign-in.
- App-private My Files library with folders/subfolders, rename, move, delete, import, export, open and share actions.
- Android Storage Access Framework support for selecting/importing files and folders.
- Built-in document scanner using Google ML Kit document scanning (where Google Play Services support it).
- In-app PDF editing tools include page preview, rotate, reorder, delete and save as a new edited copy. The editor may rasterise pages; it is not a lossless vector/text editor.
- Image/PDF utility to create a new PDF from supported source files; originals remain unchanged.
- Photo Studio and ID Studio integrations.
- Native saved-document picker can pass original documents, folder/group names, PDF preview handles and print kinds into the same customer quote/payment/print workflow.
- Session tokens and ID Studio vault content use Android Keystore-backed encryption.

The Android companion uses the same shop QR session and backend job lifecycle as the web customer flow. It does not replace the Windows Print Agent and does not provide offline printing. Do not imply an iOS app or Play Store listing unless separately released.

## 6. Windows Print Agent and printer execution

- Runs as a Windows Service on the printer-connected shop computer.
- Shop owner pairs it using a single-use enrollment code that expires after 10 minutes.
- Stores the paired secret and locally received document data using Windows DPAPI LocalMachine; queue files are protected by SYSTEM/Administrators-only ACLs.
- Sends authenticated HTTPS heartbeats and syncs Windows-installed printer queues from WMI.
- Supports multiple installed Windows print queues on one paired computer; queue names stay distinct even where ports are shared.
- Reports printer status and capabilities obtained from the installed Windows driver: colour, duplex, orientation, paper sizes, media types and resolutions where the driver reports them.
- Shop Connection diagnostics show the selected adapter/Wi-Fi and can use a short-lived one-time QR test to verify that a second phone can reach the paired computer.
- Direct local endpoint is restricted to the shop's selected local subnet on TCP 17842; it is not opened to the internet.
- Claims only paid/authorised jobs assigned to the paired Agent and one of its live printer queues.
- Verifies source SHA-256/size, stores an encrypted local document and durable manifest, and acknowledges local receipt.
- Executes approved work once through the Windows spooler and reports blocked, accepted, printing or uncertain status.
- Does not automatically resubmit an uncertain physical print, reducing duplicate-print risk.
- Shop operator confirms physical paper output; spooler acceptance is not proof of completed paper output.
- Microsoft Print to PDF is a separate output destination; PDF export is verified as a saved file.
- Standard and private document retention/purge handling are coordinated between API and Agent.

Printer compatibility is determined by the installed Windows driver and reported capabilities. Do not claim every make/model or every finishing feature is supported. Current documentation notes that the installer is unsigned, no Agent tray UI/automatic updater is included, and physical printer model/driver testing remains necessary.

## 7. SaaS platform and plan controls

The repository includes a separate platform-administration surface with:
- Platform overview and operational health.
- Shop directory and audited tenant status history/actions.
- Versioned plan catalogue and plan entitlement editing/publication.
- Shop subscription assignment/history.
- Platform-level operational quotas and document-retention settings with audit reasons/history.
- Shop subscription checkout/renewal through Razorpay for INR plans, with server-side checkout signature verification and signed/idempotent webhook processing.

Plan details can constrain printer count, Agent devices, QR stations, monthly print jobs, Cloud-assisted Printing, Advanced Reports and White-label. Entitlements are enforced on the backend, not only hidden in navigation.

Razorpay SaaS subscription billing must not be confused with customer payments to a shop for photocopy/print work. Shop customer payments remain UPI/cash with manual verification.

## 8. Important non-claims and current limitations

Do not advertise these as existing capabilities:

- Fully offline quote, payment, job coordination or printing.
- Encrypted-in-transit standard Shop Wi-Fi document transfer.
- Automatic UPI/cash verification or automatic refunds for customer print orders.
- Customer print revenue collection/settlement by SmartPrint.
- Full cyber-cafe PC time/billing, gaming-cafe control or internet access management.
- Universal printer/model compatibility or every printer finishing feature.
- Office document upload/conversion support in the customer QR workflow.
- AI OCR, AI background segmentation, AI face retouching or automatic document enhancement.
- Full CRM for guest customers; guest sessions are intentionally anonymous in shop reports.
- Full staff/operator role management or employee invitations.
- Lossless PDF editing or guaranteed fidelity after raster-based editing.
- Native iOS app or Play Store availability unless separately released.
- Signed Windows installer, tray app or automatic Agent updater.
- Malware scanning / isolated document conversion as a completed production feature; security documentation lists these as hardening work.
- Claim that Windows spooler accepted means paper physically printed.
- Claim that customer original files are deleted from the phone.

## 9. SEO content requirements from this audit

The public site should lead with the actual customer/shop owner outcome:

**Customers submit documents easily. Shop owners stay in control. Sensitive jobs can use Private Printing. Existing shop printers handle authorised output.**

Recommended public content cluster:

1. Home — product summary that combines customer ease, shop workflow, document privacy and Windows printer connection.
2. Features — one comprehensive, crawlable inventory grouped by customer, shop owner, privacy, Android, Photo Studio/ID Studio and Windows Agent.
3. Print shop software — owner workspace, queue, QR stations, pricing, payment verification, customer directory and reports.
4. QR print ordering — customer scan, upload, quote, checkout and tracking.
5. Print job management — batches, file decisions, revised quote, printer assignment, page/file refunds and physical-output confirmation.
6. Document privacy and Private Printing — Standard vs Private handling, retention, encrypted relay, local Wi-Fi limitations and deletion confirmation.
7. Photo and ID printing — Photo Studio sizes/layouts/captions and Android ID Studio with private storage and Private Printing.
8. Windows Print Agent — setup, printer discovery, driver capabilities, local queue, status and output confirmation.
9. Android companion — QR scanning, local library, scanning, PDF utilities, Photo Studio and ID Studio.

Every statement must be product-accurate and distinguish source-implemented behaviour from plan-gated behaviour and physical-release validation. No competitor comparison pages are part of this strategy.


## 10. Customer and shop problems SmartPrint is designed to solve

The SmartPrint cinematic advertising production master (11 November 2026) adds a human, problem-led explanation of the product. Its central problems are supported by the current code and should guide the public SEO narrative:

- **Scattered document collection:** customers may be asked to join a shop messaging group and send files in separate messages; owners then download and sort them. SmartPrint provides a shop Upload QR and a station-bound submission flow so the file request reaches the shop workspace.
- **Repeated copy/colour questions:** the customer chooses copies and Mono/Colour per file, with folder defaults and per-file overrides. The shop sees those settings with the corresponding job before approving it.
- **Mix-ups between document sets:** keeping file settings attached to separate jobs in one batch helps reduce avoidable confusion about which file needs which output. This is a workflow improvement, not a guarantee that mistakes are impossible.
- **Unclear print responsibility:** the shop reviews the request, confirms the quote and payment according to its own process, then explicitly authorises printer dispatch. Uploading a file does not start printing automatically.
- **Sensitive document concerns:** customers can choose Private Printing where the paired shop Agent/key requirements are met. Ordinary direct Shop Wi-Fi transfer is not encrypted in transit and must never be described as end-to-end encrypted.
- **Unclear completion:** spooler acceptance and physical output are different states; the shop checks the actual pages and confirms output.

Recommended public narrative: **“No scattered file messages. No repeating copy and colour instructions. Customers submit through your shop QR; your team reviews each file and decides when printing starts.”** Use measured phrases such as “designed to reduce back-and-forth” and “helps keep instructions with the right file.” Do not claim zero mistakes, guaranteed time/paper savings, instant printing, automatic printing on upload or universal printer compatibility.

The ad production master is a creative brief, not evidence that the Android app is publicly listed or a Windows installer is currently available. Use public Play Store/download claims only after verifying the exact live listing/release. The ad's QA capture plan specifically requires fresh evidence for per-file controls, a successful Private Printing setup, shop review/authorisation and real output confirmation.

## 11. Files audited

- apps/portal/src/App.tsx
- apps/portal/src/PublicLandingPage.tsx
- apps/portal/src/PhotoStudio.tsx
- apps/portal/src/photo-studio-layout.ts
- apps/portal/src/OrderJourneyPage.tsx
- apps/android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt
- Android ID Studio store, layout and PDF composer
- Android customer session store and image/PDF composer
- src/SmartPrint.Api/Program.cs and shop/customer/public/platform endpoints
- src/SmartPrint.Api/Domain batch, refund, retention, private-print and entitlement rules
- src/SmartPrint.Agent/README.md
- src/SmartPrint.Agent/Worker.cs
- src/SmartPrint.Agent/WindowsPrinterCapabilityDiscovery.cs
- src/SmartPrint.Agent/PrinterCapabilityRules.cs
- src/SmartPrint.Agent/WindowsPrintExecutor.cs
- docs/PRIVATE_PRINTING.md
- docs/OFFLINE_TOLERANT_PRINT_FULFILMENT.md
- docs/SECURITY_AND_OPERATIONS.md
- docs/PRODUCT_BLUEPRINT.md
- README.md
