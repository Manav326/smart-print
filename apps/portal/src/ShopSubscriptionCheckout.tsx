import { useEffect, useRef, useState } from "react";
import { AlertCircle, CheckCircle2, Clock3, CreditCard, RefreshCw, ShieldCheck } from "lucide-react";
import {
  createShopSubscriptionPaymentOrder,
  getShopSubscriptionPayments,
  getShopSubscriptionPlans,
  getShopSubscription,
  resumeShopSubscriptionPaymentOrder,
  verifyShopSubscriptionPayment,
  type ShopSubscriptionPaymentRecord,
  type ShopSubscriptionPlanOption,
  type ShopSubscriptionSummary
} from "./api";
import { formatIstDate, formatIstDateTime } from "./date-time";
import { getUiLocaleLanguage, translateUiText, useLocale, type UiLocale } from "./i18n";
import {
  formatSubscriptionAmount,
  getAnnualSavingsMinor,
  getAnnualSavingsPercent,
  getPaidBillingIntervals,
  isUnsettledSubscriptionPayment,
  subscriptionPaymentActionLabel,
  subscriptionPaymentStatusLabel,
  subscriptionPaymentStatusMessage,
  type SubscriptionBillingInterval
} from "./subscription-checkout-state";

type RazorpaySuccess = {
  razorpay_payment_id: string;
  razorpay_order_id: string;
  razorpay_signature: string;
};

type RazorpayOptions = {
  key: string;
  amount: number;
  currency: string;
  name: string;
  description: string;
  order_id: string;
  handler: (response: RazorpaySuccess) => void | Promise<void>;
  modal?: { ondismiss?: () => void };
  theme?: { color?: string };
};

type RazorpayCheckoutInstance = {
  open: () => void;
  on: (event: "payment.failed", handler: (response: { error?: { description?: string } }) => void) => void;
};

declare global {
  interface Window {
    Razorpay?: new (options: RazorpayOptions) => RazorpayCheckoutInstance;
  }
}

let checkoutScriptPromise: Promise<void> | null = null;

function ensureRazorpayCheckout(): Promise<void> {
  if (window.Razorpay) return Promise.resolve();
  if (checkoutScriptPromise) return checkoutScriptPromise;

  const loading = new Promise<void>((resolve, reject) => {
    let script = document.querySelector<HTMLScriptElement>('script[data-smartprint-razorpay="true"]');
    if (script?.dataset.failed === "true") {
      script.remove();
      script = null;
    }

    if (script) {
      if (script.dataset.loaded === "true") {
        reject(new Error("Razorpay Checkout did not initialize. Reload the page and try again."));
        return;
      }
      script.addEventListener("load", () => {
        if (window.Razorpay) resolve();
        else reject(new Error("Razorpay Checkout did not initialize."));
      }, { once: true });
      script.addEventListener("error", () => reject(new Error("Razorpay Checkout could not be loaded.")), { once: true });
      return;
    }

    script = document.createElement("script");
    script.src = "https://checkout.razorpay.com/v1/checkout.js";
    script.async = true;
    script.dataset.smartprintRazorpay = "true";
    script.onload = () => {
      script!.dataset.loaded = "true";
      if (window.Razorpay) resolve();
      else reject(new Error("Razorpay Checkout did not initialize."));
    };
    script.onerror = () => {
      script!.dataset.failed = "true";
      script!.remove();
      reject(new Error("Razorpay Checkout could not be loaded. Check your connection and try again."));
    };
    document.head.appendChild(script);
  });

  const pending = loading.catch(error => {
    checkoutScriptPromise = null;
    throw error;
  });
  checkoutScriptPromise = pending;
  return pending;
}

function formatLocalizedSubscriptionAmount(amountMinor: number, currency: string, locale: UiLocale): string {
  let digits = 2;
  try {
    digits = new Intl.NumberFormat("en-IN", { style: "currency", currency }).resolvedOptions().maximumFractionDigits ?? 2;
  } catch { /* Use the payment API two-decimal fallback. */ }
  return new Intl.NumberFormat(getUiLocaleLanguage(locale), {
    style: "currency", currency, minimumFractionDigits: digits, maximumFractionDigits: digits
  }).format(amountMinor / (10 ** digits));
}

