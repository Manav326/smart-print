import BrandLockup from "./BrandLockup";
import { useEffect, useState, type ReactNode } from "react";
import {
  Activity, Building2, CheckCircle2, ChevronLeft, ChevronRight, Megaphone,
  Clock3, CreditCard, Layers3, LogOut, Menu, Monitor, PauseCircle, PanelLeftClose, PanelLeftOpen, PlayCircle, Printer, RefreshCw, Search, Settings2, ShieldCheck, Wifi
} from "lucide-react";
import PlatformPlansSection from "./PlatformPlansSection";
import PlatformSubscriptionsSection from "./PlatformSubscriptionsSection";
import PlatformOperationsSection from "./PlatformOperationsSection";
import PlatformAdvertisingSection from "./PlatformAdvertisingSection";
import PlatformDataNetworkSection from "./PlatformDataNetworkSection";
import { adminPathForSection, adminSectionFromPath } from "./portal-routing";
import {
  getPlatformOverview, getPlatformTenants, getPlatformTenantStatusHistory, updatePlatformTenantStatus, logout,
  type AuthTokens, type PlatformOverview, type PlatformTenantsPage, type PlatformTenant
} from "./api";
import { readSidebarCollapsedPreference, saveSidebarCollapsedPreference } from "./ui-preferences";
import ThemeToggleButton from "./ThemeToggleButton";
import { formatIstDate, formatIstDateTime } from "./date-time";
import { LanguageSwitcher, useLocale, translateUiText } from "./i18n";

type Section = "Overview" | "Tenants" | "Plans" | "Subscriptions" | "Operations" | "Advertising" | "Data & Network";

