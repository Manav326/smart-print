export type PreviewMediaType = "application/pdf" | "image/jpeg" | "image/png";

const PDF_SIGNATURE = [0x25, 0x50, 0x44, 0x46, 0x2d];
const PNG_SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

function startsWithBytes(bytes: Uint8Array, signature: readonly number[]): boolean {
  return signature.every((value, index) => bytes[index] === value);
}

function containsBytes(bytes: Uint8Array, signature: readonly number[]): boolean {
  for (let offset = 0; offset <= Math.min(bytes.length - signature.length, 1024 - signature.length); offset += 1) {
    if (signature.every((value, index) => bytes[offset + index] === value)) return true;
  }
  return false;
}

/**
 * Identify only formats that the portal can preview, using the file contents
 * instead of trusting a filename or a potentially incorrect HTTP MIME header.
 */
export async function identifyDocumentPreviewMediaType(blob: Blob): Promise<PreviewMediaType | null> {
  if (!blob || blob.size === 0) return null;

  const header = new Uint8Array(await blob.slice(0, 1024).arrayBuffer());
  if (containsBytes(header, PDF_SIGNATURE)) return "application/pdf";
  if (startsWithBytes(header, PNG_SIGNATURE)) return "image/png";
  if (header.length >= 3 && header[0] === 0xff && header[1] === 0xd8 && header[2] === 0xff) {
    return "image/jpeg";
  }
  return null;
}

export interface InlinePdfPreviewCapabilities {
  pdfViewerEnabled?: boolean;
  userAgent?: string;
}

/**
 * Prefer the browser's explicit PDF-viewer capability signal. Older Safari and
 * Firefox releases do not expose it but provide a built-in inline PDF viewer.
 * Unknown/embedded WebViews are treated as unsupported instead of launching an
 * external PDF handler or showing a non-functional "Open PDF" prompt.
 */
export function supportsInlinePdfPreview(capabilities: InlinePdfPreviewCapabilities): boolean {
  if (typeof capabilities.pdfViewerEnabled === "boolean") return capabilities.pdfViewerEnabled;

  const userAgent = capabilities.userAgent ?? "";
  if (/Android/i.test(userAgent) && /;\s*wv\)/i.test(userAgent)) return false;
  if (/Firefox\//i.test(userAgent)) return true;

  const safariOnly = /Safari\//i.test(userAgent) &&
    !/(Chrome|CriOS|Chromium|Edg|OPR|Android)/i.test(userAgent);
  return safariOnly;
}

export function currentBrowserSupportsInlinePdfPreview(): boolean {
  if (typeof navigator === "undefined") return false;
  const browser = navigator as Navigator & { pdfViewerEnabled?: boolean };
  return supportsInlinePdfPreview({
    pdfViewerEnabled: browser.pdfViewerEnabled,
    userAgent: browser.userAgent
  });
}