function priceFor(plan: ShopSubscriptionPlanOption, interval: SubscriptionBillingInterval): number | null {
  return interval === "Monthly" ? plan.monthlyPriceMinor : plan.annualPriceMinor;
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  })[character] ?? character);
}

function printSubscriptionReceipt(payment: ShopSubscriptionPaymentRecord, locale: UiLocale): boolean {
  const t = (source: string) => translateUiText(locale, source);
  const amount = formatLocalizedSubscriptionAmount(payment.amountMinor, payment.currency, locale);
  if (payment.status !== "CAPTURED" || !payment.razorpayPaymentId) return false;
  const printWindow = window.open("", "_blank");
  if (!printWindow) return false;
  printWindow.opener = null;

  const rows: Array<[string, string]> = [
    [t("Receipt reference"), payment.id.toUpperCase()],
    [t("Product"), t("SmartPrint subscription")],
    [t("Billing interval"), t(payment.billingInterval === "Annual" ? "Annual" : "Monthly")],
    [t("Amount paid"), amount],
    [t("Payment status"), t("Verified")],
    [t("Paid on"), formatIstDateTime(payment.verifiedUtc ?? payment.createdUtc)],
    [t("Razorpay payment ID"), payment.razorpayPaymentId],
    [t("Razorpay order ID"), payment.razorpayOrderId],
    [t("Subscription reference"), payment.assignedSubscriptionId ?? t("Not available")]
  ];
  const details = rows.map(([label, value]) =>
    "<div class=\"receipt-row\"><span>" + escapeHtml(label) +
    "</span><strong>" + escapeHtml(value) + "</strong></div>"
  ).join("");

  printWindow.document.open();
  printWindow.document.write(`<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>SmartPrint subscription payment receipt</title>
<style>
*{box-sizing:border-box}body{margin:0;padding:36px;font:14px/1.5 Arial,sans-serif;color:#18202c;background:#fff}
main{width:min(100%,680px);margin:0 auto;border:1px solid #d9dee7;border-radius:14px;padding:30px}
header{display:flex;justify-content:space-between;align-items:flex-start;gap:20px;border-bottom:1px solid #e5e9ef;padding-bottom:20px;margin-bottom:18px}
h1{font-size:22px;margin:5px 0}.eyebrow{font-size:10px;letter-spacing:.12em;font-weight:700;color:#657084}
.amount{font-size:25px;font-weight:700;margin:18px 0}
.receipt-row{display:flex;justify-content:space-between;gap:20px;padding:11px 0;border-bottom:1px solid #edf0f4}
.receipt-row span{color:#637085}.receipt-row strong{text-align:right;overflow-wrap:anywhere}
.note{margin-top:22px;color:#6b7482;font-size:11px}
@media print{body{padding:0}main{border:0;border-radius:0;width:100%;padding:18px}}
</style></head><body><main><header><div><div class="eyebrow">SMARTPRINT BY QUANTAEDGE</div><h1>Subscription payment receipt</h1><div>Thank you for choosing SmartPrint.</div></div><div class="eyebrow">PAYMENT VERIFIED</div></header>
<div class="amount">${escapeHtml(formatSubscriptionAmount(payment.amountMinor, payment.currency))}</div>
${details}
<p class="note">${escapeHtml(t("This is a payment receipt for your records, not a GST tax invoice. It confirms the payment recorded by SmartPrint; it does not replace any applicable tax invoice issued by the legal seller."))}</p>
</main></body></html>`);
  printWindow.document.close();
  printWindow.focus();
  window.setTimeout(() => printWindow.print(), 250);
  return true;
}

const statusPollDelays = [1500, 3000, 5000, 8000];

