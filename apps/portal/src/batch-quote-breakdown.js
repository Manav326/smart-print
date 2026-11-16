export function calculateBatchQuoteBreakdown(entries) {
  const normalDocuments = entries.filter((entry) => entry.printKind === "DOCUMENT");
  const idCards = entries.filter((entry) => entry.printKind === "ID_DOCUMENT");
  const photoJobs = entries.filter((entry) => entry.printKind === "PHOTO");
  const sum = (items, selector) =>
    items.reduce((total, item) => total + selector(item), 0);

  const normalDocumentSourcePages = sum(normalDocuments, (entry) => entry.pageCount);
  // Price and report actual output sides after driver-backed N-up and copies.
  // The original PDF source-page count remains a separate metric for clarity.
  const outputSides = (entry) =>
    Math.ceil(entry.pageCount / Math.max(1, entry.pagesPerSheet ?? 1)) * entry.copies;
  const normalDocumentOutputPages = sum(normalDocuments, outputSides);
  const normalDocumentMonoOutputPages = sum(
    normalDocuments.filter((entry) => entry.colourMode === "Mono"),
    outputSides
  );
  const normalDocumentColourOutputPages = sum(
    normalDocuments.filter((entry) => entry.colourMode === "Colour"),
    outputSides
  );
  const normalDocumentMonoAmountMinor = sum(
    normalDocuments.filter((entry) => entry.colourMode === "Mono"),
    (entry) => entry.quotedAmountMinor
  );
  const normalDocumentColourAmountMinor = sum(
    normalDocuments.filter((entry) => entry.colourMode === "Colour"),
    (entry) => entry.quotedAmountMinor
  );
  const idCardSheets = sum(idCards, (entry) => entry.pageCount);
  const photoPages = sum(photoJobs, (entry) => entry.pageCount);

  return {
    normalDocumentFileCount: normalDocuments.length,
    normalDocumentSourcePages,
    normalDocumentOutputPages,
    normalDocumentMonoOutputPages,
    normalDocumentColourOutputPages,
    normalDocumentMonoAmountMinor,
    normalDocumentColourAmountMinor,
    normalDocumentAmountMinor: normalDocumentMonoAmountMinor + normalDocumentColourAmountMinor,
    idCardCount: idCards.length,
    idCardSheets,
    idCardAmountMinor: sum(idCards, (entry) => entry.quotedAmountMinor),
    photoFileCount: photoJobs.length,
    photoPages,
    finishedPhotos: sum(photoJobs, (entry) => entry.photoCount ?? 0),
    photoAmountMinor: sum(photoJobs, (entry) => entry.quotedAmountMinor),
    // The three categories are mutually exclusive: normal page sides, prepared
    // ID-card sheets, and Photo Studio A4 pages are each counted exactly once.
    totalOutputPagesAndSheets: normalDocumentOutputPages + idCardSheets + photoPages
  };
}
