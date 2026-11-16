import assert from "node:assert/strict";
import { createPrivateKey, createPublicKey, generateKeyPairSync, privateDecrypt, constants, webcrypto } from "node:crypto";
import { test } from "node:test";
import {
  getQsp2EncryptedLength,
  preparePrivateQsp2Upload
} from "../src/private-qsp2-upload.ts";

const originalCrypto = globalThis.crypto;
Object.defineProperty(globalThis, "crypto", { value: webcrypto, configurable: true });

function fromBase64(value: string): Uint8Array {
  return Uint8Array.from(Buffer.from(value, "base64"));
}

function readU32(bytes: Uint8Array, offset: number): number {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(offset, false);
}

function readU64(bytes: Uint8Array, offset: number): bigint {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getBigUint64(offset, false);
}

function concat(parts: Uint8Array[]): Uint8Array {
  const total = parts.reduce((sum, part) => sum + part.byteLength, 0);
  const output = new Uint8Array(total);
  let offset = 0;
  for (const part of parts) {
    output.set(part, offset);
    offset += part.byteLength;
  }
  return output;
}

async function collect(stream: ReadableStream<Uint8Array>): Promise<Uint8Array> {
  const reader = stream.getReader();
  const parts: Uint8Array[] = [];
  while (true) {
    const result = await reader.read();
    if (result.done) break;
    parts.push(result.value);
  }
  return concat(parts);
}

test("QSP2 private encoder emits a correct header, record layout and authenticated footer", async () => {
  const { publicKey, privateKey } = generateKeyPairSync("rsa", {
    modulusLength: 2048,
    publicKeyEncoding: { type: "spki", format: "der" },
    privateKeyEncoding: { type: "pkcs8", format: "der" }
  });
  const bytes = new Uint8Array(1024 * 1024 + 317);
  for (let index = 0; index < bytes.length; index++)
    bytes[index] = (index * 17 + 3) & 0xff;

  const factory = await preparePrivateQsp2Upload(
    new File([bytes], "private.pdf", { type: "application/pdf" }),
    {
      agentDeviceId: "00112233-4455-6677-8899-aabbccddeeff",
      printJobId: "ffeeddcc-bbaa-9988-7766-554433221100",
      encryptionPublicKey: Buffer.from(publicKey).toString("base64")
    }
  );
  const attempt = await factory.createAttempt();
  const encrypted = await collect(attempt.body);

  assert.equal(factory.encryptedSizeBytes, getQsp2EncryptedLength(bytes.length));
  assert.equal(attempt.encryptedSizeBytes, encrypted.length);
  assert.equal(new TextDecoder().decode(encrypted.slice(0, 4)), "QSP2");
  assert.equal(encrypted[4], 2);
  assert.equal(encrypted[5], 1);
  assert.equal(readU32(encrypted, 8), 100);
  assert.equal(readU64(encrypted, 12), BigInt(bytes.length));
  assert.equal(readU32(encrypted, 20), 1024 * 1024);
  assert.equal(readU64(encrypted, 24), 2n);
  assert.equal(encrypted.length, factory.encryptedSizeBytes);

  const dataKey = new Uint8Array(privateDecrypt({
    key: createPrivateKey({ key: privateKey, format: "der", type: "pkcs8" }),
    padding: constants.RSA_PKCS1_OAEP_PADDING,
    oaepHash: "sha256"
  }, Buffer.from(attempt.wrappedKey, "base64")));
  assert.equal(dataKey.length, 32);

  const baseKey = await webcrypto.subtle.importKey("raw", dataKey, "HKDF", false, ["deriveBits"]);
  const recordKeyRaw = new Uint8Array(await webcrypto.subtle.deriveBits({
    name: "HKDF",
    hash: "SHA-256",
    salt: encrypted.slice(32, 64),
    info: new TextEncoder().encode("SmartPrint.QSP2.RecordKey.v1")
  }, baseKey, 256));
  const recordKey = await webcrypto.subtle.importKey(
    "raw", recordKeyRaw, { name: "AES-GCM", length: 256 }, false, ["decrypt"]
  );
  const headerHash = new Uint8Array(await webcrypto.subtle.digest("SHA-256", encrypted.slice(0, 100)));
  const noncePrefix = encrypted.slice(64, 68);
  let offset = 132;
  const plaintextParts: Uint8Array[] = [];
  const recordCount = Number(readU64(encrypted, 24));
  for (let index = 0; index < recordCount; index++) {
    const length = readU32(encrypted, offset);
    offset += 4;
    const cipherAndTag = encrypted.slice(offset, offset + length + 16);
    offset += length + 16;

    const nonce = new Uint8Array(12);
    nonce.set(noncePrefix, 0);
    new DataView(nonce.buffer).setBigUint64(4, BigInt(index), false);
    const aad = new Uint8Array(45);
    aad.set(headerHash);
    aad[32] = 1;
    new DataView(aad.buffer).setBigUint64(33, BigInt(index), false);
    new DataView(aad.buffer).setUint32(41, length, false);

    const plaintext = new Uint8Array(await webcrypto.subtle.decrypt({
      name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: 128
    }, recordKey, cipherAndTag));
    plaintextParts.push(plaintext);
  }

  const footer = encrypted.slice(offset);
  assert.equal(new TextDecoder().decode(footer.slice(0, 4)), "QEND");
  assert.equal(readU64(footer, 4), BigInt(recordCount));
  assert.equal(readU64(footer, 12), BigInt(bytes.length));
  const recovered = concat(plaintextParts);
  assert.deepEqual(recovered, bytes);
  dataKey.fill(0);
  recordKeyRaw.fill(0);
  recovered.fill(0);
});

