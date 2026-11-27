export function shouldBlockBatchCheckoutForExpiredQuote(
  batchPaymentStatus: string | null | undefined,
  acceptedQuoteHasExpired: boolean
): boolean {
  const paymentAlreadyStarted =
    batchPaymentStatus !== null &&
    batchPaymentStatus !== undefined &&
    batchPaymentStatus !== "REJECTED";

  return acceptedQuoteHasExpired && !paymentAlreadyStarted;
}


const quoteRefreshBlockedStatuses = new Set([
  "AWAITING_CUSTOMER_PAYMENT",
  "CUSTOMER_REPORTED",
  "CASH_AWAITING_PAYMENT",
  "PAYMENT_REPORTED",
  "VERIFIED",
  "PARTIALLY_REFUNDED",
  "PAID_AUTHORISED",
  "LOCAL_QUEUE_CLAIMED",
  "LOCAL_QUEUE_READY",
  "PRINT_AUTHORISED",
  "PRINT_SUBMITTING",
  "SPOOLER_ACCEPTED",
  "PRINTING",
  "OUTPUT_UNCONFIRMED",
  "COMPLETED",
  "PDF_EXPORTED",
  "FAILED",
  "REJECTED",
  "REFUNDED",
  "CANCELLED"
]);

/** Prevents quote replacement after a payment attempt or fulfilment has started. */
export function shouldBlockQuoteRefreshForStatus(status: string | null | undefined): boolean {
  return typeof status === "string" && quoteRefreshBlockedStatuses.has(status.toUpperCase());
}
