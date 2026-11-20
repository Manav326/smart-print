export type QrStationImageFormat = "png" | "jpg";

export interface QrStationArtworkOptions {
  shopName: string;
  stationName: string;
  isEnabled: boolean;
  logoDataUri?: string | null;
}

export interface QrStationArtworkSource {
  options: QrStationArtworkOptions;
  qrSvgMarkup: string;
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

/**
 * This is the single QR poster artwork used by both the print view and image
 * exports. PNG and JPG are only different encodings of this exact SVG source.
 */
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
  if (/<\s*(?:script|foreignObject|image|use)\b|(?:href|xlink:href)\s*=|\bon[a-z]+\s*=|url\s*\(/i.test(innerMarkup)) {
    throw new Error("The QR artwork contains unsupported content.");
  }

  const shopName = escapeXml(options.shopName.trim() || "Your local print shop");
  const stationName = escapeXml(options.stationName.trim() || "Print station");
  const logo = trustedLogo(options.logoDataUri);
  const brandMark = logo
    ? '<circle cx="154" cy="154" r="58" fill="#f0edff"/><image href="' + logo + '" x="106" y="106" width="96" height="96" preserveAspectRatio="xMidYMid meet"/>'
    : '<circle cx="154" cy="154" r="58" fill="#17243d"/><text x="154" y="170" text-anchor="middle" font-family="Arial,sans-serif" font-size="42" font-weight="700" fill="#ffffff">' + escapeXml(initials(options.shopName)) + '</text>';

  const pauseBanner = options.isEnabled ? "" :
    '<g><rect x="1328" y="258" width="554" height="62" rx="31" fill="#fff0df"/><text x="1605" y="297" text-anchor="middle" font-family="Arial,sans-serif" font-size="23" font-weight="700" fill="#963d08">TEMPORARILY PAUSED · ASK THE SHOP</text></g>';

  const qrCode = '<svg x="1408" y="408" width="398" height="398" viewBox="' + escapeXml(viewBox) + '" shape-rendering="crispEdges">' + innerMarkup + '</svg>';

  return [
    '<svg xmlns="http://www.w3.org/2000/svg" width="2480" height="1748" viewBox="0 0 2100 1480">',
    '<defs>',
    '<linearGradient id="qe-poster-accent" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#8068ec"/><stop offset="1" stop-color="#5141b9"/></linearGradient>',
    '<linearGradient id="qe-poster-panel" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#28335f"/><stop offset="1" stop-color="#151e39"/></linearGradient>',
    '</defs>',
    '<rect width="2100" height="1480" fill="#eef1f7"/>',
    '<rect x="42" y="42" width="2016" height="1396" rx="48" fill="#ffffff"/>',
    '<rect x="42" y="42" width="2016" height="14" rx="7" fill="url(#qe-poster-accent)"/>',

    '<g>',
    brandMark,
    '<text x="292" y="145" font-family="Arial,sans-serif" font-size="22" font-weight="700" letter-spacing="3" fill="#7258e8">YOUR LOCAL PRINT SHOP</text>',
    '<text x="292" y="207" font-family="Arial,sans-serif" font-size="52" font-weight="700" fill="#17243d" textLength="730" lengthAdjust="spacingAndGlyphs">' + shopName + '</text>',
    '<rect x="872" y="112" width="244" height="58" rx="29" fill="#f5f2ff" stroke="#e6e0ff" stroke-width="2"/>',
    '<text x="994" y="148" text-anchor="middle" font-family="Arial,sans-serif" font-size="18" font-weight="700" letter-spacing="2" fill="#6d57cf">QUICK &amp; EASY</text>',
    '</g>',

    '<g>',
    '<text x="124" y="390" font-family="Arial,sans-serif" font-size="22" font-weight="800" letter-spacing="3" fill="#755ce0">YOUR PRINTING, MADE SIMPLE</text>',
    '<text x="124" y="506" font-family="Arial,sans-serif" font-size="98" font-weight="800" letter-spacing="-3" fill="#18233b">How it works</text>',
    '<text x="126" y="568" font-family="Arial,sans-serif" font-size="26" font-weight="500" fill="#69758b">Send your documents in a few taps. Collect them at the counter.</text>',
    '</g>',

    '<g>',
    '<rect x="116" y="642" width="1020" height="180" rx="26" fill="#fbfcff" stroke="#e8ebf2" stroke-width="3"/>',
    '<rect x="116" y="840" width="1020" height="180" rx="26" fill="#fbfcff" stroke="#e8ebf2" stroke-width="3"/>',
    '<rect x="116" y="1038" width="1020" height="180" rx="26" fill="#fbfcff" stroke="#e8ebf2" stroke-width="3"/>',

    '<rect x="154" y="678" width="112" height="108" rx="24" fill="#f0edff" stroke="#e4defd" stroke-width="3"/>',
    '<circle cx="256" cy="688" r="18" fill="#765ce5" stroke="#ffffff" stroke-width="4"/>',
    '<text x="256" y="695" text-anchor="middle" font-family="Arial,sans-serif" font-size="15" font-weight="800" fill="#ffffff">01</text>',
    '<path d="M184 710h-12v-12m64 0h-12m12 0v12m-64 42v12h12m52 0h12v-12M192 732h34m-34 15h23" fill="none" stroke="#7058d8" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/>',
    '<text x="308" y="714" font-family="Arial,sans-serif" font-size="31" font-weight="800" fill="#202b42">Scan this QR code</text>',
    '<text x="308" y="758" font-family="Arial,sans-serif" font-size="22" fill="#657287">Open the link, then choose or photograph your documents.</text>',
    '<circle cx="1080" cy="732" r="24" fill="#f0edff"/><text x="1080" y="741" text-anchor="middle" font-family="Arial,sans-serif" font-size="30" font-weight="800" fill="#755ce0">↓</text>',

    '<rect x="154" y="876" width="112" height="108" rx="24" fill="#eaf7f2" stroke="#d9eee8" stroke-width="3"/>',
    '<circle cx="256" cy="886" r="18" fill="#249675" stroke="#ffffff" stroke-width="4"/>',
    '<text x="256" y="893" text-anchor="middle" font-family="Arial,sans-serif" font-size="15" font-weight="800" fill="#ffffff">02</text>',
    '<rect x="188" y="903" width="44" height="56" rx="6" fill="none" stroke="#24866a" stroke-width="5"/>',
    '<path d="M198 918h23m-23 12h17m-17 12 7 7 16-18" fill="none" stroke="#24866a" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>',
    '<text x="308" y="912" font-family="Arial,sans-serif" font-size="31" font-weight="800" fill="#202b42">Send for shop review</text>',
    '<text x="308" y="956" font-family="Arial,sans-serif" font-size="22" fill="#657287">Your shop checks the files before printing.</text>',
    '<circle cx="1080" cy="930" r="24" fill="#eaf7f2"/><text x="1080" y="939" text-anchor="middle" font-family="Arial,sans-serif" font-size="30" font-weight="800" fill="#24866a">↓</text>',

    '<rect x="154" y="1074" width="112" height="108" rx="24" fill="#fff2e8" stroke="#f7e4d6" stroke-width="3"/>',
    '<circle cx="256" cy="1084" r="18" fill="#df793e" stroke="#ffffff" stroke-width="4"/>',
    '<text x="256" y="1091" text-anchor="middle" font-family="Arial,sans-serif" font-size="15" font-weight="800" fill="#ffffff">03</text>',
    '<path d="M178 1110h64v31h-64zm10-17h44v17h-44zm8 48v16h28v-16m-22-30h16" fill="none" stroke="#d56a2e" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>',
    '<text x="308" y="1110" font-family="Arial,sans-serif" font-size="31" font-weight="800" fill="#202b42">Collect your printouts</text>',
    '<text x="308" y="1154" font-family="Arial,sans-serif" font-size="22" fill="#657287">Visit the counter when your copies are ready.</text>',
    '<circle cx="1080" cy="1128" r="24" fill="#fff2e8"/><text x="1080" y="1138" text-anchor="middle" font-family="Arial,sans-serif" font-size="26" font-weight="800" fill="#d56a2e">✓</text>',
    '</g>',

    '<line x1="122" y1="1300" x2="1134" y2="1300" stroke="#e8eaf1" stroke-width="3"/>',
    '<text x="126" y="1360" font-family="Arial,sans-serif" font-size="38" font-weight="800" fill="#17243d">QuantaEdge</text>',
    '<text x="128" y="1394" font-family="Arial,sans-serif" font-size="18" font-weight="600" fill="#7c879b">Smarter Decisions. Greater Growth.</text>',
    '<text x="1128" y="1360" text-anchor="end" font-family="Arial,sans-serif" font-size="15" font-weight="800" letter-spacing="2" fill="#8993a5">POWERING A SIMPLER PRINT EXPERIENCE</text>',

    '<rect x="1190" y="84" width="820" height="1312" rx="48" fill="url(#qe-poster-panel)" stroke="#303c66" stroke-width="3"/>',
    '<circle cx="1920" cy="188" r="168" fill="none" stroke="#ffffff" stroke-opacity=".07" stroke-width="3"/>',
    '<circle cx="1920" cy="188" r="128" fill="none" stroke="#ffffff" stroke-opacity=".07" stroke-width="3"/>',
    '<circle cx="1266" cy="1300" r="130" fill="none" stroke="#ffffff" stroke-opacity=".05" stroke-width="3"/>',
    '<circle cx="1300" cy="190" r="9" fill="#7ee0bd"/>',
    '<text x="1325" y="198" font-family="Arial,sans-serif" font-size="21" font-weight="800" letter-spacing="3" fill="#dce5ff">START HERE</text>',
    pauseBanner,
    '<rect x="1320" y="340" width="560" height="560" rx="34" fill="#ffffff" stroke="#e8eaf1" stroke-width="3"/>',
    '<rect x="1350" y="370" width="500" height="500" rx="18" fill="#ffffff"/>',
    qrCode,
    '<text x="1600" y="1010" text-anchor="middle" font-family="Arial,sans-serif" font-size="58" font-weight="900" letter-spacing="2" fill="#ffffff">SCAN TO PRINT</text>',
    '<text x="1600" y="1060" text-anchor="middle" font-family="Arial,sans-serif" font-size="22" font-weight="600" fill="#c7cee0">Open your phone camera and scan this code</text>',
    '<rect x="1370" y="1120" width="460" height="72" rx="36" fill="#ffffff" fill-opacity=".08" stroke="#525d86" stroke-width="2"/>',
    '<circle cx="1410" cy="1156" r="8" fill="#7ee0bd"/>',
    '<text x="1600" y="1168" text-anchor="middle" font-family="Arial,sans-serif" font-size="25" font-weight="800" fill="#ffffff" textLength="370" lengthAdjust="spacingAndGlyphs">' + stationName + '</text>',
    '<text x="1600" y="1320" text-anchor="middle" font-family="Arial,sans-serif" font-size="17" font-weight="800" letter-spacing="3" fill="#919dbd">UPLOAD • REVIEW • COLLECT</text>',
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

    // A5 landscape at approximately 300 DPI. The QR itself is still rendered
    // from vector paths before the selected PNG/JPG encoding is applied.
    const canvas = document.createElement("canvas");
    canvas.width = 2480;
    canvas.height = 1748;
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
