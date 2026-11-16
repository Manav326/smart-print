import assert from "node:assert/strict";
import test from "node:test";
import { calculateBatchQuoteBreakdown } from "../../apps/portal/src/batch-quote-breakdown.js";

test("batch quote separates normal document pages, ID-card sheets, and photo pages", () => {
  const result = calculateBatchQuoteBreakdown([
    {
      printKind: "DOCUMENT",
      pageCount: 3,
      copies: 2,
      colourMode: "Mono",
      quotedAmountMinor: 120
    },
    {
      printKind: "DOCUMENT",
      pageCount: 2,
      copies: 1,
      colourMode: "Colour",
      quotedAmountMinor: 200
    },
    {
      printKind: "ID_DOCUMENT",
      pageCount: 1,
      copies: 1,
      colourMode: "Colour",
      quotedAmountMinor: 350
    },
    {
      printKind: "PHOTO",
      pageCount: 2,
      copies: 1,
      colourMode: "Colour",
      photoCount: 12,
      quotedAmountMinor: 1200
    }
  ]);

  assert.equal(result.normalDocumentFileCount, 2);
  assert.equal(result.normalDocumentSourcePages, 5);
  assert.equal(result.normalDocumentOutputPages, 8);
  assert.equal(result.normalDocumentMonoOutputPages, 6);
  assert.equal(result.normalDocumentColourOutputPages, 2);
  assert.equal(result.normalDocumentAmountMinor, 320);

  assert.equal(result.idCardCount, 1);
  assert.equal(result.idCardSheets, 1);
  assert.equal(result.idCardAmountMinor, 350);

  assert.equal(result.photoFileCount, 1);
  assert.equal(result.photoPages, 2);
  assert.equal(result.finishedPhotos, 12);
  assert.equal(result.photoAmountMinor, 1200);

  // 8 normal document page sides + 1 ID-card sheet + 2 photo pages.
  // Neither special category leaks into the ordinary B&W/colour totals.
  assert.equal(result.totalOutputPagesAndSheets, 11);
});

test("empty and single-category batches report zeroes in the other categories", () => {
  const empty = calculateBatchQuoteBreakdown([]);
  assert.equal(empty.idCardCount, 0);
  assert.equal(empty.photoPages, 0);
  assert.equal(empty.normalDocumentSourcePages, 0);
  assert.equal(empty.totalOutputPagesAndSheets, 0);

  const onlyIdCards = calculateBatchQuoteBreakdown([
    {
      printKind: "ID_DOCUMENT",
      pageCount: 1,
      copies: 1,
      colourMode: "Colour",
      quotedAmountMinor: 500
    }
  ]);

  assert.equal(onlyIdCards.idCardCount, 1);
  assert.equal(onlyIdCards.idCardSheets, 1);
  assert.equal(onlyIdCards.normalDocumentOutputPages, 0);
  assert.equal(onlyIdCards.normalDocumentColourOutputPages, 0);
  assert.equal(onlyIdCards.photoPages, 0);
  assert.equal(onlyIdCards.totalOutputPagesAndSheets, 1);
});

test("N-up reports billable output sides rather than original PDF pages", () => {
  const result = calculateBatchQuoteBreakdown([
    {
      printKind: "DOCUMENT",
      pageCount: 33,
      copies: 1,
      pagesPerSheet: 4,
      colourMode: "Mono",
      quotedAmountMinor: 900
    }
  ]);

  assert.equal(result.normalDocumentSourcePages, 33);
  assert.equal(result.normalDocumentOutputPages, 9);
  assert.equal(result.normalDocumentMonoOutputPages, 9);
  assert.equal(result.normalDocumentColourOutputPages, 0);
  assert.equal(result.totalOutputPagesAndSheets, 9);
});
