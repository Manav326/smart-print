import { useEffect, useState } from "react";
import { AlertTriangle, Clock3, LoaderCircle, RefreshCw, Save, ShieldCheck } from "lucide-react";
import { formatIstDateTime } from "./date-time";
import {
  getPlatformOperationalSettings,
  getPlatformOperationalSettingsHistory,
  getPlatformVirtualPrinterTestQueues,
  approvePlatformVirtualPrinterTestQueue,
  revokePlatformVirtualPrinterTestGrant,
  updatePlatformOperationalSettings,
  type AuthTokens,
  type PlatformOperationalSettings,
  type PlatformOperationalSettingsAudit,
  type PlatformOperationalSettingsInput,
  type PlatformVirtualPrinterTestQueue
} from "./api";

type NumericKey =
  | "trialMaxMonthlyPrintJobs"
  | "cloudCompletedDocumentRetentionDays"
  | "agentCompletedDocumentRetentionDays"
  | "abandonedDocumentRetentionDays"
  | "pendingLocalUploadRetentionDays"
  | "expiredUploadTicketRetentionDays"
  | "emptyCustomerSessionRetentionDays"
  | "staleRefreshTokenRetentionDays"
  | "apiRetentionSweepHours"
  | "agentRetentionSweepHours";

const fields: Array<{ key: NumericKey; label: string; hint: string; min: number; max: number; locked?: boolean }> = [
  { key: "trialMaxMonthlyPrintJobs", label: "Trial monthly print quota", hint: "Print jobs per calendar month. Default is 3,000. Paid plan quotas remain versioned under Plans & entitlements.", min: 1, max: 10_000_000 },
  { key: "cloudCompletedDocumentRetentionDays", label: "Cloud completed documents", hint: "Fixed policy: delete standard cloud source files 72 hours after confirmed output. The hourly API sweep performs cleanup.", min: 3, max: 3, locked: true },
  { key: "agentCompletedDocumentRetentionDays", label: "Windows Agent completed documents", hint: "Fixed policy: delete standard local source files 72 hours after confirmed output. Private Printing source files are erased after verified output; non-production test PDFs have a separate 24-hour local retention.", min: 3, max: 3, locked: true },
  { key: "abandonedDocumentRetentionDays", label: "Rejected / fully refunded documents", hint: "Days terminal rejected, quote-expired or fully refunded files are retained when no financial hold exists.", min: 1, max: 3650 },
  { key: "pendingLocalUploadRetentionDays", label: "Pending local uploads", hint: "Days after a local upload becomes terminal unpaid before the API authorises the Agent to delete it.", min: 1, max: 3650 },
  { key: "expiredUploadTicketRetentionDays", label: "Expired upload tickets", hint: "Extra days after an unfinished local upload expires before its ticket record is removed.", min: 1, max: 3650 },
  { key: "emptyCustomerSessionRetentionDays", label: "Empty customer sessions", hint: "Days expired sessions without jobs or upload tickets are kept.", min: 1, max: 3650 },
  { key: "staleRefreshTokenRetentionDays", label: "Expired/revoked refresh tokens", hint: "Additional days before expired or revoked authentication-token records are removed.", min: 1, max: 3650 },
  { key: "apiRetentionSweepHours", label: "API retention sweep interval", hint: "How often the API checks for documents whose retention period has elapsed. Default: hourly.", min: 1, max: 168 },
  { key: "agentRetentionSweepHours", label: "Windows Agent retention sweep interval", hint: "How often paired Windows Agents remove standard completed source files after the retention window. Default: hourly.", min: 1, max: 24 }
];

type Draft = Omit<PlatformOperationalSettings, "updatedByUserId" | "updatedUtc">;
import { useLocale, translateUiText } from "./i18n";
function toDraft(value: PlatformOperationalSettings): Draft {
  const draft: Partial<PlatformOperationalSettings> = { ...value };
  delete draft.updatedByUserId;
  delete draft.updatedUtc;
  return draft as Draft;
}

