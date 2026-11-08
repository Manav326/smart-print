import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import PhotoStudio, { type PreparedPhotoSheet } from "../src/PhotoStudio";
import { getPhotoSheetLayout } from "../src/photo-studio-layout";

declare global {
  interface Window {
    __photoStudioQaPageErrors?: string[];
  }
}
window.__photoStudioQaPageErrors = [];
window.addEventListener("error", event => {
  window.__photoStudioQaPageErrors?.push(event.message || "Uncaught browser error");
});
window.addEventListener("unhandledrejection", event => {
  const reason = event.reason instanceof Error ? event.reason.message : String(event.reason);
  window.__photoStudioQaPageErrors?.push("Unhandled rejection: " + reason);
});

const query = new URLSearchParams(window.location.search);
const mode = query.get("mode") === "shop" ? "shop" : "customer";
const dark = query.get("theme") === "dark";
const existingJobId = query.get("jobId") || "SP-PHOTO-STUDIO-QA-EXISTING-JOB";
const photoPrices = { "35x45": 2500, "25x35": 1800, "2x2": 3000, "4x6": 6000 };
const sizes = {
  "35x45": { widthMm: 35, heightMm: 45 },
  "25x35": { widthMm: 25, heightMm: 35 },
  "2x2": { widthMm: 50.8, heightMm: 50.8 },
  "4x6": { widthMm: 101.6, heightMm: 152.4 }
};

function fingerprint(bytes: Uint8Array): string {
  let hash = 2166136261;
  for (const byte of bytes) hash = Math.imul(hash ^ byte, 16777619);
  return (hash >>> 0).toString(16).padStart(8, "0") + ":" + bytes.length;
}

async function fileFingerprint(file: Blob): Promise<string> {
  return fingerprint(new Uint8Array(await file.arrayBuffer()));
}

function expectedBoxes(sizeCode: string, copies: number, perPage: number, pageIndex: number, captionHeightMm: number) {
  const size = sizes[sizeCode as keyof typeof sizes] ?? sizes["35x45"];
  const layout = getPhotoSheetLayout(size, captionHeightMm, perPage);
  const pageCopies = Math.max(0, Math.min(perPage, copies - pageIndex * perPage));
  const tileHeight = size.heightMm + captionHeightMm;
  const visibleRows = Math.max(1, Math.ceil(pageCopies / layout.columns));
  const visibleHeight = visibleRows * tileHeight + (visibleRows - 1) * 3;
  const pageOffsetY = Math.max(8, (297 - visibleHeight) / 2);
  const boxes = [];
  for (let index = 0; index < pageCopies; index += 1) {
    const column = index % layout.columns;
    const row = Math.floor(index / layout.columns);
    const rowStart = row * layout.columns;
    const rowCount = Math.min(layout.columns, pageCopies - rowStart);
    const rowWidth = rowCount * size.widthMm + (rowCount - 1) * 3;
    const rowOffset = (layout.contentWidthMm - rowWidth) / 2;
    boxes.push({
      xMm: layout.offsetXmm + rowOffset + column * (size.widthMm + 3),
      yMm: pageOffsetY + row * (tileHeight + 3),
      widthMm: size.widthMm,
      heightMm: size.heightMm
    });
  }
  return boxes;
}

