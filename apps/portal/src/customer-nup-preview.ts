import type { PDFDocument as PdfDocumentType } from "pdf-lib";

export type CustomerNupPreviewOptions = {
  pagesPerSheet: number;
  paperSize: string;
  orientation: string;
  pagesPerSheetDirection: string;
  sheetIndex: number;
};

export type CustomerNupPreviewResult = {
  bytes: Uint8Array;
  sheetIndex: number;
  sheetCount: number;
  firstSourcePage: number;
  lastSourcePage: number;
};

/**
 * Creates one visual-only output-side preview using the same source-page grid
 * as the Android preview. It never replaces or mutates the uploaded PDF; the
 * untouched source file remains the payload sent to the Windows print driver.
 */
export async function createCustomerNupPreview(
  sourceBytes: Uint8Array,
  options: CustomerNupPreviewOptions
): Promise<CustomerNupPreviewResult> {
  if (options.pagesPerSheet < 2 || options.pagesPerSheet > 500 ||
      !Number.isInteger(options.pagesPerSheet)) {
    throw new RangeError("Pages per sheet must be between 2 and 500.");
  }
  if (!Number.isInteger(options.sheetIndex) || options.sheetIndex < 0) {
    throw new RangeError("The preview sheet index is invalid.");
  }

  const { PDFDocument, StandardFonts, rgb, degrees } = await import("pdf-lib");
  const sourceDocument: PdfDocumentType = await PDFDocument.load(sourceBytes);
  const sourcePages = sourceDocument.getPages();
  if (sourcePages.length < 1 || sourcePages.length > 500) {
    throw new RangeError("PDFs must contain between 1 and 500 pages.");
  }

  const sheetCount = Math.ceil(sourcePages.length / options.pagesPerSheet);
  const sheetIndex = Math.min(options.sheetIndex, sheetCount - 1);
  const firstPageIndex = sheetIndex * options.pagesPerSheet;
  const lastPageIndexExclusive = Math.min(
    sourcePages.length,
    firstPageIndex + options.pagesPerSheet
  );

  const [shortSide, longSide] = paperDimensions(options.paperSize);
  const firstSourcePage = sourcePages[0];
  const automaticLandscape = firstSourcePage.getWidth() > firstSourcePage.getHeight();
  const landscape = options.orientation === "LANDSCAPE"
    ? true
    : options.orientation === "PORTRAIT"
      ? false
      : automaticLandscape;
  const sheetWidth = landscape ? longSide : shortSide;
  const sheetHeight = landscape ? shortSide : longSide;

  const previewDocument = await PDFDocument.create();
  const selectedPages = sourcePages.slice(firstPageIndex, lastPageIndexExclusive);
  const embeddedPages = await previewDocument.embedPages(selectedPages);
  const outputPage = previewDocument.addPage([sheetWidth, sheetHeight]);

  const margin = sheetWidth * 0.045;
  const gap = sheetWidth * 0.012;
  const columns = Math.max(1, Math.ceil(Math.sqrt(options.pagesPerSheet)));
  const rows = Math.max(1, Math.ceil(options.pagesPerSheet / columns));
  const contentWidth = sheetWidth - margin * 2 - gap * (columns - 1);
  const contentHeight = sheetHeight - margin * 2 - gap * (rows - 1);
  const cellWidth = contentWidth / columns;
  const cellHeight = contentHeight / rows;

  embeddedPages.forEach((embeddedPage, index) => {
    const { column, row } = resolveGridPosition(
      index,
      columns,
      rows,
      options.pagesPerSheetDirection
    );
    const inset = Math.min(cellWidth, cellHeight) * 0.035;
    const availableWidth = Math.max(1, cellWidth - inset * 2);
    const availableHeight = Math.max(1, cellHeight - inset * 2);
    const scale = Math.min(
      availableWidth / embeddedPage.width,
      availableHeight / embeddedPage.height
    );
    const drawWidth = embeddedPage.width * scale;
    const drawHeight = embeddedPage.height * scale;
    const cellLeft = margin + column * (cellWidth + gap);
    const cellBottom = sheetHeight - margin - (row + 1) * cellHeight - row * gap;
    const x = cellLeft + (cellWidth - drawWidth) / 2;
    const y = cellBottom + (cellHeight - drawHeight) / 2;

    outputPage.drawPage(embeddedPage, {
      x,
      y,
      width: drawWidth,
      height: drawHeight
    });
    outputPage.drawRectangle({
      x: cellLeft,
      y: cellBottom,
      width: cellWidth,
      height: cellHeight,
      borderColor: rgb(0.78, 0.80, 0.83),
      borderWidth: 0.55,
      borderOpacity: 0.72
    });
  });

  // The watermark belongs only to the preview PDF. The physical print request
  // still uploads the original bytes and lets the installed driver impose N-up.
  const watermarkFont = await previewDocument.embedFont(StandardFonts.Helvetica);
  outputPage.drawText("SmartPrint by QuantaEdge", {
    x: sheetWidth * 0.16,
    y: sheetHeight * 0.47,
    size: Math.min(sheetWidth, sheetHeight) * 0.035,
    font: watermarkFont,
    color: rgb(0.80, 0.81, 0.83),
    opacity: 0.40,
    rotate: degrees(28)
  });

  return {
    bytes: await previewDocument.save(),
    sheetIndex,
    sheetCount,
    firstSourcePage: firstPageIndex + 1,
    lastSourcePage: lastPageIndexExclusive
  };
}

function resolveGridPosition(
  index: number,
  columns: number,
  rows: number,
  direction: string
): { column: number; row: number } {
  switch (direction) {
    case "BottomRight":
      return { column: Math.floor(index / rows), row: index % rows };
    case "LeftBottom":
      return { column: columns - 1 - (index % columns), row: Math.floor(index / columns) };
    case "BottomLeft":
      return { column: columns - 1 - Math.floor(index / rows), row: index % rows };
    case "RightTop":
      return { column: index % columns, row: rows - 1 - Math.floor(index / columns) };
    case "TopRight":
      return { column: Math.floor(index / rows), row: rows - 1 - (index % rows) };
    case "LeftTop":
      return {
        column: columns - 1 - (index % columns),
        row: rows - 1 - Math.floor(index / columns)
      };
    case "TopLeft":
      return {
        column: columns - 1 - Math.floor(index / rows),
        row: rows - 1 - (index % rows)
      };
    case "RightBottom":
    default:
      return { column: index % columns, row: Math.floor(index / columns) };
  }
}

function paperDimensions(paperSize: string): [number, number] {
  switch (paperSize.toUpperCase()) {
    case "A3": return [841.89, 1190.55];
    case "A5": return [419.53, 595.28];
    case "LETTER": return [612, 792];
    case "LEGAL": return [612, 1008];
    case "A4":
    default: return [595.28, 841.89];
  }
}
