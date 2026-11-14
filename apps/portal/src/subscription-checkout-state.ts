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
  let minorUnitDigits = 2;
  try {
    minorUnitDigits = new Intl.NumberFormat("en-IN", {
      style: "currency",
      currency
    }).resolvedOptions().maximumFractionDigits ?? 2;
  } catch {
    // INR subscription payments are stored in paise; unsupported currency metadata
    // falls back to the two-decimal representation used by the payment API.
  }
  return new Intl.NumberFormat("en-IN", {
    style: "currency",
    currency,
    minimumFractionDigits: minorUnitDigits,
    maximumFractionDigits: minorUnitDigits
  }).format(amountMinor / (10 ** minorUnitDigits));
}

export function getAnnualSavingsMinor(plan: SubscriptionPriceOptions): number {
  if (typeof plan.monthlyPriceMinor !== "number" ||
      typeof plan.annualPriceMinor !== "number" ||
      plan.monthlyPriceMinor <= 0 || plan.annualPriceMinor < 0) {
    return 0;
  }
  return Math.max(0, plan.monthlyPriceMinor * 12 - plan.annualPriceMinor);
}

export function getAnnualSavingsPercent(plan: SubscriptionPriceOptions): number {
  if (typeof plan.monthlyPriceMinor !== "number" || plan.monthlyPriceMinor <= 0) return 0;
  const fullYearAtMonthlyRate = plan.monthlyPriceMinor * 12;
  return Math.round(getAnnualSavingsMinor(plan) / fullYearAtMonthlyRate * 100);
}

export type SubscriptionReminderState = "expired" | "due-soon" | null;

export function getSubscriptionReminderState(
  status: string,
  isAccessActive: boolean,
  periodEndUtc: string,
  now = Date.now()
): SubscriptionReminderState {
  const periodEnd = Date.parse(periodEndUtc);
  if (status.toLowerCase() === "expired" || (Number.isFinite(periodEnd) && periodEnd <= now)) {
    return "expired";
  }
  if (!isAccessActive || !Number.isFinite(periodEnd)) return null;
  const daysRemaining = Math.ceil((periodEnd - now) / 86_400_000);
  return daysRemaining <= 7 ? "due-soon" : null;
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
      return "This payment attempt failed. You can safely try again by retrying the same order.";
    case "CREATED":
      return "Payment order is ready. Continue checkout to activate the subscription.";
    case "CREATING":
    case "CREATION_UNKNOWN":
      return "SmartPrint is confirming the existing Razorpay order. Do not start another payment.";
    default:
      return "Payment status is being checked. Refresh this page to see the latest update.";
  }
}