export default function ShopSubscriptionCheckout({
  token,
  onSubscriptionUpdated,
  currentSubscription
}: {
  token: string;
  onSubscriptionUpdated: (summary: ShopSubscriptionSummary) => void;
  currentSubscription: ShopSubscriptionSummary["subscription"];
}) {
  const { locale } = useLocale();
  const t = (source: string, values: Record<string, string | number> = {}) => translateUiText(locale, source, values);
  const amount = (minor: number, currency: string) => formatLocalizedSubscriptionAmount(minor, currency, locale);
  const [plans, setPlans] = useState<ShopSubscriptionPlanOption[]>([]);
  const [payments, setPayments] = useState<ShopSubscriptionPaymentRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [busyOption, setBusyOption] = useState<string | null>(null);
  const [planError, setPlanError] = useState("");
  const [historyError, setHistoryError] = useState("");
  const [olderPaymentsError, setOlderPaymentsError] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [showAllPayments, setShowAllPayments] = useState(false);
  const [paymentsHasMore, setPaymentsHasMore] = useState(false);
  const [paymentsTotalCount, setPaymentsTotalCount] = useState(0);
  const [paymentsLoadingMore, setPaymentsLoadingMore] = useState(false);
  const paymentsRef = useRef<ShopSubscriptionPaymentRecord[]>([]);
  const paymentHistoryGeneration = useRef(0);
  const loadingMorePaymentsRef = useRef(false);
  const requestIds = useRef(new Map<string, string>());
  const pollSequence = useRef(0);
  const mounted = useRef(true);

  async function refreshSubscriptionSummary() {
    try {
      const summary = await getShopSubscription(token);
      if (mounted.current) onSubscriptionUpdated(summary);
    } catch {
      if (mounted.current) {
        setNotice(t("Your payment is verified. The subscription summary could not be refreshed just now; use Refresh status to update the page."));
      }
    }
  }

  async function refreshData(showSpinner = false): Promise<ShopSubscriptionPaymentRecord[] | null> {
    const historyGeneration = ++paymentHistoryGeneration.current;
    if (showSpinner) {
      setRefreshing(true);
      setError("");
    }

    const [plansResult, paymentsResult] = await Promise.allSettled([
      getShopSubscriptionPlans(token),
      getShopSubscriptionPayments(token)
    ]);

    if (plansResult.status === "fulfilled") {
      setPlans(plansResult.value);
      setPlanError("");
    } else {
      setPlanError(plansResult.reason instanceof Error ? t(plansResult.reason.message) : t("Published subscription plans could not be loaded. Please try again."));
    }

    let latestPayments: ShopSubscriptionPaymentRecord[] | null = null;
    if (historyGeneration === paymentHistoryGeneration.current && paymentsResult.status === "fulfilled") {
      const page = paymentsResult.value;
      latestPayments = page.items;
      paymentsRef.current = latestPayments;
      setPayments(latestPayments);
      setPaymentsTotalCount(page.totalCount);
      setPaymentsHasMore(page.skip + page.items.length < page.totalCount);
      setHistoryError("");
      setOlderPaymentsError("");
    } else if (historyGeneration === paymentHistoryGeneration.current && paymentsResult.status === "rejected") {
      setHistoryError(paymentsResult.reason instanceof Error ? t(paymentsResult.reason.message) : t("Payment history could not be refreshed. Checkout is paused until SmartPrint can confirm whether a payment is already pending."));
    }

    setLoading(false);
    setRefreshing(false);
    return latestPayments;
  }

  async function loadMorePayments() {
    if (loadingMorePaymentsRef.current || !paymentsHasMore) return;
    loadingMorePaymentsRef.current = true;
    setPaymentsLoadingMore(true);
    setOlderPaymentsError("");
    const historyGeneration = paymentHistoryGeneration.current;
    try {
      const page = await getShopSubscriptionPayments(token, paymentsRef.current.length, 20);
      if (historyGeneration !== paymentHistoryGeneration.current) return;
      const knownIds = new Set(paymentsRef.current.map(payment => payment.id));
      const merged = [...paymentsRef.current, ...page.items.filter(payment => !knownIds.has(payment.id))];
      paymentsRef.current = merged;
      setPayments(merged);
      setPaymentsTotalCount(page.totalCount);
      setPaymentsHasMore(page.skip + page.items.length < page.totalCount);
      setShowAllPayments(true);
    } catch (exception) {
      if (historyGeneration === paymentHistoryGeneration.current) {
        setOlderPaymentsError(exception instanceof Error ? t(exception.message) : t("Older subscription payments could not be loaded. Try again."));
      }
    } finally {
      loadingMorePaymentsRef.current = false;
      setPaymentsLoadingMore(false);
    }
  }

  useEffect(() => {
    mounted.current = true;
    void refreshData();
    // Warm the checkout script while the owner reviews plans, before any payable order is created.
    void ensureRazorpayCheckout().catch(() => undefined);
    return () => {
      mounted.current = false;
      pollSequence.current += 1;
    };
    // The current token identifies the shop session; do not create another payment session on refresh.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token]);

  async function pollPaymentStatus(reference: string, matchByRecordId = false) {
    const sequence = ++pollSequence.current;
    for (const delay of statusPollDelays) {
      await new Promise(resolve => window.setTimeout(resolve, delay));
      if (!mounted.current || sequence !== pollSequence.current) return;

      await refreshData();
      let payment = paymentsRef.current.find(item =>
        matchByRecordId ? item.id === reference : item.razorpayOrderId === reference
      );
      if (!payment) continue;

      if (matchByRecordId && (payment.status === "CREATING" || payment.status === "CREATION_UNKNOWN")) {
        // Re-check the provider receipt while the order-create response is still ambiguous.
        // The endpoint never creates a new order; it only reconciles the existing intent.
        try {
          await resumeShopSubscriptionPaymentOrder(token, payment.id);
        } catch {
          // A still-ambiguous result is expected until Razorpay exposes the original order.
        }
        await refreshData();
        payment = paymentsRef.current.find(item => item.id === reference);
        if (!payment) continue;
      }

      if (payment.status === "CAPTURED") {
        setNotice(t(subscriptionPaymentStatusMessage(payment.status)));
        await refreshSubscriptionSummary();
        return;
      }
      if (payment.status === "FAILED" || payment.status === "CAPTURED_REVIEW_REQUIRED") {
        setNotice(subscriptionPaymentStatusMessage(payment.status));
        return;
      }
    }

    if (mounted.current && sequence === pollSequence.current) {
      setNotice(t("Payment status is still being confirmed. SmartPrint will not activate access until Razorpay capture is verified. You can safely use Refresh status to check again."));
    }
  }

  async function openRazorpayCheckout(
    order: Awaited<ReturnType<typeof createShopSubscriptionPaymentOrder>>,
    optionKey: string,
    paymentRecordId?: string
  ) {
    if (!window.Razorpay) throw new Error("Razorpay Checkout is unavailable in this browser.");

    const checkout = new window.Razorpay({
      key: order.keyId,
      amount: order.amountMinor,
      currency: order.currency,
      name: "SmartPrint by QuantaEdge",
      description: order.planName + " · " + order.billingInterval + " subscription",
      order_id: order.orderId,
      theme: { color: "#7662E8" },
      handler: async response => {
        if (response.razorpay_order_id && response.razorpay_order_id !== order.orderId) {
          setError(t("The payment response did not match the subscription order. SmartPrint has not activated the plan."));
          setBusyOption(null);
          void pollPaymentStatus(paymentRecordId ?? order.orderId, Boolean(paymentRecordId));
          return;
        }

        try {
          const verified = await verifyShopSubscriptionPayment(token, {
            orderId: order.orderId,
            paymentId: response.razorpay_payment_id,
            signature: response.razorpay_signature
          });
          setNotice(t(subscriptionPaymentStatusMessage(verified.status)));
          if (verified.status === "CAPTURED") {
            requestIds.current.delete(optionKey);
            await refreshData();
            await refreshSubscriptionSummary();
          } else {
            await refreshData();
            void pollPaymentStatus(paymentRecordId ?? order.orderId, Boolean(paymentRecordId));
          }
        } catch (exception) {
          setNotice(t("Razorpay returned a payment response, but SmartPrint could not complete immediate verification. Paid access remains protected while the webhook and status checks reconcile the payment."));
          setError(exception instanceof Error ? t(exception.message) : t("Immediate payment verification could not be completed."));
          void pollPaymentStatus(paymentRecordId ?? order.orderId, Boolean(paymentRecordId));
        } finally {
          setBusyOption(null);
        }
      },
      modal: {
        ondismiss: () => {
          setBusyOption(null);
          setNotice(t("Checkout was closed. No subscription access was granted. SmartPrint is checking whether a payment completed before the window closed."));
          void pollPaymentStatus(paymentRecordId ?? order.orderId, Boolean(paymentRecordId));
        }
      }
    });

    checkout.on("payment.failed", response => {
      setBusyOption(null);
      setError(t(response.error?.description || "Razorpay could not complete this payment. You can retry the same order."));
      void pollPaymentStatus(paymentRecordId ?? order.orderId, Boolean(paymentRecordId));
    });
    checkout.open();
  }

  async function startCheckout(plan: ShopSubscriptionPlanOption, interval: SubscriptionBillingInterval) {
    const optionKey = plan.id + "|" + interval;
    if (busyOption) return;

    setBusyOption(optionKey);
    setError("");
    setNotice("");
    try {
      // Ensure the checkout script is ready before creating a provider order. This avoids
      // leaving an unpaid order behind when the script cannot load on the owner's device.
      await ensureRazorpayCheckout();
      const clientRequestId = requestIds.current.get(optionKey) ?? crypto.randomUUID();
      requestIds.current.set(optionKey, clientRequestId);
      const order = await createShopSubscriptionPaymentOrder(token, {
        planVersionId: plan.id,
        billingInterval: interval,
        clientRequestId
      });
      if (order.provider.toLowerCase() !== "razorpay" || !order.keyId || !order.orderId) {
        throw new Error("SmartPrint did not return a valid Razorpay checkout order.");
      }
      await openRazorpayCheckout(order, optionKey);
    } catch (exception) {
      setBusyOption(null);
      setError(exception instanceof Error ? t(exception.message) : t("Could not start Razorpay checkout."));
      void refreshData();
      void refreshSubscriptionSummary();
    }
  }

  async function resumePendingPayment(payment: ShopSubscriptionPaymentRecord) {
    if (busyOption) return;

    setBusyOption("pending:" + payment.id);
    setError("");
    setNotice("");
    try {
      const order = await resumeShopSubscriptionPaymentOrder(token, payment.id);
      if (order.provider.toLowerCase() !== "razorpay" || !order.keyId || !order.orderId) {
        throw new Error("SmartPrint did not return a resumable Razorpay order.");
      }
      // Resolve the provider state before loading Checkout; a captured or still-unknown
      // payment should never wait on the checkout SDK or be opened again.
      await ensureRazorpayCheckout();
      await openRazorpayCheckout(order, "pending:" + payment.id, payment.id);
    } catch (exception) {
      setBusyOption(null);
      setError(exception instanceof Error ? t(exception.message) : t("Could not resume the existing payment."));
      await refreshData();
      await refreshSubscriptionSummary();
      if (payment.status === "CREATING" || payment.status === "CREATION_UNKNOWN") {
        void pollPaymentStatus(payment.id, true);
      }
    }
  }

  const pendingPayment = payments.find(payment => isUnsettledSubscriptionPayment(payment.status));
  const currentPaidTerm = currentSubscription.status.toLowerCase() === "active" &&
    Date.parse(currentSubscription.periodEndUtc) > Date.now();

  async function copyPendingPaymentReference() {
    if (!pendingPayment) return;
    try {
      if (!navigator.clipboard?.writeText) throw new Error("Clipboard is unavailable");
      await navigator.clipboard.writeText(pendingPayment.razorpayOrderId);
      setNotice(t("Payment reference copied. Share it with SmartPrint support if the order remains unresolved."));
    } catch {
      setNotice(t("Payment reference: {reference}", { reference: pendingPayment.razorpayOrderId }));
    }
  }

  function printReceipt(payment: ShopSubscriptionPaymentRecord) {
    if (!printSubscriptionReceipt(payment, locale)) {
      setError(t("Your browser blocked the receipt window. Allow pop-ups for SmartPrint and try again."));
    }
  }

  return (
    <section className="panel subscription-payment-panel" aria-labelledby="subscription-payment-heading">
      <div className="subscription-payment-heading">
        <div>
          <div className="subscription-eyebrow">{t("SECURE SUBSCRIPTION BILLING")}</div>
          <h2 id="subscription-payment-heading">{t("Choose or renew a plan")}</h2>
          <p>{t("Pay SmartPrint by QuantaEdge securely with Razorpay. Your shop's customer print payments remain completely separate.")}</p>
        </div>
        <button type="button" className="secondary-button small" onClick={() => void refreshData(true)} disabled={refreshing || loading}>
          <RefreshCw size={14} className={refreshing ? "subscription-refresh-spin" : ""}/> {t("Refresh status")}
        </button>
      </div>

      {error && <div className="inline-error subscription-payment-message" role="alert"><AlertCircle size={15}/><span>{error}</span></div>}
      {notice && <div className="inline-success subscription-payment-message" role="status"><CheckCircle2 size={15}/><span>{notice}</span></div>}
      {historyError && <div className="inline-error subscription-payment-message" role="status"><AlertCircle size={15}/><span>{historyError}</span></div>}
      {planError && plans.length > 0 && <div className="inline-error subscription-payment-message" role="status"><AlertCircle size={15}/><span>{t("Could not refresh the published plan list. Existing plan choices remain visible.")}</span></div>}

      {pendingPayment && (
        <div className="inline-error subscription-payment-message" role="status">
          <Clock3 size={16}/>
          <span>
            <strong>{t("Existing subscription payment needs your attention.")}</strong>{" "}
            SmartPrint · {t(pendingPayment.billingInterval)} · {amount(pendingPayment.amountMinor, pendingPayment.currency)} — {t(subscriptionPaymentStatusMessage(pendingPayment.status))}{" "}
            {pendingPayment.status === "CAPTURED_REVIEW_REQUIRED"
              ? t("Do not pay again. SmartPrint support must finish reviewing this payment.")
              : t("Continue this exact order to avoid creating a duplicate charge. Unpaid Razorpay orders cannot be cancelled from this page.")}
            <small className="subscription-payment-reference">{t("Payment reference: {reference}", { reference: pendingPayment.razorpayOrderId })}</small>
          </span>
          {pendingPayment.status !== "CAPTURED_REVIEW_REQUIRED" && (
            <button type="button" className="secondary-button small" onClick={copyPendingPaymentReference}>
              Copy reference
            </button>
          )}
          {pendingPayment.status !== "CAPTURED_REVIEW_REQUIRED" && (
            <button
              type="button"
              className="secondary-button small"
              onClick={() => void resumePendingPayment(pendingPayment)}
              disabled={Boolean(busyOption)}
            >
              {busyOption === "pending:" + pendingPayment.id ? <span className="button-spinner"/> : <CreditCard size={14}/>}
              {t(subscriptionPaymentActionLabel(pendingPayment.status))}
            </button>
          )}
        </div>
      )}

      {currentPaidTerm && (
        <div className="subscription-payment-message subscription-payment-renewal-note" role="status">
          <ShieldCheck size={16}/>
          <span>{t("Your current paid term remains active through {date}. A SmartPrint renewal payment made now will begin after that date, preserving the time you have already paid for.", { date: formatIstDate(currentSubscription.periodEndUtc) })}</span>
        </div>
      )}

      {loading ? (
        <div className="platform-loading-inline"><span className="button-spinner"/> {t("Loading published plans…")}</div>
      ) : planError && plans.length === 0 ? (
        <div className="inline-error subscription-payment-message" role="alert">
          <AlertCircle size={15}/><span>{t("Published plans could not be loaded: {error}", { error: planError })}</span>
          <button type="button" className="secondary-button small" onClick={() => void refreshData(true)} disabled={refreshing}>{t("Try again")}</button>
        </div>
      ) : plans.length === 0 ? (
        <div className="subscription-payment-empty">{t("There are no published paid plans available right now. Please contact SmartPrint support.")}</div>
      ) : (
        <div className="subscription-payment-grid">
          {plans.map(plan => {
            const intervals = getPaidBillingIntervals(plan);
            return (
              <article className="subscription-payment-plan" key={plan.id}>
                <div className="subscription-payment-plan-top">
                  <div>
                    <span className="subscription-payment-plan-code">{t("ONE SMARTPRINT SUBSCRIPTION")}</span>
                    <h3>{plan.name}</h3>
                  </div>
                  <span className="subscription-payment-plan-lock"><ShieldCheck size={16}/></span>
                </div>
                {plan.description && <p className="subscription-payment-description">{plan.description}</p>}
                <div className="subscription-payment-entitlements">
                  <span><strong>{plan.maxPrinters == null ? t("No plan-specific cap") : t("Up to {count}", { count: formatLocaleNumber(plan.maxPrinters, locale) })}</strong> {t("printers")}</span>
                  <span><strong>{plan.maxAgentDevices == null ? t("No plan-specific cap") : t("Up to {count}", { count: formatLocaleNumber(plan.maxAgentDevices, locale) })}</strong> {t("computers")}</span>
                  <span><strong>{plan.maxQrStations == null ? t("No plan-specific cap") : t("Up to {count}", { count: formatLocaleNumber(plan.maxQrStations, locale) })}</strong> {t("QR stations")}</span>
                  <span><strong>{plan.maxMonthlyPrintJobs == null ? t("No plan-specific cap") : t("Up to {count}", { count: formatLocaleNumber(plan.maxMonthlyPrintJobs, locale) })}</strong> {t("monthly jobs")}</span>
                  <span><strong>{plan.maxDocumentBytes == null ? t("No plan-specific cap") : t("Up to {count} MB", { count: formatLocaleNumber(plan.maxDocumentBytes / (1024 * 1024), locale, { maximumFractionDigits: 1 }) })}</strong> {t("cloud document size")}</span>
                  <span className={plan.cloudAssistedPrintingEnabled ? "included" : ""}>{t("Cloud-assisted printing")} {plan.cloudAssistedPrintingEnabled ? t("included") : t("not included")}</span>
                  <span className={plan.whiteLabelEnabled ? "included" : ""}>{t("White-label")} {plan.whiteLabelEnabled ? t("included") : t("not included")}</span>
                  <span className={plan.advancedReportsEnabled ? "included" : ""}>{t("Advanced reports")} {plan.advancedReportsEnabled ? t("included") : t("not included")}</span>
                </div>
                <small className="subscription-payment-limit-note">{t("Platform-wide file-size, security and operational limits continue to apply.")}</small>
                {currentPaidTerm && currentSubscription.planCode !== plan.planCode && (
                  <div className="inline-error subscription-payment-message">
                    {t("Your current paid product remains active through {date}. A different product can only be selected after a credit or proration quote is available.", { date: formatIstDate(currentSubscription.periodEndUtc) })}
                  </div>
                )}
                <div className="subscription-payment-actions">
                  {intervals.map(interval => {
                    const amount = priceFor(plan, interval);
                    const key = plan.id + "|" + interval;
                    const annualSavings = interval === "Annual" ? getAnnualSavingsMinor(plan) : 0;
                    const annualSavingsPercent = interval === "Annual" ? getAnnualSavingsPercent(plan) : 0;
                    const blockedProductSwitch = currentPaidTerm &&
                      currentSubscription.planCode !== plan.planCode;
                    const buttonDisabled = Boolean(busyOption) || Boolean(pendingPayment) ||
                      Boolean(historyError) || blockedProductSwitch;
                    return amount === null ? null : (
                      <button
                        key={key}
                        type="button"
                        className="primary-button subscription-payment-button"
                        onClick={() => void startCheckout(plan, interval)}
                        disabled={buttonDisabled}
                        title={blockedProductSwitch ? t("Switching to a different product before your current paid term ends requires a credit quote.") : undefined}
                      >
                        {busyOption === key ? <span className="button-spinner"/> : <CreditCard size={15}/>}
                        <span>
                          <strong>{currentPaidTerm ? t("Pay for next term") : t("Pay")} {formatLocalizedSubscriptionAmount(amount, plan.currency, locale)}</strong>
                          <small>
                            {interval === "Monthly" ? t("per month") : t("per year")}
                            {annualSavings > 0 ? ` · ${t("save {amount} ({percent}%) vs 12 monthly payments", { amount: formatLocalizedSubscriptionAmount(annualSavings, plan.currency, locale), percent: formatLocaleNumber(annualSavingsPercent, locale) })}` : ""}
                            {currentPaidTerm ? ` · ${t("starts after {date}", { date: formatIstDate(currentSubscription.periodEndUtc) })}` : ""}
                          </small>
                        </span>
                      </button>
                    );
                  })}
                </div>
              </article>
            );
          })}
        </div>
      )}

      {olderPaymentsError && (
        <div className="inline-error subscription-payment-message" role="alert">{olderPaymentsError}</div>
      )}

      {payments.length > 0 && (
        <div className="subscription-payment-history">
          <div className="subscription-payment-history-heading">
            <Clock3 size={15}/>
            <strong>{t("Subscription payment history ({count})", { count: formatLocaleNumber(paymentsTotalCount, locale) })}</strong>
            {payments.length > 5 && !showAllPayments && (
              <button type="button" className="secondary-button small" onClick={() => setShowAllPayments(true)}>
                {t("Show all loaded")}
              </button>
            )}
            {showAllPayments && (
              <button type="button" className="secondary-button small" onClick={() => setShowAllPayments(false)}>
                {t("Show recent")}
              </button>
            )}
          </div>
          {(showAllPayments ? payments : payments.slice(0, 5)).map(payment => (
            <div className="subscription-payment-history-row" key={payment.id}>
              <div>
                <strong>SmartPrint · {t(payment.billingInterval)}</strong>
                <small>{t("Created {date}", { date: formatIstDateTime(payment.createdUtc) })}{payment.verifiedUtc ? ` · ${t("verified {date}", { date: formatIstDateTime(payment.verifiedUtc) })}` : ""}</small>
                <small>{t("Order: {reference}", { reference: payment.razorpayOrderId })}</small>
                {payment.razorpayPaymentId && <small>{t("Payment: {reference}", { reference: payment.razorpayPaymentId })}</small>}
              </div>
              <span className={"subscription-payment-history-status " + payment.status.toLowerCase()}>{t(subscriptionPaymentStatusLabel(payment.status))}</span>
              <div className="subscription-payment-history-amount">
                <strong>{formatSubscriptionAmount(payment.amountMinor, payment.currency)}</strong>
                {payment.status === "CAPTURED" && payment.razorpayPaymentId && (
                  <button type="button" className="secondary-button small" onClick={() => printReceipt(payment)}>{t("Print receipt")}</button>
                )}
              </div>
            </div>
          ))}
          {showAllPayments && paymentsHasMore && (
            <button
              type="button"
              className="secondary-button small subscription-payment-load-more"
              onClick={() => void loadMorePayments()}
              disabled={paymentsLoadingMore}
            >
              {paymentsLoadingMore ? <span className="button-spinner"/> : <RefreshCw size={13}/>}
              {paymentsLoadingMore ? t("Loading payments…") : t("Load 20 older payments · {count} remaining", { count: formatLocaleNumber(Math.max(0, paymentsTotalCount - payments.length), locale) })}
            </button>
          )}
        </div>
      )}

      <div className="subscription-payment-footnote">
        {t("Monthly and annual subscriptions are one-time payments, not automatic renewals. SmartPrint will not charge your shop again automatically; renew manually from this page. Paid features are enabled only after Razorpay capture is verified.")}
      </div>
    </section>
  );
}
