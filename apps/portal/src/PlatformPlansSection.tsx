import { useEffect, useMemo, useState, type ReactNode } from "react";
import { formatIstDate } from "./date-time";
import {
  CheckCircle2, CirclePlus, Cloud, FileText, Layers3, LoaderCircle,
  LockKeyhole, RefreshCw, Save, ShieldCheck, Sparkles, ToggleLeft, ToggleRight
} from "lucide-react";
import {
  createPlatformPlan, getPlatformPlans, publishPlatformPlanVersion,
  savePlatformPlanDraft, updatePlatformPlan,
  type AuthTokens, type PlatformPlan, type PlatformPlanVersion,
  type PlatformPlanVersionInput
} from "./api";

type PlanForm = {
  currency: string;
  monthlyPriceMinor: string;
  annualPriceMinor: string;
  maxPrinters: string;
  maxAgentDevices: string;
  maxQrStations: string;
  maxMonthlyPrintJobs: string;
  maxDocumentBytes: string;
  cloudAssistedPrintingEnabled: boolean;
  whiteLabelEnabled: boolean;
  advancedReportsEnabled: boolean;
};

const emptyForm: PlanForm = {
  currency: "INR",
  monthlyPriceMinor: "",
  annualPriceMinor: "",
  maxPrinters: "",
  maxAgentDevices: "",
  maxQrStations: "",
  maxMonthlyPrintJobs: "",
  maxDocumentBytes: "",
  cloudAssistedPrintingEnabled: false,
  whiteLabelEnabled: false,
  advancedReportsEnabled: false
};

function currencyMinorUnitDigits(currency: string): number {
  try {
    return new Intl.NumberFormat("en-IN", { style: "currency", currency }).resolvedOptions().maximumFractionDigits;
  } catch {
    return 2;
  }
}

function priceInputFromMinor(value: number | null, currency: string): string {
  if (value === null) return "";
  const digits = currencyMinorUnitDigits(currency);
  const formatted = (value / (10 ** digits)).toFixed(digits);
  return digits === 0 ? formatted : formatted.replace(/(\.\d*?)0+$/, "$1").replace(/\.$/, "");
}

function optionalMoneyMinor(value: string, label: string, currency: string): number | null {
  const raw = value.trim();
  if (!raw) return null;
  const digits = currencyMinorUnitDigits(currency);
  const fractionPattern = digits === 0 ? "" : "(?:\\.\\d{1," + digits + "})?";
  if (!new RegExp("^\\d+" + fractionPattern + "$").test(raw)) {
    throw new Error(label + " must be a non-negative amount with at most " + digits + " decimal places, or left blank.");
  }
  const [wholePart, fractionPart = ""] = raw.split(".");
  const multiplier = 10 ** digits;
  const minor = Number(wholePart) * multiplier +
    Number((fractionPart || "").padEnd(digits, "0") || "0");
  if (!Number.isSafeInteger(minor)) throw new Error(label + " is outside the supported amount range.");
  return minor;
}
function formFromVersion(version?: PlatformPlanVersion): PlanForm {
  if (!version) return { ...emptyForm };
  return {
    currency: version.currency,
    monthlyPriceMinor: priceInputFromMinor(version.monthlyPriceMinor, version.currency),
    annualPriceMinor: priceInputFromMinor(version.annualPriceMinor, version.currency),
    maxPrinters: version.maxPrinters?.toString() ?? "",
    maxAgentDevices: version.maxAgentDevices?.toString() ?? "",
    maxQrStations: version.maxQrStations?.toString() ?? "",
    maxMonthlyPrintJobs: version.maxMonthlyPrintJobs?.toString() ?? "",
    maxDocumentBytes: version.maxDocumentBytes?.toString() ?? "",
    cloudAssistedPrintingEnabled: version.cloudAssistedPrintingEnabled,
    whiteLabelEnabled: version.whiteLabelEnabled,
    advancedReportsEnabled: version.advancedReportsEnabled
  };
}

function optionalInteger(value: string, label: string, allowZero: boolean): number | null {
  if (!value.trim()) return null;
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < (allowZero ? 0 : 1)) {
    throw new Error(`${label} must be a whole number of at least ${allowZero ? "zero" : "one"}, or left blank.`);
  }
  return parsed;
}

