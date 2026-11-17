import { UPLOAD_LIMITS } from "./upload-limits.generated.ts";

export type CustomerUploadLimitFailure =
  | { code: "FILE_SIZE"; fileIndex: number }
  | { code: "FILE_COUNT" }
  | { code: "BATCH_SIZE" };

/** Validates new files together with files already accepted into the same batch. */
export function validateCustomerUploadBatchLimits(
  existingFileCount: number,
  existingBytes: number,
  incomingFileSizes: readonly number[]
): CustomerUploadLimitFailure | null {
  if (!Number.isSafeInteger(existingFileCount) || existingFileCount < 0 ||
      !Number.isSafeInteger(existingBytes) || existingBytes < 0) {
    return { code: "BATCH_SIZE" };
  }
  if (existingFileCount + incomingFileSizes.length > UPLOAD_LIMITS.maxBatchFiles) {
    return { code: "FILE_COUNT" };
  }
  if (existingBytes > UPLOAD_LIMITS.maxBatchBytes) {
    return { code: "BATCH_SIZE" };
  }

  let totalBytes = existingBytes;
  for (let index = 0; index < incomingFileSizes.length; index += 1) {
    const size = incomingFileSizes[index];
    if (!Number.isSafeInteger(size) || size <= 0 || size > UPLOAD_LIMITS.maxFileBytes) {
      return { code: "FILE_SIZE", fileIndex: index };
    }
    if (size > UPLOAD_LIMITS.maxBatchBytes - totalBytes) {
      return { code: "BATCH_SIZE" };
    }
    totalBytes += size;
  }
  return null;
}
