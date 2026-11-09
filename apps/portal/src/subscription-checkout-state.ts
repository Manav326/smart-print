export type SubscriptionBillingInterval = "Monthly" | "Annual";

export interface SubscriptionPriceOptions {
  monthlyPriceMinor: number | null;
  annualPriceMinor: number | null;
}

export function getPaidBillingIntervals(plan: SubscriptionPriceOptions): SubscriptionBillingInterval[] {
  const intervals: SubscriptionBillingInterval[] = [];
  if (typeof plan.monthlyPriceMinor === "number" && plan.monthlyPriceMinor > 0) {
    intervals.push("Monthly");
  }
  if (typeof plan.annualPriceMinor === "number" && plan.annualPriceMinor > 0) {
    intervals.push("Annual");
  }
  return intervals;
}

export function formatSubscriptionAmount(amountMinor: number, currency: string): string {
  return new Intl.NumberFormat("en-IN", {
    style: "currency",
    currency,
    minimumFractionDigits: 2,
    maximumFractionDigits: 2
  }).format(amountMinor / 100);
}

export function isUnsettledSubscriptionPayment(status: string): boolean {
  return ["CREATING", "CREATION_UNKNOWN", "CREATED", "FAILED", "CAPTURED_REVIEW_REQUIRED"]
    .includes(status.toUpperCase());
}

export function subscriptionPaymentActionLabel(status: string): string {
  switch (status.toUpperCase()) {
    case "CREATED":
      return "Continue payment";
    case "FAILED":
      return "Retry payment";
    case "CREATING":
    case "CREATION_UNKNOWN":
      return "Check order status";
    default:
      return "Refresh status";
  }
}

export function subscriptionPaymentStatusLabel(status: string): string {
  switch (status.toUpperCase()) {
    case "CREATION_UNKNOWN":
      return "Confirmation pending";
    case "CREATING":
      return "Creating order";
    case "CAPTURED_REVIEW_REQUIRED":
      return "Needs support review";
    default:
      return status.replace(/_/g, " ");
  }
}

export function subscriptionPaymentStatusMessage(status: string): string {
  switch (status.toUpperCase()) {
    case "CAPTURED":
      return "Payment verified. Your subscription is active.";
    case "CAPTURED_REVIEW_REQUIRED":
      return "Razorpay confirmed your payment. SmartPrint support must review the plan assignment before access is activated.";
    case "FAILED":
      return "This payment attempt failed. You can safely retry the same order.";
    case "CREATED":
      return "Payment order is ready. Continue checkout to activate the subscription.";
    case "CREATING":
    case "CREATION_UNKNOWN":
      return "SmartPrint is confirming the existing Razorpay order. Do not start another payment.";
    default:
      return "Payment status is being checked. Refresh this page to see the latest update.";
  }
}
