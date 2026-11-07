import { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import PhotoStudio, { type PreparedPhotoSheet } from "../src/PhotoStudio";
import { getPhotoSheetPhotoPositions, type PhotoSheetSize } from "../src/photo-studio-layout";

const query = new URLSearchParams(window.location.search);
const mode = query.get("mode") === "shop" ? "shop" : "customer";
const dark = query.get("theme") === "dark";
const photoPrices = query.get("prices") === "disabled"
  ? { "35x45": 0, "25x35": 0, "2x2": 0, "4x6": 0 }
  : { "35x45": 2500, "25x35": 1800, "2x2": 3000, "4x6": 6000 };
const failFirstApply = query.get("failApply") === "1";
const delayApply = query.get("delayApply") === "1";
const invalidSource = query.get("source") === "invalid";
const failRasterOnce = query.get("failRender") === "1";
const oversizedRaster = query.get("oversizeRaster") === "1";

function createFixturePhoto(): Promise<File> {
  if (invalidSource) {
    return Promise.resolve(new File(["not an image"], "invalid-photo.png", { type: "image/png" }));
  }
  const canvas = document.createElement("canvas");
  canvas.width = 720;
  canvas.height = 960;
  const context = canvas.getContext("2d");
  if (!context) return Promise.reject(new Error("Could not prepare the browser fixture image."));
  context.fillStyle = "#d8e7f5";
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
  const applyAttempts = useRef(0);

  useEffect(() => {
    let active = true;
    void createFixturePhoto().then(file => {
      if (!active) return;
      if (failRasterOnce || oversizedRaster) {
        const originalToBlob = HTMLCanvasElement.prototype.toBlob;
        let failureUsed = false;
        HTMLCanvasElement.prototype.toBlob = function (callback, type, quality) {
          if (this.width === 2480 && this.height === 3508) {
            if (oversizedRaster) {
              callback(new Blob([new Uint8Array(20 * 1024 * 1024 + 8192)], { type: type || "image/jpeg" }));
              return;
            }
            if (failRasterOnce && !failureUsed) {
              failureUsed = true;
              callback(null);
              return;
            }
          }
          originalToBlob.call(this, callback, type, quality);
        };
      }
      setSource(file);
    }).catch(reason => {
      if (active) setError(reason instanceof Error ? reason.message : String(reason));
    });
    return () => { active = false; };
  }, []);

  async function apply(sheets: PreparedPhotoSheet[]) {
    const sheet = sheets[0];
    applyAttempts.current += 1;
    if (delayApply) await new Promise(resolve => window.setTimeout(resolve, 600));
    if (failFirstApply && applyAttempts.current === 1) {
      throw new Error("Simulated Photo Studio save failure. Your original photo is unchanged; retry is safe.");
    }
    const pdfBytes = new Uint8Array(await sheet.file.arrayBuffer());
    const pdfText = new TextDecoder("latin1").decode(pdfBytes);
    const pageCount = Number(pdfText.match(/\/Count (\d+)/)?.[1] ?? 0);
    const embeddedPreviewPages = await Promise.all((sheet.previewPages ?? []).map(async pageBlob => {
      const pageBytes = new Uint8Array(await pageBlob.arrayBuffer());
      return pdfText.includes(new TextDecoder("latin1").decode(pageBytes));
    }));
    const mediaBoxCount = (pdfText.match(/\/MediaBox \[0 0 595\.275590551 841\.88976378\]/g) ?? []).length;
    const pdfImageSizes = [...pdfText.matchAll(/\/Width (\d+) \/Height (\d+) \/ColorSpace/g)]
      .map(match => [Number(match[1]), Number(match[2])]);
    const fullPageImageTransforms = (pdfText.match(/595\.275590551 0 0 841\.88976378 0 0 cm/g) ?? []).length;
    const sizeByCode: Record<string, PhotoSheetSize> = {
      "35x45": { widthMm: 35, heightMm: 45 },
      "25x35": { widthMm: 25, heightMm: 35 },
      "2x2": { widthMm: 50.8, heightMm: 50.8 },
      "4x6": { widthMm: 101.6, heightMm: 152.4 }
    };
    const photoSize = sizeByCode[sheet.sizeCode];
    if (!photoSize) throw new Error("Photo Studio returned an unsupported photo size.");
    const jpegPages = sheet.previewPages ?? [];
    const captionHeightMm = sheet.captionHeightMm;
    const pixelProbes: Array<{ page: number; slot: number; rgb: number[] }> = [];
    let finalUnusedSlotRgb: number[] | null = null;
    for (let pageIndex = 0; pageIndex < jpegPages.length; pageIndex++) {
      const bitmap = await createImageBitmap(jpegPages[pageIndex]);
      try {
        const canvas = document.createElement("canvas");
        canvas.width = bitmap.width;
        canvas.height = bitmap.height;
        const context = canvas.getContext("2d", { willReadFrequently: true });
        if (!context) throw new Error("Could not inspect the prepared photo raster.");
        context.drawImage(bitmap, 0, 0);
        const countOnPage = Math.min(sheet.photosPerPage, sheet.photoCount - pageIndex * sheet.photosPerPage);
        const positions = getPhotoSheetPhotoPositions(photoSize, captionHeightMm, sheet.photosPerPage, countOnPage);
        const fullGrid = getPhotoSheetPhotoPositions(photoSize, captionHeightMm, sheet.photosPerPage, sheet.photosPerPage);
        const sampleMm = (xMm: number, yMm: number) => {
          const x = Math.max(0, Math.min(bitmap.width - 1, Math.round(xMm * bitmap.width / 210)));
          const y = Math.max(0, Math.min(bitmap.height - 1, Math.round(yMm * bitmap.height / 297)));
          return Array.from(context.getImageData(x, y, 1, 1).data).slice(0, 3);
        };
        const sample = (position: { xMm: number; yMm: number }) =>
          sampleMm(position.xMm + photoSize.widthMm / 2, position.yMm + photoSize.heightMm * 0.35);
        const sampleBackground = (position: { xMm: number; yMm: number }) =>
          sampleMm(position.xMm + 2, position.yMm + 2);
        const countRedCaptionPixels = (position: { xMm: number; yMm: number }) => {
          if (captionHeightMm <= 0) return 0;
          const left = Math.max(0, Math.floor(position.xMm * bitmap.width / 210));
          const top = Math.max(0, Math.floor((position.yMm + photoSize.heightMm) * bitmap.height / 297));
          const width = Math.min(bitmap.width - left, Math.ceil(photoSize.widthMm * bitmap.width / 210));
          const height = Math.min(bitmap.height - top, Math.ceil(captionHeightMm * bitmap.height / 297));
          const pixels = context.getImageData(left, top, width, height).data;
          let matches = 0;
          for (let index = 0; index < pixels.length; index += 4) {
            if (pixels[index] > 140 && pixels[index + 1] < 100 && pixels[index + 2] < 100) matches += 1;
          }
          return matches;
        };
        positions.forEach((position, slot) => pixelProbes.push({
          page: pageIndex + 1,
          slot,
          rgb: sample(position),
          backgroundRgb: sampleBackground(position),
          captionRedPixels: countRedCaptionPixels(position)
        }));
        if (pageIndex === jpegPages.length - 1 && countOnPage < sheet.photosPerPage) {
          finalUnusedSlotRgb = sample(fullGrid[countOnPage]);
        }
      } finally {
        bitmap.close();
      }
    }
    setResult(JSON.stringify({
      mode,
      mediaType: sheet.file.type,
      fileName: sheet.file.name,
      sizeCode: sheet.sizeCode,
      photoCount: sheet.photoCount,
      photosPerPage: sheet.photosPerPage,
      pageCount,
      mediaBoxCount,
      pdfImageSizes,
      fullPageImageTransforms,
      previewPageCount: jpegPages.length,
      embeddedPreviewPages,
      pixelProbes,
      finalUnusedSlotRgb,
      applyAttempts: applyAttempts.current,
      bytes: sheet.file.size
    }));
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
      <button id="open-photo-studio" type="button" disabled={!source} onClick={() => { setError(""); setOpen(true); }}>
        Open Photo Studio
      </button>
      {error && <p role="alert">{error}</p>}
      <output id="photo-studio-result" data-ready={Boolean(source)}>{result}</output>
    </section>
    {open && source && <PhotoStudio
      source={source}
      mode={mode}
      dark={dark}
      photoPrices={photoPrices}
      onCancel={() => setOpen(false)}
      onApply={apply}
    />}
  </main>;
}

createRoot(document.getElementById("photo-studio-qa-root")!).render(<PhotoStudioBrowserFixture />);
