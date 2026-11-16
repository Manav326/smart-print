import { sha256 } from "@noble/hashes/sha2.js";

const QSP2_HEADER_BYTES = 100;
const QSP2_HEADER_MAC_BYTES = 32;
const QSP2_FOOTER_CORE_BYTES = 52;
const QSP2_FOOTER_BYTES = 68;
const QSP2_TAG_BYTES = 16;
const QSP2_RECORD_LENGTH_BYTES = 4;
const QSP2_RECORD_BYTES = 1024 * 1024;
const DATA_KEY_BYTES = 32;

const HEADER_KEY_INFO = new TextEncoder().encode("SmartPrint.QSP2.HeaderKey.v1");
const RECORD_KEY_INFO = new TextEncoder().encode("SmartPrint.QSP2.RecordKey.v1");
const textEncoder = new TextEncoder();

export interface PrivateQsp2UploadAttempt {
  readonly wrappedKey: string;
  readonly encryptedSizeBytes: number;
  readonly body: ReadableStream<Uint8Array>;
}

export interface PrivateQsp2UploadFactory {
  readonly encryptedSizeBytes: number;
  createAttempt(): Promise<PrivateQsp2UploadAttempt>;
}

export interface PrivateQsp2UploadOptions {
  /** The paired Agent device ID; QSP2 binds it as the authorised scope. */
  readonly agentDeviceId: string;
  /** The immutable local print job ID assigned by the API ticket. */
  readonly printJobId: string;
  /** RSA-OAEP/SHA-256 SubjectPublicKeyInfo, base64 encoded by the Agent. */
  readonly encryptionPublicKey: string;
}

/**
 * Prepares a retryable factory for a customer Private Printing transfer.
 *
 * Each transport attempt gets a fresh AES-256 data key, salt and nonce prefix.
 * The payload itself is emitted one authenticated 1 MiB QSP2 record per stream
 * pull; no full plaintext/ciphertext ArrayBuffer or Blob is ever created.
 */
export async function preparePrivateQsp2Upload(
  file: File,
  options: PrivateQsp2UploadOptions
): Promise<PrivateQsp2UploadFactory> {
  if (!(file instanceof Blob) ||
      !Number.isSafeInteger(file.size) ||
      file.size <= 0 ||
      file.size > 50 * 1024 * 1024) {
    throw new TypeError("A non-empty Private Printing file within the 50 MiB limit is required.");
  }

  const scopeId = guidToBytes(options.agentDeviceId);
  const documentId = guidToBytes(options.printJobId);
  const publicKeyBytes = base64ToBytes(options.encryptionPublicKey);
  let rsaKey: CryptoKey;
  try {
    rsaKey = await crypto.subtle.importKey(
      "spki",
      publicKeyBytes,
      { name: "RSA-OAEP", hash: "SHA-256" },
      false,
      ["encrypt"]
    );
  } finally {
    publicKeyBytes.fill(0);
  }

  const encryptedSizeBytes = getQsp2EncryptedLength(file.size);
  return {
    encryptedSizeBytes,
    async createAttempt(): Promise<PrivateQsp2UploadAttempt> {
      const dataKey = crypto.getRandomValues(new Uint8Array(DATA_KEY_BYTES));
      const salt = crypto.getRandomValues(new Uint8Array(32));
      const noncePrefix = crypto.getRandomValues(new Uint8Array(4));
      let wrappedKeyBytes: ArrayBuffer | null = null;
      let header: Uint8Array | null = null;
      let headerMac: Uint8Array | null = null;
      let headerHash: Uint8Array | null = null;
      let headerMacKeyBytes: Uint8Array | null = null;
      let recordKeyRaw: Uint8Array | null = null;
      let recordKey: CryptoKey | null = null;

      try {
        wrappedKeyBytes = await crypto.subtle.encrypt({ name: "RSA-OAEP" }, rsaKey, dataKey);
        header = createHeader(file.size, scopeId, documentId, salt, noncePrefix);
        const headerBaseKey = await crypto.subtle.importKey(
          "raw",
          dataKey,
          "HKDF",
          false,
          ["deriveBits"]
        );
        headerMacKeyBytes = new Uint8Array(await crypto.subtle.deriveBits({
          name: "HKDF",
          hash: "SHA-256",
          salt,
          info: HEADER_KEY_INFO
        }, headerBaseKey, 256));
        const headerMacCryptoKey = await crypto.subtle.importKey(
          "raw",
          headerMacKeyBytes,
          { name: "HMAC", hash: "SHA-256" },
          false,
          ["sign"]
        );
        headerMac = new Uint8Array(await crypto.subtle.sign("HMAC", headerMacCryptoKey, header));

        recordKeyRaw = new Uint8Array(await crypto.subtle.deriveBits({
          name: "HKDF",
          hash: "SHA-256",
          salt,
          info: RECORD_KEY_INFO
        }, headerBaseKey, 256));
        recordKey = await crypto.subtle.importKey(
          "raw",
          recordKeyRaw,
          { name: "AES-GCM", length: 256 },
          false,
          ["encrypt"]
        );
        headerHash = new Uint8Array(await crypto.subtle.digest("SHA-256", header));

        const attemptHeader = header;
        const attemptHeaderMac = headerMac;
        const attemptHeaderHash = headerHash;
        const attemptNoncePrefix = noncePrefix;
        const attemptSalt = salt;
        const attemptRecordKey = recordKey;
        const body = createQsp2BodyStream(
          file,
          attemptHeader,
          attemptHeaderMac,
          attemptHeaderHash,
          attemptNoncePrefix,
          attemptSalt,
          attemptRecordKey
        );

        return {
          wrappedKey: bytesToBase64(new Uint8Array(wrappedKeyBytes)),
          encryptedSizeBytes,
          body
        };
      } catch (error) {
        if (header) header.fill(0);
        if (headerMac) headerMac.fill(0);
        if (headerHash) headerHash.fill(0);
        if (headerMacKeyBytes) headerMacKeyBytes.fill(0);
        if (recordKeyRaw) recordKeyRaw.fill(0);
        salt.fill(0);
        noncePrefix.fill(0);
        throw error;
      } finally {
        dataKey.fill(0);
        if (wrappedKeyBytes) new Uint8Array(wrappedKeyBytes).fill(0);
        if (headerMacKeyBytes) headerMacKeyBytes.fill(0);
        if (recordKeyRaw) recordKeyRaw.fill(0);
      }
    }
  };
}

