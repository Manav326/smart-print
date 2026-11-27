import { useEffect, useMemo, useState } from "react";
import { Megaphone, Pause, Play, RefreshCw, Save, ShieldCheck, Trash2, Upload } from "lucide-react";
import { formatIstDate } from "./date-time";
import { useLocale, translateUiText } from "./i18n";
import {
  getPlatformAdCampaigns,
  getPlatformAdCreativePreview,
  savePlatformAdCampaign,
  updatePlatformAdCampaignStatus,
  uploadPlatformAdCreative,
  type AuthTokens,
  type PlatformAdCampaign,
  type PlatformAdCampaignInput
} from "./api";

type Placement = "HOME" | "FILES" | "SHOP_OVERVIEW";
type Draft = {
  id?: string;
  name: string;
  advertiserName: string;
  headline: string;
  body: string;
  terms: string;
  creativeId: string;
  creativeUrl: string;
  creativeFileName: string;
  destinationUrl: string;
  placement: Placement;
  startsLocal: string;
  endsLocal: string;
};

const localInput = (date: Date) => {
  const adjusted = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return adjusted.toISOString().slice(0, 16);
};

const emptyDraft = (): Draft => ({
  name: "",
  advertiserName: "",
  headline: "",
  body: "",
  terms: "",
  creativeId: "",
  creativeUrl: "",
  creativeFileName: "",
  destinationUrl: "",
  placement: "HOME",
  startsLocal: localInput(new Date()),
  endsLocal: ""
});

const formats: Record<Placement, { label: string; format: string; dimensions: string }> = {
  HOME: { label: "Customer app · Home", format: "BANNER", dimensions: "640 × 200 px (2× asset for a 320 × 100 display)" },
  FILES: { label: "Customer app · My Files", format: "CARD", dimensions: "600 × 500 px (2× asset for a 300 × 250 card)" },
  SHOP_OVERVIEW: { label: "Shop owner · Overview", format: "BANNER", dimensions: "1456 × 180 px (2× asset for a 728 × 90 display)" }
};

function fromCampaign(item: PlatformAdCampaign): Draft {
  return {
    id: item.id,
    name: item.name,
    advertiserName: item.advertiserName,
    headline: item.headline,
    body: item.body,
    terms: item.terms || "",
    creativeId: item.creativeId,
    creativeUrl: item.creativeUrl || "",
    creativeFileName: item.creativeFileName || "",
    destinationUrl: item.destinationUrl,
    placement: item.placement as Placement,
    startsLocal: localInput(new Date(item.startsUtc)),
    endsLocal: item.endsUtc ? localInput(new Date(item.endsUtc)) : ""
  };
}

