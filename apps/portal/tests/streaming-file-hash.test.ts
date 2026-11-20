import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { test } from "node:test";
import {
  CUSTOMER_UPLOAD_HASH_CHUNK_BYTES,
  sha256Blob
} from "../src/streaming-file-hash.ts";

function nodeSha256(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
}

test("streaming SHA-256 matches the standard empty-file vector", async () => {
  assert.equal(
    await sha256Blob(new Blob([])),
    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
  );
});

test("streaming SHA-256 matches WebCrypto-equivalent bytes across chunk boundaries", async () => {
  const bytes = new Uint8Array(CUSTOMER_UPLOAD_HASH_CHUNK_BYTES * 3 + 137);
  for (let index = 0; index < bytes.length; index++)
    bytes[index] = (index * 31 + 7) & 0xff;

  assert.equal(await sha256Blob(new Blob([bytes])), nodeSha256(bytes));
});

test("streaming SHA-256 requests only bounded slices and covers the complete file", async () => {
  const bytes = new Uint8Array(CUSTOMER_UPLOAD_HASH_CHUNK_BYTES * 2 + 19);
  const blob = new Blob([bytes]);
  const originalSlice = blob.slice.bind(blob);
  const slices: Array<{ start: number; end: number; size: number }> = [];

  Object.defineProperty(blob, "slice", {
    configurable: true,
    value: (start = 0, end = blob.size, contentType?: string) => {
      const part = originalSlice(start, end, contentType);
      slices.push({ start, end, size: part.size });
      return part;
    }
  });

  assert.equal(await sha256Blob(blob), nodeSha256(bytes));
  assert.deepEqual(
    slices.map(slice => [slice.start, slice.end]),
    [
      [0, CUSTOMER_UPLOAD_HASH_CHUNK_BYTES],
      [CUSTOMER_UPLOAD_HASH_CHUNK_BYTES, CUSTOMER_UPLOAD_HASH_CHUNK_BYTES * 2],
      [CUSTOMER_UPLOAD_HASH_CHUNK_BYTES * 2, bytes.length]
    ]
  );
  assert.ok(slices.every(slice => slice.size <= CUSTOMER_UPLOAD_HASH_CHUNK_BYTES));
});

test("streaming SHA-256 rejects an incomplete slice rather than returning a partial digest", async () => {
  const blob = new Blob([new Uint8Array(CUSTOMER_UPLOAD_HASH_CHUNK_BYTES + 1)]);
  const originalSlice = blob.slice.bind(blob);
  Object.defineProperty(blob, "slice", {
    configurable: true,
    value: () => ({
      arrayBuffer: async () => new ArrayBuffer(0)
    })
  });

  await assert.rejects(
    () => sha256Blob(blob),
    /file changed or ended/i
  );
  // The original File/Blob remains immutable and reusable.
  assert.equal(blob.size, CUSTOMER_UPLOAD_HASH_CHUNK_BYTES + 1);
  assert.equal(originalSlice(0, 1).size, 1);
});
