import assert from "node:assert/strict";
import test from "node:test";
import {
  buildQrStationArtworkSvg,
  createQrStationImageFile,
  qrStationImageMimeType,
  shareQrStationImage
} from "../src/qr-station-export.ts";

const qr = '<svg width="100%" height="100%" viewBox="0 0 29 29"><rect width="100%" height="100%" fill="#ffffff"/><path fill="#101c35" d="M0 0h10v10H0z"/></svg>';

test("branded QR artwork includes the shop, station and scan-first instructions", () => {
  const svg = buildQrStationArtworkSvg({
    shopName: "North Street Prints",
    stationName: "Counter 2",
    isEnabled: true
  }, qr);
  assert.match(svg, /North Street Prints/);
  assert.match(svg, /Counter 2/);
  assert.match(svg, /SCAN TO PRINT/);
  assert.match(svg, /Use your phone camera/);
  assert.match(svg, /POWERED BY SMARTPRINT/);
  assert.match(svg, /viewBox="0 0 29 29"/);
  assert.match(svg, /x="325" y="590" width="430" height="430"/);
});

test("customer-controlled shop text is escaped before entering SVG markup", () => {
  const svg = buildQrStationArtworkSvg({
    shopName: '<img src=x onerror=alert(1)>',
    stationName: 'Counter "A" & B',
    isEnabled: true
  }, qr);
  assert.match(svg, /&lt;img src=x onerror=alert\(1\)&gt;/);
  assert.match(svg, /Counter &quot;A&quot; &amp; B/);
  // Escaped event-handler text is safe inside an SVG text node. Inspect
  // actual element tags so harmless user-visible text does not false-positive.
  const elementTags = svg.match(/<(?=[A-Za-z])[^>]*>/g) ?? [];
  assert.ok(elementTags.every(tag => !/<img\\b|\\bonerror\\s*=/i.test(tag)));
});

test("disabled station exports are clearly marked as paused", () => {
  const svg = buildQrStationArtworkSvg({
    shopName: "North Street Prints",
    stationName: "Counter 2",
    isEnabled: false
  }, qr);
  assert.match(svg, /TEMPORARILY PAUSED/);
});

test("SVG exporter rejects missing view boxes and active content", () => {
  assert.throws(() => buildQrStationArtworkSvg({ shopName: "Shop", stationName: "Counter", isEnabled: true },
    '<svg><path d="M0 0"/></svg>'), /vector image/);
  assert.throws(() => buildQrStationArtworkSvg({ shopName: "Shop", stationName: "Counter", isEnabled: true },
    '<svg viewBox="0 0 1 1"><script>alert(1)</script></svg>'), /unsupported content/);
});

test("format helpers use the correct interoperable MIME types", () => {
  assert.equal(qrStationImageMimeType("png"), "image/png");
  assert.equal(qrStationImageMimeType("jpg"), "image/jpeg");
});

test("native file sharing uses the actual generated File when supported", async () => {
  const file = { name: "shop-qr.png", type: "image/png" } as File;
  let sharedFile: File | undefined;
  const result = await shareQrStationImage(file, {
    canShare: data => data?.files?.[0] === file,
    share: async data => { sharedFile = data?.files?.[0]; }
  });
  assert.equal(result, "shared");
  assert.equal(sharedFile, file);
});

test("unsupported file sharing returns a download-fallback signal", async () => {
  const file = { name: "shop-qr.jpg", type: "image/jpeg" } as File;
  assert.equal(await shareQrStationImage(file, {}), "unsupported");
  assert.equal(await shareQrStationImage(file, {
    canShare: () => false,
    share: async () => { throw new Error("must not run"); }
  }), "unsupported");
});

test("user cancellation is not reported as a successful share", async () => {
  const file = { name: "shop-qr.png", type: "image/png" } as File;
  assert.equal(await shareQrStationImage(file, {
    canShare: () => true,
    share: async () => { throw new DOMException("Cancelled", "AbortError"); }
  }), "cancelled");
});


