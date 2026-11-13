import test from "node:test";
import assert from "node:assert/strict";
import {
  copyCustomerUploadFileMetadata,
  normalizeCustomerUploadFile
} from "../src/customer-upload-file-policy.ts";

test("normalizes extensionless Android-provider JPEG, PNG and PDF files", async () => {
  const jpeg = new File([new Uint8Array([0xff, 0xd8, 0xff, 0xdb])], "Camera image", { type: "image/jpeg" });
  const png = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])], "Scanned card", { type: "" });
  const pdf = new File([new TextEncoder().encode("%PDF-1.7")], "Identity document", { type: "application/octet-stream" });

  const normalizedJpeg = await normalizeCustomerUploadFile(jpeg);
  const normalizedPng = await normalizeCustomerUploadFile(png);
  const normalizedPdf = await normalizeCustomerUploadFile(pdf);

  assert.equal(normalizedJpeg?.name, "Camera image.jpg");
  assert.equal(normalizedJpeg?.type, "image/jpeg");
  assert.equal(normalizedPng?.name, "Scanned card.png");
  assert.equal(normalizedPng?.type, "image/png");
  assert.equal(normalizedPdf?.name, "Identity document.pdf");
  assert.equal(normalizedPdf?.type, "application/pdf");
});

test("keeps the original File when only the provider MIME type is generic", async () => {
  const source = new File([new Uint8Array([0xff, 0xd8, 0xff, 0xdb])], "photo.jpg", { type: "application/octet-stream" });
  const normalized = await normalizeCustomerUploadFile(source);
  assert.equal(normalized, source);
  assert.equal(normalized?.name, "photo.jpg");
});

test("preserves ID Studio identity through extension normalization", async () => {
  const source = new File([new TextEncoder().encode("%PDF-1.7")], "identity sheet", { type: "application/octet-stream" });
  Object.assign(source, { __smartPrintPrintKind: "ID_DOCUMENT", __smartPrintBatchGroup: "ID Studio" });
  const normalized = await normalizeCustomerUploadFile(source);
  assert.ok(normalized);
  copyCustomerUploadFileMetadata(source, normalized);
  const tagged = normalized as File & { __smartPrintPrintKind?: string; __smartPrintBatchGroup?: string };
  assert.equal(tagged.__smartPrintPrintKind, "ID_DOCUMENT");
  assert.equal(tagged.__smartPrintBatchGroup, "ID Studio");
  assert.equal(normalized.name, "identity sheet.pdf");
});

test("finds a PDF signature within the first 1024 bytes", async () => {
  const bytes = new Uint8Array(1028);
  bytes.set(new TextEncoder().encode("%PDF-1.4"), 1018);
  const source = new File([bytes], "document", { type: "application/octet-stream" });
  const normalized = await normalizeCustomerUploadFile(source);
  assert.equal(normalized?.name, "document.pdf");
  assert.equal(normalized?.type, "application/pdf");
});

test("corrects a conflicting extension from the verified file signature", async () => {
  const pdf = new File([new TextEncoder().encode("%PDF-1.7")], "scan.jpg", { type: "image/jpeg" });
  const normalized = await normalizeCustomerUploadFile(pdf);
  assert.equal(normalized?.name, "scan.pdf");
  assert.equal(normalized?.type, "application/pdf");
});

test("rejects known unsupported HEIC, WebP, GIF and TIFF signatures", async () => {
  const heic = new Uint8Array(12);
  heic.set(new TextEncoder().encode("ftypheic"), 4);
  const webp = new Uint8Array(12);
  webp.set(new TextEncoder().encode("RIFFxxxxWEBP"));
  const gif = new TextEncoder().encode("GIF89a");
  const tiff = new Uint8Array([0x49, 0x49, 0x2a, 0x00]);

  for (const bytes of [heic, webp, gif, tiff]) {
    const source = new File([bytes], "photo.jpg", { type: "application/octet-stream" });
    assert.equal(await normalizeCustomerUploadFile(source), null);
  }
  const declaredHeic = new File([new Uint8Array([1, 2, 3])], "photo.jpg", { type: "image/heic" });
  assert.equal(await normalizeCustomerUploadFile(declaredHeic), null);
});

test("preserves ID/Photo Studio metadata, folder path and native preview token", async () => {
  const source = new File([new Uint8Array([0xff, 0xd8, 0xff, 0xdb])], "private-photo", { type: "image/jpeg" });
  Object.assign(source, {
    __smartPrintBatchGroup: "Photo Studio",
    __smartPrintPrintKind: "PHOTO",
    __smartPrintPhotoSettings: { sizeCode: "35x45", photoCount: 8, photosPerPage: 8 },
    webkitRelativePath: "Photo Studio/private-photo"
  });
  const previewTokens = new WeakMap<File, string>();
  previewTokens.set(source, "preview-token");

  const normalized = await normalizeCustomerUploadFile(source);
  assert.ok(normalized);
  copyCustomerUploadFileMetadata(source, normalized, previewTokens);

  const tagged = normalized as File & {
    __smartPrintBatchGroup?: string;
    __smartPrintPrintKind?: string;
    __smartPrintPhotoSettings?: { sizeCode: string; photoCount: number; photosPerPage: number };
    webkitRelativePath?: string;
  };
  assert.equal(tagged.__smartPrintBatchGroup, "Photo Studio");
  assert.equal(tagged.__smartPrintPrintKind, "PHOTO");
  assert.deepEqual(tagged.__smartPrintPhotoSettings, { sizeCode: "35x45", photoCount: 8, photosPerPage: 8 });
  assert.equal(tagged.webkitRelativePath, "Photo Studio/private-photo");
  assert.equal(previewTokens.get(normalized), "preview-token");
});
