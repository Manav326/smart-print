import { useEffect, useRef, useState } from "react";
import { AlertCircle, CheckCircle2, Clock3, CreditCard, RefreshCw, ShieldCheck } from "lucide-react";
import {
  createShopSubscriptionPaymentOrder,
  getShopSubscriptionPayments,
  getShopSubscriptionPlans,
  getShopSubscription,
  verifyShopSubscriptionPayment,
  type ShopSubscriptionPaymentRecord,
  type ShopSubscriptionPlanOption,
  type ShopSubscriptionSummary
} from "./api";
import {
  formatSubscriptionAmount,
  getPaidBillingIntervals,
  type SubscriptionBillingInterval,
  subscriptionPaymentStatusMessage
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
    const existing = document.querySelector<HTMLScriptElement>('script[data-smartprint-razorpay="true"]');
    if (existing) {
      if (existing.dataset.loaded === "true") {
        reject(new Error("Razorpay Checkout did not initialize."));
        return;
      }
      existing.addEventListener("load", () => resolve(), { once: true });
      existing.addEventListener("error", () => reject(new Error("Razorpay Checkout could not be loaded.")), { once: true });
      return;
    }

    const script = document.createElement("script");
    script.src = "https://checkout.razorpay.com/v1/checkout.js";
    script.async = true;
    script.dataset.smartprintRazorpay = "true";
    script.onload = () => {
      script.dataset.loaded = "true";
      resolve();
    };
    script.onerror = () => reject(new Error("Razorpay Checkout could not be loaded. Check your connection and try again."));
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

export default function ShopSubscriptionCheckout({
  token,
  onSubscriptionUpdated
}: {
  token: string;
  onSubscriptionUpdated: (summary: ShopSubscriptionSummary) => void;
}) {
  const [plans, setPlans] = useState<ShopSubscriptionPlanOption[]>([]);
  const [payments, setPayments] = useState<ShopSubscriptionPaymentRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [busyOption, setBusyOption] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const requestIds = useRef(new Map<string, string>());

  async function refreshData(showSpinner = false) {
    if (showSpinner) setRefreshing(true);
    setError("");
    try {
      const [nextPlans, nextPayments, summary] = await Promise.all([
        getShopSubscriptionPlans(token),
        getShopSubscriptionPayments(token),
        getShopSubscription(token)
      ]);
      setPlans(nextPlans);
      setPayments(nextPayments);
      onSubscriptionUpdated(summary);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not refresh subscription payment status.");
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }

  useEffect(() => {
    void refreshData();
    // The current token identifies the shop session; do not create another payment session on refresh.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token]);

  async function startCheckout(plan: ShopSubscriptionPlanOption, interval: SubscriptionBillingInterval) {
    const optionKey = plan.id + "|" + interval;
    if (busyOption) return;

    setBusyOption(optionKey);
    setError("");
    setNotice("");
    try {
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

      await ensureRazorpayCheckout();
      if (!window.Razorpay) {
        throw new Error("Razorpay Checkout is unavailable in this browser.");
      }

      const checkout = new window.Razorpay({
        key: order.keyId,
        amount: order.amountMinor,
        currency: order.currency,
        name: "SmartPrint by QuantaEdge",
        description: order.planName + " · " + interval + " subscription",
        order_id: order.orderId,
        theme: { color: "#7662E8" },
        handler: async response => {
          if (response.razorpay_order_id && response.razorpay_order_id !== order.orderId) {
            setError("The payment response did not match the subscription order. SmartPrint has not activated the plan.");
            setBusyOption(null);
            return;
          }

          try {
            const verified = await verifyShopSubscriptionPayment(token, {
              orderId: order.orderId,
              paymentId: response.razorpay_payment_id,
              signature: response.razorpay_signature
            });
            setNotice(subscriptionPaymentStatusMessage(verified.status));
            if (verified.status === "CAPTURED") requestIds.current.delete(optionKey);
            await refreshData();
          } catch (exception) {
            setNotice("Razorpay returned a payment response, but SmartPrint could not complete the immediate verification request. Access remains protected until the backend verifies capture. You can refresh payment status; the Razorpay webhook will also reconcile a completed payment.");
            setError(exception instanceof Error ? exception.message : "Immediate payment verification could not be completed.");
            try { await refreshData(); } catch { /* The message above keeps the recovery path visible. */ }
          } finally {
            setBusyOption(null);
          }
        },
        modal: {
          ondismiss: () => {
            setBusyOption(null);
            setNotice("Checkout was closed. No subscription access was granted. You can try again at any time.");
          }
        }
      });

      checkout.on("payment.failed", response => {
        setBusyOption(null);
        setError(response.error?.description || "Razorpay could not complete this payment. You can try again.");
        void refreshData();
      });
      checkout.open();
    } catch (exception) {
      setBusyOption(null);
      setError(exception instanceof Error ? exception.message : "Could not start Razorpay checkout.");
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

      {loading ? (
        <div className="platform-loading-inline"><span className="button-spinner"/> Loading published plans…</div>
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
                    <span className="subscription-payment-plan-code">{plan.planCode} · v{plan.version}</span>
                    <h3>{plan.name}</h3>
                  </div>
                  <span className="subscription-payment-plan-lock"><ShieldCheck size={16}/></span>
                </div>
                {plan.description && <p className="subscription-payment-description">{plan.description}</p>}
                <div className="subscription-payment-entitlements">
                  <span><strong>{plan.maxPrinters ?? "—"}</strong> printers</span>
                  <span><strong>{plan.maxAgentDevices ?? "—"}</strong> computers</span>
                  <span><strong>{plan.maxQrStations ?? "—"}</strong> QR stations</span>
                  <span><strong>{plan.maxMonthlyPrintJobs?.toLocaleString("en-IN") ?? "—"}</strong> monthly jobs</span>
                  <span className={plan.cloudAssistedPrintingEnabled ? "included" : ""}>Cloud-assisted printing {plan.cloudAssistedPrintingEnabled ? "included" : "not included"}</span>
                  <span className={plan.whiteLabelEnabled ? "included" : ""}>White-label {plan.whiteLabelEnabled ? "included" : "not included"}</span>
                  <span className={plan.advancedReportsEnabled ? "included" : ""}>Advanced reports {plan.advancedReportsEnabled ? "included" : "not included"}</span>
                </div>
                <div className="subscription-payment-actions">
                  {intervals.map(interval => {
                    const amount = priceFor(plan, interval);
                    const key = plan.id + "|" + interval;
                    return amount === null ? null : (
                      <button
                        key={key}
                        type="button"
                        className="primary-button subscription-payment-button"
                        onClick={() => void startCheckout(plan, interval)}
                        disabled={Boolean(busyOption)}
                      >
                        {busyOption === key ? <span className="button-spinner"/> : <CreditCard size={15}/>}
                        <span>
                          <strong>Pay {formatSubscriptionAmount(amount, plan.currency)}</strong>
                          <small>{interval === "Monthly" ? "per month" : "per year"}</small>
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
          <div className="subscription-payment-history-heading"><Clock3 size={15}/><strong>Recent subscription payments</strong></div>
          {payments.slice(0, 5).map(payment => (
            <div className="subscription-payment-history-row" key={payment.id}>
              <div>
                <strong>{payment.planCode} · {payment.billingInterval}</strong>
                <small>{new Date(payment.createdUtc).toLocaleString("en-IN")}</small>
              </div>
              <span className={"subscription-payment-history-status " + payment.status.toLowerCase()}>{payment.status.replace(/_/g, " ")}</span>
              <strong>{formatSubscriptionAmount(payment.amountMinor, payment.currency)}</strong>
            </div>
          ))}
        </div>
      )}

      <div className="subscription-payment-footnote">
        Razorpay confirms the payment directly with SmartPrint before paid features are enabled. A duplicate or delayed webhook cannot activate the same subscription twice.
      </div>
    </section>
  );
}
