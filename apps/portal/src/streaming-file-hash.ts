import { sha256 } from "@noble/hashes/sha2.js";

/** Maximum plaintext held while calculating a customer document digest. */
export const CUSTOMER_UPLOAD_HASH_CHUNK_BYTES = 1024 * 1024;

/**
 * Calculates the same SHA-256 digest as WebCrypto's digest("SHA-256") while
 * reading only bounded File/Blob slices. WebCrypto exposes no incremental
 * digest API, so noble-hashes provides the incremental SHA-256 state.
 *
 * Each temporary slice is zeroed immediately after it has been incorporated.
 */
export async function sha256Blob(blob: Blob): Promise<string> {
  if (!blob || typeof blob.slice !== "function" ||
      !Number.isSafeInteger(blob.size) || blob.size < 0) {
    throw new TypeError("A valid Blob is required for streaming SHA-256.");
  }

  const hasher = sha256.create();
  for (let offset = 0; offset < blob.size; offset += CUSTOMER_UPLOAD_HASH_CHUNK_BYTES) {
    const expectedLength = Math.min(
      CUSTOMER_UPLOAD_HASH_CHUNK_BYTES,
      blob.size - offset
    );
    const buffer = await blob
      .slice(offset, offset + expectedLength)
      .arrayBuffer();
    const chunk = new Uint8Array(buffer);

    if (chunk.byteLength !== expectedLength) {
      chunk.fill(0);
      throw new Error("The file changed or ended while its SHA-256 digest was being calculated.");
    }

    try {
      hasher.update(chunk);
    } finally {
      chunk.fill(0);
    }
  }

  const digest = hasher.digest();
  try {
    let hex = "";
    for (const byte of digest)
      hex += byte.toString(16).padStart(2, "0");
    return hex;
  } finally {
    digest.fill(0);
  }
}