export function getQsp2EncryptedLength(
  plaintextLength: number,
  recordSizeBytes = QSP2_RECORD_BYTES
): number {
  if (!Number.isSafeInteger(plaintextLength) || plaintextLength <= 0)
    throw new RangeError("QSP2 plaintext length must be a positive safe integer.");
  if (!Number.isSafeInteger(recordSizeBytes) || recordSizeBytes < 64 * 1024 || recordSizeBytes > 8 * 1024 * 1024)
    throw new RangeError("QSP2 record size must be between 64 KiB and 8 MiB.");

  const recordCount = Math.ceil(plaintextLength / recordSizeBytes);
  return QSP2_HEADER_BYTES + QSP2_HEADER_MAC_BYTES +
    plaintextLength + recordCount * (QSP2_RECORD_LENGTH_BYTES + QSP2_TAG_BYTES) +
    QSP2_FOOTER_BYTES;
}

function createHeader(
  fileSize: number,
  scopeId: Uint8Array,
  documentId: Uint8Array,
  salt: Uint8Array,
  noncePrefix: Uint8Array
): Uint8Array {
  const header = new Uint8Array(QSP2_HEADER_BYTES);
  header.set(textEncoder.encode("QSP2"), 0);
  header[4] = 2;
  header[5] = 1;
  writeU32(header, 8, QSP2_HEADER_BYTES);
  writeU64(header, 12, BigInt(fileSize));
  writeU32(header, 20, QSP2_RECORD_BYTES);
  writeU64(header, 24, BigInt(Math.ceil(fileSize / QSP2_RECORD_BYTES)));
  header.set(salt, 32);
  header.set(noncePrefix, 64);
  header.set(scopeId, 68);
  header.set(documentId, 84);
  return header;
}