export default function PlatformAdvertisingSection({ tokens }: { tokens: AuthTokens }) {
  const { locale } = useLocale();
  const t = (source: string, values: Record<string, string | number> = {}) =>
    translateUiText(locale, source, values);
  const formatNumber = (value: number, options?: Intl.NumberFormatOptions) =>
    new Intl.NumberFormat(locale === "hindi" ? "hi" : locale === "hinglish" ? "en-IN" : "en-IN", options).format(value);

  const [items, setItems] = useState<PlatformAdCampaign[]>([]);
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [busyId, setBusyId] = useState("");
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");

  async function reload() {
    setLoading(true);
    setError("");
    try {
      setItems(await getPlatformAdCampaigns(tokens.accessToken));
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not load campaigns."));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void reload();
    // The access token identifies the platform-admin session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tokens.accessToken]);

  const stats = useMemo(() => items.reduce((totals, item) => ({
    active: totals.active + (item.storedStatus === "Active" && item.status !== "Expired" ? 1 : 0),
    impressions: totals.impressions + item.impressions,
    clicks: totals.clicks + item.clicks
  }), { active: 0, impressions: 0, clicks: 0 }), [items]);

  function updateDraft<K extends keyof Draft>(key: K, value: Draft[K]) {
    setDraft(current => ({ ...current, [key]: value }));
  }

  function placementLabel(placement: Placement) {
    return t(formats[placement].label);
  }

  async function editCampaign(item: PlatformAdCampaign) {
    setDraft(fromCampaign(item));
    setError("");
    try {
      const preview = await getPlatformAdCreativePreview(tokens.accessToken, item.creativeId);
      setDraft(current => current.id === item.id ? { ...current, creativeUrl: preview.dataUrl } : current);
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("The saved campaign image preview could not be loaded."));
    }
  }

  async function upload(file?: File) {
    if (!file) return;
    if (!["image/jpeg", "image/png", "image/webp"].includes(file.type)) {
      setError(t("Choose JPG, PNG or WebP."));
      return;
    }
    if (file.size <= 0 || file.size > 3 * 1024 * 1024) {
      setError(t("Banner must be no larger than 3 MB."));
      return;
    }
    setUploading(true);
    setError("");
    try {
      const saved = await uploadPlatformAdCreative(tokens.accessToken, file);
      setDraft(current => ({
        ...current,
        creativeId: saved.id,
        creativeUrl: URL.createObjectURL(file),
        creativeFileName: saved.fileName
      }));
      setSuccess(t("Image uploaded and validated. Save the campaign as a draft."));
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Image upload failed."));
    } finally {
      setUploading(false);
    }
  }

  async function save() {
    const current = draft;
    if (!current.name.trim() || !current.advertiserName.trim() || !current.headline.trim() || !current.body.trim()) {
      setError(t("Complete the campaign, advertiser, headline and supporting copy."));
      return;
    }
    if (!current.creativeId) {
      setError(t("Upload a banner before saving."));
      return;
    }
    if (!/^https:\/\//i.test(current.destinationUrl.trim())) {
      setError(t("Use a public HTTPS destination URL."));
      return;
    }

    const start = new Date(current.startsLocal);
    const end = current.endsLocal ? new Date(current.endsLocal) : null;
    if (!Number.isFinite(start.getTime()) || (end && (!Number.isFinite(end.getTime()) || end < start))) {
      setError(t("Check the campaign schedule."));
      return;
    }

    const payload: PlatformAdCampaignInput = {
      name: current.name.trim(),
      advertiserName: current.advertiserName.trim(),
      headline: current.headline.trim(),
      body: current.body.trim(),
      terms: current.terms.trim() || undefined,
      creativeId: current.creativeId,
      destinationUrl: current.destinationUrl.trim(),
      placement: current.placement,
      format: formats[current.placement].format,
      startsUtc: start.toISOString(),
      endsUtc: end ? end.toISOString() : null
    };

    setSaving(true);
    setError("");
    setSuccess("");
    try {
      await savePlatformAdCampaign(tokens.accessToken, payload, current.id);
      setSuccess(current.id ? t("Campaign updated.") : t("Draft saved. Publish it when ready."));
      setDraft(emptyDraft());
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Campaign could not be saved."));
    } finally {
      setSaving(false);
    }
  }

  async function changeStatus(item: PlatformAdCampaign, next: "Active" | "Paused" | "Archived") {
    const action = next === "Active" ? "publish" : next === "Paused" ? "pause" : "archive";
    if (!window.confirm(t("Confirm {action} campaign “{name}”?", { action: t(action), name: item.name }))) return;
    setBusyId(item.id);
    setError("");
    setSuccess("");
    try {
      await updatePlatformAdCampaignStatus(tokens.accessToken, item.id, next);
      setSuccess(t("Campaign {status}.", {
        status: t(next === "Active" ? "published" : next === "Paused" ? "paused" : "archived")
      }));
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Campaign status could not be changed."));
    } finally {
      setBusyId("");
    }
  }

  const statusLabel = (status: string) => t(status);

  return (
    <section className="platform-advertising-page">
      <div className="platform-ad-intro">
        <div>
          <div className="platform-ad-kicker"><Megaphone size={14} /> {t("PLATFORM REVENUE & PROMOTIONS")}</div>
          <h2>{t("Advertising campaigns")}</h2>
          <p>{t("Create, schedule and measure direct campaigns. Every campaign uses an approved image, a fixed placement and an explicit publish step. Document, payment and private-printing screens are excluded.")}</p>
        </div>
        <button className="secondary-button" onClick={() => void reload()} disabled={loading || saving}>
          <RefreshCw size={14} /> {t("Refresh")}
        </button>
      </div>

      <div className="platform-ad-stats">
        <div className="platform-ad-stat"><span>{t("Active campaigns")}</span><strong>{formatNumber(stats.active)}</strong></div>
        <div className="platform-ad-stat"><span>{t("Impressions")}</span><strong>{formatNumber(stats.impressions)}</strong></div>
        <div className="platform-ad-stat"><span>{t("Clicks")}</span><strong>{formatNumber(stats.clicks)}</strong></div>
        <div className="platform-ad-stat"><span>{t("Click-through rate")}</span><strong>{stats.impressions ? formatNumber(stats.clicks / stats.impressions * 100, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + "%" : "0%"}</strong></div>
      </div>

      {error && <div className="platform-ad-error" role="alert">{error}</div>}
      {success && <div className="platform-ad-success" role="status">{success}</div>}

      <div className="platform-ad-layout">
        <section className="panel platform-ad-form">
          <h3>{draft.id ? t("Edit campaign") : t("Create a campaign")}</h3>
          <p>{t("Save first as a draft. Publishing is a separate action.")}</p>
          <div className="platform-ad-fields">
            <label className="platform-ad-field">
              <span>{t("Campaign name")}</span>
              <input maxLength={120} value={draft.name} onChange={event => updateDraft("name", event.target.value)} placeholder={t("e.g. Seasonal print promotion")} />
            </label>
            <label className="platform-ad-field">
              <span>{t("Advertiser / business")}</span>
              <input maxLength={140} value={draft.advertiserName} onChange={event => updateDraft("advertiserName", event.target.value)} placeholder={t("Business name")} />
            </label>
            <label className="platform-ad-field full">
              <span>{t("Placement")}</span>
              <select value={draft.placement} onChange={event => updateDraft("placement", event.target.value as Placement)}>
                <option value="HOME">{t("Customer Home · Banner")}</option>
                <option value="FILES">{t("Customer My Files · Card")}</option>
                <option value="SHOP_OVERVIEW">{t("Shop owner workspace · Overview")}</option>
              </select>
              <small>{t(formats[draft.placement].dimensions)}</small>
            </label>
            <div className="platform-ad-field full">
              <span>{t("Banner image")}</span>
              <div className="platform-ad-upload">
                {draft.creativeUrl && <img src={draft.creativeUrl} alt={t("Selected campaign banner")} />}
                <div className="platform-ad-upload-copy">
                  <strong>{draft.creativeFileName || t("No image uploaded")}</strong>
                  <small>{t("JPG, PNG or WebP · maximum 3 MB")} · {t(formats[draft.placement].dimensions)}</small>
                </div>
                <label className="secondary-button">
                  <Upload size={13} /> {uploading ? t("Uploading…") : t("Choose image")}
                  <input type="file" hidden accept="image/jpeg,image/png,image/webp" disabled={uploading} onChange={event => {
                    void upload(event.target.files?.[0]);
                    event.currentTarget.value = "";
                  }} />
                </label>
              </div>
            </div>
            <label className="platform-ad-field">
              <span>{t("Headline")}</span>
              <input maxLength={120} value={draft.headline} onChange={event => updateDraft("headline", event.target.value)} placeholder={t("Short headline")} />
            </label>
            <label className="platform-ad-field">
              <span>{t("Destination URL")}</span>
              <input type="url" maxLength={1000} value={draft.destinationUrl} onChange={event => updateDraft("destinationUrl", event.target.value)} placeholder="https://example.com/offer" />
            </label>
            <label className="platform-ad-field full">
              <span>{t("Supporting copy")}</span>
              <textarea maxLength={400} value={draft.body} onChange={event => updateDraft("body", event.target.value)} placeholder={t("What is being advertised?")} />
            </label>
            <label className="platform-ad-field full">
              <span>{t("Terms / disclaimer (optional)")}</span>
              <textarea maxLength={2000} value={draft.terms} onChange={event => updateDraft("terms", event.target.value)} placeholder={t("Validity, eligibility and conditions.")} />
            </label>
            <label className="platform-ad-field">
              <span>{t("Start date and time")}</span>
              <input type="datetime-local" value={draft.startsLocal} onChange={event => updateDraft("startsLocal", event.target.value)} />
            </label>
            <label className="platform-ad-field">
              <span>{t("End date and time (optional)")}</span>
              <input type="datetime-local" value={draft.endsLocal} onChange={event => updateDraft("endsLocal", event.target.value)} />
            </label>
          </div>

          <div className="platform-ad-preview" style={{ marginTop: 15 }}>
            <span className="platform-ad-preview-label">{t("Live preview · {placement}", { placement: placementLabel(draft.placement) })}</span>
            <div className="platform-ad-preview-banner">
              {draft.creativeUrl ? <img src={draft.creativeUrl} alt={t("Campaign preview")} /> : <div>{t("Your banner")}</div>}
              <div>
                <strong>{draft.headline || t("Campaign headline")}</strong>
                <p>{draft.body || t("Supporting copy appears here.")}</p>
                <small>{t("Sponsored")} · {draft.advertiserName || t("Advertiser")}</small>
              </div>
            </div>
          </div>

          <div className="platform-ad-actions" style={{ marginTop: 14 }}>
            <button className="primary-button" onClick={() => void save()} disabled={saving || uploading}>
              <Save size={14} /> {saving ? t("Saving…") : draft.id ? t("Save changes") : t("Save draft")}
            </button>
            <button className="secondary-button" onClick={() => { setDraft(emptyDraft()); setError(""); setSuccess(""); }}>
              {t("New campaign")}
            </button>
          </div>
        </section>

        <section className="panel platform-ad-list">
          <div className="panel-heading">
            <div><h3>{t("Campaigns")}</h3><p>{t("Latest campaigns and delivery performance")}</p></div>
            <span className="neutral-chip">{t("{count} total", { count: formatNumber(items.length) })}</span>
          </div>
          {loading ? (
            <div className="platform-loading-inline">{t("Loading campaigns…")}</div>
          ) : items.length === 0 ? (
            <div className="platform-plan-empty">{t("No campaigns yet. Create your first draft.")}</div>
          ) : (
            <div className="platform-ad-table-wrap">
              <table className="platform-ad-table">
                <thead><tr>
                  <th>{t("CAMPAIGN")}</th><th>{t("PLACEMENT")}</th><th>{t("STATUS")}</th><th>{t("VIEWS / CLICKS")}</th><th>{t("ACTIONS")}</th>
                </tr></thead>
                <tbody>
                  {items.map(item => (
                    <tr key={item.id}>
                      <td>
                        <div className="platform-ad-campaign-cell"><div>
                          <strong>{item.name}</strong>
                          <small>{item.advertiserName} · {item.headline}</small>
                          <small>{formatIstDate(item.startsUtc)}{item.endsUtc ? " – " + formatIstDate(item.endsUtc) : " · " + t("No end date")}</small>
                        </div></div>
                      </td>
                      <td>{placementLabel(item.placement as Placement)}</td>
                      <td><span className={"platform-ad-status" + (item.status === "Active" ? " active" : "")}>{statusLabel(item.status)}</span></td>
                      <td><strong>{formatNumber(item.impressions)} / {formatNumber(item.clicks)}</strong><small>{formatNumber(item.clickThroughRate, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}% CTR</small></td>
                      <td>
                        <div className="platform-ad-actions">
                          <button className="secondary-button" onClick={() => void editCampaign(item)}><Save size={12} /> {t("Edit")}</button>
                          {item.storedStatus === "Draft" && <button className="secondary-button" disabled={busyId === item.id} onClick={() => void changeStatus(item, "Active")}><Play size={12} /> {t("Publish")}</button>}
                          {item.storedStatus === "Active" && <button className="secondary-button" disabled={busyId === item.id} onClick={() => void changeStatus(item, "Paused")}><Pause size={12} /> {t("Pause")}</button>}
                          {item.storedStatus === "Paused" && <button className="secondary-button" disabled={busyId === item.id} onClick={() => void changeStatus(item, "Active")}><Play size={12} /> {t("Resume")}</button>}
                          {item.storedStatus !== "Archived" && <button className="secondary-button" disabled={busyId === item.id} onClick={() => void changeStatus(item, "Archived")}><Trash2 size={12} /> {t("Archive")}</button>}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="platform-ad-note">{t("Schedules are evaluated at request time. Impressions and clicks are recorded separately from customer print orders, shop payment records and subscriptions.")}</div>
        </section>
      </div>

      <div className="platform-ad-note"><ShieldCheck size={13} /> {t("Customer placements are Home and My Files. Shop-owner advertisements appear only on the Overview dashboard in browser and Windows desktop workspaces—not the Print Queue, payment views, document previews or private-print screens.")}</div>
    </section>
  );
}
