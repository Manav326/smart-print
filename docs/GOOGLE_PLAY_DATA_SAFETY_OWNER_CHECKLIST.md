# Google Play: Data Safety and advertising owner checklist

**Application:** SmartPrint by QuantaEdge  
**Package:** com.quantaedge.smartprint  
**Service origin:** https://smartprint.quantaedge.in  
**Last reviewed against source:** 27 November 2026

This is an implementation inventory to help complete Play Console. It is not a submitted Data Safety form or a legal certification. Play Console answers must describe the actual production build, production API, embedded customer portal/WebView, complete SDK set and infrastructure providers in use at submission time.

## Public URLs implemented in this branch

- Privacy policy: https://smartprint.quantaedge.in/privacy-policy/
- Account deletion: https://smartprint.quantaedge.in/account-deletion/

Publish these pages with the website release before entering the URLs in Play Console. The deletion page requires customer authentication, supports the existing email/password and Google customer sign-in paths, then submits an authenticated DELETE request. It does not store the access token in localStorage or sessionStorage.

## Advertising declaration

**Declare that the app contains ads: YES.**

The Android customer app renders SmartPrint-managed sponsored/campaign placements and sends impression and click events to the SmartPrint advertising API. First-party campaign delivery still means the app contains ads. Inspect production campaign configuration and external destination links before submitting the declaration.

## Data Safety inventory for owner verification

The following categories are visible in the source and product flows. Confirm each category and its purpose against the production API and shop workflow before answering the Play Console questionnaire.

| Candidate Play Console data type | Source and purpose to verify |
| --- | --- |
| Personal info — Name | Customer display name supplied at account creation or returned by Google sign-in; used for the customer account. |
| Personal info — Email address | Email/password account identifier and verified Google identity; used for authentication and customer account access. |
| Files and docs | PDFs and other supported documents selected by the customer for printing; original filename, type, size and document metadata support upload and order fulfilment. |
| Photos and videos | JPG/JPEG/PNG images or photo content the customer explicitly selects for printing or preparation. Verify whether each image-selection flow sends source images to the API or keeps them local. |
| Financial info / purchase history | Customer print quotes, order amounts, payment method/status, refund amounts and user/shop-provided transaction references are present in print history and shop workflows. Map exact fields to the current Play data-type taxonomy; do not classify these as card or bank credentials. |
| App activity — App interactions | SmartPrint records sponsored campaign impressions and clicks. Verify whether additional interaction analytics are enabled in the production API. |
| Other data supplied in a job | A print request can contain recipient details such as a name or phone number. Confirm whether the customer-facing Android journey collects these fields and classify them if it does. |

Do not mark unrelated categories (for example precise location, contacts, microphone, health, or payment card credentials) merely because Android permissions or third-party services exist. Confirm the manifest, code paths, dependencies and provider documentation before deciding whether they apply.

## Collection, sharing and processing notes

- Customer account fields are sent to the SmartPrint API for account creation, sign-in and profile/history access.
- Customer documents and photos are sent when the customer chooses to submit a print request. The selected print shop receives the documents and job details required to review and fulfil that request.
- Google sign-in uses Google Identity Services / Credential Manager and a server-verified ID token. Google ML Kit Document Scanner is supplied through Google Play services. Review Google SDK disclosures for the versions shipped in the final app.
- QR decoding is performed on the device. Do not declare raw camera-frame upload for QR scanning unless a separate code path is added.
- First-party ad impression/click events are sent to the SmartPrint API. Verify whether campaign operators receive event-level data or only aggregate reporting.
- Customer print payments are handled through the selected shop's configured process, including UPI or cash. The app does not request card numbers, CVVs or bank passwords for ordinary shop print payments. A user-provided transaction reference and payment status are still order-related data.
- Review API, hosting, database, security/CDN, document-storage and support providers. Add each actual provider and processing region to the privacy policy and Data Safety review as required.

## Security and deletion questions

- **Account deletion mechanism:** after this branch is deployed, the Android Account screen exposes Delete account and the external URL above provides an authenticated web route. Both call DELETE /api/v1/customer/account.
- **What deletion does:** deletes the customer identity and associated authentication material, including password/provider link, claims and refresh tokens. Database relationships unlink that account from customer print sessions and print jobs.
- **What can remain:** shop-side print-job, payment, refund, audit and reconciliation records may be retained without the account link where needed for open orders, disputes or legal recordkeeping. Standard completed document sources follow the current 72-hour post-completion purge schedule; Private Printing follows its source-erasure workflow. Local files saved in Android My Files are not remotely deleted.
- **Encryption in transit:** public service connections use HTTPS. Direct Shop Wi-Fi Agent document transfer uses the authenticated encrypted QSP2 document stream over a local HTTP endpoint. Review every request/data path before selecting the Play Console encryption answer; do not infer it from the public service URL alone.
- **Local storage:** account tokens and the customer-history snapshot are protected with Android Keystore-backed encryption. Documents intentionally saved in My Files are app-private local files and remain on the device after server-side account deletion.

## Required owner actions before submission

1. Confirm the legal publisher/controller name, registered business/contact details and monitored privacy/support email. The public policy currently directs non-deletion privacy requests to the developer contact email shown in the Play listing; make sure that address exists, is monitored and belongs to the correct legal operator.
2. Confirm actual hosting, database, document-storage, CDN/security and support providers and their processing regions. Update the policy if production infrastructure differs from what is described.
3. Review the final AAB's Android manifest, dependency/SBOM list, Google Play services SDK disclosures, embedded WebView network traffic, backend event logging and production advertising configuration.
4. Complete the Data Safety questionnaire in Play Console, including collection/sharing status, purpose, optionality, retention, encryption and deletion answers. Keep a dated copy of the submitted answers with the release record.
5. Set the Play Console advertising declaration to Yes.
6. Publish both public URLs and verify them in a signed-out browser on mobile and desktop. Test password and Google deletion against a non-production customer account before using the production account.
7. Ensure the privacy policy and Play Console listing contact details are reviewed by the responsible legal/business owner. Source implementation does not substitute for legal review or the owner's Play Console submission.

## Do not put these values in source control

The Data Safety form and advertising declaration do not require a new app build secret. Do not add OAuth client secrets, Play service-account JSON, signing keystores or user account credentials to this document or to the repository.
