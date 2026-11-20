import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { UPLOAD_LIMITS } from "../src/upload-limits.generated.ts";
import { validateCustomerUploadBatchLimits } from "../src/upload-batch-policy.ts";

const MiB = 1024 * 1024;

describe("customer upload limits for web, local and private routes", () => {
  it("accepts one file exactly at 50 MiB", () => {
    assert.equal(validateCustomerUploadBatchLimits(0, 0, [UPLOAD_LIMITS.maxFileBytes]), null);
  });

  it("rejects one byte above the 50 MiB per-file limit", () => {
    assert.deepEqual(
      validateCustomerUploadBatchLimits(0, 0, [UPLOAD_LIMITS.maxFileBytes + 1]),
      { code: "FILE_SIZE", fileIndex: 0 }
    );
  });

  it("accepts 20 files totalling exactly 500 MiB", () => {
    const sizes = Array(20).fill(25 * MiB);
    assert.equal(sizes.length, UPLOAD_LIMITS.maxBatchFiles);
    assert.equal(sizes.reduce((total, size) => total + size, 0), UPLOAD_LIMITS.maxBatchBytes);
    assert.equal(validateCustomerUploadBatchLimits(0, 0, sizes), null);
  });

  it("rejects a 21st file even when bytes remain", () => {
    assert.deepEqual(
      validateCustomerUploadBatchLimits(20, 20, [1]),
      { code: "FILE_COUNT" }
    );
  });

  it("rejects one byte above the 500 MiB batch limit", () => {
    assert.deepEqual(
      validateCustomerUploadBatchLimits(19, UPLOAD_LIMITS.maxBatchBytes - 1, [2]),
      { code: "BATCH_SIZE" }
    );
  });

  it("includes previously uploaded files when accepting the last file", () => {
    assert.equal(
      validateCustomerUploadBatchLimits(
        19,
        UPLOAD_LIMITS.maxBatchBytes - UPLOAD_LIMITS.maxFileBytes,
        [UPLOAD_LIMITS.maxFileBytes]
      ),
      null
    );
  });
});