function VirtualPrinterTestGrantPanel({ tokens }: { tokens: AuthTokens }) {
  const [queues, setQueues] = useState<PlatformVirtualPrinterTestQueue[]>([]);
  const [reason, setReason] = useState("");
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState("");
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");

  async function reload() {
    setLoading(true);
    setError("");
    try {
      setQueues(await getPlatformVirtualPrinterTestQueues(tokens.accessToken));
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not load virtual printer queues."));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { void reload(); }, [tokens.accessToken]);

  async function approve(queue: PlatformVirtualPrinterTestQueue) {
    if (reason.trim().length < 5) {
      setError(t("Enter an approval reason of at least five characters."));
      return;
    }
    setBusyId(queue.id);
    setError("");
    setSuccess("");
    try {
      await approvePlatformVirtualPrinterTestQueue(tokens.accessToken, queue.tenantId, queue.id, reason.trim());
      setReason("");
      setSuccess(t("Virtual queue approved for this shop for seven days. The queue identity remains virtual."));
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not approve this virtual printer."));
    } finally {
      setBusyId("");
    }
  }

  async function revoke(queue: PlatformVirtualPrinterTestQueue, grantId: string) {
    if (reason.trim().length < 5) {
      setError(t("Enter a revocation reason of at least five characters."));
      return;
    }
    const confirmed = window.confirm(
      `Revoke virtual test access for ${queue.displayName} at ${queue.shopName}? New dispatch authorisations will be denied immediately. A job already authorised at the Windows spooler boundary may still be in progress and must be reconciled; revocation does not erase output already produced.`
    );
    if (!confirmed) return;
    setBusyId(queue.id);
    setError("");
    setSuccess("");
    try {
      await revokePlatformVirtualPrinterTestGrant(tokens.accessToken, grantId, reason.trim());
      setReason("");
      setSuccess(t("Virtual printer grant revoked. New dispatches will be denied."));
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not revoke this virtual printer grant."));
    } finally {
      setBusyId("");
    }
  }

  return (
    <section className="panel platform-ops-history">
      <div className="platform-ops-heading">
        <div><h3>{t("Shop-scoped virtual printer test access")}</h3><p>{t("Approve one discovered Windows virtual queue for one shop. Approval is bound to its Agent, queue, driver and port identity, expires after seven days by default, and never changes its printer type.")}</p></div>
        <button className="subtle-button" onClick={() => void reload()} disabled={loading}><RefreshCw size={14}/> {t("Refresh")}</button>
      </div>
      <label className="platform-ops-reason"><span>{t("Approval / revocation reason")}</span><input value={reason} onChange={event => setReason(event.target.value)} maxLength={500} placeholder={t("Reason required for the audit history")}/></label>
      {error && <div className="inline-error" role="alert">{error}</div>}
      {success && <div className="inline-success" role="status">{success}</div>}
      {loading ? <div className="platform-plan-empty">Loading discovered virtual queues…</div> : queues.length === 0
        ? <div className="platform-plan-empty">{t("No unsupported virtual Windows queues are currently discovered.")}</div>
        : <div className="platform-ops-history-list">
            {queues.map(queue => {
              const activeGrant = queue.grants.find(grant => grant.isActive);
              const latestGrant = queue.grants[0];
              const queueFresh = !!queue.lastSeenUtc && Date.parse(queue.lastSeenUtc) >= Date.now() - 2 * 60 * 1000;
              const ready = queue.isActive && queueFresh && queue.agentOnline && (queue.status === "Ready" || queue.status === "Busy");
              return (
                <article className="platform-ops-history-item" key={queue.tenantId + ":" + queue.id}>
                  <div className="platform-ops-heading">
                    <div><strong>{queue.shopName} · {queue.displayName}</strong><small>{queue.shopSlug} · {queue.model} · {queue.connectionType} · Queue state: {queue.status} · {queue.isActive ? "Enabled" : "Disabled"}</small><small>Windows queue: {queue.queueName || "Not reported"} · IPP endpoint / port: {queue.connectionAddress || "Not reported"}</small><small>Paired Agent: {queue.agentDeviceId || "Not paired"} · {queue.agentOnline ? "Online" : "Offline / stale"}{queue.agentLastSeenUtc ? " · last seen " + formatIstDateTime(queue.agentLastSeenUtc) : ""}</small><small>Queue inventory: {queueFresh ? "Fresh" : "Stale / not reported"} · Identity: <code title={queue.identityFingerprint || "Identity unavailable"}>{queue.identityFingerprint ? queue.identityFingerprint.slice(0, 16) + "…" : "Unavailable"}</code></small></div>
                    <span className="neutral-chip">{activeGrant ? ready ? "Approved · ready" : "Grant active · unavailable" : "Not approved"}</span>
                  </div>
                  {activeGrant
                    ? <div><small>Active grant · approved {formatIstDateTime(activeGrant.createdUtc)} by {activeGrant.approvedByName} · expires {formatIstDateTime(activeGrant.expiresUtc)}</small><small>Approval reason: {activeGrant.reason}</small><div className="platform-ops-save-row"><button className="subtle-button" disabled={!!busyId} onClick={() => void revoke(queue, activeGrant.id)}>{busyId === queue.id ? <LoaderCircle size={14} className="platform-spin"/> : "Revoke approval"}</button></div></div>
                    : <div><small>{latestGrant?.revokedUtc ? "Last grant revoked: " + (latestGrant.revocationReason || latestGrant.reason) : !queue.isActive ? "This printer is disabled in the shop. Enable it before requesting a test grant." : !queue.agentOnline ? "The paired Windows Agent is offline or its heartbeat is stale." : !queueFresh ? "Waiting for a fresh Windows queue inventory report." : "No active approval. Queue must be Ready or Busy before approval."}</small><div className="platform-ops-save-row"><button className="primary-button" disabled={!!busyId || !ready || !queue.agentDeviceId || !queue.identityFingerprint} onClick={() => void approve(queue)}>{busyId === queue.id ? <LoaderCircle size={14} className="platform-spin"/> : <ShieldCheck size={14}/>} Approve for 7 days</button></div></div>}
                  {queue.grants.length > 0 && <details><summary>Approval history · {queue.grants.length} record(s)</summary><ul>{queue.grants.map(grant => <li key={grant.id}><strong>{grant.isActive ? "Active" : grant.revokedUtc ? "Revoked" : "Inactive · expired or identity changed"}</strong> · {formatIstDateTime(grant.createdUtc)} · expires {formatIstDateTime(grant.expiresUtc)} · approved by {grant.approvedByName} · {grant.reason}{grant.revokedUtc ? ` · revoked ${formatIstDateTime(grant.revokedUtc)} by ${grant.revokedByName || "system"}: ${grant.revocationReason || "No reason recorded"}` : ""}</li>)}</ul></details>}
                </article>
              );
            })}
          </div>}
    </section>
  );
}

export default function PlatformOperationsSection({ tokens }: { tokens: AuthTokens }) {
  const { locale } = useLocale();
  const t = (value: string) => translateUiText(locale, value);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [history, setHistory] = useState<PlatformOperationalSettingsAudit[]>([]);
  const [reason, setReason] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");

  async function reload() {
    setLoading(true);
    setError("");
    try {
      const [current, events] = await Promise.all([
        getPlatformOperationalSettings(tokens.accessToken),
        getPlatformOperationalSettingsHistory(tokens.accessToken)
      ]);
      setDraft(toDraft(current));
      setHistory(events);
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not load platform operational settings."));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { void reload(); }, [tokens.accessToken]);

  function updateNumber(key: NumericKey, value: string) {
    const parsed = value === "" ? 0 : Number(value);
    if (!Number.isSafeInteger(parsed)) return;
    setDraft(current => current ? { ...current, [key]: parsed } : current);
  }

  async function save() {
    if (!draft) return;
    if (reason.trim().length < 5) {
      setError(t("Enter an audit reason of at least five characters."));
      return;
    }
    setSaving(true);
    setError("");
    setSuccess("");
    try {
      const payload: PlatformOperationalSettingsInput = { ...draft, reason: reason.trim() };
      const saved = await updatePlatformOperationalSettings(tokens.accessToken, payload);
      setDraft(toDraft(saved));
      setReason("");
      setSuccess(t("Platform settings saved. The API and connected Windows Agents will apply the updated values."));
      setHistory(await getPlatformOperationalSettingsHistory(tokens.accessToken));
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not save platform settings."));
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="platform-ops-page">
      <div className="platform-ops-intro">
        <div>
          <div className="platform-plan-kicker"><ShieldCheck size={14}/> {t("PLATFORM POLICY")}</div>
          <h2>{t("Operations, quotas and data retention")}</h2>
          <p>{t("Central controls enforced by the API and paired Windows Agents. Every change is persisted with an audit reason.")}</p>
        </div>
        <button className="secondary-button" onClick={() => void reload()} disabled={loading || saving}>
          <RefreshCw size={14}/> {t("Refresh")}
        </button>
      </div>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {success && <div className="platform-plan-success" role="status"><ShieldCheck size={15}/>{success}</div>}
      {loading && !draft ? (
        <div className="panel platform-loading"><LoaderCircle size={17} className="platform-spin"/> Loading platform settings…</div>
      ) : draft ? (
        <>
          <section className="panel platform-ops-panel">
            <div className="platform-ops-heading">
              <div><h3>{t("Monthly print quota and retention")}</h3><p>{t("Quota units are print jobs, not pages or copies. Paid plan limits remain versioned per plan.")}</p></div>
              <span className="neutral-chip">{t("Platform-managed")}</span>
            </div>
            <div className="platform-ops-grid">
              {fields.map(field => (
                <label className="platform-ops-field" key={field.key}>
                  <span><strong>{field.label}</strong><small>{field.hint}</small></span>
                  <input type="number" min={field.min} max={field.max} step={1} disabled={field.locked} value={draft[field.key]}
                    onChange={event => updateNumber(field.key, event.target.value)} aria-label={field.label}/>
                </label>
              ))}
            </div>
            <label className="platform-ops-toggle">
              <input type="checkbox" checked={draft.allowPrivatePrintingVirtualOutput}
                onChange={event => {
                  const checked = event.target.checked;
                  setDraft(current => current ? { ...current, allowPrivatePrintingVirtualOutput: checked } : current);
                }}/>
              <span><strong>{t("Allow Microsoft Print to PDF for Private Printing tests")}</strong><small>{t("Non-production only. The Agent currently recognizes Microsoft Print to PDF as its supported virtual test queue. Other virtual drivers remain ineligible until their unattended output behaviour is explicitly validated. Production APIs always reject virtual output, even if this setting is accidentally enabled.")}</small></span>
            </label>
            <label className="platform-ops-toggle">
              <input type="checkbox" checked={draft.allowPaidJobClaimsWhenSuspended}
                onChange={event => {
                  const checked = event.target.checked;
                  setDraft(current => current ? { ...current, allowPaidJobClaimsWhenSuspended: checked } : current);
                }}/>
              <span><strong>{t("Allow already-paid jobs to continue during suspension")}</strong><small>{t("Only jobs created and payment-verified before the latest suspension can be claimed. New orders and post-suspension payments remain blocked.")}</small></span>
            </label>
            <div className="platform-ops-save-row">
              <label className="platform-ops-reason"><span>{t("Audit reason")}</span><input value={reason} onChange={event => setReason(event.target.value)} maxLength={500} placeholder={t("Why are these platform settings changing?")}/></label>
              <button className="primary-button" onClick={() => void save()} disabled={saving || loading}>
                {saving ? <LoaderCircle size={14} className="platform-spin"/> : <Save size={14}/>} Save settings
              </button>
            </div>
          </section>

          <VirtualPrinterTestGrantPanel tokens={tokens}/>

          <section className="panel platform-ops-policy-note">
            <AlertTriangle size={17}/>
            <div><strong>{t("Safety behaviour")}</strong><p>{t("Output-unconfirmed and unfinished jobs are never automatically deleted. Completed Standard Printing documents are deleted 72 hours after confirmed output. Eligible abandoned files are purged only after the abandonment window and once financial holds clear. Private Printing source files are removed only after verified physical output or a verified non-production test output and the paired Agent acknowledges deletion. Test PDFs are not physical fulfilment and are separately retained in protected local storage for 24 hours. Permanent tenant deactivation is blocked until unsettled print and financial work is resolved.")}</p></div>
          </section>

          <section className="panel platform-ops-history">
            <div className="platform-ops-heading"><div><h3>{t("Settings audit history")}</h3><p>{t("Most recent platform-wide changes")}</p></div><span>{history.length} events</span></div>
            {history.length === 0 ? <div className="platform-plan-empty">{t("No operational settings changes have been recorded yet.")}</div> : (
              <div className="platform-ops-history-list">
                {history.map(item => (
                  <details className="platform-ops-history-item" key={item.id}>
                    <summary><span><strong>{item.reason}</strong><small>{formatIstDateTime(item.createdUtc)} · {item.actorUserId ?? "Platform administrator"}</small></span><Clock3 size={15}/></summary>
                    <pre>{item.changesJson}</pre>
                  </details>
                ))}
              </div>
            )}
          </section>
        </>
      ) : null}
    </section>
  );
}
