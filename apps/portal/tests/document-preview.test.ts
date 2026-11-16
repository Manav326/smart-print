import test from "node:test";
import assert from "node:assert/strict";
import {
  identifyDocumentPreviewMediaType,
  supportsInlinePdfPreview
} from "../src/document-preview.ts";

test("preview media type is identified from PDF, JPEG and PNG signatures", async () => {
  const pdf = new Blob(["%PDF-1.7\n"]);
  const jpeg = new Blob([Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10])]);
  const png = new Blob([Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])]);

  assert.equal(await identifyDocumentPreviewMediaType(pdf), "application/pdf");
  assert.equal(await identifyDocumentPreviewMediaType(jpeg), "image/jpeg");
  assert.equal(await identifyDocumentPreviewMediaType(png), "image/png");
});

test("a filename or MIME label cannot turn unsupported content into a preview", async () => {
  assert.equal(await identifyDocumentPreviewMediaType(new Blob(["<html>Open PDF</html>"], { type: "application/pdf" })), null);
  assert.equal(await identifyDocumentPreviewMediaType(new Blob([])), null);
  assert.equal(await identifyDocumentPreviewMediaType(new Blob(["not an image"], { type: "image/jpeg" })), null);
});

test("inline PDF support respects the browser capability signal", () => {
  assert.equal(supportsInlinePdfPreview({ pdfViewerEnabled: true, userAgent: "Android; wv) Chrome/130" }), true);
  assert.equal(supportsInlinePdfPreview({ pdfViewerEnabled: false, userAgent: "Chrome/130" }), false,
    "An explicit unsupported signal takes priority over the browser name");
});

test("legacy Firefox and Safari PDF viewers are supported, unknown WebViews are not", () => {
  assert.equal(supportsInlinePdfPreview({ userAgent: "Mozilla/5.0 Firefox/120" }), true);
  assert.equal(supportsInlinePdfPreview({ userAgent: "Mozilla/5.0 Version/17.0 Safari/605.1" }), true);
  assert.equal(supportsInlinePdfPreview({ userAgent: "Mozilla/5.0 Chrome/130" }), false);
  assert.equal(supportsInlinePdfPreview({ userAgent: "Mozilla/5.0 (Linux; Android 14; wv) Chrome/130" }), false);
});
