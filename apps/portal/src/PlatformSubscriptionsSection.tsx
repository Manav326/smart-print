import { useEffect, useMemo, useState } from "react";
import { formatIstDate } from "./date-time";
import { CalendarClock, CheckCircle2, ChevronLeft, ChevronRight, CreditCard, History, RefreshCw, Search, ShieldCheck, X } from "lucide-react";
import {
  assignPlatformSubscription,
  getPlatformPlans,
  getPlatformSubscriptionHistory,
  getPlatformSubscriptions,
  type AuthTokens,
  type PlatformPlan,
  type PlatformSubscriptionAudit,
  type PlatformSubscriptionItem,
  type PlatformSubscriptionsPage
} from "./api";

type AssignmentOption = {
  value: string;
  label: string;
  requiresPayment: boolean;
};

function formatPlanPrice(amountMinor: number, currency: string): string {
  return `${currency} ${(amountMinor / 100).toFixed(2)}`;
}

function buildAssignmentOptions(plans: PlatformPlan[]): AssignmentOption[] {
  const options: AssignmentOption[] = [];
  for (const plan of plans) {
    if (!plan.isActive) continue;
    for (const version of plan.versions) {
      if (version.status !== "Published") continue;
      if (plan.isSystemPlan && plan.code === "TRIAL" && version.publishedUtc) {
        options.push({
          value: `${version.id}|Trial`,
          label: `${plan.name} · version ${version.version} · 14-day trial`,
          requiresPayment: false
        });
        continue;
      }
      if (plan.isSystemPlan) continue;
      if (version.monthlyPriceMinor !== null) {
        options.push({
          value: `${version.id}|Monthly`,
          label: `${plan.name} · version ${version.version} · ${version.monthlyPriceMinor === 0 ? "free monthly" : `${formatPlanPrice(version.monthlyPriceMinor, version.currency)} / month`}`,
          requiresPayment: version.monthlyPriceMinor > 0
        });
      }
      if (version.annualPriceMinor !== null) {
        options.push({
          value: `${version.id}|Annual`,
          label: `${plan.name} · version ${version.version} · ${version.annualPriceMinor === 0 ? "free annual" : `${formatPlanPrice(version.annualPriceMinor, version.currency)} / year`}`,
          requiresPayment: version.annualPriceMinor > 0
        });
      }
    }
  }
  return options;
}

function date(value: string | null | undefined) {
  return value
    ? formatIstDate(value)
    : "—";
}

