# SmartPrint brand and asset usage

## Brand separation
- SmartPrint is the product brand. Its mark and icon use the violet identity: `#7258E8`, `#9B8CFF`, and `#4B2CBF`.
- QuantaEdge is the parent signature only. Keep the approved QuantaEdge Learning logo/lockup separate from SmartPrint's mark. Parent wordmark: Quanta `#152038`; Edge `#F97316`.
- Never recolor the parent logo to SmartPrint violet or introduce QuantaEdge orange into the SmartPrint symbol.

## Use by platform
- Web and Windows desktop: use `/smartprint-mark.svg` for the shared mark; use `/branding/smartprint-app-icon.svg` for browser/app icon; use the existing `BrandLockup` component for `SmartPrint by QuantaEdge`.
- Browser/PWA icons: `manifest.webmanifest`, `favicon.ico`, `branding/apple-touch-icon.png`, and the SVG app icon.
- Android: adaptive launcher XML in `mipmap-anydpi-v26`; foreground, monochrome, and background layers in `res/drawable`. The same foreground vector is used by the in-app native toolbar.
- Windows installer: `apps/desktop/src-tauri/icons/icon.ico`, generated from the same app-icon master.

## Approved lockup and launch identity
- In the app, use the full SmartPrint lockup: SmartPrint (one line, one wordmark) with smaller "by QuantaEdge" directly beneath.
- The SmartPrint icon/mark remains visible beside the SmartPrint name in the full lockup. Do not insert the QuantaEdge launcher mark between the product name and its parent endorsement.
- The QuantaEdge endorsement is plain lettering on a transparent background: Quanta navy `#152038`, Edge orange `#F97316`. No white capsule, navy/blue rectangle, pill, or badge behind either word.
- On dark surfaces, retain Quanta's navy fill with a fine light outline for visibility; on light surfaces, retain Edge's orange fill with a fine dark outline. Do not recolour either wordmark.
- Android and Windows launchers use the SmartPrint symbol only, with no text and no QuantaEdge mark. Their displayed app name is **Smart Print**.
- Android adaptive, monochrome, Windows ICO, web favicon, and installed-app icon assets must all use the same clean symbol-only direction.

## Rules
- Do not redraw, stretch, skew, crop, or add effects to the master mark. Preserve clear space around it.
- Keep the violet mark unchanged in both themes. Theme changes affect surrounding UI and wordmark text only.
- Use the light/dark horizontal logo variant for static media; in app UI prefer the shared component so accessible text and responsive sizing remain intact.
- Never use the brand presentation board as a logo asset. It is concept artwork only.
