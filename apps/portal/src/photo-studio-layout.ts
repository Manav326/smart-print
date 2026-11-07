export const A4_WIDTH_MM = 210;
export const A4_HEIGHT_MM = 297;
export const A4_WIDTH_PX = 2480;
export const A4_HEIGHT_PX = 3508;
export const A4_WIDTH_POINTS = 595.275590551;
export const A4_HEIGHT_POINTS = 841.88976378;
export const PHOTO_SHEET_MARGIN_MM = 8;
export const PHOTO_SHEET_GUTTER_MM = 3;

export type PhotoSheetSize = { widthMm: number; heightMm: number };
export type PhotoSheetLayout = {
  columns: number; rows: number; perSheet: number;
  offsetXmm: number; offsetYmm: number; contentWidthMm: number; contentHeightMm: number;
};

/** captionHeightMm adds a label region without shrinking the physical photo. */
export function getPhotoSheetLayout(size: PhotoSheetSize, captionHeightMm = 0, requestedPhotosPerPage?: number): PhotoSheetLayout {
  const margin = PHOTO_SHEET_MARGIN_MM, gap = PHOTO_SHEET_GUTTER_MM, tileHeight = size.heightMm + captionHeightMm;
  const usableW = A4_WIDTH_MM - margin * 2, usableH = A4_HEIGHT_MM - margin * 2;
  const maximumColumns = Math.max(1, Math.floor((usableW + gap) / (size.widthMm + gap)));
  const maximumRows = Math.max(1, Math.floor((usableH + gap) / (tileHeight + gap)));
  const maximumPhotos = maximumColumns * maximumRows;
  if (requestedPhotosPerPage !== undefined &&
      (!Number.isInteger(requestedPhotosPerPage) || requestedPhotosPerPage < 1 || requestedPhotosPerPage > maximumPhotos)) {
    throw new RangeError(`Photos per A4 page must be between 1 and ${maximumPhotos} for this layout.`);
  }

  // Keep the requested density, while ensuring every photo still fits at its exact physical size.
  const perSheet = requestedPhotosPerPage ?? maximumPhotos;
  const columns = Math.min(maximumColumns, perSheet);
  const rows = Math.max(1, Math.ceil(perSheet / columns));
  const contentWidthMm = columns * size.widthMm + (columns - 1) * gap;
  const contentHeightMm = rows * tileHeight + (rows - 1) * gap;
  return {
    columns, rows, perSheet,
    offsetXmm: Math.max(margin, (A4_WIDTH_MM - contentWidthMm) / 2),
    offsetYmm: margin,
    contentWidthMm, contentHeightMm
  };
}


export type PhotoSheetPhotoPosition = { photoIndex: number; row: number; column: number; xMm: number; yMm: number };

/**
 * Returns deterministic cut-grid coordinates for the populated slots on one A4 page.
 * Every page uses the same origin and slot positions; a partial final row leaves later
 * slots empty instead of recentering the remaining photos.
 */
export function getPhotoSheetPhotoPositions(
  size: PhotoSheetSize,
  captionHeightMm: number,
  photosPerPage: number,
  photosOnPage: number
): PhotoSheetPhotoPosition[] {
  const layout = getPhotoSheetLayout(size, captionHeightMm, photosPerPage);
  if (!Number.isInteger(photosOnPage) || photosOnPage < 0 || photosOnPage > photosPerPage) {
    throw new RangeError(`Photos on this A4 page must be between 0 and ${photosPerPage}.`);
  }

  return Array.from({ length: photosOnPage }, (_, photoIndex) => {
    const column = photoIndex % layout.columns;
    const row = Math.floor(photoIndex / layout.columns);
    return {
      photoIndex,
      row,
      column,
      xMm: layout.offsetXmm + column * (size.widthMm + PHOTO_SHEET_GUTTER_MM),
      yMm: layout.offsetYmm + row * (size.heightMm + captionHeightMm + PHOTO_SHEET_GUTTER_MM)
    };
  });
}

