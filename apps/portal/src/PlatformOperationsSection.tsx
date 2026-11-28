import { useEffect, useState } from "react";
import { AlertTriangle, Clock3, LoaderCircle, RefreshCw, Save, ShieldCheck } from "lucide-react";
import { formatIstDateTime } from "./date-time";
import {
  getPlatformOperationalSettings,
  getPlatformOperationalSettingsHistory,
  getPlatformPrinterAccess,
  placePrinterOnPlatformHold,
  releasePrinterPlatformHold,
  updatePlatformOperationalSettings,
  type AuthTokens,
  type PlatformOperationalSettings,
  type PlatformOperationalSettingsAudit,
  type PlatformOperationalSettingsInput,
  type PlatformPrinterAccessItem,
  type PlatformPrinterAccessPage
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

function PrinterPlatformAccessPanel({ tokens }: { tokens: AuthTokens }) {
  const { locale } = useLocale();
  const t = (value: string) => translateUiText(locale, value);
  const [page, setPage] = useState<PlatformPrinterAccessPage | null>(null);
  const [query, setQuery] = useState("");
  const [appliedQuery, setAppliedQuery] = useState("");
  const [filter, setFilter] = useState<"all" | "available" | "held">("all");
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState("");
  const [actionItem, setActionItem] = useState<PlatformPrinterAccessItem | null>(null);
  const [actionType, setActionType] = useState<"hold" | "release">("hold");
  const [reason, setReason] = useState("");
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");
  const pageSize = 50;

  async function reload(
    nextOffset = offset,
    nextQuery = appliedQuery,
    nextFilter = filter
  ) {
    setLoading(true);
    setError("");
    try {
      const result = await getPlatformPrinterAccess(
        tokens.accessToken, nextQuery, nextFilter, nextOffset, pageSize
      );
      setPage(result);
      setOffset(nextOffset);
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not load printer access."));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { void reload(0, "", "all"); }, [tokens.accessToken]);

  function openAction(item: PlatformPrinterAccessItem, action: "hold" | "release") {
    setActionItem(item);
    setActionType(action);
    setReason("");
    setError("");
    setSuccess("");
  }

  async function submitAction() {
    if (!actionItem || busyId) return;
    const trimmedReason = reason.trim();
    if (trimmedReason.length < 5) {
      setError(t("Enter a reason of at least five characters."));
      return;
    }

    setBusyId(actionItem.id);
    setError("");
    setSuccess("");
    try {
      if (actionType === "hold") {
        await placePrinterOnPlatformHold(tokens.accessToken, actionItem.id, trimmedReason);
        setSuccess(t("Printer placed on hold. New use is blocked; shop selection is preserved."));
      } else {
        await releasePrinterPlatformHold(tokens.accessToken, actionItem.id, trimmedReason);
        setSuccess(t("Printer hold released. The existing shop selection is restored."));
      }
      setActionItem(null);
      setReason("");
      await reload(offset, appliedQuery, filter);
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("The printer access change could not be saved."));
    } finally {
      setBusyId("");
    }
  }

  function applySearch() {
    const nextQuery = query.trim();
    setAppliedQuery(nextQuery);
    void reload(0, nextQuery, filter);
  }

  function applyFilter(nextFilter: "all" | "available" | "held") {
    setFilter(nextFilter);
    void reload(0, appliedQuery, nextFilter);
  }

  const total = page?.total ?? 0;
  const currentPage = page ? Math.floor(page.offset / page.limit) + 1 : 1;
  const pageCount = page ? Math.max(1, Math.ceil(page.total / page.limit)) : 1;
  const rangeStart = total === 0 ? 0 : (page?.offset ?? 0) + 1;
  const rangeEnd = Math.min((page?.offset ?? 0) + (page?.items.length ?? 0), total);

  return (
    <section className="panel platform-printer-access">
      <div className="platform-ops-heading">
        <div>
          <h3>{t("Printer access")}</h3>
          <p>{t("All discovered printers are available by default. Place an explicit platform hold only when a printer must be restricted.")}</p>
        </div>
        <button className="subtle-button" onClick={() => void reload()} disabled={loading}>
          <RefreshCw size={14}/> {t("Refresh")}
        </button>
      </div>

      <div className="platform-printer-toolbar">
        <div className="platform-printer-search">
          <input
            value={query}
            onChange={event => setQuery(event.target.value)}
            onKeyDown={event => { if (event.key === "Enter") applySearch(); }}
            placeholder={t("Search shop, printer, driver or port")}
            aria-label={t("Search printers")}
          />
          <button className="subtle-button" onClick={applySearch} disabled={loading}>
            {t("Search")}
          </button>
        </div>
        <div className="platform-printer-filters" role="group" aria-label={t("Printer access filter")}>
          {([
            ["all", "All printers"],
            ["available", "Available"],
            ["held", "On hold"]
          ] as const).map(([value, label]) => (
            <button
              type="button"
              key={value}
              className={filter === value ? "active" : ""}
              aria-pressed={filter === value}
              onClick={() => applyFilter(value)}
              disabled={loading}
            >
              {t(label)}
            </button>
          ))}
        </div>
      </div>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {success && <div className="inline-success" role="status">{success}</div>}

      {loading && !page ? (
        <div className="platform-plan-empty"><LoaderCircle size={16} className="platform-spin"/> {t("Loading printers…")}</div>
      ) : !loading && page?.items.length === 0 ? (
        <div className="platform-plan-empty">{t("No printers match this search.")}</div>
      ) : (
        <div className="platform-printer-grid">
          {(page?.items ?? []).map(item => {
            const outputLabel = item.outputMode === "PHYSICAL"
              ? t("Physical output")
              : item.outputMode === "PDF_EXPORT"
                ? t("PDF export")
                : t("Virtual output · digital test only");
            const queueFresh = !!item.lastSeenUtc && Date.parse(item.lastSeenUtc) >= Date.now() - 120_000;
            const queueReady = item.outputMode === "PDF_EXPORT" || item.status === "Ready" || item.status === "Busy";
            return (
              <article className={"platform-printer-card" + (item.isPlatformHeld ? " is-held" : "")} key={item.tenantId + ":" + item.id}>
                <div className="platform-printer-card-top">
                  <div className="platform-printer-card-title">
                    <strong>{item.shopName} · {item.displayName}</strong>
                    <span>{outputLabel} · {item.model} · {item.connectionType}</span>
                    <small>{item.agentName || t("Agent not paired")} · {item.agentOnline ? t("Agent online") : t("Agent offline")} · {t("Queue")} {item.status}</small>
                  </div>
                  <span className={"platform-printer-state" + (item.isPlatformHeld ? " held" : " allowed")}>
                    {item.isPlatformHeld ? t("On hold") : t("Allowed")}
                  </span>
                </div>

                <div className="platform-printer-compact-meta">
                  <span>{item.shopSlug}</span>
                  <span>{item.queueName || t("Queue name not reported")}</span>
                  <span>{queueFresh ? t("Inventory fresh") : t("Inventory stale")}</span>
                  {item.isActive && <span>{t("Selected by shop")}</span>}
                </div>

                <details className="platform-printer-details">
                  <summary>{t("Details")}</summary>
                  <div className="platform-printer-detail-grid">
                    <div><span>{t("Output type")}</span><strong>{outputLabel}</strong></div>
                    <div><span>{t("Windows driver")}</span><strong>{item.model || t("Not reported")}</strong></div>
                    <div><span>{t("Windows queue")}</span><strong>{item.queueName || t("Not reported")}</strong></div>
                    <div><span>{t("Connection port")}</span><strong>{item.connectionAddress || t("Not reported")}</strong></div>
                    <div><span>{t("Paired Agent")}</span><strong>{item.agentName || t("Not paired")}</strong></div>
                    <div><span>{t("Agent last seen")}</span><strong>{item.agentLastSeenUtc ? formatIstDateTime(item.agentLastSeenUtc) : t("Not reported")}</strong></div>
                    <div><span>{t("Queue last discovered")}</span><strong>{item.lastSeenUtc ? formatIstDateTime(item.lastSeenUtc) : t("Not reported")}</strong></div>
                    <div><span>{t("Shop selection")}</span><strong>{item.isActive ? t("Selected") : t("Not selected")}</strong></div>
                    <div><span>{t("Default Windows queue")}</span><strong>{item.isDefault ? t("Yes") : t("No")}</strong></div>
                    <div><span>{t("Queue identity")}</span><strong><code title={item.identityFingerprint || ""}>{item.identityFingerprint || t("Not reported")}</code></strong></div>
                    {item.isPlatformHeld && (
                      <>
                        <div><span>{t("Hold reason")}</span><strong>{item.platformHoldReason || t("No reason recorded")}</strong></div>
                        <div><span>{t("Held by")}</span><strong>{item.platformHeldByName || t("Platform administrator")}</strong></div>
                        <div><span>{t("Held at")}</span><strong>{item.platformHeldUtc ? formatIstDateTime(item.platformHeldUtc) : t("Not reported")}</strong></div>
                      </>
                    )}
                  </div>
                  {item.history.length > 0 && (
                    <div className="platform-printer-history">
                      <strong>{t("Hold history")}</strong>
                      {item.history.map(entry => (
                        <div key={entry.id}>
                          <span>{entry.action === "HOLD" ? t("Placed on hold") : t("Hold released")}</span>
                          <small>{entry.reason} · {entry.actorName} · {formatIstDateTime(entry.createdUtc)}</small>
                        </div>
                      ))}
                    </div>
                  )}
                </details>

                <div className="platform-printer-actions">
                  {item.isPlatformHeld ? (
                    <button type="button" className="primary-button" disabled={!!busyId} onClick={() => openAction(item, "release")}>
                      {busyId === item.id ? <LoaderCircle size={14} className="platform-spin"/> : <ShieldCheck size={14}/>}
                      {t("Release hold")}
                    </button>
                  ) : (
                    <button type="button" className="subtle-button platform-printer-hold-button" disabled={!!busyId} onClick={() => openAction(item, "hold")}>
                      {busyId === item.id ? <LoaderCircle size={14} className="platform-spin"/> : <AlertTriangle size={14}/>}
                      {t("Place on hold")}
                    </button>
                  )}
                </div>

                {actionItem?.id === item.id && (
                  <div className="platform-printer-action-form">
                    <label>
                      <span>{t(actionType === "hold" ? "Reason for hold" : "Reason for release")}</span>
                      <input
                        value={reason}
                        onChange={event => setReason(event.target.value)}
                        maxLength={500}
                        placeholder={t("Enter an audit reason (at least 5 characters)")}
                      />
                    </label>
                    <div>
                      <button type="button" className="subtle-button" disabled={!!busyId} onClick={() => { setActionItem(null); setReason(""); }}>
                        {t("Cancel")}
                      </button>
                      <button type="button" className="primary-button" disabled={!!busyId || reason.trim().length < 5} onClick={() => void submitAction()}>
                        {busyId === item.id ? <LoaderCircle size={14} className="platform-spin"/> : <ShieldCheck size={14}/>}
                        {t(actionType === "hold" ? "Confirm hold" : "Confirm release")}
                      </button>
                    </div>
                  </div>
                )}
              </article>
            );
          })}
        </div>
      )}

      <div className="platform-printer-pagination">
        <span>{t("Showing")} {rangeStart}–{rangeEnd} {t("of")} {total}</span>
        <div>
          <button type="button" className="subtle-button" disabled={loading || offset === 0} onClick={() => void reload(Math.max(0, offset - pageSize))}>
            {t("Previous")}
          </button>
          <span>{currentPage} / {pageCount}</span>
          <button type="button" className="subtle-button" disabled={loading || offset + pageSize >= total} onClick={() => void reload(offset + pageSize)}>
            {t("Next")}
          </button>
        </div>
      </div>
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
        <div className="panel platform-loading"><LoaderCircle size={17} className="platform-spin"/>{t("Loading platform settings…")}</div>
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
                  <span><strong>{t(field.label)}</strong><small>{t(field.hint)}</small></span>
                  <input type="number" min={field.min} max={field.max} step={1} disabled={field.locked} value={draft[field.key]}
                    onChange={event => updateNumber(field.key, event.target.value)} aria-label={t(field.label)}/>
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

          <PrinterPlatformAccessPanel tokens={tokens}/>

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
