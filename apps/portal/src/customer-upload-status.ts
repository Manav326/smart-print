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
  PARTIALLY_REFUNDED: "A partial refund has been issued. Check the payment details with the shop.",
  CANCELLED: "This request was cancelled.",
  FAILED: "Printing could not be completed. Please speak to the shop.",
  REJECTED: "The shop could not accept this file. Please ask the shop for help.",
  QUOTE_EXPIRED: "Your quote has expired · Request a new quote.",
  LOCAL_QUEUE_CLAIMED: "Your files have reached the shop and are being prepared.",
  LOCAL_QUEUE_READY: "Your files have reached the shop and are being prepared.",
  VERIFIED: "Payment received. The shop is preparing your print."
};

const paymentStatusMessages: Record<string, string> = {
  AWAITING_CUSTOMER_PAYMENT: "Complete the payment using the details shown.",
  AWAITING_COUNTER_PAYMENT: "Please pay at the shop counter.",
  CASH_AWAITING_PAYMENT: "Please pay at the shop counter.",
  PAYMENT_REPORTED: "Your payment details were sent to the shop. The shop is checking the payment.",
  CUSTOMER_REPORTED: "Your payment reference was received. The shop is checking it.",
  PAYMENT_REJECTED: "The payment could not be confirmed. Please check with the shop before paying again.",
  REJECTED: "The payment could not be confirmed. Please check with the shop before paying again.",
  VERIFIED: "Payment received. The shop is preparing your print.",
  REFUNDED: "Your payment was refunded. Contact the shop if you need help.",
  PARTIALLY_REFUNDED: "A partial refund has been issued. Check the payment details with the shop."
};

const paymentLifecycleJobStatuses = new Set([
  "AWAITING_PAYMENT",
  "AWAITING_CUSTOMER_PAYMENT",
  "AWAITING_COUNTER_PAYMENT",
  "CASH_AWAITING_PAYMENT",
  "PAYMENT_REPORTED",
  "CUSTOMER_REPORTED",
  "PAYMENT_REJECTED",
  "PAID_AUTHORISED"
]);

const activeUploadStages = new Set([
  "QUEUED", "PREPARING", "TRANSFERRING", "STREAMING_ENCRYPTED", "WAITING_FOR_SHOP", "PROCESSING"
]);

export function resolveCustomerUploadStatusMessage(state: CustomerUploadStatusState): string {
  const status = state.jobStatus?.trim().toUpperCase();
  const paymentStatus = state.paymentStatus?.trim().toUpperCase();
  const progress = state.uploadProgress;

  // A new transfer is the customer's immediate task. It must not be hidden
  // behind a status retained from an earlier file or order in this session.
  if (progress?.files.length) {
    const failed = progress.files.some(file => file.status === "FAILED");
    const pending = progress.files.some(file => activeUploadStages.has(file.status));
    if (failed && pending) {
      return "Some files need attention while the remaining files are still uploading. Wait for each result, then retry only the failed files.";
    }
    if (pending) return "Uploading your documents · Each file will be confirmed separately.";
    if (failed) return "Some files need attention · Review the upload results and retry only the failed files.";
  }

  if ((state.filesSelected ?? 0) > 0) {
    return "Review your selected files and upload.";
  }

  // A payment update can arrive before the associated job status refreshes.
  // Prefer it while the job is still in a payment stage, but preserve later
  // printing/fulfilment and terminal job states as the more useful status.
  const paymentMessage = paymentStatus ? paymentStatusMessages[paymentStatus] : undefined;
  if (paymentMessage && (!status || paymentLifecycleJobStatuses.has(status) ||
      paymentStatus === "REJECTED" || paymentStatus === "PAYMENT_REJECTED")) {
    return paymentMessage;
  }

  if (status && statusMessages[status]) return statusMessages[status];
  if (paymentMessage) return paymentMessage;

  if (state.quoteAvailable) {
    return "Check the price and print options before sending your request to the shop.";
  }
  if ((progress?.files.length ?? 0) > 0 &&
      progress!.files.every(file => file.status === "COMPLETED")) {
    return "Files uploaded successfully · Choose print settings or calculate a quote.";
  }
  if ((state.uploadedFileCount ?? 0) > 0) {
    return "Files uploaded successfully · Choose print settings or calculate a quote.";
  }
  return "Choose documents or images to begin.";
}
