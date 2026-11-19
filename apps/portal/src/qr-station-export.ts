export type QrStationImageFormat = "png" | "jpg";

export interface QrStationArtworkOptions {
  shopName: string;
  stationName: string;
  isEnabled: boolean;
  logoDataUri?: string | null;
}

export type QrStationShareResult = "shared" | "cancelled" | "unsupported";

export interface QrStationShareNavigator {
  canShare?: (data?: ShareData) => boolean;
  share?: (data?: ShareData) => Promise<void>;
}

function escapeXml(value: string): string {
  return value.replace(/[<>&"']/g, character => {
    switch (character) {
      case "<": return "&lt;";
      case ">": return "&gt;";
      case "&": return "&amp;";
      case "\"": return "&quot;";
      default: return "&apos;";
    }
  });
}

function initials(value: string): string {
  const parts = value.trim().split(/\s+/).filter(Boolean);
  return (parts.length > 1 ? parts[0][0] + parts[1][0] : (parts[0] ?? "S").slice(0, 2)).toUpperCase();
}

function trustedLogo(value?: string | null): string | null {
  if (!value || !/^data:image\/(?:png|jpeg|webp);base64,[a-z0-9+/=\s]+$/i.test(value)) return null;
  return value.replace(/\s/g, "");
}

export function buildQrStationArtworkSvg(
  options: QrStationArtworkOptions,
  qrSvgMarkup: string
): string {
  const opening = qrSvgMarkup.match(/<svg\b([^>]*)>/i);
  const viewBox = opening?.[1].match(/\bviewBox=["']([^"']+)["']/i)?.[1];
  if (!opening || !viewBox || !/^[-\d.\s]+$/.test(viewBox)) {
    throw new Error("The station QR code could not be prepared as a vector image.");
  }
  const innerMarkup = qrSvgMarkup
    .replace(/^.*?<svg\b[^>]*>/is, "")
    .replace(/<\/svg>\s*$/i, "");
  if (/<\s*(?:script|foreignObject|image|use)\b|(?:href|xlink:href)\s*=|url\s*\(/i.test(innerMarkup)) {
    throw new Error("The QR artwork contains unsupported content.");
  }

  const shopName = escapeXml(options.shopName.trim() || "Your local print shop");
  const stationName = escapeXml(options.stationName.trim() || "Print station");
  const logo = trustedLogo(options.logoDataUri);
  const brandMark = logo
    ? '<image href="' + logo + '" x="136" y="116" width="120" height="120" preserveAspectRatio="xMidYMid meet"/>'
    : '<circle cx="196" cy="176" r="60" fill="#17243d"/><text x="196" y="190" text-anchor="middle" font-family="Arial,sans-serif" font-size="39" font-weight="700" fill="#ffffff">' + escapeXml(initials(options.shopName)) + '</text>';
  const pauseBanner = options.isEnabled ? "" :
    '<rect x="250" y="276" width="580" height="58" rx="29" fill="#fff0df"/><text x="540" y="313" text-anchor="middle" font-family="Arial,sans-serif" font-size="24" font-weight="700" fill="#963d08">TEMPORARILY PAUSED · ASK THE SHOP</text>';

  return [
    '<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="1080" height="1350" viewBox="0 0 1080 1350">',
    '<defs><linearGradient id="qr-accent" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#7157e8"/><stop offset="1" stop-color="#4b3caf"/></linearGradient></defs>',
    '<rect width="1080" height="1350" fill="#eef1f7"/>',
    '<rect x="54" y="48" width="972" height="1254" rx="42" fill="#ffffff"/>',
    '<rect x="54" y="48" width="972" height="12" rx="6" fill="url(#qr-accent)"/>',
    brandMark,
    '<text x="300" y="162" font-family="Arial,sans-serif" font-size="21" font-weight="700" letter-spacing="3" fill="#7258e8">YOUR LOCAL PRINT SHOP</text>',
    '<text x="300" y="216" font-family="Arial,sans-serif" font-size="43" font-weight="700" fill="#17243d" textLength="610" lengthAdjust="spacingAndGlyphs">' + shopName + '</text>',
    '<rect x="300" y="238" width="610" height="3" rx="2" fill="#e8eaf2"/>',
    pauseBanner,
    '<text x="540" y="405" text-anchor="middle" font-family="Arial,sans-serif" font-size="72" font-weight="800" letter-spacing="-2" fill="#17243d">SCAN TO PRINT</text>',
    '<text x="540" y="455" text-anchor="middle" font-family="Arial,sans-serif" font-size="27" fill="#58647a">Use your phone camera to send documents to this shop</text>',
    '<rect x="235" y="500" width="610" height="610" rx="35" fill="#ffffff" stroke="#e5e8f0" stroke-width="3"/>',
    '<rect x="265" y="530" width="550" height="550" rx="20" fill="#ffffff"/>',
    '<svg x="325" y="590" width="430" height="430" viewBox="' + escapeXml(viewBox) + '" shape-rendering="crispEdges">' + innerMarkup + '</svg>',
    '<text x="540" y="1170" text-anchor="middle" font-family="Arial,sans-serif" font-size="34" font-weight="700" fill="#17243d" textLength="820" lengthAdjust="spacingAndGlyphs">' + stationName + '</text>',
    '<text x="540" y="1222" text-anchor="middle" font-family="Arial,sans-serif" font-size="24" fill="#58647a">Scan · Upload · Review · Collect</text>',
    '<rect x="300" y="1250" width="480" height="2" fill="#e8eaf2"/>',
    '<text x="540" y="1284" text-anchor="middle" font-family="Arial,sans-serif" font-size="19" font-weight="700" letter-spacing="3" fill="#7258e8">POWERED BY SMARTPRINT</text>',
    '</svg>'
  ].join("");
}

export function qrStationImageMimeType(format: QrStationImageFormat): string {
  return format === "png" ? "image/png" : "image/jpeg";
}

function safeFileSegment(value: string): string {
  return value.normalize("NFKD").replace(/[\u0300-\u036f]/g, "")
    .replace(/[^a-z0-9]+/gi, "-").replace(/^-+|-+$/g, "").toLowerCase().slice(0, 48) || "shop";
}

export async function loadQrStationLogoDataUri(url?: string | null): Promise<string | null> {
  if (!url) return null;
  try {
    const response = await fetch(url, { mode: "cors", credentials: "omit" });
    if (!response.ok) return null;
    const blob = await response.blob();
    if (blob.size <= 0 || blob.size > 2 * 1024 * 1024 ||
        !["image/png", "image/jpeg", "image/webp"].includes(blob.type.toLowerCase())) return null;
    return await new Promise<string | null>(resolve => {
      const reader = new FileReader();
      reader.onload = () => resolve(typeof reader.result === "string" ? trustedLogo(reader.result) : null);
      reader.onerror = () => resolve(null);
      reader.readAsDataURL(blob);
    });
  } catch {
    // A cross-origin logo must never prevent a QR image export.
    return null;
  }
}

export async function createQrStationImageFile(
  options: QrStationArtworkOptions,
  qrSvgMarkup: string,
  format: QrStationImageFormat
): Promise<File> {
  const svg = buildQrStationArtworkSvg(options, qrSvgMarkup);
  const svgBlob = new Blob([svg], { type: "image/svg+xml;charset=utf-8" });
  const objectUrl = URL.createObjectURL(svgBlob);
  try {
    const image = new Image();
    image.decoding = "async";
    await new Promise<void>((resolve, reject) => {
      image.onload = () => resolve();
      image.onerror = () => reject(new Error("Could not render the QR image on this device."));
      image.src = objectUrl;
    });
    const canvas = document.createElement("canvas");
    canvas.width = 1080;
    canvas.height = 1350;
    const context = canvas.getContext("2d", { alpha: false });
    if (!context) throw new Error("Image export is not available in this browser.");
    context.fillStyle = "#ffffff";
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.imageSmoothingEnabled = true;
    context.imageSmoothingQuality = "high";
    context.drawImage(image, 0, 0, canvas.width, canvas.height);
    const mimeType = qrStationImageMimeType(format);
    const blob = await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob(
        value => value ? resolve(value) : reject(new Error("The QR image could not be encoded.")),
        mimeType,
        format === "jpg" ? 0.94 : undefined
      );
    });
    if (blob.type.toLowerCase() !== mimeType) {
      throw new Error("This browser could not create the selected image format. Choose PNG or try another browser.");
    }
    const extension = format === "jpg" ? "jpg" : "png";
    const fileName = safeFileSegment(options.shopName) + "-" +
      safeFileSegment(options.stationName) + "-smartprint-qr." + extension;
    return new File([blob], fileName, { type: mimeType, lastModified: Date.now() });
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
}

export function downloadQrStationImage(file: File): void {
  const url = URL.createObjectURL(file);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = file.name;
  anchor.style.display = "none";
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 30_000);
}

export async function shareQrStationImage(
  file: File,
  shareNavigator: QrStationShareNavigator = navigator
): Promise<QrStationShareResult> {
  if (typeof shareNavigator.share !== "function" || typeof shareNavigator.canShare !== "function") {
    return "unsupported";
  }
  try {
    if (!shareNavigator.canShare({ files: [file] })) return "unsupported";
  } catch {
    return "unsupported";
  }
  try {
    await shareNavigator.share({ files: [file], title: "SmartPrint shop QR", text: "Scan this QR code to send documents to the shop." });
    return "shared";
  } catch (error) {
    if (error && typeof error === "object" && "name" in error) {
      if (error.name === "AbortError") return "cancelled";
      if (error.name === "NotAllowedError") return "unsupported";
    }
    throw error;
  }
}