/** Checks the cropped pixels available after framing/zoom against both 300-DPI axes. */
export function hasSufficientPhotoResolution(
  sourceWidth: number,
  sourceHeight: number,
  size: PhotoSheetSize,
  zoom = 1,
  dpi = 300
): boolean {
  if (![sourceWidth, sourceHeight, size.widthMm, size.heightMm, zoom, dpi].every(Number.isFinite) ||
      sourceWidth <= 0 || sourceHeight <= 0 || size.widthMm <= 0 || size.heightMm <= 0 || zoom < 1 || dpi <= 0) {
    return false;
  }

  const targetRatio = size.widthMm / size.heightMm;
  let cropWidth = sourceWidth;
  let cropHeight = sourceHeight;
  if (sourceWidth / sourceHeight > targetRatio) {
    cropWidth = sourceHeight * targetRatio;
  } else {
    cropHeight = sourceWidth / targetRatio;
  }
  cropWidth /= zoom;
  cropHeight /= zoom;

  const requiredWidth = size.widthMm / 25.4 * dpi;
  const requiredHeight = size.heightMm / 25.4 * dpi;
  // Avoid rejecting exact-DPI sources because of sub-pixel floating-point division drift.
  const pixelTolerance = 1e-6;
  return cropWidth + pixelTolerance >= requiredWidth && cropHeight + pixelTolerance >= requiredHeight;
}

/** One exact-A4 multi-page PDF: one upload/job, with the requested number of photos. */
export function buildPhotoPdf(jpegPages: readonly Uint8Array[]): Uint8Array {
  if (!jpegPages.length) throw new Error("At least one prepared photo page is required.");
  for (const [index, page] of jpegPages.entries()) {
    if (page.length < 4 || page[0] !== 0xff || page[1] !== 0xd8 ||
        page[page.length - 2] !== 0xff || page[page.length - 1] !== 0xd9) {
      throw new Error(`Prepared photo page ${index + 1} is not a complete JPEG image.`);
    }
  }
  const encoder = new TextEncoder();
  const chunks: Uint8Array[] = [], offsets: number[] = [0];
  let byteLength = 0;
  const writeText = (value: string) => { const bytes=encoder.encode(value);chunks.push(bytes);byteLength+=bytes.length; };
  const writeBytes = (value: Uint8Array) => { chunks.push(value);byteLength+=value.length; };
  const begin = (number: number) => { offsets[number]=byteLength;writeText(`${number} 0 obj\n`); };
  const end = () => writeText("\nendobj\n");
  const objectCount = 2 + jpegPages.length * 3;
  writeText("%PDF-1.4\n");
  begin(1);writeText("<< /Type /Catalog /Pages 2 0 R >>");end();
  const kids=jpegPages.map((_,i)=>`${3+i*3} 0 R`).join(" ");
  begin(2);writeText(`<< /Type /Pages /Kids [ ${kids} ] /Count ${jpegPages.length} >>`);end();
  jpegPages.forEach((jpeg,index)=>{
    const page=3+index*3,image=page+1,content=page+2;
    begin(page);
    writeText(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${A4_WIDTH_POINTS} ${A4_HEIGHT_POINTS}] /Resources << /XObject << /Im0 ${image} 0 R >> >> /Contents ${content} 0 R >>`);
    end();
    begin(image);
    writeText(`<< /Type /XObject /Subtype /Image /Width ${A4_WIDTH_PX} /Height ${A4_HEIGHT_PX} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${jpeg.length} >>\nstream\n`);
    writeBytes(jpeg);writeText("\nendstream");end();
    const stream=encoder.encode(`q\n${A4_WIDTH_POINTS} 0 0 ${A4_HEIGHT_POINTS} 0 0 cm\n/Im0 Do\nQ\n`);
    begin(content);writeText(`<< /Length ${stream.length} >>\nstream\n`);writeBytes(stream);writeText("endstream");end();
  });
  const xref=byteLength;
  writeText(`xref\n0 ${objectCount+1}\n0000000000 65535 f \n`);
  for(let n=1;n<=objectCount;n++)writeText(`${String(offsets[n]).padStart(10,"0")} 00000 n \n`);
  writeText(`trailer\n<< /Size ${objectCount+1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`);
  const output=new Uint8Array(byteLength);let position=0;
  for(const chunk of chunks){output.set(chunk,position);position+=chunk.length;}
  return output;
}