test("logo failure or unsupported logo content falls back to a safe shop monogram", () => {
  const svg = buildQrStationArtworkSvg({
    shopName: "North Street Prints",
    stationName: "Counter 2",
    isEnabled: true,
    logoDataUri: "data:image/svg+xml;base64,PHN2Zy8+"
  }, qr);
  assert.match(svg, /<circle cx="196" cy="176" r="60"/);
  assert.ok(!svg.includes("data:image/svg+xml"));
});

test("JPG export and PNG export are explicitly named as distinct output formats", () => {
  assert.equal(qrStationImageMimeType("png"), "image/png");
  assert.equal(qrStationImageMimeType("jpg"), "image/jpeg");
});


test("canvas export produces PNG and JPEG files with matching MIME, extension and image signatures", async () => {
  const originalImage = Object.getOwnPropertyDescriptor(globalThis, "Image");
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const originalCreate = Object.getOwnPropertyDescriptor(URL, "createObjectURL");
  const originalRevoke = Object.getOwnPropertyDescriptor(URL, "revokeObjectURL");
  const requests: string[] = [];
  class MockImage {
    decoding = "sync";
    onload: ((event: Event) => void) | null = null;
    onerror: ((event: Event) => void) | null = null;
    set src(_value: string) { this.onload?.(new Event("load")); }
  }
  Object.defineProperty(globalThis, "Image", { configurable: true, writable: true, value: MockImage });
  Object.defineProperty(URL, "createObjectURL", { configurable: true, writable: true, value: () => "blob:qr-test" });
  Object.defineProperty(URL, "revokeObjectURL", { configurable: true, writable: true, value: () => undefined });
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    value: {
      createElement: (tag: string) => {
        assert.equal(tag, "canvas");
        return {
          width: 0,
          height: 0,
          getContext: () => ({
            fillStyle: "#fff", fillRect: () => undefined, imageSmoothingEnabled: false,
            imageSmoothingQuality: "low", drawImage: () => undefined
          }),
          toBlob: (callback: (blob: Blob | null) => void, type: string) => {
            requests.push(type);
            const signature = type === "image/png"
              ? [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]
              : [0xff, 0xd8, 0xff, 0xe0];
            callback(new Blob([new Uint8Array(signature)], { type }));
          }
        };
      }
    }
  });
  try {
    for (const [format, mime, extension, signature] of [
      ["png", "image/png", ".png", [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]],
      ["jpg", "image/jpeg", ".jpg", [0xff, 0xd8, 0xff, 0xe0]]
    ] as const) {
      const file = await createQrStationImageFile({
        shopName: "North Street Prints", stationName: "Counter 2", isEnabled: true
      }, qr, format);
      assert.equal(file.type, mime);
      assert.ok(file.name.endsWith(extension));
      assert.deepEqual(Array.from(new Uint8Array(await file.arrayBuffer()).slice(0, signature.length)), [...signature]);
    }
    assert.deepEqual(requests, ["image/png", "image/jpeg"]);
  } finally {
    if (originalImage) Object.defineProperty(globalThis, "Image", originalImage);
    else delete (globalThis as typeof globalThis & { Image?: typeof Image }).Image;
    if (originalDocument) Object.defineProperty(globalThis, "document", originalDocument);
    else delete (globalThis as typeof globalThis & { document?: Document }).document;
    if (originalCreate) Object.defineProperty(URL, "createObjectURL", originalCreate);
    if (originalRevoke) Object.defineProperty(URL, "revokeObjectURL", originalRevoke);
  }
});


test("share activation denial returns the normal file-download fallback", async () => {
  const file = { name: "shop-qr.png", type: "image/png" } as File;
  assert.equal(await shareQrStationImage(file, {
    canShare: () => true,
    share: async () => { throw new DOMException("User activation required", "NotAllowedError"); }
  }), "unsupported");
});
