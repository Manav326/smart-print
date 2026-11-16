export type BatchQuotePrintKind = "DOCUMENT" | "PHOTO" | "ID_DOCUMENT";

export interface BatchQuoteBreakdownEntry {
  printKind: BatchQuotePrintKind;
  pageCount: number;
  copies: number;
  pagesPerSheet?: number;
  colourMode: "Mono" | "Colour";
  photoCount?: number | null;
  quotedAmountMinor: number;
}

export interface BatchQuoteBreakdown {
  normalDocumentFileCount: number;
  normalDocumentSourcePages: number;
  normalDocumentOutputPages: number;
  normalDocumentMonoOutputPages: number;
  normalDocumentColourOutputPages: number;
  normalDocumentMonoAmountMinor: number;
  normalDocumentColourAmountMinor: number;
  normalDocumentAmountMinor: number;
  idCardCount: number;
  idCardSheets: number;
  idCardAmountMinor: number;
  photoFileCount: number;
  photoPages: number;
  finishedPhotos: number;
  photoAmountMinor: number;
  totalOutputPagesAndSheets: number;
}

export declare function calculateBatchQuoteBreakdown(
  entries: readonly BatchQuoteBreakdownEntry[]
): BatchQuoteBreakdown;