function toVersionInput(form: PlanForm): PlatformPlanVersionInput {
  const currency = form.currency.trim().toUpperCase();
  if (!/^[A-Z]{3}$/.test(currency)) throw new Error("Enter a three-letter currency code.");
  return {
    currency,
    monthlyPriceMinor: optionalMoneyMinor(form.monthlyPriceMinor, "Monthly price", currency),
    annualPriceMinor: optionalMoneyMinor(form.annualPriceMinor, "Annual price", currency),
    maxPrinters: optionalInteger(form.maxPrinters, "Printer limit", false),
    maxAgentDevices: optionalInteger(form.maxAgentDevices, "Agent computer limit", false),
    maxQrStations: optionalInteger(form.maxQrStations, "QR station limit", false),
    maxMonthlyPrintJobs: optionalInteger(form.maxMonthlyPrintJobs, "Monthly print-job limit", false),
    maxDocumentBytes: optionalInteger(form.maxDocumentBytes, "Document size limit (bytes)", false),
    cloudAssistedPrintingEnabled: form.cloudAssistedPrintingEnabled,
    whiteLabelEnabled: form.whiteLabelEnabled,
    advancedReportsEnabled: form.advancedReportsEnabled
  };
}

function latestVersion(plan: PlatformPlan): PlatformPlanVersion | undefined {
  return [...plan.versions].sort((a, b) => b.version - a.version)[0];
}

function draftVersion(plan: PlatformPlan): PlatformPlanVersion | undefined {
  return plan.versions.find(version => version.status === "Draft") ?? latestVersion(plan);
}

function formatPrice(value: number | null, currency: string): string {
  if (value === null) return "Not set";
  try {
    const digits = currencyMinorUnitDigits(currency);
    return new Intl.NumberFormat("en-IN", {
      style: "currency",
      currency
    }).format(value / (10 ** digits));
  } catch {
    return `${currency} ${(value / 100).toFixed(2)}`;
  }
}

