# SmartPrint Advertising

## Initial first-party campaign system

The platform-owned advertising manager serves three fixed placements:

| Placement | Client surface | Format | Suggested uploaded asset | Approximate display |
|---|---|---|---|---|
| HOME | Native Android customer Home | Banner | 640 × 200 px | 320 × 100 px |
| FILES | Native Android My Files library | Card | 600 × 500 px | 300 × 250 px |
| SHOP_OVERVIEW | Shop-owner browser and Windows desktop Overview | Banner | 1456 × 180 px | 728 × 90 px |

Each campaign has a single placement, a verified JPG/PNG/WebP creative (maximum 3 MB), advertiser label, headline, supporting copy, HTTPS destination, optional terms and a start/end window. New campaigns remain Draft until an administrator explicitly publishes them. Published campaigns can be paused, resumed or archived. Changes and status transitions are recorded in campaign audit history. Impression and click events are aggregated into campaign metrics.

The ad loader is best-effort: a missing/invalid campaign, image or network response does not block the customer file library or shop workflow. Native customer ads appear only on the normal Home and My Files screens. They are not loaded in file pickers, batch-selection mode, document viewers, Photo Studio, ID Studio, checkout, payment, print queue, order tracking or private-printing flows. Shop-owner ads appear only on the Overview dashboard and are shared by the browser and Windows desktop portal renderer.

Creative files are validated by MIME type and file signature, stored alongside SmartPrint's managed document storage, and only served publicly while at least one campaign using that creative is active. Draft-creative previews are available only through the authenticated platform-admin API. Destination URLs must use HTTPS on the standard HTTPS port and cannot point to obvious loopback/private IP hosts. Ad content is plain text and a raster image; campaigns cannot supply executable HTML, JavaScript or iframe content.

## Third-party provider compatibility

The placement identifiers and responsive slots are deliberately separate from direct campaign delivery. A later provider adapter can choose a direct SmartPrint campaign or a configured provider for each placement without changing the screen layout or file/print workflows.

Google's products are not interchangeable:
- AdSense is for eligible web inventory. Google publisher policies prohibit AdSense ad code in software applications and on pages primarily focused on private communications or non-content tasks.
- AdMob is Google's mobile-app monetization SDK/network and is the appropriate starting point for eligible native Android inventory.
- Google Ad Manager supports web and app inventory and can include AdSense and third-party demand; it is aimed at publishers with meaningful direct-sold demand and more complex inventory operations.

The initial SmartPrint release implements first-party direct campaigns only. It does not yet include AdSense, AdMob, Google Ad Manager, mediation, advertiser invoicing or automated billing. Provider credentials, app/ad-unit registrations, privacy disclosures, consent handling and policy review must be configured before enabling any third-party network.
