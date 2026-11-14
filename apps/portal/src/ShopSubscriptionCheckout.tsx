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

function priceFor(plan: ShopSubscriptionPlanOption, interval: SubscriptionBillingInterval): number | null {
  return interval === "Monthly" ? plan.monthlyPriceMinor : plan.annualPriceMinor;
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  })[character] ?? character);
}

function printSubscriptionReceipt(payment: ShopSubscriptionPaymentRecord): boolean {
  if (payment.status !== "CAPTURED" || !payment.razorpayPaymentId) return false;
  const printWindow = window.open("", "_blank");
  if (!printWindow) return false;
  printWindow.opener = null;

  const rows: Array<[string, string]> = [
    ["Receipt reference", payment.id.toUpperCase()],
    ["Product", "SmartPrint subscription"],
    ["Billing interval", payment.billingInterval === "Annual" ? "Annual" : "Monthly"],
    ["Amount paid", formatSubscriptionAmount(payment.amountMinor, payment.currency)],
    ["Payment status", "Verified"],
    ["Paid on", formatIstDateTime(payment.verifiedUtc ?? payment.createdUtc)],
    ["Razorpay payment ID", payment.razorpayPaymentId],
    ["Razorpay order ID", payment.razorpayOrderId],
    ["Subscription reference", payment.assignedSubscriptionId ?? "Not available"]
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
<p class="note">This is a payment receipt for your records, not a GST tax invoice. It confirms the payment recorded by SmartPrint; it does not replace any applicable tax invoice issued by the legal seller.</p>
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
  const [plans, setPlans] = useState<ShopSubscriptionPlanOption[]>([]);
  const [payments, setPayments] = useState<ShopSubscriptionPaymentRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [busyOption, setBusyOption] = useState<string | null>(null);
  const [planError, setPlanError] = useState("");
  const [historyError, setHistoryError] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [showAllPayments, setShowAllPayments] = useState(false);
  const paymentsRef = useRef<ShopSubscriptionPaymentRecord[]>([]);
  const requestIds = useRef(new Map<string, string>());
  const pollSequence = useRef(0);
  const mounted = useRef(true);

  async function refreshSubscriptionSummary() {
    try {
      const summary = await getShopSubscription(token);
      if (mounted.current) onSubscriptionUpdated(summary);
    } catch {
      if (mounted.current) {
        setNotice("Your payment is verified. The subscription summary could not be refreshed just now; use Refresh status to update the page.");
      }
    }
  }

  async function refreshData(showSpinner = false): Promise<ShopSubscriptionPaymentRecord[] | null> {
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
      setPlanError(plansResult.reason instanceof Error
        ? plansResult.reason.message
        : "Published subscription plans could not be loaded. Please try again.");
    }

    let latestPayments: ShopSubscriptionPaymentRecord[] | null = null;
    if (paymentsResult.status === "fulfilled") {
      latestPayments = paymentsResult.value;
      paymentsRef.current = latestPayments;
      setPayments(latestPayments);
      setHistoryError("");
    } else {
      setHistoryError(paymentsResult.reason instanceof Error
        ? paymentsResult.reason.message
        : "Payment history could not be refreshed. Checkout is paused until SmartPrint can confirm whether a payment is already pending.");
    }

    setLoading(false);
    setRefreshing(false);
    return latestPayments;
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
        setNotice(subscriptionPaymentStatusMessage(payment.status));
        await refreshSubscriptionSummary();
        return;
      }
      if (payment.status === "FAILED" || payment.status === "CAPTURED_REVIEW_REQUIRED") {
        setNotice(subscriptionPaymentStatusMessage(payment.status));
        return;
      }
    }

    if (mounted.current && sequence === pollSequence.current) {
      setNotice("Payment status is still being confirmed. SmartPrint will not activate access until Razorpay capture is verified. You can safely use Refresh status to check again.");
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
          setError("The payment response did not match the subscription order. SmartPrint has not activated the plan.");
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
          setNotice(subscriptionPaymentStatusMessage(verified.status));
          if (verified.status === "CAPTURED") {
            requestIds.current.delete(optionKey);
            await refreshData();
            await refreshSubscriptionSummary();
          } else {
            await refreshData();
            void pollPaymentStatus(paymentRecordId ?? order.orderId, Boolean(paymentRecordId));
          }
        } catch (exception) {
          setNotice("Razorpay returned a payment response, but SmartPrint could not complete immediate verification. Paid access remains protected while the webhook and status checks reconcile the payment.");
          setError(exception instanceof Error ? exception.message : "Immediate payment verification could not be completed.");
          void pollPaymentStatus(paymentRecordId ?? order.orderId, Boolean(paymentRecordId));
        } finally {
          setBusyOption(null);
        }
      },
      modal: {
        ondismiss: () => {
          setBusyOption(null);
          setNotice("Checkout was closed. No subscription access was granted. SmartPrint is checking whether a payment completed before the window closed.");
          void pollPaymentStatus(paymentRecordId ?? order.orderId, Boolean(paymentRecordId));
        }
      }
    });

    checkout.on("payment.failed", response => {
      setBusyOption(null);
      setError(response.error?.description || "Razorpay could not complete this payment. You can retry the same order.");
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
      setError(exception instanceof Error ? exception.message : "Could not start Razorpay checkout.");
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
      setError(exception instanceof Error ? exception.message : "Could not resume the existing payment.");
      await refreshData();
      await refreshSubscriptionSummary();
      if (payment.status === "CREATING" || payment.status === "CREATION_UNKNOWN") {
        void pollPaymentStatus(payment.id, true);
      }
    }
  }

  const pendingPayment = payments.find(payment => isUnsettledSubscriptionPayment(payment.status));
  const currentPaidTerm = currentSubscription.status === "Active" &&
    Date.parse(currentSubscription.periodEndUtc) > Date.now();

  async function copyPendingPaymentReference() {
    if (!pendingPayment) return;
    try {
      if (!navigator.clipboard?.writeText) throw new Error("Clipboard is unavailable");
      await navigator.clipboard.writeText(pendingPayment.razorpayOrderId);
      setNotice("Payment reference copied. Share it with SmartPrint support if the order remains unresolved.");
    } catch {
      setNotice("Payment reference: " + pendingPayment.razorpayOrderId);
    }
  }

  function printReceipt(payment: ShopSubscriptionPaymentRecord) {
    if (!printSubscriptionReceipt(payment)) {
      setError("Your browser blocked the receipt window. Allow pop-ups for SmartPrint and try again.");
    }
  }

  return (
    <section className="panel subscription-payment-panel" aria-labelledby="subscription-payment-heading">
      <div className="subscription-payment-heading">
        <div>
          <div className="subscription-eyebrow">SECURE SUBSCRIPTION BILLING</div>
          <h2 id="subscription-payment-heading">Choose or renew a plan</h2>
          <p>Pay SmartPrint by QuantaEdge securely with Razorpay. Your shop's customer print payments remain completely separate.</p>
        </div>
        <button type="button" className="secondary-button small" onClick={() => void refreshData(true)} disabled={refreshing || loading}>
          <RefreshCw size={14} className={refreshing ? "subscription-refresh-spin" : ""}/> Refresh status
        </button>
      </div>

      {error && <div className="inline-error subscription-payment-message" role="alert"><AlertCircle size={15}/><span>{error}</span></div>}
      {notice && <div className="inline-success subscription-payment-message" role="status"><CheckCircle2 size={15}/><span>{notice}</span></div>}
      {historyError && <div className="inline-error subscription-payment-message" role="status"><AlertCircle size={15}/><span>{historyError}</span></div>}
      {planError && plans.length > 0 && <div className="inline-error subscription-payment-message" role="status"><AlertCircle size={15}/><span>Could not refresh the published plan list. Existing plan choices remain visible.</span></div>}

      {pendingPayment && (
        <div className="inline-error subscription-payment-message" role="status">
          <Clock3 size={16}/>
          <span>
            <strong>Existing subscription payment needs your attention.</strong>{" "}
            SmartPrint · {pendingPayment.billingInterval} · {formatSubscriptionAmount(pendingPayment.amountMinor, pendingPayment.currency)} — {subscriptionPaymentStatusMessage(pendingPayment.status)}{" "}
            {pendingPayment.status === "CAPTURED_REVIEW_REQUIRED"
              ? "Do not pay again. SmartPrint support must finish reviewing this payment."
              : "Continue this exact order to avoid creating a duplicate charge. Unpaid Razorpay orders cannot be cancelled from this page."}
            <small className="subscription-payment-reference">Payment reference: {pendingPayment.razorpayOrderId}</small>
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
              {subscriptionPaymentActionLabel(pendingPayment.status)}
            </button>
          )}
        </div>
      )}

      {currentPaidTerm && (
        <div className="subscription-payment-message subscription-payment-renewal-note" role="status">
          <ShieldCheck size={16}/>
          <span>Your current paid term remains active through <strong>{formatIstDate(currentSubscription.periodEndUtc)}</strong>. A SmartPrint renewal payment made now will begin after that date, preserving the time you have already paid for.</span>
        </div>
      )}

      {loading ? (
        <div className="platform-loading-inline"><span className="button-spinner"/> Loading published plans…</div>
      ) : planError && plans.length === 0 ? (
        <div className="inline-error subscription-payment-message" role="alert">
          <AlertCircle size={15}/><span>Published plans could not be loaded: {planError}</span>
          <button type="button" className="secondary-button small" onClick={() => void refreshData(true)} disabled={refreshing}>Try again</button>
        </div>
      ) : plans.length === 0 ? (
        <div className="subscription-payment-empty">There are no published paid plans available right now. Please contact SmartPrint support.</div>
      ) : (
        <div className="subscription-payment-grid">
          {plans.map(plan => {
            const intervals = getPaidBillingIntervals(plan);
            return (
              <article className="subscription-payment-plan" key={plan.id}>
                <div className="subscription-payment-plan-top">
                  <div>
                    <span className="subscription-payment-plan-code">ONE SMARTPRINT SUBSCRIPTION</span>
                    <h3>{plan.name}</h3>
                  </div>
                  <span className="subscription-payment-plan-lock"><ShieldCheck size={16}/></span>
                </div>
                {plan.description && <p className="subscription-payment-description">{plan.description}</p>}
                <div className="subscription-payment-entitlements">
                  <span><strong>{plan.maxPrinters == null ? "No plan cap" : `Up to ${plan.maxPrinters.toLocaleString("en-IN")}`}</strong> printers</span>
                  <span><strong>{plan.maxAgentDevices == null ? "No plan cap" : `Up to ${plan.maxAgentDevices.toLocaleString("en-IN")}`}</strong> computers</span>
                  <span><strong>{plan.maxQrStations == null ? "No plan cap" : `Up to ${plan.maxQrStations.toLocaleString("en-IN")}`}</strong> QR stations</span>
                  <span><strong>{plan.maxMonthlyPrintJobs == null ? "No plan cap" : `Up to ${plan.maxMonthlyPrintJobs.toLocaleString("en-IN")}`}</strong> monthly jobs</span>
                  <span><strong>{plan.maxDocumentBytes == null ? "No plan cap" : `Up to ${(plan.maxDocumentBytes / (1024 * 1024)).toLocaleString("en-IN", { maximumFractionDigits: 1 })} MB`}</strong> cloud document size</span>
                  <span className={plan.cloudAssistedPrintingEnabled ? "included" : ""}>Cloud-assisted printing {plan.cloudAssistedPrintingEnabled ? "included" : "not included"}</span>
                  <span className={plan.whiteLabelEnabled ? "included" : ""}>White-label {plan.whiteLabelEnabled ? "included" : "not included"}</span>
                  <span className={plan.advancedReportsEnabled ? "included" : ""}>Advanced reports {plan.advancedReportsEnabled ? "included" : "not included"}</span>
                </div>
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
                        title={blockedProductSwitch ? "Switching to a different product before your current paid term ends requires a credit quote." : undefined}
                      >
                        {busyOption === key ? <span className="button-spinner"/> : <CreditCard size={15}/>}
                        <span>
                          <strong>{currentPaidTerm ? "Pay for next term " : "Pay "}{formatSubscriptionAmount(amount, plan.currency)}</strong>
                          <small>
                            {interval === "Monthly" ? "per month" : "per year"}
                            {annualSavings > 0 ? ` · save ${formatSubscriptionAmount(annualSavings, plan.currency)} (${annualSavingsPercent}%)` : ""}
                            {currentPaidTerm ? ` · starts after ${formatIstDate(currentSubscription.periodEndUtc)}` : ""}
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

      {payments.length > 0 && (
        <div className="subscription-payment-history">
          <div className="subscription-payment-history-heading">
            <Clock3 size={15}/>
            <strong>Subscription payment history ({payments.length})</strong>
            {payments.length > 5 && (
              <button type="button" className="secondary-button small" onClick={() => setShowAllPayments(value => !value)}>
                {showAllPayments ? "Show recent" : "Show all"}
              </button>
            )}
          </div>
          {(showAllPayments ? payments : payments.slice(0, 5)).map(payment => (
            <div className="subscription-payment-history-row" key={payment.id}>
              <div>
                <strong>SmartPrint · {payment.billingInterval}</strong>
                <small>Created {formatIstDateTime(payment.createdUtc)}{payment.verifiedUtc ? ` · verified ${formatIstDateTime(payment.verifiedUtc)}` : ""}</small>
                <small>Order: {payment.razorpayOrderId}</small>
                {payment.razorpayPaymentId && <small>Payment: {payment.razorpayPaymentId}</small>}
              </div>
              <span className={"subscription-payment-history-status " + payment.status.toLowerCase()}>{subscriptionPaymentStatusLabel(payment.status)}</span>
              <div className="subscription-payment-history-amount">
                <strong>{formatSubscriptionAmount(payment.amountMinor, payment.currency)}</strong>
                {payment.status === "CAPTURED" && payment.razorpayPaymentId && (
                  <button type="button" className="secondary-button small" onClick={() => printReceipt(payment)}>Print receipt</button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      <div className="subscription-payment-footnote">
        Monthly and annual subscriptions are one-time payments, not automatic renewals. SmartPrint will not charge your shop again automatically; renew manually from this page. Paid features are enabled only after Razorpay capture is verified.
      </div>
    </section>
  );
}