function createQsp2BodyStream(
  file: Blob,
  header: Uint8Array,
  headerMac: Uint8Array,
  headerHash: Uint8Array,
  noncePrefix: Uint8Array,
  salt: Uint8Array,
  recordKey: CryptoKey
): ReadableStream<Uint8Array> {
  const fileHasher = sha256.create();
  const recordCount = Math.ceil(file.size / QSP2_RECORD_BYTES);
  let headerEmitted = false;
  let offset = 0;
  let recordIndex = 0;
  let footerEmitted = false;
  let disposed = false;

  const cleanup = () => {
    if (disposed) return;
    disposed = true;
    header.fill(0);
    headerMac.fill(0);
    headerHash.fill(0);
    noncePrefix.fill(0);
    salt.fill(0);
  };

  return new ReadableStream<Uint8Array>({
    async pull(controller) {
      try {
        if (!headerEmitted) {
          headerEmitted = true;
          const prefix = new Uint8Array(QSP2_HEADER_BYTES + QSP2_HEADER_MAC_BYTES);
          prefix.set(header, 0);
          prefix.set(headerMac, QSP2_HEADER_BYTES);
          controller.enqueue(prefix);
          return;
        }

        if (recordIndex < recordCount) {
          const length = Math.min(QSP2_RECORD_BYTES, file.size - offset);
          const chunk = new Uint8Array(await file.slice(offset, offset + length).arrayBuffer());
          if (chunk.byteLength !== length)
            throw new Error("The source file changed or ended during Private Printing encryption.");

          const digestAad = buildRecordAad(headerHash, recordIndex, length);
          const nonce = buildNonce(noncePrefix, recordIndex);
          let encrypted: ArrayBuffer | null = null;
          try {
            fileHasher.update(chunk);
            encrypted = await crypto.subtle.encrypt({
              name: "AES-GCM",
              iv: nonce,
              additionalData: digestAad,
              tagLength: 128
            }, recordKey, chunk);

            const cipherAndTag = new Uint8Array(encrypted);
            const record = new Uint8Array(QSP2_RECORD_LENGTH_BYTES + cipherAndTag.byteLength);
            writeU32(record, 0, length);
            record.set(cipherAndTag, QSP2_RECORD_LENGTH_BYTES);
            cipherAndTag.fill(0);

            offset += length;
            recordIndex++;
            controller.enqueue(record);
            return;
          } finally {
            chunk.fill(0);
            nonce.fill(0);
            digestAad.fill(0);
            if (encrypted) new Uint8Array(encrypted).fill(0);
          }
        }

        if (!footerEmitted) {
          footerEmitted = true;
          const digest = fileHasher.digest();
          const footerCore = new Uint8Array(QSP2_FOOTER_CORE_BYTES);
          footerCore.set(textEncoder.encode("QEND"), 0);
          writeU64(footerCore, 4, BigInt(recordCount));
          writeU64(footerCore, 12, BigInt(file.size));
          footerCore.set(digest, 20);

          const nonce = buildNonce(noncePrefix, recordCount);
          const aad = new Uint8Array(headerHash.byteLength + 1 + footerCore.byteLength);
          aad.set(headerHash, 0);
          aad[headerHash.byteLength] = 2;
          aad.set(footerCore, headerHash.byteLength + 1);
          try {
            const encryptedTag = await crypto.subtle.encrypt({
              name: "AES-GCM",
              iv: nonce,
              additionalData: aad,
              tagLength: 128
            }, recordKey, new Uint8Array(0));
            const tag = new Uint8Array(encryptedTag);
            if (tag.byteLength !== QSP2_TAG_BYTES)
              throw new Error("The final QSP2 authentication tag has an invalid size.");

            const footer = new Uint8Array(QSP2_FOOTER_BYTES);
            footer.set(footerCore, 0);
            footer.set(tag, QSP2_FOOTER_CORE_BYTES);
            tag.fill(0);
            new Uint8Array(encryptedTag).fill(0);
            controller.enqueue(footer);
          } finally {
            digest.fill(0);
            footerCore.fill(0);
            nonce.fill(0);
            aad.fill(0);
            cleanup();
          }
          return;
        }

        controller.close();
        cleanup();
      } catch (error) {
        cleanup();
        controller.error(error);
      }
    },
    cancel() {
      cleanup();
    }
  });
}

function buildRecordAad(headerHash: Uint8Array, index: number, recordLength: number): Uint8Array {
  const aad = new Uint8Array(headerHash.byteLength + 1 + 8 + 4);
  aad.set(headerHash, 0);
  aad[headerHash.byteLength] = 1;
  writeU64(aad, headerHash.byteLength + 1, BigInt(index));
  writeU32(aad, headerHash.byteLength + 9, recordLength);
  return aad;
}

function buildNonce(prefix: Uint8Array, recordIndex: number): Uint8Array {
  const nonce = new Uint8Array(12);
  nonce.set(prefix, 0);
  writeU64(nonce, 4, BigInt(recordIndex));
  return nonce;
}

function writeU32(target: Uint8Array, offset: number, value: number): void {
  new DataView(target.buffer, target.byteOffset, target.byteLength)
    .setUint32(offset, value, false);
}

function writeU64(target: Uint8Array, offset: number, value: bigint): void {
  new DataView(target.buffer, target.byteOffset, target.byteLength)
    .setBigUint64(offset, value, false);
}

function guidToBytes(value: string): Uint8Array {
  const compact = value.replace(/-/g, "");
  if (!/^[0-9a-f]{32}$/i.test(compact))
    throw new TypeError("A canonical Agent or print-job GUID is required.");
  const result = new Uint8Array(16);
  for (let index = 0; index < 16; index++)
    result[index] = Number.parseInt(compact.slice(index * 2, index * 2 + 2), 16);
  return result;
}

function base64ToBytes(value: string): Uint8Array {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++)
    bytes[index] = binary.charCodeAt(index);
  return bytes;
}

function bytesToBase64(value: Uint8Array): string {
  let binary = "";
  for (let offset = 0; offset < value.length; offset += 0x8000)
    binary += String.fromCharCode(...value.subarray(offset, Math.min(value.length, offset + 0x8000)));
  return btoa(binary);
}