export default function PlatformSubscriptionsSection({ tokens }: { tokens: AuthTokens }) {
  const { locale } = useLocale();
  const t = (value: string) => translateUiText(locale, value);
  const [page, setPage] = useState<PlatformSubscriptionsPage | null>(null);
  const [plans, setPlans] = useState<PlatformPlan[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");
  const [search, setSearch] = useState("");
  const [pageNumber, setPageNumber] = useState(1);
  const [refreshKey, setRefreshKey] = useState(0);
  const [assignTenant, setAssignTenant] = useState<PlatformSubscriptionItem | null>(null);
  const [selectedOption, setSelectedOption] = useState("");
  const [reason, setReason] = useState("");
  const [paymentReference, setPaymentReference] = useState("");
  const [paymentConfirmed, setPaymentConfirmed] = useState(false);
  const [requestKey, setRequestKey] = useState("");
  const [historyTenant, setHistoryTenant] = useState<string | null>(null);
  const [history, setHistory] = useState<PlatformSubscriptionAudit[]>([]);
  const [historyLoading, setHistoryLoading] = useState(false);

  const options = useMemo(() => buildAssignmentOptions(plans), [plans]);
  const selectedAssignment = options.find(option => option.value === selectedOption);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    void Promise.all([
      getPlatformSubscriptions(tokens.accessToken, { search, page: pageNumber, pageSize: 20 }),
      getPlatformPlans(tokens.accessToken)
    ]).then(([subscriptions, catalogue]) => {
      if (!current) return;
      setPage(subscriptions);
      setPlans(catalogue);
    }).catch(exception => {
      if (current) setError(exception instanceof Error ? exception.message : "Could not load subscription administration.");
    }).finally(() => {
      if (current) setLoading(false);
    });
    return () => { current = false; };
  }, [tokens.accessToken, search, pageNumber, refreshKey]);

  function openAssignment(tenant: PlatformSubscriptionItem) {
    setAssignTenant(tenant);
    setSelectedOption(options[0]?.value ?? "");
    setReason("");
    setPaymentReference("");
    setPaymentConfirmed(false);
    setRequestKey(crypto.randomUUID());
    setError("");
    setSuccess("");
  }

  async function submitAssignment() {
    if (!assignTenant) return;
    const [planVersionId, billingInterval] = selectedOption.split("|");
    if (!planVersionId || !billingInterval || reason.trim().length < 10) {
      setError(t("Choose a plan and provide a reason of at least 10 characters."));
      return;
    }
    const normalizedPaymentReference = paymentReference.trim();
    if (selectedAssignment?.requiresPayment &&
        (!paymentConfirmed || !/^[A-Za-z0-9_-]{4,100}$/.test(normalizedPaymentReference))) {
      setError(t("Confirm that the payment was received and enter its transaction reference."));
      return;
    }
    setBusy(true);
    setError("");
    setSuccess("");
    try {
      const result = await assignPlatformSubscription(tokens.accessToken, assignTenant.id, {
        planVersionId,
        billingInterval,
        reason: reason.trim(),
        idempotencyKey: requestKey,
        paymentConfirmed: Boolean(selectedAssignment?.requiresPayment && paymentConfirmed),
        paymentReference: selectedAssignment?.requiresPayment ? normalizedPaymentReference : undefined
      });
      setSuccess(`${result.planCode} assigned to ${assignTenant.businessName}. ${result.status} until ${date(result.periodEndUtc)}.`);
      setAssignTenant(null);
      setHistoryTenant(null);
      setRefreshKey(value => value + 1);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "The subscription could not be assigned.");
    } finally {
      setBusy(false);
    }
  }

  async function toggleHistory(tenant: PlatformSubscriptionItem) {
    if (historyTenant === tenant.id) {
      setHistoryTenant(null);
      setHistory([]);
      return;
    }
    setHistoryTenant(tenant.id);
    setHistory([]);
    setHistoryLoading(true);
    setError("");
    try {
      setHistory(await getPlatformSubscriptionHistory(tokens.accessToken, tenant.id));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load subscription history.");
    } finally {
      setHistoryLoading(false);
    }
  }

  const canAssign = options.length > 0;

  return (
    <section className="platform-plans platform-subscriptions">
      <div className="platform-plans-intro">
        <div>
          <div className="platform-plan-kicker"><CreditCard size={14}/> {t("SUBSCRIPTION OPERATIONS")}</div>
          <h2>{t("Shop subscriptions")}</h2>
          <p>{t("Choose a published plan. Once the payment is received and confirmed, grant the shop access from this screen.")}</p>
        </div>
        <div className="platform-plans-actions">
          <button className="secondary-button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}><RefreshCw size={14}/> {t("Refresh")}</button>
        </div>
      </div>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {success && <div className="inline-success" role="status"><CheckCircle2 size={15}/><span>{success}</span><button className="icon-button" onClick={() => setSuccess("")} aria-label={t("Dismiss")}><X size={14}/></button></div>}

      <section className="panel platform-tenants-panel">
        <div className="platform-tenants-heading">
          <div><h2>{t("Subscription directory")}</h2><p>{t("Current subscription per tenant; older assignments remain available in history.")}</p></div>
          <span className="neutral-chip">{page?.totalCount ?? 0} shops</span>
        </div>
        <div className="platform-tenant-filters">
          <label className="platform-search"><Search size={15}/><input value={search} onChange={event => { setSearch(event.target.value); setPageNumber(1); }} placeholder={t("Search shop name or tenant slug")}/></label>
          <span className="platform-status-filter"><ShieldCheck size={14}/> {t("Platform-only controls")}</span>
        </div>

        {loading && !page ? <div className="platform-loading-inline"><span className="button-spinner"/> Loading subscriptions…</div> : null}
        {!loading && page && page.items.length === 0 ? <div className="platform-empty">{t("No shops match this search.")}</div> : null}
        {page && page.items.length > 0 && (
          <div className="platform-table-scroll">
            <table className="platform-tenant-table platform-subscription-table">
              <thead><tr><th>{t("SHOP")}</th><th>{t("ASSIGNED PLAN")}</th><th>{t("STATUS")}</th><th>{t("PERIOD END")}</th><th>{t("ACTIONS")}</th></tr></thead>
              <tbody>
                {page.items.map(tenant => (
                  <tr key={tenant.id}>
                    <td><div className="platform-shop-cell"><div className="platform-shop-icon"><CreditCard size={15}/></div><div><strong>{tenant.businessName}</strong><small>{tenant.slug}</small></div></div></td>
                    <td>
                      <div className="platform-subs-copy">
                        <strong>{tenant.plan?.name ?? tenant.subscription?.planCode ?? "No plan assigned"}</strong>
                        <small>{tenant.subscription ? `${tenant.subscription.billingInterval} · ${tenant.plan ? `v${tenant.plan.version}` : "legacy"}` : "No subscription record"}</small>
                      </div>
                    </td>
                    <td><span className={`platform-subscription-state ${(tenant.subscription?.status ?? "unassigned").toLowerCase()}`}>{tenant.subscription?.status ?? "Unassigned"}</span></td>
                    <td><div className="platform-subs-copy"><strong>{date(tenant.subscription?.periodEndUtc)}</strong><small>{tenant.subscription ? `Started ${date(tenant.subscription.periodStartUtc)}` : "—"}</small></div></td>
                    <td><div className="platform-subscription-actions">
                      <button className="secondary-button small" type="button" onClick={() => void toggleHistory(tenant)}><History size={13}/> {t("History")}</button>
                      <button className="primary-button small" type="button" disabled={!canAssign} onClick={() => openAssignment(tenant)}>{t("Grant plan access")}</button>
                    </div></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {page && <div className="platform-pagination">
          <span>Page {page.page} · {page.totalCount.toLocaleString("en-IN")} shops</span>
          <div>
            <button className="icon-button" onClick={() => setPageNumber(value => Math.max(1, value - 1))} disabled={page.page <= 1 || loading} aria-label={t("Previous page")}><ChevronLeft size={17}/></button>
            <button className="icon-button" onClick={() => setPageNumber(value => value + 1)} disabled={page.page * page.pageSize >= page.totalCount || loading} aria-label={t("Next page")}><ChevronRight size={17}/></button>
          </div>
        </div>}
      </section>

      {historyTenant && (
        <section className="panel platform-tenants-panel platform-subscription-history">
          <div className="platform-tenants-heading"><div><h2>{t("Assignment history")}</h2><p>{t("Append-only platform audit events for {shop}.").replace("{shop}", page?.items.find(item => item.id === historyTenant)?.businessName ?? t("this shop"))}</p></div><button className="icon-button" onClick={() => setHistoryTenant(null)} aria-label={t("Close history")}><X size={15}/></button></div>
          {historyLoading ? <div className="platform-loading-inline"><span className="button-spinner"/> Loading history…</div> :
            history.length === 0 ? <div className="platform-empty">{t("No platform assignment events have been recorded for this shop yet.")}</div> :
            <div className="platform-table-scroll"><table className="platform-tenant-table platform-history-table">
              <thead><tr><th>{t("EVENT")}</th><th>{t("PLAN")}</th><th>{t("STATUS")}</th><th>{t("PREVIOUS STATUS")}</th><th>{t("PERIOD")}</th><th>{t("REASON")}</th><th>{t("RECORDED")}</th></tr></thead>
              <tbody>{history.map(event => <tr key={event.id}>
                <td><strong>{event.eventType.replace(/_/g, " ")}</strong></td>
                <td>{event.planCode} · {event.billingInterval}{event.paymentReference ? <small>Payment ref: {event.paymentReference}</small> : null}</td>
                <td>{event.status}</td>
                <td>{event.previousStatus ?? "First assignment"}</td>
                <td>{date(event.periodStartUtc)} – {date(event.periodEndUtc)}</td>
                <td className="platform-history-reason">{event.reason}</td>
                <td>{date(event.createdUtc)}</td>
              </tr>)}</tbody>
            </table></div>
          }
        </section>
      )}

      {assignTenant && (
        <div className="platform-assignment-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !busy) setAssignTenant(null); }}>
          <section className="panel platform-assignment-modal" role="dialog" aria-modal="true" aria-labelledby="subscription-assignment-title">
            <div className="platform-assignment-heading">
              <div><div className="platform-plan-kicker"><CalendarClock size={14}/> {t("SUBSCRIPTION ACCESS")}</div><h2 id="subscription-assignment-title">{t("Grant plan access")}</h2><p>{assignTenant.businessName} · {assignTenant.slug}</p></div>
              <button className="icon-button" onClick={() => setAssignTenant(null)} disabled={busy} aria-label={t("Close assignment")}><X size={16}/></button>
            </div>
            <label className="platform-assignment-field">Published plan
              <select value={selectedOption} onChange={event => {
                setSelectedOption(event.target.value);
                setPaymentReference("");
                setPaymentConfirmed(false);
              }} disabled={busy}>
                {options.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </label>
            {selectedAssignment?.requiresPayment && <>
              <label className="platform-assignment-field">Payment transaction reference / UTR
                <input value={paymentReference} onChange={event => setPaymentReference(event.target.value)} maxLength={100} placeholder={t("Enter the bank / UPI transaction reference")} disabled={busy}/>
              </label>
              <label className="platform-assignment-payment-check">
                <input type="checkbox" checked={paymentConfirmed} onChange={event => setPaymentConfirmed(event.target.checked)} disabled={busy}/>
                <span>{t("I checked the payment account and confirmed that this amount was received.")}</span>
              </label>
            </>}
            <label className="platform-assignment-field">Reason for access grant
              <textarea rows={3} maxLength={500} value={reason} onChange={event => setReason(event.target.value)} placeholder={t("Record why this plan is being granted.")} disabled={busy}/>
              <small>{reason.trim().length}/500 characters · minimum 10</small>
            </label>
            <div className="platform-assignment-guard"><ShieldCheck size={15}/><span>{selectedAssignment?.requiresPayment ? "Access is granted as soon as you confirm the payment was received and enter its transaction reference. The amount and granting operator are retained in the audit history." : "Trial and explicitly free plans can be granted without payment. Every assignment is recorded in the audit history."}</span></div>
            <div className="platform-assignment-actions">
              <button className="secondary-button" onClick={() => setAssignTenant(null)} disabled={busy}>{t("Cancel")}</button>
              <button className="primary-button" onClick={() => void submitAssignment()} disabled={busy || !selectedOption || reason.trim().length < 10 || Boolean(selectedAssignment?.requiresPayment && (!paymentConfirmed || !/^[A-Za-z0-9_-]{4,100}$/.test(paymentReference.trim())))}>{busy ? "Granting…" : "Grant access"}</button>
            </div>
          </section>
        </div>
      )}
    </section>
  );
}
