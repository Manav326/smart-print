import test from "node:test";
import assert from "node:assert/strict";
import { isSupportedPhotoSource, resolvePhotoSourceMimeType } from "../src/photo-source-policy.ts";

test("accepts the supported JPEG and PNG MIME types", () => {
  assert.equal(resolvePhotoSourceMimeType({ type: "image/jpeg", name: "photo.jpg" }), "image/jpeg");
  assert.equal(resolvePhotoSourceMimeType({ type: "image/png", name: "photo.png" }), "image/png");
  assert.equal(resolvePhotoSourceMimeType({ type: "image/jpeg; charset=binary", name: "photo.bin" }), "image/jpeg");
});

test("uses a JPG or PNG extension only when Android provides no MIME type", () => {
  assert.equal(resolvePhotoSourceMimeType({ type: "", name: "photo.jpg" }), "image/jpeg");
  assert.equal(resolvePhotoSourceMimeType({ type: "", name: "photo.jpeg" }), "image/jpeg");
  assert.equal(resolvePhotoSourceMimeType({ type: "", name: "photo.png" }), "image/png");
  assert.equal(isSupportedPhotoSource({ type: "", name: "photo.heic" }), false);
});

test("does not relabel a known unsupported image type using a misleading filename", () => {
  assert.equal(resolvePhotoSourceMimeType({ type: "image/webp", name: "photo.jpg" }), null);
  assert.equal(resolvePhotoSourceMimeType({ type: "image/heic", name: "photo.png" }), null);
  assert.equal(resolvePhotoSourceMimeType({ type: "image/gif", name: "photo.jpeg" }), null);
  assert.equal(isSupportedPhotoSource({ type: "application/pdf", name: "photo.jpg" }), false);
});

test("rejects unsupported filenames when the Android provider supplies no MIME type", () => {
  for (const name of ["photo.webp", "photo.heic", "photo.gif", "document.pdf", "photo"]) {
    assert.equal(resolvePhotoSourceMimeType({ type: "", name }), null, name);
  }
});