async function inspectJpegPage(blob: Blob, boxes: ReturnType<typeof expectedBoxes>) {
  const url = URL.createObjectURL(blob);
  try {
    const image = new Image();
    image.src = url;
    await image.decode();
    const canvas = document.createElement("canvas");
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;
    const context = canvas.getContext("2d", { willReadFrequently: true });
    if (!context) throw new Error("Could not inspect the prepared A4 page image.");
    context.drawImage(image, 0, 0);
    const sample = (xMm: number, yMm: number) => {
      const x = Math.max(0, Math.min(canvas.width - 1, Math.round(xMm / 210 * canvas.width)));
      const y = Math.max(0, Math.min(canvas.height - 1, Math.round(yMm / 297 * canvas.height)));
      return Array.from(context.getImageData(x, y, 1, 1).data);
    };
    const photos = boxes.map(box => ({
      centre: sample(box.xMm + box.widthMm / 2, box.yMm + box.heightMm / 2),
      topLeft: sample(box.xMm + 1, box.yMm + 1),
      bottomRight: sample(box.xMm + box.widthMm - 1, box.yMm + box.heightMm - 1),
      rightEdge: sample(box.xMm + box.widthMm - 0.5, box.yMm + box.heightMm / 2),
      horizontalGap: sample(box.xMm + box.widthMm + 1.5, box.yMm + box.heightMm / 2)
    }));
    const isWhite = (rgba: number[]) => rgba[0] >= 238 && rgba[1] >= 238 && rgba[2] >= 238;
    return {
      width: image.naturalWidth,
      height: image.naturalHeight,
      photoCount: photos.length,
      allPhotoCentresContainImage: photos.every(item => !isWhite(item.centre)),
      allPhotoBoundsContainImage: photos.every(item =>
        !isWhite(item.topLeft) && !isWhite(item.bottomRight) && !isWhite(item.rightEdge)),
      allInterPhotoGapsRemainBlank: photos.every(item => isWhite(item.horizontalGap)),
      samples: photos
    };
  } finally {
    URL.revokeObjectURL(url);
  }
}

function containsBytes(haystack: Uint8Array, needle: Uint8Array): boolean {
  if (!needle.length || needle.length > haystack.length) return false;
  const prefix = new Uint32Array(needle.length);
  for (let i = 1, j = 0; i < needle.length; i += 1) {
    while (j > 0 && needle[i] !== needle[j]) j = prefix[j - 1];
    if (needle[i] === needle[j]) j += 1;
    prefix[i] = j;
  }
  for (let i = 0, j = 0; i < haystack.length; i += 1) {
    while (j > 0 && haystack[i] !== needle[j]) j = prefix[j - 1];
    if (haystack[i] === needle[j]) j += 1;
    if (j === needle.length) return true;
  }
  return false;
}

function createFixturePhoto(): Promise<File> {
  const canvas = document.createElement("canvas");
  canvas.width = 720;
  canvas.height = 960;
  const context = canvas.getContext("2d");
  if (!context) return Promise.reject(new Error("Could not prepare the browser fixture image."));
  const background = context.createLinearGradient(0, 0, 720, 960);
  background.addColorStop(0, "#d8e7f5");
  background.addColorStop(1, "#f4dfd0");
  context.fillStyle = background;
  context.fillRect(0, 0, 720, 960);
  context.fillStyle = "#526783";
  context.beginPath();
  context.ellipse(360, 335, 156, 205, 0, 0, Math.PI * 2);
  context.fill();
  context.fillStyle = "#e1b99c";
  context.beginPath();
  context.ellipse(360, 330, 132, 176, 0, 0, Math.PI * 2);
  context.fill();
  context.fillStyle = "#34465e";
  context.beginPath();
  context.moveTo(130, 960);
  context.quadraticCurveTo(155, 630, 360, 620);
  context.quadraticCurveTo(565, 630, 590, 960);
  context.closePath();
  context.fill();
  context.fillStyle = "#f4f7fb";
  context.fillRect(270, 515, 180, 82);
  return new Promise((resolve, reject) => {
    canvas.toBlob(blob => {
      if (!blob) {
        reject(new Error("Could not encode the browser fixture image."));
        return;
      }
      resolve(new File([blob], "photo-studio-qa.png", { type: "image/png" }));
    }, "image/png");
  });
}