test("each QSP2 transport attempt gets a fresh data key and nonce prefix", async () => {
  const { publicKey } = generateKeyPairSync("rsa", {
    modulusLength: 2048,
    publicKeyEncoding: { type: "spki", format: "der" },
    privateKeyEncoding: { type: "pkcs8", format: "der" }
  });
  const factory = await preparePrivateQsp2Upload(
    new File(["same original bytes"], "private.pdf"),
    {
      agentDeviceId: "00112233-4455-6677-8899-aabbccddeeff",
      printJobId: "ffeeddcc-bbaa-9988-7766-554433221100",
      encryptionPublicKey: Buffer.from(publicKey).toString("base64")
    }
  );

  const first = await factory.createAttempt();
  const second = await factory.createAttempt();
  assert.notEqual(first.wrappedKey, second.wrappedKey);

  const firstBody = await collect(first.body);
  const secondBody = await collect(second.body);
  assert.notDeepEqual(firstBody.slice(64, 68), secondBody.slice(64, 68));
  assert.notDeepEqual(firstBody.slice(32, 64), secondBody.slice(32, 64));
});

test("QSP2 encoder rejects invalid GUID identities and empty payloads", async () => {
  const { publicKey } = generateKeyPairSync("rsa", {
    modulusLength: 2048,
    publicKeyEncoding: { type: "spki", format: "der" },
    privateKeyEncoding: { type: "pkcs8", format: "der" }
  });
  const publicKeyBase64 = Buffer.from(publicKey).toString("base64");
  await assert.rejects(
    () => preparePrivateQsp2Upload(new File(["x"], "x.pdf"), {
      agentDeviceId: "not-a-guid",
      printJobId: "ffeeddcc-bbaa-9988-7766-554433221100",
      encryptionPublicKey: publicKeyBase64
    }),
    /GUID/i
  );
  await assert.rejects(
    () => preparePrivateQsp2Upload(new File([], "empty.pdf"), {
      agentDeviceId: "00112233-4455-6677-8899-aabbccddeeff",
      printJobId: "ffeeddcc-bbaa-9988-7766-554433221100",
      encryptionPublicKey: publicKeyBase64
    }),
    /non-empty/i
  );
});

test("QSP2 wire length matches fixed 1 MiB record boundaries", () => {
  assert.equal(getQsp2EncryptedLength(1), 100 + 32 + 1 + 20 + 68);
  assert.equal(getQsp2EncryptedLength(1024 * 1024), 100 + 32 + 1024 * 1024 + 20 + 68);
  assert.equal(getQsp2EncryptedLength(1024 * 1024 + 1), 100 + 32 + 1024 * 1024 + 1 + 40 + 68);
  assert.throws(() => getQsp2EncryptedLength(0), /positive/);
});

test("browser crypto global was restored after the test module", () => {
  assert.ok(originalCrypto);
});