export default function PlatformPlansSection({ tokens }: { tokens: AuthTokens }) {
  const [plans, setPlans] = useState<PlatformPlan[]>([]);
  const [selectedPlanId, setSelectedPlanId] = useState("");
  const [creating, setCreating] = useState(false);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [active, setActive] = useState(true);
  const [form, setForm] = useState<PlanForm>({ ...emptyForm });

  const selectedPlan = useMemo(
    () => plans.find(plan => plan.id === selectedPlanId) ?? null,
    [plans, selectedPlanId]
  );
  const currentDraft = selectedPlan ? draftVersion(selectedPlan) : undefined;
  const hasDraft = currentDraft?.status === "Draft";
  const isSystemPlan = selectedPlan?.isSystemPlan ?? false;

  async function reload(selectId?: string) {
    setLoading(true);
    setError("");
    try {
      const result = await getPlatformPlans(tokens.accessToken);
      setPlans(result);
      const nextId = selectId ?? selectedPlanId;
      if (nextId && result.some(plan => plan.id === nextId)) {
        setSelectedPlanId(nextId);
      } else if (result.length) {
        setSelectedPlanId((result.find(plan => !plan.isSystemPlan) ?? result[0]).id);
      } else {
        setSelectedPlanId("");
      }
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load subscription plans.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void reload();
    // The token is stable for the duration of this platform session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tokens.accessToken]);

  useEffect(() => {
    if (creating) return;
    if (!selectedPlan) {
      setName("");
      setDescription("");
      setCode("");
      setActive(true);
      setForm({ ...emptyForm });
      return;
    }
    setCode(selectedPlan.code);
    setName(selectedPlan.name);
    setDescription(selectedPlan.description ?? "");
    setActive(selectedPlan.isActive);
    setForm(formFromVersion(draftVersion(selectedPlan)));
  }, [selectedPlanId, plans, creating]);

  function startCreate() {
    setCreating(true);
    setSelectedPlanId("");
    setError("");
    setSuccess("");
    setCode("");
    setName("");
    setDescription("");
    setActive(true);
    setForm({ ...emptyForm });
  }

  function changeForm<K extends keyof PlanForm>(key: K, value: PlanForm[K]) {
    setForm(previous => ({ ...previous, [key]: value }));
  }

  async function savePlan() {
    setBusy("save");
    setError("");
    setSuccess("");
    try {
      const version = toVersionInput(form);
      if (creating) {
        if (!/^[A-Za-z][A-Za-z0-9_]{1,39}$/.test(code.trim())) {
          throw new Error("Plan code must contain 2–40 letters, digits or underscores and start with a letter.");
        }
        if (!name.trim()) throw new Error("Plan name is required.");
        const result = await createPlatformPlan(tokens.accessToken, {
          code: code.trim().toUpperCase(),
          name: name.trim(),
          description: description.trim() || undefined,
          version
        });
        setCreating(false);
        await reload(result.id);
        setSuccess("Plan created. Its first version is still a draft and is not available for subscription assignment.");
      } else if (selectedPlan) {
        await savePlatformPlanDraft(tokens.accessToken, selectedPlan.id, version);
        await reload(selectedPlan.id);
        setSuccess("Draft version saved. Published versions and existing subscriptions remain unchanged.");
      }
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save the plan.");
    } finally {
      setBusy("");
    }
  }

  async function saveMetadata() {
    if (!selectedPlan) return;
    setBusy("metadata");
    setError("");
    setSuccess("");
    try {
      await updatePlatformPlan(tokens.accessToken, selectedPlan.id, {
        name: name.trim(),
        description: description.trim() || undefined,
        isActive: active
      });
      await reload(selectedPlan.id);
      setSuccess("Plan details updated.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update plan details.");
    } finally {
      setBusy("");
    }
  }

  async function publishDraft() {
    if (!selectedPlan || !currentDraft || currentDraft.status !== "Draft") return;
    setBusy("publish");
    setError("");
    setSuccess("");
    try {
      await publishPlatformPlanVersion(tokens.accessToken, selectedPlan.id, currentDraft.version);
      await reload(selectedPlan.id);
      setSuccess(`Version ${currentDraft.version} published. New subscriptions can now use these terms.`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not publish this plan version.");
    } finally {
      setBusy("");
    }
  }

  const monthlyMinor = optionalPreview(form.monthlyPriceMinor, form.currency);
  const annualMinor = optionalPreview(form.annualPriceMinor, form.currency);

  return (
    <section className="platform-plans">
      <div className="platform-plans-intro">
        <div>
          <div className="platform-plan-kicker"><Layers3 size={14}/> COMMERCIAL CATALOGUE</div>
          <h2>Plans and entitlements</h2>
          <p>Configure plan pricing and product capabilities. Changes are versioned so published terms remain stable.</p>
        </div>
        <div className="platform-plans-actions">
          <button className="secondary-button" onClick={() => void reload()} disabled={loading || Boolean(busy)}>
            <RefreshCw size={14}/> Refresh
          </button>
          <button className="primary-button" onClick={startCreate} disabled={loading || Boolean(busy)}>
            <CirclePlus size={15}/> New plan
          </button>
        </div>
      </div>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {success && <div className="platform-plan-success" role="status"><CheckCircle2 size={15}/>{success}</div>}

      <div className="platform-plan-layout">
        <aside className="panel platform-plan-list-panel">
          <div className="platform-plan-list-heading"><strong>Plan catalogue</strong><span>{plans.length}</span></div>
          {loading ? (
            <div className="platform-plan-empty"><LoaderCircle size={17} className="platform-spin"/> Loading plans…</div>
          ) : plans.length === 0 ? (
            <div className="platform-plan-empty">No plans have been created.</div>
          ) : (
            <div className="platform-plan-list">
              {plans.map(plan => {
                const version = latestVersion(plan);
                return (
                  <button
                    type="button"
                    className={selectedPlanId === plan.id && !creating ? "platform-plan-list-item selected" : "platform-plan-list-item"}
                    key={plan.id}
                    onClick={() => { setCreating(false); setSelectedPlanId(plan.id); setSuccess(""); setError(""); }}
                  >
                    <span className="platform-plan-list-icon">{plan.isSystemPlan ? <LockKeyhole size={16}/> : <Layers3 size={16}/>}</span>
                    <span className="platform-plan-list-copy">
                      <strong>{plan.name}</strong>
                      <small>{plan.code} · {version ? `v${version.version} · ${version.status}` : "No versions"}</small>
                    </span>
                    <span className={plan.isActive ? "platform-plan-state active" : "platform-plan-state inactive"}>{plan.isActive ? "Active" : "Inactive"}</span>
                  </button>
                );
              })}
            </div>
          )}
          <div className="platform-plan-list-foot"><ShieldCheck size={14}/> System trial plan is protected.</div>
        </aside>

        <div className="platform-plan-editor">
          {creating ? (
            <section className="panel platform-plan-panel">
              <div className="platform-plan-panel-heading">
                <div><span className="platform-plan-panel-kicker">NEW COMMERCIAL PLAN</span><h3>Create a plan</h3><p>The initial version is saved as a draft and requires explicit publication.</p></div>
                <span className="platform-plan-draft-pill">Draft</span>
              </div>
              <div className="platform-plan-meta-grid">
                <Field label="Plan code" hint="Stable uppercase identifier">
                  <input value={code} onChange={event => setCode(event.target.value)} placeholder="e.g. STARTER" maxLength={40}/>
                </Field>
                <Field label="Plan name">
                  <input value={name} onChange={event => setName(event.target.value)} placeholder="Plan name" maxLength={100}/>
                </Field>
                <Field label="Description" wide>
                  <textarea value={description} onChange={event => setDescription(event.target.value)} placeholder="What this subscription includes" rows={2} maxLength={500}/>
                </Field>
              </div>
              <PlanVersionEditor form={form} onChange={changeForm}/>
              <div className="platform-plan-editor-actions">
                <span className="platform-plan-note">Enter amounts in the selected currency. Blank means unavailable, while zero is explicitly free.</span>
                <button className="primary-button" onClick={() => void savePlan()} disabled={Boolean(busy)}>
                  {busy === "save" ? <LoaderCircle size={14} className="platform-spin"/> : <Save size={14}/>} Create plan draft
                </button>
              </div>
            </section>
          ) : selectedPlan ? (
            <>
              <section className="panel platform-plan-panel">
                <div className="platform-plan-panel-heading">
                  <div>
                    <span className="platform-plan-panel-kicker">{selectedPlan.isSystemPlan ? "PROTECTED SYSTEM PLAN" : "PLAN DETAILS"}</span>
                    <h3>{selectedPlan.name}</h3>
                    <p>{selectedPlan.code} · created {formatIstDate(selectedPlan.createdUtc)}</p>
                  </div>
                  {selectedPlan.isSystemPlan ? <span className="platform-plan-protected"><LockKeyhole size={13}/> Protected</span> : <span className={active ? "platform-plan-state active" : "platform-plan-state inactive"}>{active ? "Active" : "Inactive"}</span>}
                </div>
                <div className="platform-plan-meta-grid">
                  <Field label="Plan name"><input value={name} onChange={event => setName(event.target.value)} disabled={isSystemPlan} maxLength={100}/></Field>
                  <Field label="Plan code"><input value={selectedPlan.code} disabled/></Field>
                  <Field label="Description" wide><textarea value={description} onChange={event => setDescription(event.target.value)} disabled={isSystemPlan} rows={2} maxLength={500}/></Field>
                </div>
                {!isSystemPlan && (
                  <div className="platform-plan-editor-actions platform-plan-metadata-actions">
                    <label className="platform-plan-toggle">
                      <input type="checkbox" checked={active} onChange={event => setActive(event.target.checked)}/>
                      <span>{active ? "Available for future assignment" : "Hidden from future assignment"}</span>
                    </label>
                    <button className="secondary-button" onClick={() => void saveMetadata()} disabled={Boolean(busy)}>
                      {busy === "metadata" ? <LoaderCircle size={14} className="platform-spin"/> : <Save size={14}/>} Save details
                    </button>
                  </div>
                )}
              </section>

              <section className="panel platform-plan-panel">
                <div className="platform-plan-panel-heading">
                  <div><span className="platform-plan-panel-kicker">VERSIONED TERMS</span><h3>{hasDraft ? `Edit draft · v${currentDraft?.version}` : `Prepare next version · v${(latestVersion(selectedPlan)?.version ?? 0) + 1}`}</h3><p>Saving creates or updates a draft. Published terms cannot be edited in place.</p></div>
                  <span className={hasDraft ? "platform-plan-draft-pill" : "platform-plan-protected"}>{hasDraft ? "Unpublished draft" : "New draft"}</span>
                </div>
                {isSystemPlan ? (
                  <div className="platform-plan-system-note"><LockKeyhole size={16}/><div><strong>Trial safeguards</strong><span>This system plan preserves existing 14-day trials. Its terms are intentionally not editable from the commercial catalogue.</span></div></div>
                ) : (
                  <>
                    <PlanVersionEditor form={form} onChange={changeForm}/>
                    <div className="platform-plan-editor-actions">
                      <span className="platform-plan-note">Current entries: monthly {formatPrice(monthlyMinor, form.currency.toUpperCase())}; annual {formatPrice(annualMinor, form.currency.toUpperCase())}.</span>
                      <button className="primary-button" onClick={() => void savePlan()} disabled={Boolean(busy)}>
                        {busy === "save" ? <LoaderCircle size={14} className="platform-spin"/> : <Save size={14}/>} Save draft
                      </button>
                    </div>
                    {hasDraft && (
                      <div className="platform-plan-publish-row">
                        <div><strong>Ready to publish?</strong><span>At least one price is required. Enter zero for an explicit free plan.</span></div>
                        <button className="secondary-button" onClick={() => void publishDraft()} disabled={Boolean(busy) || (currentDraft?.monthlyPriceMinor == null && currentDraft?.annualPriceMinor == null)}>
                          {busy === "publish" ? <LoaderCircle size={14} className="platform-spin"/> : <Sparkles size={14}/>} Publish version
                        </button>
                      </div>
                    )}
                  </>
                )}
              </section>
            </>
          ) : (
            <section className="panel platform-plan-panel platform-plan-empty-editor">
              <Layers3 size={24}/><h3>Select or create a plan</h3><p>Choose a plan from the catalogue to inspect its versions and configure draft terms.</p>
            </section>
          )}
        </div>
      </div>

      {selectedPlan && !creating && (
        <section className="panel platform-plan-history">
          <div className="platform-plan-history-heading"><div><h3>Version history</h3><p>Each published version remains available for existing subscription references.</p></div><span>{selectedPlan.versions.length} versions</span></div>
          {selectedPlan.versions.length === 0 ? <div className="platform-plan-empty">No versions yet.</div> : (
            <div className="platform-plan-version-list">
              {[...selectedPlan.versions].sort((a,b)=>b.version-a.version).map(version => (
                <div className="platform-plan-version-row" key={version.id}>
                  <span className="platform-plan-version-number">v{version.version}</span>
                  <div className="platform-plan-version-copy">
                    <strong>{version.status}</strong>
                    <small>{version.currency} · monthly {formatPrice(version.monthlyPriceMinor, version.currency)} · annual {formatPrice(version.annualPriceMinor, version.currency)}</small>
                  </div>
                  <span className="platform-plan-version-date">{version.publishedUtc ? `Published ${formatIstDate(version.publishedUtc)}` : `Updated ${formatIstDate(version.updatedUtc)}`}</span>
                </div>
              ))}
            </div>
          )}
        </section>
      )}
    </section>
  );
}

function optionalPreview(value: string, currency: string): number | null {
  try {
    return optionalMoneyMinor(value, "Price", currency);
  } catch {
    return null;
  }
}

function Field({ label, hint, wide, children }: {
  label: string;
  hint?: string;
  wide?: boolean;
  children: ReactNode;
}) {
  return <label className={wide ? "platform-plan-field wide" : "platform-plan-field"}>
    <span>{label}{hint && <small>{hint}</small>}</span>
    {children}
  </label>;
}

function PlanVersionEditor({ form, onChange }: {
  form: PlanForm;
  onChange: <K extends keyof PlanForm>(key: K, value: PlanForm[K]) => void;
}) {
  const currencyLabel = form.currency.trim().toUpperCase() === "INR" ? "₹" : form.currency.toUpperCase();
  const priceStep = 10 ** -currencyMinorUnitDigits(form.currency);
  return (
    <div className="platform-plan-version-editor">
      <div className="platform-plan-subheading"><div><h4>Pricing</h4><p>Enter amounts in {currencyLabel}; SmartPrint stores the currency's minor units.</p></div><Field label="Currency"><input value={form.currency} onChange={event => onChange("currency", event.target.value.toUpperCase())} maxLength={3} placeholder="INR"/></Field></div>
      <div className="platform-plan-meta-grid">
        <Field label={`Monthly price (${currencyLabel})`}><input type="number" min="0" step={priceStep} value={form.monthlyPriceMinor} onChange={event => onChange("monthlyPriceMinor", event.target.value)} placeholder="e.g. 149"/></Field>
        <Field label={`Annual price (${currencyLabel})`}><input type="number" min="0" step={priceStep} value={form.annualPriceMinor} onChange={event => onChange("annualPriceMinor", event.target.value)} placeholder="e.g. 1499"/></Field>
      </div>

      <div className="platform-plan-subheading"><div><h4>Usage limits</h4><p>Blank means no configured cap; positive numbers enforce the configured limit when enforcement is connected.</p></div><span className="platform-plan-unlimited">Optional limits</span></div>
      <div className="platform-plan-meta-grid platform-plan-limits-grid">
        <Field label="Printers"><input type="number" min="1" step="1" value={form.maxPrinters} onChange={event => onChange("maxPrinters", event.target.value)} placeholder="No configured cap"/></Field>
        <Field label="Agent computers"><input type="number" min="1" step="1" value={form.maxAgentDevices} onChange={event => onChange("maxAgentDevices", event.target.value)} placeholder="No configured cap"/></Field>
        <Field label="QR stations"><input type="number" min="1" step="1" value={form.maxQrStations} onChange={event => onChange("maxQrStations", event.target.value)} placeholder="No configured cap"/></Field>
        <Field label="Print jobs / month"><input type="number" min="1" step="1" value={form.maxMonthlyPrintJobs} onChange={event => onChange("maxMonthlyPrintJobs", event.target.value)} placeholder="No configured cap"/></Field>
        <Field label="Maximum document size · bytes"><input type="number" min="1" step="1" value={form.maxDocumentBytes} onChange={event => onChange("maxDocumentBytes", event.target.value)} placeholder="No configured cap"/></Field>
      </div>

      <div className="platform-plan-subheading"><div><h4>Product capabilities</h4><p>Saved centrally and intended to be evaluated by the server.</p></div></div>
      <div className="platform-plan-feature-grid">
        <FeatureToggle icon={<Cloud size={16}/>} label="Cloud-assisted printing" description="Allow secure cloud transfer for print jobs" checked={form.cloudAssistedPrintingEnabled} onChange={value => onChange("cloudAssistedPrintingEnabled", value)}/>
        <FeatureToggle icon={<Sparkles size={16}/>} label="White-label experience" description="Remove or replace Smart Print platform branding" checked={form.whiteLabelEnabled} onChange={value => onChange("whiteLabelEnabled", value)}/>
        <FeatureToggle icon={<FileText size={16}/>} label="Advanced reports" description="Enable expanded shop reporting capabilities" checked={form.advancedReportsEnabled} onChange={value => onChange("advancedReportsEnabled", value)}/>
      </div>
    </div>
  );
}

function FeatureToggle({ icon, label, description, checked, onChange }: {
  icon: ReactNode;
  label: string;
  description: string;
  checked: boolean;
  onChange: (value: boolean) => void;
}) {
  return <label className={checked ? "platform-plan-feature enabled" : "platform-plan-feature"}>
    <span className="platform-plan-feature-icon">{icon}</span>
    <span className="platform-plan-feature-copy"><strong>{label}</strong><small>{description}</small></span>
    <input type="checkbox" checked={checked} onChange={event => onChange(event.target.checked)}/>
    <span className="platform-plan-feature-switch" aria-hidden="true">{checked ? <ToggleRight size={23}/> : <ToggleLeft size={23}/>}</span>
  </label>;
}
