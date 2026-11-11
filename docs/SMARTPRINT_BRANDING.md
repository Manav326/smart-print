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

## Rules
- Do not redraw, stretch, skew, crop, or add effects to the master mark. Preserve clear space around it.
- Keep the violet mark unchanged in both themes. Theme changes affect surrounding UI and wordmark text only.
- Use the light/dark horizontal logo variant for static media; in app UI prefer the shared component so accessible text and responsive sizing remain intact.
- Never use the brand presentation board as a logo asset. It is concept artwork only.
