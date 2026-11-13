export type CustomerUploadMimeType = "application/pdf" | "image/jpeg" | "image/png";

type TaggedCustomerFile = File & {
  __smartPrintBatchGroup?: string;
  __smartPrintPrintKind?: string;
  __smartPrintPhotoSettings?: {
    sizeCode: string;
    photoCount: number;
    photosPerPage: number;
  };
  webkitRelativePath?: string;
};

const extensionMime: Record<string, CustomerUploadMimeType> = {
  pdf: "application/pdf",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  png: "image/png"
};

const mimeExtension: Record<CustomerUploadMimeType, string> = {
  "application/pdf": "pdf",
  "image/jpeg": "jpg",
  "image/png": "png"
};

const genericMimeTypes = new Set(["", "application/octet-stream", "binary/octet-stream"]);

function ascii(bytes: Uint8Array, offset: number, length: number): string {
  return Array.from(bytes.subarray(offset, offset + length), value => String.fromCharCode(value)).join("");
}

function startsWith(bytes: Uint8Array, signature: number[]): boolean {
  return bytes.length >= signature.length && signature.every((value, index) => bytes[index] === value);
}

function detectSignature(bytes: Uint8Array): CustomerUploadMimeType | "UNSUPPORTED" | null {
  const pdf = [0x25, 0x50, 0x44, 0x46, 0x2d]; // %PDF-
  const lastPdfOffset = Math.min(1023, bytes.length - pdf.length);
  for (let offset = 0; offset <= lastPdfOffset; offset++) {
    if (pdf.every((value, index) => bytes[offset + index] === value))
      return "application/pdf";
  }

  if (startsWith(bytes, [0xff, 0xd8, 0xff])) return "image/jpeg";
  if (startsWith(bytes, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) return "image/png";

  // Explicitly recognized unsupported formats must not be relabelled by their
  // filenames. The shop upload contract intentionally supports only PDF/JPEG/PNG.
  if (ascii(bytes, 0, 6) === "GIF87a" || ascii(bytes, 0, 6) === "GIF89a" ||
      ascii(bytes, 0, 2) === "BM" ||
      (ascii(bytes, 0, 4) === "RIFF" && ascii(bytes, 8, 4) === "WEBP") ||
      (ascii(bytes, 4, 4) === "ftyp" &&
        ["heic", "heix", "hevc", "hevx", "mif1", "msf1", "avif"].includes(ascii(bytes, 8, 4))) ||
      startsWith(bytes, [0x49, 0x49, 0x2a, 0x00]) ||
      startsWith(bytes, [0x4d, 0x4d, 0x00, 0x2a])) {
    return "UNSUPPORTED";
  }

  return null;
}

function resolveDeclaredMimeType(value: string): CustomerUploadMimeType | null | "UNSUPPORTED" {
  const normalized = value.split(";")[0].trim().toLowerCase();
  if (normalized === "application/pdf" || normalized === "application/x-pdf")
    return "application/pdf";
  if (normalized === "image/jpeg" || normalized === "image/jpg")
    return "image/jpeg";
  if (normalized === "image/png") return "image/png";
  if (genericMimeTypes.has(normalized)) return null;
  return "UNSUPPORTED";
}

function resolveExtensionMimeType(name: string): CustomerUploadMimeType | null {
  const extension = name.toLowerCase().split(".").pop() ?? "";
  return extensionMime[extension] ?? null;
}

function normalizedFileName(name: string, mimeType: CustomerUploadMimeType): string {
  const expected = mimeExtension[mimeType];
  const dot = name.lastIndexOf(".");
  const currentExtension = dot > 0 ? name.slice(dot + 1).toLowerCase() : "";
  const extensionMatches = mimeType === "image/jpeg"
    ? currentExtension === "jpg" || currentExtension === "jpeg"
    : currentExtension === expected;
  if (extensionMatches) return name;
  const base = dot > 0 ? name.slice(0, dot) : name;
  return `${base || "document"}.${expected}`;
}

/**
 * Android content providers sometimes expose valid files with a generic MIME
 * type or no filename extension. Read only a bounded header, validate known
 * signatures, and normalize the extension before the API derives media type.
 */
export async function normalizeCustomerUploadFile(file: File): Promise<File | null> {
  if (file.size <= 0) return null;
  const header = new Uint8Array(await file.slice(0, 1028).arrayBuffer());
  const signature = detectSignature(header);
  if (signature === "UNSUPPORTED") return null;

  const declared = resolveDeclaredMimeType(file.type);
  if (declared === "UNSUPPORTED") return null;
  const mimeType = signature ?? declared ?? resolveExtensionMimeType(file.name);
  if (!mimeType || mimeType === "UNSUPPORTED") return null;

  const name = normalizedFileName(file.name, mimeType);
  if (name === file.name && file.type === mimeType) return file;
  return new File([file], name, { type: mimeType, lastModified: file.lastModified });
}

/** Preserve Android Studio identity and browser folder/preview metadata on File clones. */
export function copyCustomerUploadFileMetadata(
  source: File,
  target: File,
  previewTokens?: WeakMap<File, string>
): void {
  const original = source as TaggedCustomerFile;
  const normalized = target as TaggedCustomerFile;
  if (original.__smartPrintBatchGroup !== undefined)
    normalized.__smartPrintBatchGroup = original.__smartPrintBatchGroup;
  if (original.__smartPrintPrintKind !== undefined)
    normalized.__smartPrintPrintKind = original.__smartPrintPrintKind;
  if (original.__smartPrintPhotoSettings !== undefined)
    normalized.__smartPrintPhotoSettings = original.__smartPrintPhotoSettings;
  if (original.webkitRelativePath) {
    Object.defineProperty(normalized, "webkitRelativePath", {
      configurable: true,
      enumerable: true,
      value: original.webkitRelativePath
    });
  }
  const previewToken = previewTokens?.get(source);
  if (previewToken && previewTokens) previewTokens.set(target, previewToken);
}