function PhotoStudioBrowserFixture() {
  const [source, setSource] = useState<File | null>(null);
  const [open, setOpen] = useState(false);
  const [result, setResult] = useState("");
  const [error, setError] = useState("");
  const [sourceFingerprint, setSourceFingerprint] = useState("");

  useEffect(() => {
    let active = true;
    void createFixturePhoto().then(async file => {
      const digest = await fileFingerprint(file);
      if (active) {
        setSource(file);
        setSourceFingerprint(digest);
      }
    }).catch(reason => {
      if (active) setError(reason instanceof Error ? reason.message : String(reason));
    });
    return () => { active = false; };
  }, []);

  async function apply(sheets: PreparedPhotoSheet[]) {
    const sheet = sheets[0];
    const pdfBytes = new Uint8Array(await sheet.file.arrayBuffer());
    const pdfText = new TextDecoder("latin1").decode(pdfBytes);
    const pageCount = Number(pdfText.match(/\/Count (\d+)/)?.[1] ?? 0);
    const hasCaptions = document.querySelector<HTMLInputElement>("input[type=checkbox]")?.checked ?? false;
    const name = document.querySelector<HTMLInputElement>('input[placeholder*="Enter the name"]')?.value.trim() ?? "";
    const dob = document.querySelector<HTMLInputElement>('input[type="date"]')?.value ?? "";
    const captionHeightMm = hasCaptions && Boolean(name || dob) ? 7 : 0;
    const previews = sheet.previewPages ?? [];
    const pages = [];
    for (let pageIndex = 0; pageIndex < previews.length; pageIndex += 1) {
      const jpegBytes = new Uint8Array(await previews[pageIndex].arrayBuffer());
      const boxes = expectedBoxes(sheet.sizeCode, sheet.photoCount, sheet.photosPerPage, pageIndex, captionHeightMm);
      pages.push({
        pageNumber: pageIndex + 1,
        ...(await inspectJpegPage(previews[pageIndex], boxes)),
        jpegPayloadEmbeddedInPdf: containsBytes(pdfBytes, jpegBytes)
      });
    }
    const sourceAfter = source ? await fileFingerprint(source) : "";
    const sourceJobId = mode === "shop" ? existingJobId : null;
    setResult(JSON.stringify({
      mode,
      originalJobId: mode === "shop" ? existingJobId : null,
      sourceJobId,
      harness: "shared-component-only; persistence is verified by the separate shop-app browser test",
      sourceName: source?.name ?? "",
      sourceFingerprintBefore: sourceFingerprint,
      sourceFingerprintAfter: sourceAfter,
      sourceIntegrityPreserved: Boolean(sourceFingerprint) && sourceFingerprint === sourceAfter,
      mediaType: sheet.file.type,
      fileName: sheet.file.name,
      outputBytes: sheet.file.size,
      bytes: sheet.file.size,
      sizeCode: sheet.sizeCode,
      photoCount: sheet.photoCount,
      photosPerPage: sheet.photosPerPage,
      pageCount,
      pdfHasExactA4MediaBox: pdfText.includes("/MediaBox [0 0 595.275590551 841.88976378]"),
      pageImageCount: previews.length,
      backgroundSampled: document.querySelector('[data-testid=photo-studio-background-state] strong')?.textContent !== "Not selected",
      captionsIncluded: hasCaptions && Boolean(name || dob),
      pages
    }));
    setOpen(false);
  }

  function cancel() {
    setOpen(false);
  }

  return <main style={{
    minHeight: "100dvh",
    display: "grid",
    placeItems: "center",
    padding: 24,
    background: dark ? "#101722" : "#f3f5fa",
    color: dark ? "#eef3fb" : "#26344b",
    fontFamily: "system-ui, sans-serif"
  }}>
    <section style={{ display: "grid", gap: 12, maxWidth: 520 }}>
      <h1 style={{ margin: 0, fontSize: 22 }}>Photo Studio QA fixture</h1>
      <p style={{ margin: 0, lineHeight: 1.6 }}>A synthetic test image is used. No customer document or external service is loaded.</p>
      <button id="open-photo-studio" type="button" disabled={!source || !sourceFingerprint} onClick={() => { setError(""); setResult(""); setOpen(true); }}>
        Open Photo Studio
      </button>
      {error && <p role="alert">{error}</p>}
      <output id="photo-studio-result" data-ready={Boolean(source)}>{result}</output>
      <output id="photo-studio-source-fingerprint" hidden>{sourceFingerprint}</output>
    </section>
    {open && source && <PhotoStudio
      source={source}
      mode={mode}
      dark={dark}
      photoPrices={photoPrices}
      onCancel={cancel}
      onApply={apply}
    />}
  </main>;
}

createRoot(document.getElementById("photo-studio-qa-root")!).render(<PhotoStudioBrowserFixture />);
