export interface CustomerUploadStatusState {
  jobStatus?: string | null;
  filesSelected?: number;
  uploadedFileCount?: number;
  quoteAvailable?: boolean;
  paymentStatus?: string | null;
  uploadProgress?: {
    files: ReadonlyArray<{ status: string }>;
  } | null;
}

const statusMessages: Record<string, string> = {
  TEST_OUTPUT_READY: "This was a test only. No physical pages were printed.",
  AWAITING_SHOP_APPROVAL: "Your files are with the shop for review.",
  AWAITING_PAYMENT: "Your files are accepted. Complete payment to continue.",
  AWAITING_COUNTER_PAYMENT: "Please pay at the shop counter.",
  CASH_AWAITING_PAYMENT: "Please pay at the shop counter.",
  AWAITING_CUSTOMER_PAYMENT: "Complete the payment using the details shown.",
  PAYMENT_REPORTED: "Your payment details were sent to the shop. The shop is checking the payment.",
  CUSTOMER_REPORTED: "Your payment reference was received. The shop is checking it.",
  PAYMENT_REJECTED: "The payment could not be confirmed. Please check with the shop before paying again.",
  PAID_AUTHORISED: "Payment received. The shop is preparing your print.",
  PRINT_AUTHORISED: "The shop approved your print. Printing will start shortly.",
  PRINT_SUBMITTING: "Your print is being sent to the printer.",
  SPOOLER_ACCEPTED: "The printer queue accepted your document.",
  PRINTING: "Your print is in progress.",
  OUTPUT_UNCONFIRMED: "The shop is checking whether your pages were printed. Please do not submit the same file again.",
  COMPLETED: "Printing is complete. Your pages are ready for collection.",
  PDF_EXPORTED: "Your PDF is ready.",
  REFUNDED: "This file was not printed and the payment was returned.",
  CANCELLED: "This request was cancelled.",
  FAILED: "Printing could not be completed. Please speak to the shop.",
  REJECTED: "The shop could not accept this file. Please ask the shop for help.",
  QUOTE_EXPIRED: "Your quote has expired · Request a new quote.",
  LOCAL_QUEUE_CLAIMED: "Your files have reached the shop and are being prepared.",
  LOCAL_QUEUE_READY: "Your files have reached the shop and are being prepared.",
  VERIFIED: "Payment received. The shop is preparing your print."
};

const activeUploadStages = new Set([
  "QUEUED", "PREPARING", "TRANSFERRING", "STREAMING_ENCRYPTED", "WAITING_FOR_SHOP", "PROCESSING"
]);

export function resolveCustomerUploadStatusMessage(state: CustomerUploadStatusState): string {
  const status = state.jobStatus?.trim().toUpperCase();
  if (status && statusMessages[status]) return statusMessages[status];

  const paymentStatus = state.paymentStatus?.trim().toUpperCase();
  if (paymentStatus && statusMessages[paymentStatus]) return statusMessages[paymentStatus];

  const progress = state.uploadProgress;
  if (progress?.files.length) {
    const failed = progress.files.some(file => file.status === "FAILED");
    const pending = progress.files.some(file => activeUploadStages.has(file.status));
    if (failed && !pending) return "Some files need attention · Review the upload results and retry only the failed files.";
    if (pending) return "Uploading your documents · Each file will be confirmed separately.";
  }

  if (state.quoteAvailable) {
    return "Check the price and print options before sending your request to the shop.";
  }
  if (progress?.files.length && progress.files.every(file => file.status === "COMPLETED")) {
    return "Files uploaded successfully · Choose print settings or calculate a quote.";
  }
  if ((state.uploadedFileCount ?? 0) > 0) {
    return "Files uploaded successfully · Choose print settings or calculate a quote.";
  }
  if ((state.filesSelected ?? 0) > 0) {
    return "Review your selected files and upload.";
  }
  return "Choose documents or images to begin.";
}