export default function PlatformAdminConsole({
  tokens,
  onSignOut,
  dark,
  onToggleTheme
}: {
  tokens: AuthTokens;
  onSignOut: () => Promise<void>;
  dark: boolean;
  onToggleTheme: () => void;
}) {
  const { locale } = useLocale();
  const t = (value: string) => translateUiText(locale, value);
  const [section, setSection] = useState<Section>(() => adminSectionFromPath(window.location.pathname));

  useEffect(() => {
    const syncSectionFromPath = () => setSection(adminSectionFromPath(window.location.pathname));
    window.addEventListener("popstate", syncSectionFromPath);
    return () => window.removeEventListener("popstate", syncSectionFromPath);
  }, []);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(readSidebarCollapsedPreference);
  useEffect(() => { saveSidebarCollapsedPreference(sidebarCollapsed); }, [sidebarCollapsed]);
  const [overview, setOverview] = useState<PlatformOverview | null>(null);
  const [overviewLoading, setOverviewLoading] = useState(true);
  const [overviewError, setOverviewError] = useState("");
  const [tenants, setTenants] = useState<PlatformTenantsPage | null>(null);
  const [tenantLoading, setTenantLoading] = useState(false);
  const [tenantError, setTenantError] = useState("");
  const [tenantActionError, setTenantActionError] = useState("");
  const [tenantActionId, setTenantActionId] = useState("");
  const [statusHistoryTenant, setStatusHistoryTenant] = useState<PlatformTenant | null>(null);
  const [statusHistory, setStatusHistory] = useState<Array<{ id: string; previousStatus: string; newStatus: string; reason: string; actorUserId: string | null; createdUtc: string }> | null>(null);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const [refreshKey, setRefreshKey] = useState(0);
  const [signingOut, setSigningOut] = useState(false);

  useEffect(() => {
    let active = true;
    setOverviewLoading(true);
    setOverviewError("");
    void getPlatformOverview(tokens.accessToken)
      .then(value => { if (active) setOverview(value); })
      .catch(exception => {
        if (active) setOverviewError(exception instanceof Error ? exception.message : t("Could not load platform overview."));
      })
      .finally(() => { if (active) setOverviewLoading(false); });
    return () => { active = false; };
  }, [tokens.accessToken, refreshKey]);

  useEffect(() => {
    let active = true;
    setTenantLoading(true);
    setTenantError("");
    void getPlatformTenants(tokens.accessToken, { search, status, page, pageSize: 20 })
      .then(value => { if (active) setTenants(value); })
      .catch(exception => {
        if (active) setTenantError(exception instanceof Error ? exception.message : t("Could not load the tenant directory."));
      })
      .finally(() => { if (active) setTenantLoading(false); });
    return () => { active = false; };
  }, [tokens.accessToken, search, status, page, refreshKey]);

  async function changeTenantStatus(tenant: PlatformTenant, nextStatus: "Active" | "Suspended" | "Inactive") {
    const action = nextStatus === "Active" ? t("reactivate") : nextStatus === "Suspended" ? t("suspend") : t("permanently deactivate");
    const reason = window.prompt(t("Why are you choosing to {action} {shop}? This reason is recorded in the platform audit history.").replace("{action}", action).replace("{shop}", tenant.businessName));
    if (!reason?.trim() || reason.trim().length < 5) {
      setTenantActionError(t("A reason of at least five characters is required. No tenant status was changed."));
      return;
    }
    if (!window.confirm(t("Confirm: {action} {shop}? This affects new print activity for this shop.").replace("{action}", action).replace("{shop}", tenant.businessName))) return;

    setTenantActionId(tenant.id);
    setTenantActionError("");
    try {
      await updatePlatformTenantStatus(tokens.accessToken, tenant.id, nextStatus, reason.trim());
      setRefreshKey(value => value + 1);
    } catch (exception) {
      setTenantActionError(exception instanceof Error ? exception.message : t("Could not update this shop's status."));
    } finally {
      setTenantActionId("");
    }
  }

  async function showTenantStatusHistory(tenant: PlatformTenant) {
    setStatusHistoryTenant(tenant);
    setStatusHistory(null);
    try {
      const history = await getPlatformTenantStatusHistory(tokens.accessToken, tenant.id);
      setStatusHistory(history);
    } catch (exception) {
      setTenantActionError(exception instanceof Error ? exception.message : t("Could not load tenant status history."));
    }
  }

  async function signOut() {
    setSigningOut(true);
    try { await logout(tokens.accessToken); } catch { /* Local sign-out still clears the secure session. */ }
    try {
      await onSignOut();
    } catch (exception) {
      setTenantActionError(exception instanceof Error ? exception.message : t("Could not clear the secure desktop session."));
      setSigningOut(false);
    }
  }

  function toggleSidebarCollapsed() { setSidebarCollapsed(value => !value); }

  function navigate(next: Section) {
    const nextPath = adminPathForSection(next);
    if (window.location.pathname !== nextPath) {
      window.history.pushState({}, "", nextPath);
    }
    setSection(next);
    setSidebarOpen(false);
  }

  const title = t(section === "Overview" ? "Platform overview" : section === "Tenants" ? "Shop tenants" : section === "Plans" ? "Plans and entitlements" : section === "Subscriptions" ? "Shop subscriptions" : section === "Advertising" ? "Advertising" : section === "Data & Network" ? "Data & Network" : "Operations and retention");
  const subtitle = t(section === "Overview"
    ? "A cross-platform view of tenant health, subscriptions, devices and recent activity."
    : section === "Tenants"
      ? "Find shops and inspect their current subscription and connected-device footprint."
      : section === "Plans" ? "Configure versioned SaaS plans, prices and centrally managed capabilities."
      : section === "Subscriptions" ? "Manage trial and free-plan assignments with a permanent audit trail."
       : section === "Advertising" ? "Create, schedule and measure campaigns across customer and shop-owner placements."
       : section === "Data & Network" ? "Measure original-document movement, shop Wi-Fi transfers, and Agent storage across your platform."
       : "Configure platform-wide print quotas, retention, cleanup schedules and suspended-shop recovery policy.");

  return (
    <div className={`app-shell platform-admin-shell${dark ? "" : " light"}${sidebarCollapsed ? " sidebar-collapsed" : ""}`}>
      {sidebarOpen && <button className="mobile-scrim" aria-label={t("Close navigation")} onClick={() => setSidebarOpen(false)}/>}
      <aside id="platform-navigation" className={`sidebar platform-sidebar${sidebarOpen ? " sidebar-open" : ""}`}>
        <BrandLockup />
        <div className="workspace-switcher platform-workspace">
          <div className="platform-shield"><ShieldCheck size={18}/></div>
          <div className="workspace-copy"><strong>{t("Platform control")}</strong><span>{t("Owner workspace")}</span></div>
          <span className="platform-online-dot"/>
        </div>
        <div className="nav-label">{t("ADMINISTRATION")}</div>
        <nav className="primary-nav" aria-label={t("Platform administration")}>
          <button title={t("Overview")} aria-current={section === "Overview" ? "page" : undefined} className={section === "Overview" ? "nav-item active" : "nav-item"} onClick={() => navigate("Overview")}><Activity size={17}/><span>{t("Overview")}</span></button>
          <button title={t("Shop tenants")} aria-current={section === "Tenants" ? "page" : undefined} className={section === "Tenants" ? "nav-item active" : "nav-item"} onClick={() => navigate("Tenants")}><Building2 size={17}/><span>{t("Shop tenants")}</span>{overview && <span className="nav-count">{overview.tenantCount}</span>}</button>
          <button title={t("Plans & entitlements")} aria-current={section === "Plans" ? "page" : undefined} className={section === "Plans" ? "nav-item active" : "nav-item"} onClick={() => navigate("Plans")}><Layers3 size={17}/><span>{t("Plans & entitlements")}</span></button>
          <button title={t("Shop subscriptions")} aria-current={section === "Subscriptions" ? "page" : undefined} className={section === "Subscriptions" ? "nav-item active" : "nav-item"} onClick={() => navigate("Subscriptions")}><CreditCard size={17}/><span>{t("Shop subscriptions")}</span></button>
          <button title={t("Advertising")} aria-current={section === "Advertising" ? "page" : undefined} className={section === "Advertising" ? "nav-item active" : "nav-item"} onClick={() => navigate("Advertising")}><Megaphone size={17}/><span>{t("Advertising")}</span></button>
          <button title={t("Data & Network")} aria-current={section === "Data & Network" ? "page" : undefined} className={section === "Data & Network" ? "nav-item active" : "nav-item"} onClick={() => navigate("Data & Network")}><Wifi size={17}/><span>{t("Data & Network")}</span></button>
          <button title={t("Operations & retention")} aria-current={section === "Operations" ? "page" : undefined} className={section === "Operations" ? "nav-item active" : "nav-item"} onClick={() => navigate("Operations")}><Settings2 size={17}/><span>{t("Operations & retention")}</span></button>
        </nav>
        <div className="sidebar-bottom">
          <div className="platform-security-note"><ShieldCheck size={15}/><span>{t("Platform scope is separate from shop accounts and tenant data.")}</span></div>
          <div className="profile-row">
            <div className="profile-avatar platform-avatar">PA</div>
            <div className="profile-copy"><strong>{t("Platform administrator")}</strong><span>{t("Privileged access")}</span></div>
            <button className="icon-button" title={t("Sign out")} aria-label={t("Sign out")} onClick={() => void signOut()} disabled={signingOut}>
              <LogOut size={16}/>
            </button>
          </div>
        </div>
      </aside>

      <main className="main-panel">
        <header className="topbar">
          <div className="topbar-left">
            <button className="icon-button sidebar-toggle" aria-label={t(sidebarCollapsed ? "Expand navigation" : "Collapse navigation")} aria-controls="platform-navigation" aria-expanded={!sidebarCollapsed} title={t(sidebarCollapsed ? "Expand navigation" : "Collapse navigation")} onClick={toggleSidebarCollapsed}>{sidebarCollapsed ? <PanelLeftOpen size={18}/> : <PanelLeftClose size={18}/>}</button>
            <button className="icon-button mobile-menu" onClick={() => setSidebarOpen(true)} aria-label={t("Open navigation")} aria-controls="platform-navigation"><Menu size={19}/></button>
            <div className="breadcrumb"><span>Smart Print</span><span className="crumb-slash">/</span><strong>{title}</strong></div>
          </div>
          <div className="topbar-actions">
            <span className="platform-session-pill"><i/> {t("Platform session secured")}</span>
            <span className="topbar-divider"/>
            <ThemeToggleButton dark={dark} onToggle={onToggleTheme} className="theme-toggle-compact"/>
            <LanguageSwitcher compact/>
            <button className="icon-button" onClick={() => void signOut()} disabled={signingOut} title={t("Sign out")} aria-label={t("Sign out")}>
              <LogOut size={17}/>
            </button>
          </div>
        </header>

        <div className="content-wrap">
          <div className="page-heading platform-page-heading">
            <div>
              <div className="eyebrow"><span className="eyebrow-line"/> {t("PLATFORM CONTROL CENTRE")}</div>
              <h1>{title}</h1>
              <p>{subtitle}</p>
            </div>
            <div className="platform-heading-actions">
              <span className="platform-readonly-label"><ShieldCheck size={13}/> {section === "Plans" ? t("Plan configuration") : section === "Operations" ? t("Platform policy") : t("Read-only monitoring")}</span>
              <button className="secondary-button platform-refresh" onClick={() => setRefreshKey(value => value + 1)} disabled={overviewLoading || tenantLoading}>
                <RefreshCw size={14}/> {t("Refresh data")}
              </button>
            </div>
          </div>

          {overviewError && <div className="inline-error" role="alert">{overviewError}</div>}
          {section === "Overview" && (
            <section className="platform-overview">
              {overviewLoading && !overview ? (
                <div className="panel platform-loading"><span className="button-spinner"/> {t("Loading platform metrics…")}</div>
              ) : overview ? (
                <>
                  <div className="stat-grid platform-stat-grid">
                    <Metric icon={<Building2 size={18}/>} label={t("Shop tenants")} value={overview.tenantCount} note={t("{count} active shops").replace("{count}",String(overview.activeTenantCount))} tone="violet"/>
                    <Metric icon={<Clock3 size={18}/>} label={t("Active trials")} value={overview.trialSubscriptionCount} note={t("Subscriptions still within trial")} tone="amber"/>
                    <Metric icon={<CheckCircle2 size={18}/>} label={t("Active subscriptions")} value={overview.activeSubscriptionCount} note={t("Paid/active state recorded")} tone="mint"/>
                    <Metric icon={<Printer size={18}/>} label={t("Printers registered")} value={overview.printerCount} note={t("{count} enabled in shops").replace("{count}",String(overview.activePrinterCount))} tone="blue"/>
                    <Metric icon={<Monitor size={18}/>} label={t("Online agents")} value={overview.onlineAgentCount} note={t("{count} non-revoked agents").replace("{count}",String(overview.agentCount))} tone="blue"/>
                    <Metric icon={<FileIcon/>} label={t("Print jobs · 30 days")} value={overview.jobsLast30Days} note={t("Across all shops")} tone="violet"/>
                  </div>

                  <div className="platform-overview-grid">
                    <section className="panel platform-summary-panel">
                      <div className="panel-heading">
                        <div><h2>{t("Subscription status")}</h2><p>{t("Recorded subscription states across all tenants")}</p></div>
                        <span className="neutral-chip">{t("Platform-wide")}</span>
                      </div>
                      {overview.subscriptionsByStatus.length === 0 ? (
                        <div className="platform-empty">{t("No subscription records are available yet.")}</div>
                      ) : (
                        <div className="platform-status-list">
                          {overview.subscriptionsByStatus.map(item => (
                            <div className="platform-status-row" key={item.status}>
                              <span className={`platform-status-dot ${toneForStatus(item.status)}`}/>
                              <strong>{t(prettyStatus(item.status))}</strong>
                              <span className="platform-status-count">{item.count.toLocaleString("en-IN")}</span>
                            </div>
                          ))}
                        </div>
                      )}
                    </section>

                    <section className="panel platform-summary-panel">
                      <div className="panel-heading">
                        <div><h2>{t("Operational health")}</h2><p>{t("Connected devices and recent print activity")}</p></div>
                        <span className="neutral-chip">{t("Live snapshot")}</span>
                      </div>
                      <div className="platform-health-list">
                        <HealthRow icon={<Monitor size={16}/>} title={t("Print agents online")} value={`${overview.onlineAgentCount} / ${overview.agentCount}`} detail={t("Heartbeat observed within two minutes")}/>
                        <HealthRow icon={<Printer size={16}/>} title={t("Enabled printers")} value={`${overview.activePrinterCount} / ${overview.printerCount}`} detail={t("Configured across shop tenants")}/>
                        <HealthRow icon={<Activity size={16}/>} title={t("Jobs created")} value={overview.jobsLast30Days.toLocaleString("en-IN")} detail={t("Last 30 days, all tenant scopes")}/>
                      </div>
                    </section>
                  </div>
                  <div className="platform-footer-note">
                    <ShieldCheck size={15}/>
                    <span>{t("Aggregate operational metrics only. This console does not expose customer documents, UPI details, passwords, or shop payment credentials.")}</span>
                  </div>
                  <div className="platform-generated-note">{t("Snapshot generated {date}.").replace("{date}",formatIstDateTime(overview.generatedUtc))}</div>
                </>
              ) : !overviewError ? <div className="panel platform-loading">{t("No platform summary is available.")}</div> : null}
            </section>
          )}

          {section === "Tenants" && (
            <section className="panel platform-tenants-panel">
              <div className="platform-tenants-heading">
                <div><h2>{t("Shop directory")}</h2><p>{t("Tenant metadata and operational footprint. Customer-level information is intentionally excluded.")}</p></div>
                <span className="setup-count">{tenants ? t("{count} shops").replace("{count}",tenants.totalCount.toLocaleString("en-IN")) : t("Loading")}</span>
              </div>
              <div className="platform-tenant-filters">
                <label className="platform-search"><Search size={15}/><input value={search} onChange={event => { setSearch(event.target.value); setPage(1); }} placeholder={t("Search shop name or slug")} aria-label={t("Search shop name or slug")}/></label>
                <label className="platform-status-filter">
                  <span>{t("Status")}</span>
                  <select value={status} onChange={event => { setStatus(event.target.value); setPage(1); }} aria-label={t("Filter by tenant status")}>
                    <option value="">{t("All statuses")}</option>
                    <option value="Active">{t("Active")}</option>
                    <option value="Suspended">{t("Suspended")}</option>
                    <option value="Inactive">{t("Inactive")}</option>
                  </select>
                </label>
              </div>
              {tenantError && <div className="inline-error" role="alert">{tenantError}</div>}
              {tenantActionError && <div className="inline-error" role="alert">{tenantActionError}</div>}
              {tenantLoading && !tenants ? <div className="platform-loading-inline"><span className="button-spinner"/> {t("Loading shops…")}</div> : null}
              {statusHistoryTenant && <section className="panel platform-status-history">
                <div className="panel-heading"><div><h2>{t("Status history · {shop}").replace("{shop}",statusHistoryTenant.businessName)}</h2><p>{t("Audited platform tenant lifecycle changes")}</p></div><button className="text-action" onClick={() => { setStatusHistoryTenant(null); setStatusHistory(null); }}>{t("Close")}</button></div>
                {statusHistory === null ? <div className="platform-loading-inline">{t("Loading status history…")}</div> :
                  statusHistory.length === 0 ? <div className="platform-loading-inline">{t("No status changes have been recorded.")}</div> :
                  <div className="platform-status-history-list">{statusHistory.map(item => <div className="platform-status-history-row" key={item.id}>
                    <strong>{item.previousStatus} → {item.newStatus}</strong><span>{item.reason}</span><small>{formatIstDateTime(item.createdUtc)} · {item.actorUserId || "Platform administrator"}</small>
                  </div>)}</div>}
              </section>}
              {tenants && (
                <>
                  <div className="platform-table-scroll">
                    <table className="platform-tenant-table">
                      <thead><tr><th>{t("SHOP")}</th><th>{t("TENANT STATUS")}</th><th>{t("SUBSCRIPTION")}</th><th>{t("DEVICES")}</th><th>{t("JOBS · 30 DAYS")}</th><th>{t("CREATED")}</th><th>{t("ACTIONS")}</th></tr></thead>
                      <tbody>
                        {tenants.items.map(tenant => (
                          <tr key={tenant.id}>
                            <td><div className="platform-shop-cell"><div className="platform-shop-icon"><Building2 size={15}/></div><div><strong>{tenant.businessName}</strong><small>{tenant.slug}</small></div></div></td>
                            <td><span className={`platform-tenant-badge ${toneForStatus(tenant.status)}`}>{prettyStatus(tenant.status)}</span></td>
                            <td><div className="platform-subscription-cell"><strong>{tenant.planCode || t("Unassigned")}</strong><small>{tenant.subscriptionStatus ? t(prettyStatus(tenant.subscriptionStatus)) : t("No subscription")}</small>{tenant.subscriptionPeriodEndUtc && <small>{t("Until {date}").replace("{date}",formatIstDate(tenant.subscriptionPeriodEndUtc))}</small>}</div></td>
                            <td><div className="platform-device-cell"><strong>{t("{active} / {total} printers").replace("{active}",String(tenant.activePrinterCount)).replace("{total}",String(tenant.printerCount))}</strong><small>{t("{paired} paired · {online} online · {offline} offline").replace("{paired}",String(tenant.agentCount)).replace("{online}",String(tenant.onlineAgentCount)).replace("{offline}",String(tenant.offlineAgentCount))}</small><small>{tenant.latestAgentHeartbeatUtc ? t("Last contact {date}").replace("{date}",formatIstDateTime(tenant.latestAgentHeartbeatUtc)) : t("No Agent heartbeat received")}</small></div></td>
                            <td><strong className="platform-numeric">{tenant.jobsLast30Days.toLocaleString("en-IN")}</strong></td>
                            <td><span className="platform-date">{formatIstDate(tenant.createdUtc)}</span></td>
                            <td><div className="platform-tenant-actions">
                              {tenant.status === "Active" && <>
                                <button className="secondary-button" onClick={() => void changeTenantStatus(tenant, "Suspended")} disabled={tenantActionId === tenant.id || tenantLoading} title={t("Block new print activity while preserving recovery")}><PauseCircle size={13}/> {t("Suspend")}</button>
                              </>}
                              {tenant.status === "Suspended" && <>
                                <button className="secondary-button" onClick={() => void changeTenantStatus(tenant, "Active")} disabled={tenantActionId === tenant.id || tenantLoading}><PlayCircle size={13}/> {t("Reactivate")}</button>
                                <button className="secondary-button" onClick={() => void changeTenantStatus(tenant, "Inactive")} disabled={tenantActionId === tenant.id || tenantLoading}>{t("Deactivate")}</button>
                              </>}
                              {tenant.status === "Inactive" && <span className="platform-date">{t("Permanently inactive")}</span>}
                              <button className="text-action" onClick={() => void showTenantStatusHistory(tenant)}>{t("History")}</button>
                            </div></td>
                          </tr>
                        ))}
                        {tenants.items.length === 0 && <tr><td className="platform-no-results" colSpan={7}>{t("No shops match this filter.")}</td></tr>}
                      </tbody>
                    </table>
                  </div>
                  <div className="platform-pagination">
                    <span>{tenants.totalCount === 0 ? "No shops" : `Showing ${((tenants.page - 1) * tenants.pageSize) + 1}–${Math.min(tenants.page * tenants.pageSize, tenants.totalCount)} of ${tenants.totalCount.toLocaleString("en-IN")}`}</span>
                    <div>
                      <button className="icon-button" onClick={() => setPage(value => Math.max(1, value - 1))} disabled={tenants.page <= 1 || tenantLoading} aria-label={t("Previous page")}><ChevronLeft size={17}/></button>
                      <span>{t("Page {page}").replace("{page}",String(tenants.page))}</span>
                      <button className="icon-button" onClick={() => setPage(value => value + 1)} disabled={tenants.page * tenants.pageSize >= tenants.totalCount || tenantLoading} aria-label={t("Next page")}><ChevronRight size={17}/></button>
                    </div>
                  </div>
                </>
              )}
            </section>
          )}
          {section === "Plans" && <PlatformPlansSection tokens={tokens}/>}
          {section === "Subscriptions" && <PlatformSubscriptionsSection tokens={tokens}/>}
          {section === "Operations" && <PlatformOperationsSection tokens={tokens}/>}
          {section === "Advertising" && <PlatformAdvertisingSection tokens={tokens}/>}
          {section === "Data & Network" && <PlatformDataNetworkSection tokens={tokens}/>}
        </div>
      </main>
    </div>
  );
}

