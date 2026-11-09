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

export function subscriptionPaymentStatusMessage(status: string): string {
  switch (status.toUpperCase()) {
    case "CAPTURED":
      return "Payment verified. Your subscription is active.";
    case "CAPTURED_REVIEW_REQUIRED":
      return "Razorpay confirmed your payment. SmartPrint support must review the plan assignment before access is activated.";
    case "FAILED":
      return "This payment attempt failed. You can safely try again.";
    case "CREATED":
      return "Payment order created. Complete checkout to activate the subscription.";
    default:
      return "Payment status is being checked. Refresh this page to see the latest update.";
  }
}