function Metric({ icon, label, value, note, tone }: {
  icon: ReactNode; label: string; value: number; note: string; tone: string;
}) {
  return (
    <section className="metric-card platform-metric-card">
      <div className="metric-top"><div className={`metric-icon ${tone}`}>{icon}</div></div>
      <div className="metric-label">{label}</div>
      <div className="metric-value">{value.toLocaleString("en-IN")}</div>
      <div className="metric-foot"><span>{note}</span></div>
    </section>
  );
}

function HealthRow({ icon, title, value, detail }: {
  icon: React.ReactNode; title: string; value: string; detail: string;
}) {
  return (
    <div className="platform-health-row">
      <div className="platform-health-icon">{icon}</div>
      <div><strong>{title}</strong><small>{detail}</small></div>
      <span>{value}</span>
    </div>
  );
}

function FileIcon() {
  return <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6M8 13h8M8 17h8"/></svg>;
}

function toneForStatus(value: string): string {
  const status = value.toLowerCase();
  if (status === "active" || status === "completed" || status === "verified") return "tone-success";
  if (status === "trial" || status.includes("pending") || status.includes("awaiting")) return "tone-warning";
  if (status === "suspended" || status === "cancelled" || status === "expired" || status === "revoked") return "tone-danger";
  return "tone-neutral";
}

function prettyStatus(value: string): string {
  return value.replace(/[_-]+/g, " ").toLowerCase().replace(/\b\w/g, letter => letter.toUpperCase());
}

