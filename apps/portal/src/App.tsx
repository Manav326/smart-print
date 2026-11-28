import { UPLOAD_LIMITS } from "./upload-limits.generated";
import { isValidCustomerPhotoSettings, type CustomerPhotoSettings } from "./customer-photo-settings";
import { resolvePhotoMediaCapabilities } from "./photo-media-capabilities";
import { validateCustomerUploadBatchLimits } from "./upload-batch-policy";
import { CUSTOMER_UPLOAD_CONCURRENCY, mapConcurrentSettled } from "./upload-concurrency";
import { identifySupportedDocument, type SupportedDocumentType } from "./document-content-policy";
import { calculateBatchQuoteBreakdown } from "./batch-quote-breakdown.js";
import { Fragment, useCallback, useEffect, useId, useRef, useState, type ChangeEvent, type FormEvent, type ReactNode } from "react";
import {
  Activity, ArrowRight, Check, Clock3, Copy, CreditCard, Download, FileText, FileUp, FolderOpen, LayoutDashboard,
  ChevronDown, LifeBuoy, LogOut, Menu, Monitor, Plus, Printer, QrCode, Search, Settings, Wifi, Image as ImageIcon, UserRound, Share2,
  ShieldCheck, AlertTriangle, Users, Wallet, X, PanelLeftClose, PanelLeftOpen, RotateCcw, RefreshCw, Zap, Info, Lock
} from "lucide-react";
import QRCode from "react-qr-code";
import PhotoStudio, { type PreparedPhotoSheet, type PhotoStudioEditorSettings } from "./PhotoStudio";
import PrivatePhotoStudioEntry from "./PrivatePhotoStudioEntry";
import { isSupportedPhotoSource, resolvePhotoSourceMimeType } from "./photo-source-policy";
import CustomerUpiQr from "./CustomerUpiQr";
import ShopDocumentPreviewSurface from "./ShopDocumentPreviewSurface";
import ShopSubscriptionCheckout from "./ShopSubscriptionCheckout";
import { buildCustomerUpiIntent } from "./upi-payment-intent";
import { getPrinterReadinessReason, isPrinterAvailableForShop } from "./printer-availability";
import { shouldBlockBatchCheckoutForExpiredQuote, shouldBlockQuoteRefreshForStatus } from "./batch-checkout-state";
import { isCustomerBatchFinished } from "./customer-session-state";
import { canFallbackToCloudFromLocalFailure, resolveCustomerUploadMode } from "./local-upload-routing";
import AuthScreen from "./AuthScreen";
import PublicLandingPage, { DownloadAction } from "./PublicLandingPage";
import QrStationPrintDocument from "./QrStationPrintDocument";
import { buildQrStationArtworkSvg, createQrStationImageFile, downloadQrStationImage, loadQrStationLogoDataUri, shareQrStationImage, type QrStationArtworkSource, type QrStationImageFormat } from "./qr-station-export";
import { resolveCustomerUploadStatusMessage } from "./customer-upload-status";
import { getPublicDownloadPath, usePublicRelease } from "./release-metadata";
import PlatformAdminConsole from "./PlatformAdminConsole";
import PlatformAdminAuthScreen from "./PlatformAdminAuthScreen";
import { resolvePortalRoute, shopPathForSection, shopSectionFromPath } from "./portal-routing";
import {
  ApiError, LocalUploadOutcomeUnknownError, LocalUploadPreflightError, hasPendingLocalCustomerUploadAttempt, isPlatformAdminSession, acceptCustomerQuote, applyShopPrintBatchReview, approveShopPrintJob, assignShopPrintJobPrinter, calculateCustomerQuote, getShopCustomerHistory, getShopCustomers, getShopReportSummary,
  confirmShopPrintOutput, confirmPrinterDiagnostic, getPrinterDiagnosticHistory, getPrinterDiagnosticProfile, startPrinterDiagnostic, dispatchShopPrintJob, resumeShopUnprintedBatch,
  createAgentEnrollmentCode, createCustomerSession, createStation,
  createShopPhotoJob, downloadShopDocument, replaceShopPhotoDocument, replaceShopPhotoDocumentLocally, getCustomerJobStatus, getShopAgentDevices, getShopJobEvents, getShopPrinters, getShopPrintJobsPage, getShopPrintBatchJobs, getShopPayments, loadShopData,
  logout, readStoredTokens, rejectShopPrintJob, rejectShopPayment, revokeAgentDevice,
  reportCustomerBatchPayment, reportCustomerUpiPayment, renewCustomerPrintSession, getNativeCustomerPrintSession, saveNativeCustomerPrintSession, saveNativeCustomerShopProfile, clearNativeCustomerPrintSession, resumeCustomerSession, rotateStationCode, startCustomerBatchPayment, startCustomerPayment, storeTokens,
  updatePrinterActive, updateShopPaymentSettings, updateShopPricing, updateShopPrintJobSettings,
  updateShopOwnerAccount, updateShopProfile, updateStationEnabled, uploadCustomerDocument, uploadCustomerDocumentLocally,
  verifyShopPayment, retryShopPdfExport, retryShopPrivateTestOutput, recordShopPrintRefund, previewShopPrintRefund, type CustomerBatchPayment, type CustomerPayment, type CustomerPhotoPricing, type ShopPayment, type ShopPaymentSettings, type ShopRefund,
  type AgentDevice, type AuthTokens, type CustomerJobStatus, type PrinterDevice, type PrinterDiagnosticAttempt, type PrinterDiagnosticProfile, type PrintQuoteResult, type ShopJobEvent,
  type QrStation, type ShopCustomer, type ShopCustomerHistory, type ShopCustomerPage, type ShopPricingProfile, type ShopPrintJob, type ShopProfile, type ShopReportSummary, type ShopSubscriptionSummary,
  type UploadedPrintJob, type CustomerPrintRequestSettings
} from "./api";
import {
  enrollLocalAgent, getLocalAgentStatus, getLocalNetworkSnapshot, selectLocalNetwork, startLocalPhoneTest,
  isDesktopApp, setDesktopContentProtection, type DesktopAgentStatus, type DesktopLocalNetworkSnapshot, type DesktopLocalPhoneTest
} from "./desktop-bridge";
import { identifyDocumentPreviewMediaType } from "./document-preview";
import { verifiedPdfExportPath } from "./print-output";
import { canConfirmPhysicalOutput, canManuallyReconcileUncertainOutput, hasIncompleteSpoolerPageProgress, hasVerifiedAgentSpoolerObservation, isPhysicalOutputConfirmationStatus, isRetainedSpoolerJob } from "./spooler-output-confirmation";
import { applyThemePreference, readDarkModePreference, readSidebarCollapsedPreference, saveDarkModePreference, saveSidebarCollapsedPreference } from "./ui-preferences";
import ThemeToggleButton from "./ThemeToggleButton";
import BrandLockup from "./BrandLockup";
import OrderJourneyPage from "./OrderJourneyPage";
import ShopAdPlacement from "./ShopAdPlacement";
import { IST_TIME_ZONE, formatIstDate, formatIstDateTime, formatIstTime, istDateKey } from "./date-time";
import { getSubscriptionReminderState } from "./subscription-checkout-state";
import { LanguageSwitcher, useLocale, translateUi, translateUiText, formatUiCurrency, formatUiNumber, formatUiDate, type MessageValues } from "./i18n";

type Section = "Order Journey" | "Overview" | "Print Queue" | "Printers" | "QR Stations" | "Print Agent" | "Pricing" | "Subscription" | "Payments" | "Customers" | "Reports" | "Settings";
type Modal = "station" | null;

type CustomerUploadFileProgress = {
  name: string;
  sizeBytes: number;
  bytesSent: number;
  totalBytes: number;
  status: "QUEUED" | "PREPARING" | "TRANSFERRING" | "STREAMING_ENCRYPTED" | "WAITING_FOR_SHOP" | "PROCESSING" | "COMPLETED" | "FAILED";
  message: string;
};

type CustomerUploadProgressState = {
  completed: number;
  total: number;
  files: CustomerUploadFileProgress[];
};

const sections: { label: Section; icon: typeof LayoutDashboard }[] = [
  { label: "Order Journey", icon: Activity },
  { label: "Overview", icon: LayoutDashboard },
  { label: "Print Queue", icon: FileText },
  { label: "Printers", icon: Printer },
  { label: "QR Stations", icon: QrCode },
  { label: "Print Agent", icon: Monitor },
  { label: "Pricing", icon: Wallet },
  { label: "Subscription", icon: ShieldCheck },
  { label: "Payments", icon: CreditCard },
  { label: "Customers", icon: Users },
  { label: "Reports", icon: Activity },
  { label: "Settings", icon: Settings }
];

const sectionGroups: { label: string; items: Section[] }[] = [
  { label: "DAILY WORK", items: ["Order Journey", "Overview", "Print Queue", "Customers"] },
  { label: "DEVICES & CONNECTIVITY", items: ["Printers", "QR Stations", "Print Agent"] },
  { label: "BUSINESS", items: ["Pricing", "Payments"] },
  { label: "INSIGHTS", items: ["Reports"] }
];


export default function App() {
  const route = resolvePortalRoute(window.location.pathname);
  const [dark, setDark] = useState(readDarkModePreference);
  const toggleTheme = () => setDark(value => !value);
  useEffect(() => {
    saveDarkModePreference(dark);
    applyThemePreference(dark);
  }, [dark]);
  useEffect(() => {
    const syncNativeTheme = (event: Event) => {
      const selected = (event as CustomEvent<string>).detail;
      if (selected === "light" || selected === "dark") setDark(selected === "dark");
    };
    window.addEventListener("smartprint-theme-change", syncNativeTheme);
    return () => window.removeEventListener("smartprint-theme-change", syncNativeTheme);
  }, []);
  const [tokens, setTokens] = useState<AuthTokens | null>(() =>
    route.kind === "admin" ? readStoredTokens("admin")
      : route.kind === "shop" ? readStoredTokens("shop")
      : null
  );

  if (route.kind === "photo-studio") {
    return <PrivatePhotoStudioEntry dark={dark} />;
  }

  if (route.kind === "station") {
    return <CustomerEntry publicCode={route.publicCode} dark={dark} onToggleTheme={toggleTheme} />;
  }

  if (route.kind === "public") {
    return <PublicLandingPage dark={dark} onToggleTheme={toggleTheme} />;
  }

  if (route.kind === "admin") {
    const onAdminSignOut = async () => {
      await storeTokens(null, "admin");
      setTokens(null);
    };
    if (!tokens) return <PlatformAdminAuthScreen onAuthenticated={setTokens} dark={dark} onToggleTheme={toggleTheme}/>;
    if (!isPlatformAdminSession(tokens)) {
      return <ScopeAccessDenied scope="platform administration" onSignOut={onAdminSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
    }
    return <PlatformAdminConsole tokens={tokens} onSignOut={onAdminSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
  }

  const onShopSignOut = async () => {
    await storeTokens(null, "shop");
    setTokens(null);
  };
  if (!tokens) return <AuthScreen onAuthenticated={setTokens} dark={dark} onToggleTheme={toggleTheme}/>;
  if (isPlatformAdminSession(tokens)) {
    return <ScopeAccessDenied scope="the shop workspace" onSignOut={onShopSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
  }
  return <ShopConsole tokens={tokens} onSignOut={onShopSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
}

function ScopeAccessDenied({ scope, onSignOut, dark, onToggleTheme }: { scope: string; onSignOut: () => Promise<void>; dark: boolean; onToggleTheme: () => void }) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [busy, setBusy] = useState(false);
  async function signOut() {
    setBusy(true);
    try { await onSignOut(); } finally { setBusy(false); }
  }
  return (
    <main className="auth-screen">
      <header className="auth-topbar">
        <BrandLockup className="auth-brand" />
        <div className="auth-top-actions"><ThemeToggleButton dark={dark} onToggle={onToggleTheme}/><LanguageSwitcher compact/><a className="auth-home-link" href="/">{t("Back to website")}</a></div>
      </header>
      <section className="auth-card" style={{ maxWidth: 520, margin: "12vh auto" }}>
        <div className="auth-card-heading"><div className="auth-card-icon"><ShieldCheck size={20}/></div><div><span>{t("APPLICATION SCOPE MISMATCH")}</span><h2>{t("Access not available")}</h2><p>{t("This signed-in account is not authorized for {scope}.",{scope})}</p></div></div>
        <button className="auth-submit" onClick={() => void signOut()} disabled={busy}>{busy ? <span className="button-spinner"/> : t("Sign out and use another account")} <ArrowRight size={16}/></button>
      </section>
    </main>
  );
}

function ShopConsole({ tokens, onSignOut, dark, onToggleTheme }: { tokens: AuthTokens; onSignOut: () => Promise<void>; dark: boolean; onToggleTheme: () => void }) {
  const { locale } = useLocale();
  const [active, setActive] = useState<Section>(() => shopSectionFromPath(window.location.pathname));

  useEffect(() => {
    const syncSectionFromPath = () => setActive(shopSectionFromPath(window.location.pathname));
    window.addEventListener("popstate", syncSectionFromPath);
    return () => window.removeEventListener("popstate", syncSectionFromPath);
  }, []);

  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(readSidebarCollapsedPreference);
  const [accountMenuOpen, setAccountMenuOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [ownerAccountOpen, setOwnerAccountOpen] = useState(false);
  const accountMenuRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!accountMenuOpen) return;
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!accountMenuRef.current?.contains(event.target as Node)) setAccountMenuOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setAccountMenuOpen(false);
    };
    document.addEventListener("pointerdown", closeOnOutsidePointer);
    window.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer);
      window.removeEventListener("keydown", closeOnEscape);
    };
  }, [accountMenuOpen]);
  const reloadInFlight = useRef(false);
  const desktopAgentSyncTimer = useRef<number | null>(null);
  const desktopAgentSyncInFlight = useRef(false);
  const desktopAgentSyncAttempts = useRef(0);
  const desktopConnectInFlight = useRef(false);
  const locallyAuthorizedAgentId = useRef<string | null>(null);
  const [localAgentStatus, setLocalAgentStatus] = useState<DesktopAgentStatus | null>(null);
  const [localAgentConflict, setLocalAgentConflict] = useState(false);
  const [localAgentRevoked, setLocalAgentRevoked] = useState(false);
  const [localAgentNeedsRepair, setLocalAgentNeedsRepair] = useState(false);
  const [localAgentEnrollmentIssue, setLocalAgentEnrollmentIssue] = useState<{ code: string | null; message: string } | null>(null);
  const localAgentEnrollmentIssueRef = useRef<{ code: string | null; message: string } | null>(null);
  const [localAgentOwnershipChecking, setLocalAgentOwnershipChecking] = useState(false);
  const [cloudStatus, setCloudStatus] = useState<"checking" | "connected" | "unavailable">("checking");
  const [profile, setProfile] = useState<ShopProfile | null>(null);
  const [printers, setPrinters] = useState<PrinterDevice[]>([]);
  const [stations, setStations] = useState<QrStation[]>([]);
  const [pricing, setPricing] = useState<ShopPricingProfile | null>(null);
  const [printJobs, setPrintJobs] = useState<ShopPrintJob[]>([]);
  const [journeyJobs, setJourneyJobs] = useState<ShopPrintJob[]>([]);
  const [journeyPayments, setJourneyPayments] = useState<ShopPayment[]>([]);
  const [journeyRefreshing, setJourneyRefreshing] = useState(false);
  const journeyRefreshInFlight = useRef(false);
  const [hasMoreQueueJobs, setHasMoreQueueJobs] = useState(false);
  const [loadingOlderQueueJobs, setLoadingOlderQueueJobs] = useState(false);
  const loadingOlderQueueJobsRef = useRef(false);
  const [queueApiOffset, setQueueApiOffset] = useState(0);
  const [queueSearch, setQueueSearch] = useState("");
  const queueSearchGeneration = useRef(0);
  const queuePageGeneration = useRef(0);
  const queueFirstPageIds = useRef<Set<string>>(new Set());
  const [agentDevices, setAgentDevices] = useState<AgentDevice[]>([]);
  const agentDevicesRef = useRef<AgentDevice[]>([]);
  agentDevicesRef.current = agentDevices;
  const [paymentSettings, setPaymentSettings] = useState<ShopPaymentSettings | null>(null);
  const [payments, setPayments] = useState<ShopPayment[]>([]);
  const [refunds, setRefunds] = useState<ShopRefund[]>([]);
  const [subscription, setSubscription] = useState<ShopSubscriptionSummary | null>(null);
  const [apiConnectionState, setApiConnectionState] = useState<"checking" | "connected" | "degraded" | "offline">("checking");
  const [enrollmentCode, setEnrollmentCode] = useState<{ code: string; expiresUtc: string } | null>(null);
  const [agentBusy, setAgentBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [modal, setModal] = useState<Modal>(null);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState("");
  const [preview, setPreview] = useState<{ name: string; url: string; blob: Blob; mediaType: string; job: ShopPrintJob; readOnly: boolean; isSavedOutput?: boolean } | null>(null);
  const [focusShopPhotoEdit,setFocusShopPhotoEdit]=useState(false);
  const [shopPhotoStudio,setShopPhotoStudio]=useState<{
    source:File;
    job:ShopPrintJob;
    initialSettings?:Partial<PhotoStudioEditorSettings>;
    returnPreview?:{name:string;blob:Blob;mediaType:string;job:ShopPrintJob;readOnly:boolean};
  }|null>(null);
  const [shopPhotoOrderOpen,setShopPhotoOrderOpen]=useState(false);
  const [shopPhotoOrderDraft,setShopPhotoOrderDraft]=useState<ShopPhotoOrderDraft|null>(null);
  const [shopPhotoOrderReceipt,setShopPhotoOrderReceipt]=useState<Awaited<ReturnType<typeof createShopPhotoJob>>|null>(null);
  const [refundJob, setRefundJob] = useState<ShopPrintJob | null>(null);
  const [batchReviewJobs, setBatchReviewJobs] = useState<ShopPrintJob[] | null>(null);
  const [batchReviewDecisions, setBatchReviewDecisions] = useState<Record<string, boolean>>({});
  const [batchReviewRetry, setBatchReviewRetry] = useState(false);
  const [batchReviewRetryIds, setBatchReviewRetryIds] = useState<string[]>([]);
  const [batchReviewLoadingId, setBatchReviewLoadingId] = useState<string | null>(null);
  type ActionDialogState = { title: string; message: string; inputLabel?: string; confirmLabel: string; resolve: (value: string | null) => void };
  const [actionDialog, setActionDialog] = useState<ActionDialogState | null>(null);
  const [actionDialogValue, setActionDialogValue] = useState("");
  const [busyJobId, setBusyJobId] = useState<string | null>(null);
  const [busyResourceId, setBusyResourceId] = useState<string | null>(null);
  const [eventHistory, setEventHistory] = useState<{ job: ShopPrintJob; events: ShopJobEvent[] } | null>(null);

  async function reload() {
    if (reloadInFlight.current) return;
    queueSearchGeneration.current++;
    queuePageGeneration.current++;
    reloadInFlight.current = true;
    setApiConnectionState("checking");
    setLoading(true);
    setError("");
    try {
      const [snapshot, unfilteredJourneyJobs] = await Promise.all([
        loadShopData(tokens.accessToken, queueSearch),
        queueSearch.trim()
          ? getShopPrintJobsPage(tokens.accessToken, 0, 100)
          : Promise.resolve(null)
      ]);
      setProfile(snapshot.profile);
      setPrinters(snapshot.printers);
      setStations(snapshot.stations);
      setPricing(snapshot.pricing);
      setPrintJobs(snapshot.printJobs);
      setJourneyJobs(unfilteredJourneyJobs ?? snapshot.printJobs);
      setJourneyPayments(snapshot.payments);
      queueFirstPageIds.current = new Set(snapshot.printJobs.map(job => job.id));
      setHasMoreQueueJobs(snapshot.printJobs.length === 100);
      setQueueApiOffset(snapshot.printJobs.length);
      agentDevicesRef.current = snapshot.agentDevices;
      setAgentDevices(snapshot.agentDevices);
      setPaymentSettings(snapshot.paymentSettings);
      setPayments(snapshot.payments);
      setRefunds(snapshot.refunds);
      setSubscription(snapshot.subscription);
      setApiConnectionState("connected");
      setCloudStatus("connected");
      if (isDesktopApp()) {
        // The Windows service may still be starting after installation or reboot.
        // Keep discovery off the initial shop-data load and retry quietly.
        scheduleDesktopAgentSync(0);
      }
    } catch (exception) {
      setCloudStatus("unavailable");
      if (exception instanceof ApiError && exception.status === 401) {
        storeTokens(null);
        try { await onSignOut(); }
        catch (signOutError) { setError(signOutError instanceof Error ? signOutError.message : t("Could not clear the secure desktop session.")); }
      } else {
        handleShopApiError(exception, "Could not load your shop.");
      }
    } finally {
      setLoading(false);
      reloadInFlight.current = false;
    }
  }

  function handleShopApiError(exception: unknown, fallback: string) {
    if (exception instanceof ApiError && exception.status === 401) {
      storeTokens(null);
      onSignOut();
      return;
    }
    const status = exception instanceof ApiError ? exception.status : 0;
    setApiConnectionState(status === 0 ? "offline" : status >= 500 ? "degraded" : "connected");
    setError(exception instanceof Error ? t(exception.message) : fallback);
  }

  async function refreshPrintQueue() {
    const generation = queuePageGeneration.current;
    try {
      const [latest, latestPrinters, latestAgents] = await Promise.all([
        getShopPrintJobsPage(tokens.accessToken, 0, 100, queueSearch),
        getShopPrinters(tokens.accessToken),
        getShopAgentDevices(tokens.accessToken)
      ]);
      if (generation !== queuePageGeneration.current) return;
      setApiConnectionState("connected");
      setPrinters(latestPrinters);
      setAgentDevices(latestAgents);
      const latestIds = new Set(latest.map(job => job.id));
      const firstPageMembershipChanged = latest.length !== queueFirstPageIds.current.size ||
        latest.some(job => !queueFirstPageIds.current.has(job.id));
      if (firstPageMembershipChanged) {
        // Any insert/removal shifts every offset page. Reset pagination to the
        // current first page so no older records are skipped or duplicated.
        queuePageGeneration.current++;
        queueFirstPageIds.current = latestIds;
        setPrintJobs(latest);
        setQueueApiOffset(latest.length);
        setHasMoreQueueJobs(latest.length === 100);
        return;
      }
      setPrintJobs(current => {
        const byId = new Map(current.map(job => [job.id, job]));
        for (const job of latest) byId.set(job.id, job);
        return Array.from(byId.values()).sort((left, right) =>
          Date.parse(right.createdUtc) - Date.parse(left.createdUtc) || right.id.localeCompare(left.id)
        );
      });
      setHasMoreQueueJobs(latest.length === 100 || printJobs.length > latest.length);
    } catch (exception) {
      if (generation !== queuePageGeneration.current) return;
      handleShopApiError(exception, "Could not refresh the print queue.");
    }
  }

  async function loadOlderQueueJobs() {
    if (loadingOlderQueueJobsRef.current || !hasMoreQueueJobs) return;
    loadingOlderQueueJobsRef.current = true;
    const generation = queuePageGeneration.current;
    const offset = queueApiOffset;
    setLoadingOlderQueueJobs(true);
    try {
      const older = await getShopPrintJobsPage(tokens.accessToken, offset, 100, queueSearch);
      if (generation !== queuePageGeneration.current) return;
      setPrintJobs(current => {
        const knownIds = new Set(current.map(job => job.id));
        const merged = [...current, ...older.filter(job => !knownIds.has(job.id))];
        return merged.sort((left, right) =>
          Date.parse(right.createdUtc) - Date.parse(left.createdUtc) || right.id.localeCompare(left.id)
        );
      });
      setQueueApiOffset(offset => offset + older.length);
      setHasMoreQueueJobs(older.length === 100);
    } catch (exception) {
      if (generation !== queuePageGeneration.current) return;
      handleShopApiError(exception, "Could not load older print jobs.");
    } finally {
      loadingOlderQueueJobsRef.current = false;
      setLoadingOlderQueueJobs(false);
    }
  }

  async function searchPrintQueue(value: string) {
    const normalized = value.trim().slice(0, 120);
    const generation = ++queueSearchGeneration.current;
    queuePageGeneration.current++;
    setQueueSearch(normalized);
    try {
      const matches = await getShopPrintJobsPage(tokens.accessToken, 0, 100, normalized);
      if (generation !== queueSearchGeneration.current) return;
      queueFirstPageIds.current = new Set(matches.map(job => job.id));
      setPrintJobs(matches);
      setQueueApiOffset(matches.length);
      setHasMoreQueueJobs(matches.length === 100);
    } catch (exception) {
      if (generation !== queueSearchGeneration.current) return;
      handleShopApiError(exception, "Could not search the print queue.");
    }
  }

  async function refreshOrderJourney() {
    if (journeyRefreshInFlight.current) return;
    journeyRefreshInFlight.current = true;
    setJourneyRefreshing(true);
    try {
      const [latestJobs, latestPayments] = await Promise.all([
        getShopPrintJobsPage(tokens.accessToken, 0, 100),
        getShopPayments(tokens.accessToken)
      ]);
      setJourneyJobs(latestJobs);
      setJourneyPayments(latestPayments);
      setApiConnectionState("connected");
    } catch (exception) {
      handleShopApiError(exception, "Could not refresh the order journey.");
    } finally {
      journeyRefreshInFlight.current = false;
      setJourneyRefreshing(false);
    }
  }

  function requestActionDialog(options: { title: string; message: string; inputLabel?: string; confirmLabel?: string }): Promise<string | null> {
    return new Promise(resolve => {
      setActionDialogValue("");
      setActionDialog({ ...options, confirmLabel: options.confirmLabel || "Confirm", resolve });
    });
  }

  async function confirmInApp(message: string, title = t("Please confirm"), confirmLabel = t("Confirm")): Promise<boolean> {
    return (await requestActionDialog({ title, message, confirmLabel })) !== null;
  }

  async function promptInApp(message: string, title = t("Enter details"), inputLabel = t("Reason")): Promise<string | null> {
    return requestActionDialog({ title, message, inputLabel, confirmLabel: "Continue" });
  }

  function closeActionDialog() {
    const current = actionDialog;
    setActionDialog(null);
    current?.resolve(null);
  }

  function submitActionDialog() {
    const current = actionDialog;
    setActionDialog(null);
    current?.resolve(actionDialogValue);
  }

  function toggleSidebarCollapsed() {
    setSidebarCollapsed(current => {
      const next = !current;
      saveSidebarCollapsedPreference(next);
      return next;
    });
  }

  useEffect(() => {
    void reload();
    // A token change is the only trigger; mutations call reload explicitly.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tokens.accessToken]);

  useEffect(() => () => {
    if (desktopAgentSyncTimer.current !== null) {
      window.clearTimeout(desktopAgentSyncTimer.current);
      desktopAgentSyncTimer.current = null;
    }
  }, []);

  useEffect(() => {
    if (active === "Print Queue") return;
    let mounted = true;
    let inFlight = false;
    const refreshDevices = async () => {
      if (inFlight) return;
      inFlight = true;
      try {
        const [latestPrinters, latestAgents] = await Promise.all([
          getShopPrinters(tokens.accessToken),
          getShopAgentDevices(tokens.accessToken)
        ]);
        if (mounted) {
          setPrinters(latestPrinters);
          setAgentDevices(latestAgents);
          setApiConnectionState("connected");
        }
      } catch (exception) {
        if (mounted) {
          if (exception instanceof ApiError && exception.status === 401) {
            storeTokens(null);
            onSignOut();
          } else {
            const status = exception instanceof ApiError ? exception.status : 0;
            setApiConnectionState(status === 0 ? "offline" : status >= 500 ? "degraded" : "connected");
          }
        }
      } finally {
        inFlight = false;
      }
    };
    void refreshDevices();
    const timer = window.setInterval(() => void refreshDevices(), 15_000);
    return () => {
      mounted = false;
      window.clearInterval(timer);
    };
  }, [active, tokens.accessToken]);

  useEffect(() => {
    if (active !== "Order Journey") return;
    void refreshOrderJourney();
    const timer = window.setInterval(() => void refreshOrderJourney(), 15_000);
    return () => window.clearInterval(timer);
    // The refresh callback is scoped to the active shop session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, tokens.accessToken]);

  const activeAgentCount = agentDevices.filter(agent => !agent.revokedUtc).length;
  const onlineAgentIds = new Set(agentDevices.filter(agent =>
    !agent.revokedUtc && agent.status === "Online" && !!agent.lastSeenUtc &&
    Date.parse(agent.lastSeenUtc) > Date.now() - 2 * 60 * 1000
  ).map(agent => agent.id));
  const onlineAgentCount = onlineAgentIds.size;
  const usablePrinterCount = printers.filter(printer => isPrinterAvailableForShop(printer, agentDevices)).length;
  const offlinePrinterCount = printers.filter(printer =>
    printer.isActive && !isPrinterAvailableForShop(printer, agentDevices)
  ).length;
  const today = new Intl.DateTimeFormat("en-IN", { timeZone: IST_TIME_ZONE, weekday: "long", day: "numeric", month: "long" }).format(new Date());

  async function signOut() {
    try { await logout(tokens.accessToken); } catch { /* Local sign-out still clears the session. */ }
    try { await onSignOut(); }
    catch (exception) { setError(exception instanceof Error ? t(exception.message) : t("Could not clear the secure desktop session.")); }
  }

  async function submitStation(values: { displayName: string; printerDeviceId?: string }) {
    setBusy(true);
    try {
      const created = await createStation(tokens.accessToken, values);
      setStations(current => [...current, created]);
      setModal(null);
      setToast("QR station created. Save or print its QR code now.");
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not create QR station."));
    } finally {
      setBusy(false);
    }
  }

  async function togglePrinter(printer: PrinterDevice, replacePrinterId?: string): Promise<boolean> {
    if (busyResourceId) return false;
    const replacedPrinter = replacePrinterId
      ? printers.find(candidate => candidate.id === replacePrinterId)
      : undefined;
    if (replacePrinterId && !replacedPrinter) {
      setError(t("The selected printer list changed. Refresh and choose the replacement again."));
      return false;
    }
    // The PrintersPage replacement dialog is the explicit owner confirmation.
    // Do not ask for a second confirmation after that dialog has already been accepted.
    if (printer.isActive && !(await confirmInApp(
      `Deselect ${printer.displayName}? New jobs will no longer be assigned to it. Existing Windows output is not cancelled; an active or uncertain job must be resolved first.`,
      "Deselect this printer?", "Deselect printer"
    ))) return false;

    setBusyResourceId(printer.id);
    setError("");
    try {
      await updatePrinterActive(tokens.accessToken, printer.id, !printer.isActive, replacePrinterId);
      setToast(replacedPrinter
        ? `${printer.displayName} selected instead of ${replacedPrinter.displayName}. QR station defaults were updated; review pending orders for reassignment.`
        : printer.isActive
          ? t("Printer deselected. Existing files and Windows output were not deleted.")
          : t("Printer selected for new assignments."));
      await reload();
      return true;
    } catch (exception) {
      if (exception instanceof ApiError && exception.status === 409) {
        try { await reload(); } catch { /* Keep the original action error visible. */ }
      }
      setError(exception instanceof Error ? t(exception.message) : t("Could not update printer."));
      return false;
    } finally {
      setBusyResourceId(null);
    }
  }

  async function toggleStation(station: QrStation) {
    if (busyResourceId) return;
    if (station.isEnabled && !(await confirmInApp(
      `Disable the QR station “${station.displayName}”? Customers will not be able to start new sessions from this QR until you enable it again. Previously started customer sessions are not recalled.`,
      "Disable this QR station?", "Disable station"
    ))) return;
    setBusyResourceId(station.id);
    setError("");
    try {
      const updated = await updateStationEnabled(tokens.accessToken, station.id, !station.isEnabled);
      setStations(current => current.map(item => item.id === station.id ? { ...item, isEnabled: updated.isEnabled } : item));
      setToast(updated.isEnabled ? t("QR station enabled.") : t("QR station disabled. Existing customer sessions are unchanged."));
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not update QR station."));
    } finally {
      setBusyResourceId(null);
    }
  }

  async function createFreshStationCode(station: QrStation) {
    if (busyResourceId) return;
    const confirmed = await confirmInApp("Generating a replacement QR will invalidate every previously printed sign for this station. Continue only when you intend to replace them.", "Replace this station's QR?", "Replace QR");
    if (!confirmed) return;
    setBusyResourceId(station.id);
    setError("");
    try {
      const result = await rotateStationCode(tokens.accessToken, station.id);
      setStations(current => current.map(item => item.id === station.id
        ? { ...item, publicUrl: result.publicUrl, publicCodeHint: result.publicCodeHint }
        : item));
      setToast("New QR code generated. The previous code is now invalid.");
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not generate a QR code."));
    } finally {
      setBusyResourceId(null);
    }
  }

  async function generateAgentCode() {
    setAgentBusy(true);
    setError("");
    setEnrollmentCode(null);
    try {
      const code = await createAgentEnrollmentCode(tokens.accessToken);
      setEnrollmentCode(code);
      setToast("One-time pairing code generated. It expires in 10 minutes.");
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not generate an agent pairing code."));
    } finally {
      setAgentBusy(false);
    }
  }

  async function connectLocalComputer(
    silent = false,
    knownAgents: AgentDevice[] = agentDevicesRef.current,
    allowPreviousShopReconnection = false
  ) {
    if (desktopConnectInFlight.current) return;
    desktopConnectInFlight.current = true;
    if (!silent) {
      setAgentBusy(true);
      setError("");
      localAgentEnrollmentIssueRef.current = null;
      setLocalAgentEnrollmentIssue(null);
    }
    try {
      const status = await getLocalAgentStatus();
      setLocalAgentStatus(status);
      setLocalAgentConflict(false);
      setLocalAgentOwnershipChecking(false);
      setLocalAgentRevoked(status.status === "REVOKED");
      setLocalAgentNeedsRepair(status.status === "IDENTITY_UNAVAILABLE");

      if (!status.serviceReachable) {
        if (!silent) setError(t("The SmartPrint background service is not responding. Restart Windows or repair SmartPrint, then retry."));
        return;
      }

      if (status.paired) {
        const currentShopDevice = knownAgents.find(agent =>
          !agent.revokedUtc && agent.id === status.agentDeviceId
        );
        const revokedCurrentShopDevice = knownAgents.find(agent =>
          agent.revokedUtc && agent.id === status.agentDeviceId
        );
        if (!currentShopDevice) {
          if (revokedCurrentShopDevice) {
            // This shop revoked this exact device. Only an explicit reconnect
            // action may create a fresh authorization; background sync never does.
            setLocalAgentRevoked(true);
            if (silent) return;
          } else {
            setLocalAgentConflict(true);
            if (!allowPreviousShopReconnection) {
              if (!silent) setError(t("This computer is still paired with another shop. Its owner must revoke it before you can connect it here."));
              return;
            }

            const confirmed = await confirmInApp(
              "Continue only after the previous shop owner has revoked this computer from that shop's Print Agent page. SmartPrint will verify the revocation with the server before changing this computer's connection.",
              "Reconnect this computer?",
              "Check and reconnect"
            );
            if (!confirmed) return;
          }
        } else {
          setLocalAgentRevoked(false);
          setToast("This computer is already connected to your shop.");
          return;
        }
      }

      const authorization = await createAgentEnrollmentCode(tokens.accessToken);
      const enrolled = await enrollLocalAgent(authorization.code);
      if (!enrolled.success) {
        const message = enrolled.message || "This computer could not be connected to the shop.";
        if (enrolled.code) throw new ApiError(message, 409, enrolled.code);
        throw new Error(message);
      }
      locallyAuthorizedAgentId.current = enrolled.agentDeviceId;
      let postEnrollmentStatus = status;
      try {
        // Read back the local listener state after enrollment. Failure here must
        // not undo a successful cloud pairing or interrupt the owner's workflow.
        postEnrollmentStatus = await getLocalAgentStatus();
      } catch {
        // Keep the last known listener status; the background health loop retries.
      }
      setLocalAgentStatus({
        serviceReachable: true,
        localApiReachable: postEnrollmentStatus.localApiReachable,
        apiBaseUrl: postEnrollmentStatus.apiBaseUrl,
        paired: true,
        agentDeviceId: enrolled.agentDeviceId,
        status: "CONNECTED"
      });
      setLocalAgentConflict(false);
      setLocalAgentRevoked(false);
      setLocalAgentNeedsRepair(false);
      localAgentEnrollmentIssueRef.current = null;
      setLocalAgentEnrollmentIssue(null);
      setLocalAgentOwnershipChecking(false);

      // Enrollment creates the AgentDevice in the cloud immediately. Refresh its
      // shop-scoped record so the UI recognizes this computer as connected on the
      // first run instead of waiting for the next manual reload.
      try {
        const refreshedSnapshot = await loadShopData(tokens.accessToken);
        agentDevicesRef.current = refreshedSnapshot.agentDevices;
        setAgentDevices(refreshedSnapshot.agentDevices);
        setPrinters(refreshedSnapshot.printers);
      } catch {
        // Keep the successful local enrollment state; the next normal refresh
        // will synchronize the cloud inventory without enrolling a second time.
      }

      setToast("This computer is connected. SmartPrint is finding its printers.");
      if (!silent) await reload();
    } catch (exception) {
      if (exception instanceof ApiError && exception.status === 401) {
        try { await onSignOut(); }
        catch (signOutError) { setError(signOutError instanceof Error ? signOutError.message : t("Could not clear the secure desktop session.")); }
      } else if (exception instanceof ApiError && exception.status === 409) {
        const issue = { code: exception.code, message: exception.message };
        localAgentEnrollmentIssueRef.current = issue;
        setLocalAgentEnrollmentIssue(issue);
        setLocalAgentConflict(exception.code === "agent_already_enrolled");
        setLocalAgentRevoked(false);
        setLocalAgentNeedsRepair(false);
        setLocalAgentOwnershipChecking(false);
        if (!silent) setError(t(exception.message));
      } else if (!silent) {
        const message = exception instanceof Error ? exception.message : "Could not connect this computer to the shop.";
        setError(message);
      }
    } finally {
      desktopConnectInFlight.current = false;
      if (!silent) setAgentBusy(false);
    }
  }

  function nextDesktopAgentRetryDelay(): number {
    const exponent = Math.min(desktopAgentSyncAttempts.current, 4);
    desktopAgentSyncAttempts.current += 1;
    return Math.min(2_000 * (2 ** exponent), 30_000);
  }

  function scheduleDesktopAgentSync(delayMs: number): void {
    if (!isDesktopApp()) return;
    if (desktopAgentSyncTimer.current !== null) {
      window.clearTimeout(desktopAgentSyncTimer.current);
    }
    desktopAgentSyncTimer.current = window.setTimeout(() => {
      desktopAgentSyncTimer.current = null;
      void synchronizeDesktopAgent();
    }, delayMs);
  }

  async function synchronizeDesktopAgent(): Promise<void> {
    if (!isDesktopApp() || desktopAgentSyncInFlight.current) return;
    desktopAgentSyncInFlight.current = true;
    try {
      const status = await getLocalAgentStatus();
      setLocalAgentStatus(status);

      if (!status.serviceReachable) {
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
        return;
      }

      // A server-revoked identity is intentionally not auto-enrolled again.
      // The shop owner can reconnect it with one explicit click, without typing a code.
      if (status.status === "REVOKED") {
        setLocalAgentRevoked(true);
        setLocalAgentNeedsRepair(false);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(60_000);
        return;
      }

      if (status.status === "IDENTITY_UNAVAILABLE") {
        // Don't create endless one-time enrollment codes while the old cloud
        // device may still be active. Let the owner review/revoke it first.
        setLocalAgentNeedsRepair(true);
        setLocalAgentRevoked(false);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(60_000);
        return;
      }

      if (localAgentEnrollmentIssueRef.current) {
        // The API has already explained why enrollment cannot proceed. Do not
        // keep creating short-lived one-time authorizations in the background.
        setLocalAgentEnrollmentIssue(localAgentEnrollmentIssueRef.current);
        setLocalAgentConflict(localAgentEnrollmentIssueRef.current.code === "agent_already_enrolled");
        setLocalAgentOwnershipChecking(false);
        return;
      }

      setLocalAgentNeedsRepair(false);
      if (status.paired && status.agentDeviceId) {
        let deviceRecord = agentDevicesRef.current.find(agent =>
          agent.id === status.agentDeviceId
        );

        if (deviceRecord?.revokedUtc) {
          setLocalAgentRevoked(true);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(false);
          scheduleDesktopAgentSync(60_000);
          return;
        }

        if (!deviceRecord) {
          // Verify cloud ownership before declaring another-shop pairing.
          setLocalAgentOwnershipChecking(true);
          try {
            const refreshedSnapshot = await loadShopData(tokens.accessToken);
            agentDevicesRef.current = refreshedSnapshot.agentDevices;
            setAgentDevices(refreshedSnapshot.agentDevices);
            setPrinters(refreshedSnapshot.printers);
            deviceRecord = refreshedSnapshot.agentDevices.find(agent =>
              agent.id === status.agentDeviceId
            );
          } catch {
            scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
            return;
          }
        }

        if (deviceRecord?.revokedUtc) {
          setLocalAgentRevoked(true);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(false);
          scheduleDesktopAgentSync(60_000);
          return;
        }

        if (!deviceRecord && locallyAuthorizedAgentId.current === status.agentDeviceId) {
          // Enrollment succeeded but its shop record has not appeared yet.
          // Keep checking; do not mislabel it as another shop or re-enroll it.
          setLocalAgentRevoked(false);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(true);
          scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
          return;
        }

        const belongsToCurrentShop = Boolean(deviceRecord && !deviceRecord.revokedUtc);
        setLocalAgentOwnershipChecking(false);
        setLocalAgentRevoked(false);
        setLocalAgentConflict(!belongsToCurrentShop);
        if (belongsToCurrentShop) {
          desktopAgentSyncAttempts.current = 0;
          scheduleDesktopAgentSync(15_000);
        } else {
          // Never move a computer across shops without the previous owner revoking it.
          scheduleDesktopAgentSync(60_000);
        }
        return;
      }

      setLocalAgentRevoked(false);
      setLocalAgentConflict(false);
      setLocalAgentOwnershipChecking(false);
      await connectLocalComputer(true, agentDevicesRef.current);
      const verifiedStatus = await getLocalAgentStatus();
      setLocalAgentStatus(verifiedStatus);

      if (verifiedStatus.status === "REVOKED") {
        setLocalAgentRevoked(true);
        setLocalAgentNeedsRepair(false);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(60_000);
        return;
      }

      if (verifiedStatus.serviceReachable && verifiedStatus.paired && verifiedStatus.agentDeviceId) {
        const verifiedRecord = agentDevicesRef.current.find(agent =>
          agent.id === verifiedStatus.agentDeviceId
        );
        if (verifiedRecord?.revokedUtc) {
          setLocalAgentRevoked(true);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(false);
          scheduleDesktopAgentSync(60_000);
          return;
        }
        if (verifiedRecord && !verifiedRecord.revokedUtc) {
          setLocalAgentRevoked(false);
          setLocalAgentOwnershipChecking(false);
          setLocalAgentConflict(false);
          desktopAgentSyncAttempts.current = 0;
          scheduleDesktopAgentSync(15_000);
          return;
        }
        if (locallyAuthorizedAgentId.current === verifiedStatus.agentDeviceId) {
          setLocalAgentRevoked(false);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(true);
          scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
          return;
        }
        setLocalAgentRevoked(false);
        setLocalAgentConflict(true);
        setLocalAgentOwnershipChecking(false);
      }

      scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
    } catch {
      // Startup, network and temporary pipe failures recover without owner action.
      scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
    } finally {
      desktopAgentSyncInFlight.current = false;
    }
  }

  async function revokeAgent(agent: AgentDevice) {
    if (!(await confirmInApp(`Revoke the Print Agent on ${agent.deviceName}? It will no longer be able to report printers or connect to this shop.`, "Revoke Print Agent", "Revoke agent"))) return;
    setAgentBusy(true);
    setError("");
    try {
      await revokeAgentDevice(tokens.accessToken, agent.id);
      if (localAgentStatus?.agentDeviceId === agent.id) {
        setLocalAgentRevoked(true);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
      }
      setToast("Agent access revoked.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not revoke this agent."));
    } finally {
      setAgentBusy(false);
    }
  }



  async function assignPrinter(job: ShopPrintJob, printerDeviceId: string) {
    if (!printerDeviceId || printerDeviceId === job.printerDeviceId) return;
    setBusyJobId(job.id);
    setError("");
    try {
      const assignment=await assignShopPrintJobPrinter(tokens.accessToken, job.id, printerDeviceId);
      setToast(`${assignment.printerName} assigned to ${assignment.assignedJobCount} file(s) in this batch. The paired computer can now receive the documents.`);
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not assign this printer."));
    } finally {
      setBusyJobId(null);
    }
  }


  async function updatePrintJobSettings(job:ShopPrintJob,settings:CustomerPrintRequestSettings) {
    setBusyJobId(job.id);
    setError("");
    try {
      const revised=await updateShopPrintJobSettings(tokens.accessToken,job.id,settings);
      setToast(`Revised quote ${formatMoney(revised.quotedAmountMinor,revised.currency)} sent to the customer for approval. Printing remains paused until approval and release.`);
      await reload();
    } catch(exception) {
      setError(exception instanceof Error?t(exception.message):t("Could not revise this print request."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function dispatchPrintJob(job: ShopPrintJob) {
    const pdfExport = job.outputMode === "PDF_EXPORT";
    const virtualTestOutput = job.outputMode === "UNSUPPORTED_VIRTUAL";
    const privateTestOutput = Boolean(job.privatePrinting && pdfExport);
    const ok = await confirmInApp(virtualTestOutput
      ? `Run a non-production virtual printer test for ${job.jobNumber} on ${job.printerName || "the assigned virtual queue"}? It uses the existing Windows spooler pipeline. The result is recorded as test output, not physical fulfilment or a collection-ready print.`
      : privateTestOutput
        ? `Create a non-production Private Printing test PDF for ${job.jobNumber}? The Agent stores it in its protected test folder for up to 24 hours. This is not a physical print and cannot be collected as a printed order.`
        : pdfExport
          ? `Save a PDF for ${job.jobNumber} in the shop's QRtoPrint Exports folder? No Windows printer dialog will be opened.`
          : `Start printing ${job.jobNumber} on ${job.printerName || "the assigned printer"}? SmartPrint will send this job once. Please check the actual pages before marking the order complete.`,
      virtualTestOutput ? "Run virtual printer test?" : privateTestOutput ? "Create private test PDF?" : pdfExport ? "Save PDF export?" : "Start printing?",
      virtualTestOutput ? "Run virtual test" : privateTestOutput ? "Create test PDF" : pdfExport ? "Save PDF" : "Start printing");
    if (!ok) return;
    setBusyJobId(job.id); setError("");
    try {
      await dispatchShopPrintJob(tokens.accessToken, job.id);
      setToast(virtualTestOutput
        ? t("Virtual printer test authorised. The existing Windows spooler pipeline will run; no physical fulfilment will be recorded.")
        : privateTestOutput
          ? t("Private test PDF authorised. The Agent will save a protected local test artifact; no physical print is recorded.")
          : pdfExport
            ? "PDF export authorised. The Windows Agent will create the PDF in the shop's export folder."
            : "Print authorised. Waiting for the shop computer to receive this job; SmartPrint will update the order automatically.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not start printing."));
    } finally { setBusyJobId(null); }
  }

  async function retryPdfExport(job: ShopPrintJob) {
    const privateTestOutput = Boolean(job.privatePrinting && job.outputType === "TEST_VIRTUAL");
    const ok = await confirmInApp(
      privateTestOutput
        ? `Confirm that no valid test PDF was generated for ${job.jobNumber}? The Agent will retry from its protected local copy and save only to its protected test folder. Do not continue if the test PDF already exists; this is not a physical print.`
        : `Confirm that no PDF file was saved for ${job.jobNumber}? The Agent will create the PDF in the shop's QRtoPrint Exports folder. Do not continue if a file from this job already exists.`,
      privateTestOutput ? "Retry private test PDF?" : "Retry PDF export?",
      privateTestOutput ? "Retry test PDF" : "Retry export"
    );
    if (!ok) return;
    setBusyJobId(job.id);
    setError("");
    try {
      if (privateTestOutput) await retryShopPrivateTestOutput(tokens.accessToken, job.id);
      else await retryShopPdfExport(tokens.accessToken, job.id);
      await dispatchShopPrintJob(tokens.accessToken, job.id);
      setToast(privateTestOutput
        ? "Private test PDF retry authorised. The Agent will save a protected local test artifact; no physical print is made."
        : "PDF export retry authorised. The Windows Agent will save the PDF locally.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not retry this PDF export."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function confirmPrintedOutput(job: ShopPrintJob) {
    if (hasIncompleteSpoolerPageProgress(job.spoolerPagesPrinted,job.spoolerTotalPages)) {
      setError(t("Windows recorded {printed} of {total} pages for {job}. Finish and reconcile the remaining pages separately before marking this file complete.").replace("{printed}", String(job.spoolerPagesPrinted??0)).replace("{total}", String(job.spoolerTotalPages)).replace("{job}", job.jobNumber));
      return;
    }
    const manualQueueReconciliation=canManuallyReconcileUncertainOutput(
      job.status, job.spoolerStatus, job.failureCode, job.spoolerJobId, job.queueName, job.spoolerJobName
    );
    const queueCheck=manualQueueReconciliation
      ? `The Agent restarted before Windows returned a spooler ID. Open the ${job.queueName} Windows queue and verify that the exact job "${job.spoolerJobName}" is not queued or printing. Then physically inspect every requested paper page. Only continue after both checks are complete.`
      : `Have you physically checked all requested pages for ${job.jobNumber}? Only confirm after checking the actual paper output.`;
    const ok = await confirmInApp(
      queueCheck,
      manualQueueReconciliation ? t("Manually reconcile print output") : t("Confirm paper output"),
      manualQueueReconciliation ? "I checked the queue and paper" : "Confirm output"
    );
    if (!ok) return;
    setBusyJobId(job.id); setError("");
    try {
      await confirmShopPrintOutput(tokens.accessToken, job.id, manualQueueReconciliation);
      setToast(manualQueueReconciliation
        ? "Output manually reconciled after checking the Windows queue and physical pages."
        : t("Physical output confirmed by the shop operator."));
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not confirm physical output."));
    } finally { setBusyJobId(null); }
  }


  async function resumeUnprintedBatch(batchId:string,jobIds:string[]):Promise<boolean>{
    if(!jobIds.length||busyJobId)return false;
    const selectedJobs=jobIds.map(id=>journeyJobs.find(job=>job.id===id));
    const resolvedJobs=selectedJobs.filter((job):job is ShopPrintJob=>job!==undefined);
    if(resolvedJobs.length!==jobIds.length){
      setError(t("The selected files changed in the latest order refresh. Refresh the order and select the files again."));
      try{await reload();}catch{/* Keep the stale-selection message visible. */}
      return false;
    }
    let privateFileIndex=0;
    const labels=resolvedJobs.map(job=>job.privatePrinting
      ?`Private file ${++privateFileIndex}`
      :job.document.originalFileName);
    const virtualTestOutput=resolvedJobs.some(job=>job.outputMode==="UNSUPPORTED_VIRTUAL"&&job.outputType==="TEST_VIRTUAL");
    const ok=await confirmInApp(
      virtualTestOutput
        ? `Only continue after verifying that each exact job is absent from the ORIGINAL virtual Windows queue and that no test output artifact was created. Selected files: ${labels.join("; ")}. Any file with recorded output or an unknown page count must not be retried here.`
        : `Only continue after checking the ORIGINAL Windows queue and physical output tray for every selected file and verifying that none produced paper. Selected files: ${labels.join("; ")}. Any file that printed even one page must not be retried here.`,
      virtualTestOutput ? "Confirm no virtual test output was produced" : "Confirm no physical pages were produced",
      virtualTestOutput ? "I checked; retry virtual tests" : "I checked; resume selected files"
    );
    if(!ok)return false;
    setBusyJobId(`resume-unprinted-${batchId}`);
    setError("");
    try{
      const result=await resumeShopUnprintedBatch(tokens.accessToken,batchId,jobIds);
      setToast(virtualTestOutput
        ? `${result.resumedCount} virtual test job(s) authorised to retry on ${result.printerName}.`
        : `${result.resumedCount} verified-unprinted file(s) authorised to resume on ${result.printerName}.`);
      try{
        await reload();
      }catch{
        setError(t("The resume was authorised, but the order view could not refresh. Use Refresh to check the latest status; do not submit the same files again."));
      }
      // The server has committed the authorisation. Return success even when
      // refreshing the view fails, so the selected files cannot be submitted twice.
      return true;
    }catch(exception){
      setError(exception instanceof Error?t(exception.message):t("Could not resume the selected files."));
      try{await reload();}catch{/* Preserve the original action error. */}
      return false;
    }finally{
      setBusyJobId(null);
    }
  }

  async function savePaymentSettings(values: {
    upiEnabled: boolean; upiId: string | null; payeeName: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean;
  }) {
    setBusy(true);
    setError("");
    try {
      const updated = await updateShopPaymentSettings(tokens.accessToken, values);
      setPaymentSettings(updated);
      setToast("Shop payment settings saved. Customer print revenue continues to go directly to your shop.");
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not save shop payment settings."));
    } finally {
      setBusy(false);
    }
  }

  async function verifyPayment(payment: ShopPayment) {
    const isBatch = Boolean(payment.batchPaymentId);
    const confirmation = payment.method === "UPI"
      ? isBatch
        ? `Have you independently confirmed the full batch UPI amount in your shop's bank/UPI app? Reference: ${payment.paymentReference || "not supplied"}`
        : `Have you independently confirmed this UPI transaction in your shop's bank/UPI app? Reference: ${payment.paymentReference || "not supplied"}`
      : isBatch
        ? "Have you physically received the full cash amount for this batch?"
        : "Have you physically received the cash for this print job?";
    if (!(await confirmInApp(confirmation, "Verify payment received", isBatch ? "Verify batch payment" : "Verify payment"))) return;

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await verifyShopPayment(tokens.accessToken, payment.id);
      setToast(isBatch
        ? "Batch payment verified. Every accepted file is now authorised; assign and dispatch each file through the print queue."
        : "Payment verified. The job is paid/authorised; printing still requires printer assignment and an explicit dispatch.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not verify this payment."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function rejectPayment(payment: ShopPayment) {
    const cancellationWarning = payment.method === "UPI" &&
      ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED"].includes(payment.status)
      ? "Before rejecting this UPI attempt, check your bank/UPI ledger against the full amount and the customer's reference. A submitted or missing UTR does not prove that money was or was not received. If the credit is present, verify this attempt instead of rejecting it.\n\n"
      : "";
    const reason = await promptInApp(
      cancellationWarning + (payment.batchPaymentId
        ? "Explain why this full batch payment is being rejected."
        : "Explain why this payment report is being rejected."),
      payment.batchPaymentId ? "Reject batch payment" : "Reject payment report",
      "Rejection reason"
    );
    if (reason === null) return;
    if (reason.trim().length < 3) {
      setError(t("A rejection reason of at least 3 characters is required."));
      return;
    }

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await rejectShopPayment(tokens.accessToken, payment.id, reason.trim());
      setToast(payment.batchPaymentId
        ? "Batch payment rejected. The customer can retry checkout for the accepted files."
        : "Payment report rejected. The customer can choose a new payment attempt.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not reject this payment."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function saveProfile(values: { displayName: string; logoUrl: string | null; accentColor: string }) {
    setBusy(true);
    try {
      await updateShopProfile(tokens.accessToken, values);
      setToast("Shop identity updated.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not update shop identity."));
    } finally {
      setBusy(false);
    }
  }

  async function saveOwnerAccount(values: { ownerName: string; email: string; currentPassword?: string }) {
    setBusy(true);
    try {
      const updated = await updateShopOwnerAccount(tokens.accessToken, values);
      setProfile(current => current ? {
        ...current,
        ownerName: updated.ownerName,
        ownerEmail: updated.ownerEmail
      } : current);
      setOwnerAccountOpen(false);
      setToast(updated.emailChanged
        ? t("Personal details saved. Your new email is now your sign-in address.")
        : t("Personal details updated."));
    } finally {
      setBusy(false);
    }
  }

  async function savePricing(values: {
    monoPerSideMinor: number;
    colourPerSideMinor: number;
    monoDuplexPerSheetMinor: number;
    colourDuplexPerSheetMinor: number;
    minimumChargeMinor: number;
    photo35x45Minor:number; photo25x35Minor:number; photo2x2Minor:number; photo4x6Minor:number;
    idDocumentPriceMinor:number;
  }) {
    setBusy(true);
    try {
      const updated = await updateShopPricing(tokens.accessToken, values);
      setPricing(updated);
      setToast("Print rate card saved. New quotes will use this pricing version.");
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not save print prices."));
    } finally {
      setBusy(false);
    }
  }

  async function previewJob(
    job: ShopPrintJob,
    readOnly = false,
    variant: "original" | "output" = "original"
  ) {
    setBusyJobId(job.id);
    setError("");
    try {
      const blob = await downloadShopDocument(tokens.accessToken, job.id, variant);
      const isSavedOutput = variant === "output";
      // Classify the actual file bytes. A stale MIME header or a misleading
      // extension must never make an unsupported file appear previewable.
      const detectedMediaType = await identifyDocumentPreviewMediaType(blob);
      const mediaType = isSavedOutput
        ? detectedMediaType === "application/pdf" ? detectedMediaType : ""
        : detectedMediaType ?? "";
      const previewBlob = mediaType ? new Blob([blob], { type: mediaType }) : blob;
      const url = URL.createObjectURL(previewBlob);
      setPreview({
        name: isSavedOutput ? job.outputFileName || `${job.jobNumber}_SmartPrint.pdf` : job.document.originalFileName,
        url,
        blob: previewBlob,
        mediaType,
        job,
        readOnly: readOnly || isSavedOutput,
        isSavedOutput
      });
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not open the document."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function applyShopCreatedPhotoStudio(sheets:PreparedPhotoSheet[]){
    const draft=shopPhotoOrderDraft;
    if(!draft||sheets.length!==1)throw new Error("Prepare exactly one multi-page A4 photo sheet before creating this order.");
    const sheet=sheets[0];
    setBusyJobId("shop-photo-create");
    setError("");
    try{
      const result=await createShopPhotoJob(tokens.accessToken,sheet.file,{
        photoSizeCode:sheet.sizeCode,photoCount:sheet.photoCount,photosPerPage:sheet.photosPerPage,
        photoOutputLayout:sheet.photoOutputLayout,photoPaperFinish:sheet.photoPaperFinish,
        recipientType:draft.recipientType,customerUserId:draft.customerUserId,
        recipientName:draft.recipientName,recipientPhone:draft.recipientPhone,
        paymentMethod:draft.paymentMethod,idempotencyKey:draft.idempotencyKey
      });
      setShopPhotoOrderDraft(null);
      setShopPhotoOrderReceipt(result);
      const amount=formatMoney(result.job.quotedAmountMinor,result.job.currency);
      const paymentNote=result.payment.method==="UPI"
        ? `UPI payment is pending. Collect at ${result.payment.upiIdSnapshot||"the shop's configured UPI ID"} and verify actual credit in your UPI/bank app before authorising print.`
        : "Cash payment is pending. Mark received only after cash is physically collected.";
      setToast(`Photo order ${result.job.jobNumber} created for ${result.job.recipientName} · ${amount}. ${paymentNote}`);
      await reload();
    }catch(exception){
      throw exception instanceof Error?exception:new Error("Could not create the shop photo order. Your original photo is unchanged; retry safely.");
    }finally{setBusyJobId(null);}
  }

  async function openShopPhotoStudioForJob(job:ShopPrintJob,returningFromPreview?:NonNullable<typeof preview>){
    if(!job.photoSourceAvailable||job.privatePrinting){
      setError(t("This job has no shop-editable original photo. Private Printing files stay with the customer."));
      return;
    }
    setBusyJobId(job.id);setError("");setToast("");
    try{
      const blob=await downloadShopDocument(tokens.accessToken,job.id,"photo-source");
      const response=(blob.type||"").split(";")[0].trim().toLowerCase();
      const mediaType=response==="image/jpeg"||response==="image/png"?response:"";
      if(!mediaType)throw new Error("The original JPG or PNG photo is not available for editing.");
      const sourceBlob=blob.type===mediaType?blob:new Blob([blob],{type:mediaType});
      const extension=mediaType==="image/png"?"png":"jpg";
      const sourceName=job.document.mediaType==="image/jpeg"||job.document.mediaType==="image/png"
        ?job.document.originalFileName
        :`photo-${job.jobNumber}.${extension}`;
      const source=new File([sourceBlob],sourceName,{type:mediaType,lastModified:Date.now()});
      const returnPreview=returningFromPreview
        ?{name:returningFromPreview.name,blob:returningFromPreview.blob,mediaType:returningFromPreview.mediaType,job:returningFromPreview.job,readOnly:returningFromPreview.readOnly}
        :undefined;
      let initialSettings:Partial<PhotoStudioEditorSettings>|undefined;
      if(job.photoEditorSettingsJson){
        try{
          const parsed=JSON.parse(job.photoEditorSettingsJson);
          if(parsed&&typeof parsed==="object"&&!Array.isArray(parsed))
            initialSettings=parsed as Partial<PhotoStudioEditorSettings>;
        }catch{
          // Older jobs have no saved editor settings; keep the safe defaults.
        }
      }
      if(returningFromPreview){
        URL.revokeObjectURL(returningFromPreview.url);
        setPreview(null);
      }
      setFocusShopPhotoEdit(false);
      setShopPhotoStudio({source,job,initialSettings,returnPreview});
    }catch(exception){setError(exception instanceof Error?t(exception.message):t("Could not open Photo Studio for this file."));}
    finally{setBusyJobId(null);}
  }

  function openShopPhotoStudio(){
    if(!preview?.job.photoSourceAvailable||preview.job.privatePrinting)return;
    void openShopPhotoStudioForJob(preview.job,preview);
  }

  function cancelShopPhotoStudio(){
    if(!shopPhotoStudio){setShopPhotoStudio(null);return;}
    const restored=shopPhotoStudio.returnPreview;
    if(!restored){
      setShopPhotoStudio(null);
      setFocusShopPhotoEdit(false);
      return;
    }
    setPreview({...restored,url:URL.createObjectURL(restored.blob)});
    setFocusShopPhotoEdit(true);
    setShopPhotoStudio(null);
  }

  async function applyShopPhotoStudio(sheets:PreparedPhotoSheet[]){
    const editor=shopPhotoStudio;
    if(!editor||sheets.length!==1)throw new Error("Prepare one multi-page A4 photo PDF before saving this job.");
    if(editor.job.privatePrinting || !editor.job.photoSourceAvailable){
      const message=editor.job.privatePrinting
        ? t("Private Printing keeps the original photo and editor settings on the customer's device. Shop-side revisions are disabled.")
        : t("The original photo is no longer available for editing. Refresh the queue before trying again.");
      setError(message);
      throw new Error(message);
    }
    const sheet=sheets[0];
    setBusyJobId(editor.job.id);
    setToast("");
    try{
      if(editor.job.storageMode==="LOCAL_AGENT") {
        await replaceShopPhotoDocumentLocally(tokens.accessToken,editor.job.id,sheet.file,sheet.sizeCode,sheet.photoCount,sheet.photosPerPage,JSON.stringify(sheet.editorSettings??{}),sheet.photoOutputLayout,sheet.photoPaperFinish);
      } else {
        await replaceShopPhotoDocument(tokens.accessToken,editor.job.id,sheet.file,sheet.sizeCode,sheet.photoCount,sheet.photosPerPage,JSON.stringify(sheet.editorSettings??{}),sheet.photoOutputLayout,sheet.photoPaperFinish);
      }
    }catch(exception){
      setError(exception instanceof Error?t(exception.message):t("Could not save the enhanced photo sheet."));
      throw exception;
    }

    setShopPhotoStudio(null);
    setFocusShopPhotoEdit(false);
    const finishLabel=sheet.photoPaperFinish==="GLOSSY"?"glossy":sheet.photoPaperFinish==="MATTE"?"matte":"plain";
    setToast(sheet.photoOutputLayout==="PHOTO_PAPER"?"The individual "+finishLabel+" photo-paper prints are saved to the same job. Printer compatibility will be checked before dispatch.":"The enhanced multi-page A4 photo PDF is saved to the same print job; its "+finishLabel+" stock and photo price are updated.");
    try{
      await reload();
      const blob=await downloadShopDocument(tokens.accessToken,editor.job.id,"original");
      const detectedMediaType=await identifyDocumentPreviewMediaType(blob);
      const mediaType=detectedMediaType==="application/pdf"?detectedMediaType:"";
      const previewBlob=mediaType?new Blob([blob],{type:mediaType}):blob;
      const savedJob:ShopPrintJob={...editor.job,photoEditorSettingsJson:JSON.stringify(sheet.editorSettings??{}),printKind:"PHOTO",photoSizeCode:sheet.sizeCode,photoCount:sheet.photoCount,photosPerPage:sheet.photosPerPage,pageCount:Math.max(1,Math.ceil(sheet.photoCount/sheet.photosPerPage)),photoOutputLayout:sheet.photoOutputLayout,photoPaperFinish:sheet.photoPaperFinish,document:{...editor.job.document,originalFileName:sheet.file.name,mediaType:"application/pdf",sizeBytes:sheet.file.size}};
      setPreview({name:sheet.file.name,url:URL.createObjectURL(previewBlob),blob:previewBlob,mediaType,job:savedJob,readOnly:true});
    }catch{
      setError(t("The prepared photo output was saved to the existing print job, but the queue could not refresh its preview. Refresh the queue before approving or printing."));
    }finally{
      setBusyJobId(null);
    }
  }

  async function viewJobHistory(job: ShopPrintJob) {
    setBusyJobId(job.id);
    setError("");
    try {
      const events = await getShopJobEvents(tokens.accessToken, job.id);
      setEventHistory({ job, events });
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not load job history."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function approveJob(job: ShopPrintJob) {
    if (!(await confirmInApp(`Approve ${job.jobNumber} for payment? It will not be sent to a printer yet.`, "Accept this file", "Accept file"))) return;
    setBusyJobId(job.id);
    setError("");
    try {
      const result = await approveShopPrintJob(tokens.accessToken, job.id);
      setToast(result.nextStep || "Shop approval saved. This job is awaiting verified payment.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not approve this job."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function rejectJob(job: ShopPrintJob) {
    if (!(await confirmInApp(`Reject ${job.jobNumber}? The customer will see that the shop declined the request.`, "Reject this file?", "Reject file"))) return;
    setBusyJobId(job.id);
    setError("");
    try {
      await rejectShopPrintJob(tokens.accessToken, job.id);
      setToast("The print request was rejected.");
      await reload();
      if (preview?.job.id === job.id) {
        URL.revokeObjectURL(preview.url);
        setPreview(null);
      }
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not reject this job."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function applyBatchReview(batchJobs: ShopPrintJob[], acceptedJobIds: string[], pdfBrandingEnabled: boolean) {
    const batchId=batchJobs.find(job=>job.batchId)?.batchId;
    if(!batchId||batchJobs.length===0)return;
    const accepted=new Set(acceptedJobIds);
    const acceptCount=batchJobs.filter(job=>accepted.has(job.id)).length;
    const rejectCount=batchJobs.length-acceptCount;
    if (!(await confirmInApp(
      `Accept ${acceptCount} file(s) and reject ${rejectCount} file(s)? Previously rejected files you tick will be restored only if no payment has started. Expired quotes must be recalculated before checkout. PDF footer branding will be ${pdfBrandingEnabled ? "enabled on safe blank margins" : "off"} for this batch. Original uploads and normal paper prints remain unchanged. The decisions are applied together.`,
      "Confirm batch review",
      "Apply review"
    ))) return;

    setBusyJobId("batch-review");
    setError("");
    try {
      const result=await applyShopPrintBatchReview(
        tokens.accessToken,
        batchId,
        batchJobs.map(job=>job.id),
        acceptedJobIds,
        pdfBrandingEnabled
      );
      await reload();
      setBatchReviewJobs(null);
      setBatchReviewRetry(false);
      setBatchReviewRetryIds(current=>current.filter(id=>!batchJobs.some(job=>job.id===id)));
      setBatchReviewDecisions(current=>{
        const next={...current};
        for(const job of batchJobs)delete next[job.id];
        return next;
      });
      setToast(result.quoteRefreshRequiredCount>0
        ?`Batch review saved: ${acceptCount} accepted and ${rejectCount} rejected. ${result.quoteRefreshRequiredCount} accepted file(s) need a fresh quote before checkout.`
        :`Batch review saved: ${acceptCount} accepted and ${rejectCount} rejected. All accepted files remain part of one checkout.`);
    } catch(exception) {
      await reload().catch(()=>undefined);
      setError(exception instanceof Error?t(exception.message):t("Could not apply this batch review. The decisions were not changed; refresh and retry the same review."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function recordFileRefund(job: ShopPrintJob, values: { refundMethod: "UPI" | "CASH"; refundReference: string | null; reason: string; rejectedPageNumbers: number[]; idempotencyKey: string }) {
    setBusyJobId(job.id);
    setError("");
    try {
      const result = await recordShopPrintRefund(tokens.accessToken, job.id, {
        ...values, confirmedSent: true
      });
      setToast(`Refund of ${formatMoney(result.amountMinor, result.currency)} recorded for ${job.document.originalFileName}. The return was logged under the signed-in shopkeeper.`);
      setRefundJob(null);
      if (preview?.job.id === job.id) {
        URL.revokeObjectURL(preview.url);
        setPreview(null);
      }
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not record this refund."));
    } finally {
      setBusyJobId(null);
    }
  }

  async function openBatchReview(batchId: string) {
    setBatchReviewLoadingId(batchId);
    setError("");
    try {
      // Always load the complete batch from the tenant-scoped API. The queue
      // may currently be filtered or paginated, so its visible rows are not
      // a safe source for per-file acceptance/rejection decisions.
      const allBatchJobs = await getShopPrintBatchJobs(tokens.accessToken, batchId);
      const reviewableStatuses=new Set(["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED","REJECTED"]);
      const waiting = allBatchJobs.filter(job => reviewableStatuses.has(job.status));
      if (waiting.length === 0) {
        setBatchReviewJobs(null);
        setToast("This batch has no files eligible for pre-checkout review.");
        return;
      }
      setBatchReviewDecisions(current => {
        const next = { ...current };
        for (const job of waiting) {
          if (next[job.id] === undefined) next[job.id] = job.status !== "REJECTED";
        }
        return next;
      });
      setBatchReviewRetry(waiting.some(job => batchReviewRetryIds.includes(job.id)));
      setBatchReviewJobs(waiting);
    } catch (exception) {
      setError(exception instanceof Error ? t(exception.message) : t("Could not load every file in this batch. No review decisions were changed."));
    } finally {
      setBatchReviewLoadingId(current => current === batchId ? null : current);
    }
  }

  const navAction = (section: Section) => {
    const nextPath = shopPathForSection(section);
    if (window.location.pathname !== nextPath) {
      window.history.pushState({}, "", nextPath);
    }
    setActive(section);
    setSidebarOpen(false);
    setAccountMenuOpen(false);
    setError("");
  };

  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const activePageTitle = t(active === "Settings" ? "Shop profile" : active);

  if (loading && !profile) {
    return <div className={dark ? "app-shell dark boot-screen" : "app-shell light boot-screen"}><div className="boot-card"><BrandLockup className="boot-brand-lockup" /><div className="boot-title">{t("Preparing your workspace")}</div><div className="boot-subtitle">{t("Connecting securely to your shop…")}</div><div className="button-spinner boot-spinner"/></div></div>;
  }

  return (
    <div className={`${dark ? "app-shell dark" : "app-shell light"}${sidebarCollapsed ? " sidebar-collapsed" : ""}`}>
      <aside className={sidebarOpen ? "sidebar sidebar-open" : "sidebar"}>
        <BrandLockup>
          <button className="icon-button sidebar-close" aria-label={t("Close navigation")} title={t("Close navigation")} onClick={() => setSidebarOpen(false)}><X size={18}/></button>
        </BrandLockup>
        <div className="workspace-switcher">
          <div className="shop-avatar">{(profile?.displayName || "S").slice(0,1).toUpperCase()}</div>
          <div className="workspace-copy"><strong>{profile?.displayName || "Your shop"}</strong><span>{profile?.businessName || "Shop workspace"}</span></div>
          <span className="tenant-status-dot" title={t("Tenant workspace active")}/>
        </div>
        <nav className="primary-nav" aria-label={t("Shop navigation")}>
          {sectionGroups.map(({ label: groupLabel, items }) => (
            <div className="nav-group" key={groupLabel}>
              <div className="nav-label">{translateUi(locale, groupLabel === "DAILY WORK" ? "nav.dailyWork" : groupLabel === "DEVICES & CONNECTIVITY" ? "nav.devices" : groupLabel === "BUSINESS" ? "nav.business" : "nav.insights")}</div>
              <div className="nav-group-items">
                {items.map(label => {
                  const section = sections.find(item => item.label === label);
                  if (!section) return null;
                  const Icon = section.icon;
                  const translatedLabel = translateUi(locale, ({
                    "Order Journey":"nav.orderJourney","Overview":"nav.overview","Print Queue":"nav.printQueue",
                    "Printers":"nav.printers","QR Stations":"nav.qrStations","Print Agent":"nav.printAgent",
                    "Pricing":"nav.pricing","Subscription":"nav.subscription","Payments":"nav.payments",
                    "Customers":"nav.customers","Reports":"nav.reports","Settings":"nav.settings"
                  } as Record<Section,string>)[label]);
                  return (
                    <button key={label} title={translatedLabel} aria-label={translatedLabel}
                      className={active === label ? "nav-item active" : "nav-item"}
                      onClick={() => navAction(label)}>
                      <Icon size={18} strokeWidth={1.8}/><span>{translatedLabel}</span>
                    </button>
                  );
                })}
              </div>
            </div>
          ))}
        </nav>
      </aside>
      {sidebarOpen && <button className="mobile-scrim" aria-label={t("Close navigation overlay")} onClick={() => setSidebarOpen(false)}/>}

      <main className="main-panel">
        <header className="topbar">
          <div className="topbar-left">
            <button className="icon-button sidebar-toggle" aria-label={sidebarCollapsed ? t("Expand navigation") : t("Collapse navigation")} title={sidebarCollapsed ? t("Expand navigation") : t("Collapse navigation")} onClick={toggleSidebarCollapsed}>{sidebarCollapsed ? <PanelLeftOpen size={18}/> : <PanelLeftClose size={18}/>}</button>
            <button className="icon-button mobile-menu" aria-label={t("Open navigation")} onClick={() => setSidebarOpen(true)}><Menu size={20}/></button>
            <div className="breadcrumb"><span>{t("Shop workspace")}</span><span className="crumb-slash">/</span><strong>{activePageTitle}</strong></div>
          </div>
          <div className="topbar-actions">
            <div className="connection-pill" role="status" aria-live="polite" title={cloudStatus === "connected" ? t("The latest shop data refresh succeeded.") : cloudStatus === "checking" ? t("Checking your shop connection.") : t("The latest shop data refresh could not reach the cloud service.")}>
              <span className="live-dot" style={{ opacity: cloudStatus === "unavailable" ? 0.45 : 1 }}/>
              <span>{cloudStatus === "connected" ? t("Cloud connected") : cloudStatus === "checking" ? t("Checking cloud") : t("Cloud unavailable")}</span>
            </div>
            <ThemeToggleButton dark={dark} onToggle={onToggleTheme} className="theme-toggle-compact"/>
            <LanguageSwitcher compact/>
            <div className="service-status-row" aria-label={t("Live shop system status")}>
              <span className={"service-status "+apiConnectionState} title={t("API connection")}><i/>{t("API {status}",{status:t(apiConnectionState==="connected"?"Connected":apiConnectionState==="degraded"?"Degraded":apiConnectionState==="offline"?"Offline":"Checking")})}</span>
              <span className={"service-status "+(onlineAgentCount > 0 ? "connected" : activeAgentCount > 0 ? "offline" : "checking")} title={t("Windows Agent heartbeat")}><i/>{t("Agent {status}",{status:onlineAgentCount>0?onlineAgentCount+" "+t("online"):activeAgentCount>0?t("Offline"):t("Not paired")})}</span>
              <span className={"service-status "+(usablePrinterCount > 0 ? "connected" : offlinePrinterCount > 0 ? "offline" : "checking")} title={t("Active printer availability")}><i/>{t("Printers {status}",{status:usablePrinterCount>0?usablePrinterCount+" "+t("available"):offlinePrinterCount>0?offlinePrinterCount+" "+t("unavailable"):t("unavailable")})}</span>
            </div>
            <div className="topbar-divider"/>
            <span className="topbar-product-tag"><span className="mode-indicator"/> {isDesktopApp() ? t("Desktop workspace") : t("Shop console")}</span>
            <div className="account-menu" ref={accountMenuRef}>
              <button type="button" className="account-menu-trigger"
                aria-label={t("Account options for {name}",{name:profile?.ownerName||t("shop owner")})}
                aria-expanded={accountMenuOpen} aria-controls="shop-account-menu"
                title={t("Account and shop settings")}
                onClick={() => setAccountMenuOpen(value => !value)}>
                <span className="account-avatar">{initials(profile?.ownerName || t("Shop owner"))}</span>
                <span className="account-menu-trigger-copy">
                  <strong>{profile?.ownerName || t("Shop owner")}</strong>
                  <small>{t("Account")}</small>
                </span>
                <ChevronDown size={14} aria-hidden="true"/>
              </button>
              {accountMenuOpen && (
                <div className="account-menu-popover" id="shop-account-menu" aria-label={t("Account options")}>
                  <div className="account-menu-heading">
                    <span className="account-avatar">{initials(profile?.ownerName || t("Shop owner"))}</span>
                    <div>
                      <strong>{profile?.ownerName || t("Shop owner")}</strong>
                      <span>{profile?.businessName || t("Your shop")}</span>
                    </div>
                  </div>
                  <div className="account-menu-divider"/>
                  <button type="button" className="account-menu-item" onClick={() => { setAccountMenuOpen(false); setOwnerAccountOpen(true); }}>
                    <UserRound size={16}/>{t("Personal details")}
                  </button>
                  <button type="button" className="account-menu-item" onClick={() => navAction("Settings")}>
                    <Settings size={16}/>{t("Shop profile & branding")}
                  </button>
                  <button type="button" className="account-menu-item" onClick={() => navAction("Subscription")}>
                    <ShieldCheck size={16}/>{t("Trial & subscription")}
                  </button>
                  <button type="button" className="account-menu-item" onClick={() => { setAccountMenuOpen(false); setHelpOpen(true); }}>
                    <LifeBuoy size={16}/>{t("Help & support")}
                  </button>
                  <div className="account-menu-divider"/>
                  <button type="button" className="account-menu-item account-menu-signout" onClick={() => { setAccountMenuOpen(false); void signOut(); }}>
                    <LogOut size={16}/>{t("Sign out")}
                  </button>
                </div>
              )}
            </div>
          </div>
        </header>

        <div className="content-wrap">
          <div className="page-heading">
            <div><div className="eyebrow"><span className="eyebrow-line"/>{t("SHOP WORKSPACE")}</div><h1>{active === "Overview" ? t("Welcome, {name}", { name: (profile?.ownerName || t("there")).split(" ")[0] }) : activePageTitle}</h1><p>{active === "Overview" ? t("Your shop setup and operations, all in one place.") : t(pageDescription(active))}</p></div>
            <div className="heading-actions"><span className="date-chip"><Clock3 size={15}/>{today}</span>{active === "QR Stations" && <button className="primary-button" onClick={() => setModal("station")}><Plus size={16}/> {t("Create QR station")}</button>}</div>
          </div>

          {error && <div className="inline-error" role="alert"><span>{error}</span><button onClick={() => setError("")} aria-label={t("Dismiss error")}><X size={15}/></button></div>}
          {toast && <div className="inline-success" role="status"><Check size={15}/><span>{toast}</span><button onClick={() => setToast("")} aria-label={t("Dismiss message")}><X size={15}/></button></div>}
          {subscription && <SubscriptionExpiryReminder summary={subscription} onNavigate={() => navAction("Subscription")}/>}

          {active === "Overview" && <ShopAdPlacement placement="SHOP_OVERVIEW"/>}
          {active === "Order Journey" && <OrderJourneyPage
            jobs={journeyJobs}
            payments={journeyPayments}
            printers={printers}
            agents={agentDevices}
            refreshing={journeyRefreshing}
            onRefresh={() => void refreshOrderJourney()}
            onNavigate={navAction}
            onReviewBatch={openBatchReview}
            onVerifyPayment={verifyPayment}
            onPreview={job=>void previewJob(job,true)}
            onApprove={approveJob}
            onReject={rejectJob}
            onRefund={job=>setRefundJob(job)}
            onAssignPrinter={assignPrinter}
            onDispatch={dispatchPrintJob}
            onConfirmOutput={confirmPrintedOutput}
            onResumeUnprintedBatch={resumeUnprintedBatch}
            onRetryPdfExport={retryPdfExport}
            onHistory={viewJobHistory}
            onOpenPhotoStudio={job=>void openShopPhotoStudioForJob(job)}
          />}
          {active === "Overview" && <Overview profile={profile} printers={printers} stations={stations} printJobs={printJobs} pricing={pricing} agentDevices={agentDevices} paymentSettings={paymentSettings} onNavigate={navAction}/>}
          {active === "Printers" && <PrintersPage printers={printers} agents={agentDevices} token={tokens.accessToken} maxPrinters={subscription?.entitlements.maxPrinters} subscriptionActive={subscription?.entitlements.isAccessActive} busyResourceId={busyResourceId} onToggle={togglePrinter} onOpenAgent={() => navAction("Print Agent")}/>} 
          {active === "QR Stations" && <StationsPage stations={stations} profile={profile} busyResourceId={busyResourceId} onAdd={() => setModal("station")} onToggle={toggleStation} onGenerate={createFreshStationCode}/>}
          {active === "Print Agent" && <PrintAgentPage agents={agentDevices} enrollmentCode={enrollmentCode} busy={agentBusy} onGenerate={generateAgentCode} onRevoke={revokeAgent} desktop={isDesktopApp()} localAgentStatus={localAgentStatus} localAgentConflict={localAgentConflict} localAgentRevoked={localAgentRevoked} localAgentNeedsRepair={localAgentNeedsRepair} localAgentEnrollmentIssue={localAgentEnrollmentIssue} localAgentOwnershipChecking={localAgentOwnershipChecking} onConnect={()=>void connectLocalComputer()} onReconnectAfterRevocation={()=>void connectLocalComputer(false,agentDevices,true)}/>}
          {active === "Pricing" && pricing && <PricingPage pricing={pricing} onSave={savePricing} busy={busy}/>}
          {active === "Subscription" && subscription && <SubscriptionPage summary={subscription} token={tokens.accessToken} onUpdated={setSubscription}/>}
          {active === "Payments" && paymentSettings && <PaymentsPage settings={paymentSettings} payments={payments} refunds={refunds} onSave={savePaymentSettings} onVerify={verifyPayment} onReject={rejectPayment} busy={busy} busyJobId={busyJobId}/>}
          {active === "Settings" && profile && <SettingsPage profile={profile} onSave={saveProfile} busy={busy}/>}
          {active === "Print Queue" && <PrintQueuePage jobs={printJobs} printers={printers} agentDevices={agentDevices} searchQuery={queueSearch} loadingBatchId={batchReviewLoadingId} onCreatePhotoOrder={()=>setShopPhotoOrderOpen(true)} onSearch={searchPrintQueue} onPreview={previewJob} onPreviewOutput={job=>void previewJob(job,true,"output")} onApprove={approveJob} onReject={rejectJob} onRefund={job=>setRefundJob(job)} onReviewBatch={openBatchReview} onHistory={viewJobHistory} onAssignPrinter={assignPrinter} onUpdateSettings={updatePrintJobSettings} onDispatch={dispatchPrintJob} onConfirmOutput={confirmPrintedOutput} onRetryPdfExport={retryPdfExport} onRefresh={refreshPrintQueue} onLoadOlder={loadOlderQueueJobs} hasMoreJobs={hasMoreQueueJobs} loadingOlderJobs={loadingOlderQueueJobs} busyJobId={busyJobId}/>} 
          {active === "Customers" && <CustomersPage token={tokens.accessToken}/>}
          {active === "Reports" && <ReportsPage token={tokens.accessToken}/>} 

          <footer className="app-footer"><span>© 2026 SmartPrint by QuantaEdge</span><span><span className="footer-status"/> {t("API session authenticated")}</span><span>{t("Made for print shops, with care.")}</span></footer>
        </div>
      </main>

      {ownerAccountOpen && profile && <ShopOwnerAccountModal profile={profile} busy={busy} onClose={() => setOwnerAccountOpen(false)} onSave={saveOwnerAccount}/>}
      {helpOpen && <HelpSupportModal onClose={() => setHelpOpen(false)} onNavigate={section => { setHelpOpen(false); navAction(section); }}/>}
      {modal === "station" && <StationModal busy={busy} printers={printers.filter(printer => isPrinterAvailableForShop(printer, agentDevices))} onClose={() => setModal(null)} onSubmit={submitStation}/>}
            {batchReviewJobs && <BatchReviewModal jobs={batchReviewJobs} busy={busyJobId==="batch-review"} decisions={batchReviewDecisions} retrying={batchReviewRetry} onDecision={(jobId,accepted)=>setBatchReviewDecisions(current=>({...current,[jobId]:accepted}))} onClose={()=>setBatchReviewJobs(null)} onPreview={job=>void previewJob(job,true)} onApply={(acceptedIds,pdfBrandingEnabled)=>void applyBatchReview(batchReviewJobs,acceptedIds,pdfBrandingEnabled)}/>} 
      {preview && <DocumentPreviewModal name={preview.name} url={preview.url} mediaType={preview.mediaType} job={preview.job} readOnly={preview.readOnly} isSavedOutput={preview.isSavedOutput} focusEditOnMount={focusShopPhotoEdit} onEditPhoto={preview.job.photoSourceAvailable&&!preview.job.privatePrinting&&(preview.job.storageMode==="CLOUD"||preview.job.storageMode==="LOCAL_AGENT")&&preview.job.status==="AWAITING_SHOP_APPROVAL"&&[pricing?.photo35x45Minor,pricing?.photo25x35Minor,pricing?.photo2x2Minor,pricing?.photo4x6Minor].some(rate=>(rate??0)>0)?openShopPhotoStudio:undefined} onApprove={()=>void approveJob(preview.job)} onReject={()=>void rejectJob(preview.job)} onRefund={()=>setRefundJob(preview.job)} onClose={() => { URL.revokeObjectURL(preview.url); setPreview(null); setFocusShopPhotoEdit(false); }}/>} 
      {shopPhotoStudio && <PhotoStudio source={shopPhotoStudio.source} mode="shop" dark={dark} initialSettings={shopPhotoStudio.initialSettings} photoPrices={{"35x45":pricing?.photo35x45Minor??0,"25x35":pricing?.photo25x35Minor??0,"2x2":pricing?.photo2x2Minor??0,"4x6":pricing?.photo4x6Minor??0}} onCancel={cancelShopPhotoStudio} onApply={applyShopPhotoStudio}/>} 
      {shopPhotoOrderReceipt&&<ShopPhotoOrderReceipt receipt={shopPhotoOrderReceipt} onClose={()=>setShopPhotoOrderReceipt(null)} onOpenPayments={()=>{setShopPhotoOrderReceipt(null);navAction("Payments");}}/>}
      {shopPhotoOrderOpen&&<ShopPhotoOrderModal token={tokens.accessToken} paymentSettings={paymentSettings} photoPrices={{"35x45":pricing?.photo35x45Minor??0,"25x35":pricing?.photo25x35Minor??0,"2x2":pricing?.photo2x2Minor??0,"4x6":pricing?.photo4x6Minor??0}} onClose={()=>setShopPhotoOrderOpen(false)} onContinue={draft=>{setShopPhotoOrderOpen(false);setShopPhotoOrderDraft(draft);}}/>}
      {shopPhotoOrderDraft&&<PhotoStudio source={shopPhotoOrderDraft.source} mode="shop-create" dark={dark} photoPrices={{"35x45":pricing?.photo35x45Minor??0,"25x35":pricing?.photo25x35Minor??0,"2x2":pricing?.photo2x2Minor??0,"4x6":pricing?.photo4x6Minor??0}} onCancel={()=>setShopPhotoOrderDraft(null)} onApply={applyShopCreatedPhotoStudio}/>} 
      {eventHistory && <JobHistoryModal job={eventHistory.job} events={eventHistory.events} onClose={() => setEventHistory(null)}/>}
      {refundJob && <RefundModal job={refundJob} token={tokens.accessToken} busy={busyJobId===refundJob.id} onClose={()=>setRefundJob(null)} onSubmit={values=>void recordFileRefund(refundJob,values)}/>} 
      {actionDialog && <Modal title={actionDialog.title} subtitle={t("This action will be recorded against your shop account.")} onClose={closeActionDialog}>
        <div className="in-app-dialog-body"><p>{actionDialog.message}</p>{actionDialog.inputLabel&&<label className="dialog-input-label">{actionDialog.inputLabel}<textarea autoFocus rows={3} maxLength={300} value={actionDialogValue} onChange={event=>setActionDialogValue(event.target.value)} placeholder={t("Enter at least 3 characters")}/></label>}</div>
        <div className="modal-actions"><button className="secondary-button" onClick={closeActionDialog}>{t("Cancel")}</button><button className="primary-button" disabled={!!actionDialog.inputLabel&&actionDialogValue.trim().length<3} onClick={submitActionDialog}>{actionDialog.confirmLabel}</button></div>
      </Modal>}
    </div>
  );
}

type ShopPhotoOrderDraft = {
  source:File; recipientType:"EXISTING_CUSTOMER"|"WALK_IN"; customerUserId?:string;
  recipientName?:string; recipientPhone?:string; paymentMethod:"UPI"|"CASH"; idempotencyKey:string;
};

function ShopPhotoOrderReceipt({receipt,onClose,onOpenPayments}:{
  receipt:Awaited<ReturnType<typeof createShopPhotoJob>>;
  onClose:()=>void;
  onOpenPayments:()=>void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const {job,payment}=receipt;
  const amount=formatMoney(payment.amountMinor,payment.currency);
  const upiIntent=payment.method==="UPI"&&payment.status!=="VERIFIED"
    ? buildCustomerUpiIntent({
        upiId:payment.upiIdSnapshot,
        payeeName:payment.payeeNameSnapshot,
        amountMinor:payment.amountMinor,
        currency:payment.currency,
        transactionReference:job.jobNumber,
        description:`SmartPrint photo order ${job.jobNumber}`
      })
    : null;
  return <Modal title={t("Photo order created")} subtitle={t("Job {job} · {recipient}").replace("{job}",job.jobNumber).replace("{recipient}",job.recipientName)} onClose={onClose}>
    <div className="photo-order-receipt">
      <div className="photo-order-receipt-summary">
        <span>{t("Server-calculated amount")}</span><strong>{amount}</strong>
        <small>{t("{count} photos · {size} · {pages} A4 page(s)", {count: formatUiNumber(job.photoCount, locale), size: job.photoSizeCode, pages: formatUiNumber(job.pageCount, locale)})}</small>
      </div>
      {payment.status==="VERIFIED"
        ? <div className="file-review-note"><Check size={16}/><span>{t("Payment is already verified. Continue from the Print Queue to assign a printer and explicitly dispatch the job.")}</span></div>
        : payment.method==="UPI"
          ? <div className="photo-order-upi">
              {upiIntent?<QRCode value={upiIntent} size={184} level="M"/>:<p className="form-error">{t("A UPI QR could not be prepared from the saved shop payment details. Open Payments and check the configured UPI ID.")}</p>}
              <strong>{payment.payeeNameSnapshot||t("Shop payment")}</strong>
              <span>{payment.upiIdSnapshot||t("UPI ID not available")}</span>
              <small>{t("Payment pending · Ask the customer to scan and pay the exact amount. Verify the actual credit in your UPI/bank app; this QR does not confirm payment.")}</small>
            </div>
          : <div className="file-review-note"><Wallet size={16}/><span>{t("Cash payment pending. Collect {amount} at the counter and mark it received only after the cash is physically in the shop.", { amount })}</span></div>}
      <div className="form-hint">{t("The job is already in your Print Queue. It cannot be dispatched until payment has been manually verified. Printing must then be started explicitly.")}</div>
      <div className="modal-actions">
        <button type="button" className="secondary-button" onClick={onClose}>{t("Done")}</button>
        <button type="button" className="primary-button" onClick={onOpenPayments}>{t("Open Payments")} <ArrowRight size={15}/></button>
      </div>
    </div>
  </Modal>;
}

function ShopPhotoOrderModal({token,paymentSettings,photoPrices,onClose,onContinue}:{
  token:string; paymentSettings:ShopPaymentSettings|null; photoPrices:Record<string,number>;
  onClose:()=>void; onContinue:(draft:ShopPhotoOrderDraft)=>void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [file,setFile]=useState<File|null>(null);
  const [recipientType,setRecipientType]=useState<"EXISTING_CUSTOMER"|"WALK_IN">("EXISTING_CUSTOMER");
  const [customers,setCustomers]=useState<ShopCustomer[]>([]);
  const [customerSearch,setCustomerSearch]=useState("");
  const [selectedCustomerId,setSelectedCustomerId]=useState("");
  const [loadingCustomers,setLoadingCustomers]=useState(false);
  const [customerError,setCustomerError]=useState("");
  const [recipientName,setRecipientName]=useState("");
  const [recipientPhone,setRecipientPhone]=useState("");
  const [paymentMethod,setPaymentMethod]=useState<"UPI"|"CASH">(paymentSettings?.cashEnabled?"CASH":"UPI");
  const [error,setError]=useState("");

  async function searchCustomers(search=customerSearch) {
    setLoadingCustomers(true);setCustomerError("");
    try {
      const page=await getShopCustomers(token,{search,page:1,pageSize:20});
      setCustomers(page.items);
      if(selectedCustomerId&&!page.items.some(item=>item.id===selectedCustomerId))setSelectedCustomerId("");
    } catch(exception) {
      setCustomerError(exception instanceof Error?exception.message:"Could not load this shop's customers.");
    } finally {setLoadingCustomers(false);}
  }
  useEffect(()=>{void searchCustomers("");},[token]);

  const enabledPhotoSizes=Object.values(photoPrices).some(rate=>rate>0);
  const hasPaymentMethod=!!(paymentSettings?.upiEnabled||paymentSettings?.cashEnabled);
  const selectedPaymentEnabled=paymentMethod==="UPI"?!!paymentSettings?.upiEnabled:!!paymentSettings?.cashEnabled;
  const selectedCustomer=customers.find(item=>item.id===selectedCustomerId);
  const canContinue=!!file&&enabledPhotoSizes&&selectedPaymentEnabled&&
    (recipientType==="EXISTING_CUSTOMER"?!!selectedCustomer:recipientName.trim().length>0);

  function selectFile(event:ChangeEvent<HTMLInputElement>) {
    const candidate=event.currentTarget.files?.[0]??null;setError("");
    if(!candidate){setFile(null);return;}
    const inferredType=resolvePhotoSourceMimeType(candidate);
    if(!inferredType){
      setFile(null);setError(t("Choose a JPG, JPEG or PNG image. Other formats are not supported by Photo Studio."));event.currentTarget.value="";return;
    }
    if(candidate.size<=0||candidate.size>UPLOAD_LIMITS.maxFileBytes){
      setFile(null);setError(t("The original photo must be between 1 byte and {max} MB.").replace("{max}", String(UPLOAD_LIMITS.maxFileBytes / (1024 * 1024))));event.currentTarget.value="";return;
    }
    setFile(candidate.type===inferredType?candidate:new File([candidate],candidate.name,{type:inferredType,lastModified:candidate.lastModified}));
  }

  function submit(event:FormEvent<HTMLFormElement>) {
    event.preventDefault();setError("");
    if(!file){setError(t("Choose a photo before opening Photo Studio."));return;}
    if(!enabledPhotoSizes){setError(t("Configure at least one photo size and per-photo rate in Pricing first."));return;}
    if(!selectedPaymentEnabled){setError(t("Choose a payment method currently enabled by this shop."));return;}
    if(recipientType==="EXISTING_CUSTOMER"&&!selectedCustomer){setError(t("Choose a customer already known to this shop."));return;}
    if(recipientType==="WALK_IN"&&!recipientName.trim()){setError(t("Enter the walk-in customer's name."));return;}
    onContinue({
      source:file,recipientType,
      customerUserId:recipientType==="EXISTING_CUSTOMER"?selectedCustomer!.id:undefined,
      recipientName:recipientType==="WALK_IN"?recipientName.trim():undefined,
      recipientPhone:recipientType==="WALK_IN"?recipientPhone.trim()||undefined:undefined,
      paymentMethod,idempotencyKey:crypto.randomUUID()
    });
  }

  return <Modal title={t("New photo order")} subtitle={t("Upload a shop-owned original, prepare its A4 photo sheets and create one shop-scoped print job.")} onClose={onClose}>
    <form className="modal-form" onSubmit={submit}>
      <label>{t("Original photo")}<input aria-label={t("Choose original photo")} type="file" accept=".jpg,.jpeg,.png,image/jpeg,image/png" onChange={selectFile}/></label>
      {file&&<div className="file-review-note"><ImageIcon size={16}/><span>{file.name} · {(file.size/1024/1024).toFixed(2)} MB. The original is not modified.</span></div>}
      <label>{t("Order for")}<select value={recipientType} onChange={event=>setRecipientType(event.currentTarget.value as "EXISTING_CUSTOMER"|"WALK_IN")}><option value="EXISTING_CUSTOMER">{t("Existing customer of this shop")}</option><option value="WALK_IN">{t("Walk-in customer")}</option></select></label>
      {recipientType==="EXISTING_CUSTOMER"&&<>
        <label>{t("Find a customer")}<div className="queue-search-form"><input aria-label={t("Search shop customers")} value={customerSearch} onChange={event=>setCustomerSearch(event.currentTarget.value)} onKeyDown={event=>{if(event.key==="Enter"){event.preventDefault();void searchCustomers();}}} placeholder={t("Name or email")}/><button type="button" className="secondary-button" onClick={()=>void searchCustomers()} disabled={loadingCustomers}>{loadingCustomers?"Searching…":"Search"}</button></div></label>
        <label>{t("Select customer")}<select aria-label={t("Select shop customer")} value={selectedCustomerId} onChange={event=>setSelectedCustomerId(event.currentTarget.value)}><option value="">{t("Choose a customer")}</option>{customers.map(customer=><option key={customer.id} value={customer.id}>{customer.displayName} · {customer.maskedEmail}</option>)}</select></label>
        {customerError&&<p className="form-error" role="alert">{customerError}</p>}
        {!loadingCustomers&&!customerError&&customers.length===0&&<p className="form-hint">{t("No customers found for this shop. Choose Walk-in customer when appropriate.")}</p>}
        {selectedCustomer&&<p className="form-hint">This order will be linked to {selectedCustomer.displayName}'s account and shop history. No customer login or QR session will be created.</p>}
      </>}
      {recipientType==="WALK_IN"&&<>
        <label>{t("Walk-in customer name")}<input value={recipientName} maxLength={160} onChange={event=>setRecipientName(event.currentTarget.value)} placeholder={t("Customer name")} required/></label>
        <label>{t("Contact number (optional)")}<input type="tel" maxLength={40} value={recipientPhone} onChange={event=>setRecipientPhone(event.currentTarget.value)} placeholder={t("Phone number")}/></label>
        <p className="form-hint">{t("A walk-in is saved only as a recipient on this shop's job. No global customer account is created.")}</p>
      </>}
      <label>{t("Payment method")}<select aria-label={t("Photo order payment method")} value={paymentMethod} onChange={event=>setPaymentMethod(event.currentTarget.value as "UPI"|"CASH")}>{paymentSettings?.upiEnabled&&<option value="UPI">{t("UPI · verify receipt manually")}</option>}{paymentSettings?.cashEnabled&&<option value="CASH">{t("Cash at counter · mark received manually")}</option>}</select></label>
      <p className="form-hint">{t("Payment will be recorded as pending. This action does not verify payment or start printing. The shop must verify the actual money received and explicitly dispatch the job.")}</p>
      {!enabledPhotoSizes&&<p className="form-error" role="alert">{t("No photo sizes are priced. Configure Photo Printing under Pricing before creating an order.")}</p>}
      {!hasPaymentMethod&&<p className="form-error" role="alert">{t("No payment method is enabled. Configure UPI or cash under Payment Settings.")}</p>}
      {error&&<p className="form-error" role="alert">{error}</p>}
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>{t("Cancel")}</button><button type="submit" className="primary-button" disabled={!canContinue}>{t("Open Photo Studio")} <ArrowRight size={15}/></button></div>
    </form>
  </Modal>;
}

function CustomersPage({ token }: { token: string }) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const money = (minor: number, currency: string) => formatUiCurrency(minor / 100, locale, currency);
  const date = (value: string | number | Date) => formatUiDate(value, locale, { dateStyle: "medium", timeStyle: "short", timeZone: IST_TIME_ZONE });
  const [searchDraft, setSearchDraft] = useState("");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [refreshKey, setRefreshKey] = useState(0);
  const [result, setResult] = useState<ShopCustomerPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedCustomer, setSelectedCustomer] = useState<ShopCustomer | null>(null);
  const [history, setHistory] = useState<ShopCustomerHistory | null>(null);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState("");
  const historyRequest = useRef(0);
  const pageSize = 20;

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    void getShopCustomers(token, { search, page, pageSize })
      .then(value => { if (current) setResult(value); })
      .catch(exception => {
        if (current) setError(exception instanceof Error ? t(exception.message) : t("Could not load shop customers."));
      })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, search, page, pageSize, refreshKey]);

  async function openHistory(customer: ShopCustomer) {
    const request = ++historyRequest.current;
    setSelectedCustomer(customer);
    setHistory(null);
    setHistoryError("");
    setHistoryLoading(true);
    try {
      const value = await getShopCustomerHistory(token, customer.id);
      if (request === historyRequest.current) setHistory(value);
    } catch (exception) {
      if (request === historyRequest.current) {
        setHistoryError(exception instanceof Error ? exception.message : "Could not load this customer's print history.");
      }
    } finally {
      if (request === historyRequest.current) setHistoryLoading(false);
    }
  }

  function closeHistory() {
    historyRequest.current++;
    setSelectedCustomer(null);
    setHistory(null);
    setHistoryError("");
    setHistoryLoading(false);
  }

  const totalPages = Math.max(1, Math.ceil((result?.totalCount ?? 0) / pageSize));
  const firstRow = result?.totalCount ? (page - 1) * pageSize + 1 : 0;
  const lastRow = Math.min(page * pageSize, result?.totalCount ?? 0);

  return (
    <section className="customers-workspace">
      <div className="customers-intro">
        <div>
          <span className="reports-eyebrow">{t("SHOP RELATIONSHIPS")}</span>
          <h2>{t("Customer directory")}</h2>
          <p>{t("Signed-in SmartPrint customers who have connected to this shop. Guest sessions stay anonymous.")}</p>
        </div>
        <div className="customers-total">
          <strong>{result?.totalCount ?? "—"}</strong>
          <span>{t("linked customers")}</span>
        </div>
      </div>

      <form className="customers-toolbar" onSubmit={event => {
        event.preventDefault();
        setPage(1);
        setSearch(searchDraft.trim());
      }}>
        <label className="search-field customers-search">
          <Search size={14}/>
          <input value={searchDraft} onChange={event => setSearchDraft(event.target.value)}
            placeholder={t("Search customer name or email")} aria-label={t("Search customer name or email")}/>
        </label>
        <button className="primary-button" type="submit"><Search size={14}/> {t("Search")}</button>
        <button className="secondary-button" type="button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}>
          <Activity size={14}/> Refresh
        </button>
      </form>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {loading && !result ? <div className="panel reports-loading"><span className="button-spinner"/> {t("Loading customers…")}</div> : null}
      {!loading && !error && result?.items.length === 0 && (
        <div className="panel customers-empty">
          <span className="customers-empty-icon"><Users size={24}/></span>
          <h3>{search ? t("No matching customers") : t("No signed-in customers yet")}</h3>
          <p>{search
            ? t("Try another name or email search.")
            : t("When a customer signs in before scanning this shop's QR code, their shop-specific activity will appear here. Guest jobs are intentionally not identified or linked.")}</p>
        </div>
      )}

      {result && result.items.length > 0 && (
        <div className="panel customers-table-panel">
          <div className="panel-heading customers-panel-heading">
            <div><h2>{t("Customers connected to this shop")}</h2><p>{t("Only activity created through this shop's QR sessions is included.")}</p></div>
            <span className="neutral-chip">{result.totalCount} {t("total")}</span>
          </div>
          <div className="table-scroll">
            <table className="customers-table">
              <thead><tr><th>{t("CUSTOMER")}</th><th>{t("PRINT JOBS")}</th><th>{t("COMPLETED")}</th><th>{t("LAST QR SESSION")}</th><th>{t("HISTORY")}</th></tr></thead>
              <tbody>
                {result.items.map(customer => (
                  <tr key={customer.id}>
                    <td><div className="customer-cell"><strong>{customer.displayName || t("SmartPrint customer")}</strong><span>{customer.maskedEmail}</span></div></td>
                    <td><strong className="customers-number">{customer.printJobCount}</strong><small className="customers-subvalue">{customer.sessionCount} {t("QR sessions")}</small></td>
                    <td><strong className="customers-number">{customer.completedJobCount}</strong><small className="customers-subvalue">{t("output confirmed")}</small></td>
                    <td><span className="customers-date">{date(customer.lastSeenUtc)}</span></td>
                    <td><button type="button" className="customers-history-button" onClick={() => void openHistory(customer)}>{t("View history")} <ArrowRight size={13}/></button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="table-footer customers-pagination">
            <span>{t("Showing")} <strong>{firstRow}–{lastRow}</strong> {t("of")} <strong>{result.totalCount}</strong></span>
            <div><button type="button" disabled={page <= 1 || loading} onClick={() => setPage(value => Math.max(1, value - 1))}>{t("← Previous")}</button>
              <span>{page} / {totalPages}</span>
              <button type="button" disabled={page >= totalPages || loading} onClick={() => setPage(value => Math.min(totalPages, value + 1))}>{t("Next →")}</button></div>
          </div>
        </div>
      )}

      {selectedCustomer && (
        <Modal title={selectedCustomer.displayName || t("SmartPrint customer")}
          subtitle={selectedCustomer.maskedEmail + " · Only this shop's print history"}
          onClose={closeHistory}>
          <div className="customer-history-modal">
            {historyLoading && <div className="reports-loading"><span className="button-spinner"/> {t("Loading print history…")}</div>}
            {historyError && <div className="inline-error" role="alert">{historyError}</div>}
            {history && (
              <>
                <div className="customer-history-summary"><strong>{history.totalJobs}</strong><span>{t("print jobs at this shop")}</span></div>
                {history.jobs.length === 0
                  ? <div className="empty-search">{t("No print jobs are available for this customer yet.")}</div>
                  : <div className="customer-history-list">
                    {history.jobs.map(job => {
                      const netPaid = job.payment
                        ? Math.max(0, job.payment.amountMinor - job.payment.refundedAmountMinor)
                        : 0;
                      const tone = job.status === "COMPLETED" ? "green"
                        : job.status.includes("REJECT") || job.status === "FAILED" ? "red"
                        : job.status.includes("AWAITING") ? "amber" : "blue";
                      return (
                        <article className="customer-history-row" key={job.id}>
                          <div className="customer-history-heading">
                            <div><strong>{job.document.originalFileName}</strong><PrintKindTag printKind={job.printKind}/><span>{job.jobNumber} · {date(job.createdUtc)}</span></div>
                            <Status label={prettyStatus(job.status)} tone={tone}/>
                          </div>
                          <div className="customer-history-facts">
                            <span>{job.printKind==="PHOTO"?`Photo sheet · ${job.photoCount??0} × ${job.photoSizeCode??"photo"} · ${job.photosPerPage??"layout not recorded"} per A4 page · ${job.pageCount} A4 pages`:`${job.pageCount} pages · ${job.copies} copies`}</span>
                            <strong>{money(job.quotedAmountMinor, job.currency)}</strong>
                          </div>
                          {job.payment && <div className="customer-history-payment">
                            {t(job.payment.status.replaceAll("_", " "))} · {job.payment.method === "UPI" ? "UPI" : t("Cash at counter")} · {t("Net recorded")}: {money(netPaid, job.payment.currency)}
                            {job.payment.refundedAmountMinor > 0 ? " · Refund recorded" : ""}
                          </div>}
                          <div className="customer-history-output">{job.outputType === "PDF_EXPORT"
                            ? (job.outputCompletedUtc ? t("PDF export created and verified") : t("PDF export not yet completed"))
                            : (job.outputConfirmedUtc ? t("Shop confirmed physical output") : t("Physical output not yet confirmed"))}</div>
                        </article>
                      );
                    })}
                  </div>}
              </>
            )}
          </div>
        </Modal>
      )}
    </section>
  );
}

function ReportsPage({ token }: { token: string }) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const money = (minor: number, currency: string) => formatUiCurrency(minor / 100, locale, currency);
  const number = (value: number) => formatUiNumber(value, locale);
  const date = (value: string | number | Date) => formatUiDate(value, locale, { dateStyle: "medium", timeStyle: "short", timeZone: IST_TIME_ZONE });
  const [days, setDays] = useState<7 | 30 | 90>(30);
  const [summary, setSummary] = useState<ShopReportSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    setSummary(null);
    void getShopReportSummary(token, days)
      .then(result => { if (current) setSummary(result); })
      .catch(exception => {
        if (current) {
          setError(exception instanceof Error ? t(exception.message) : t("Could not load shop reports."));
        }
      })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, days, refreshKey]);

  const statusMaximum = Math.max(1, ...(summary?.jobsByStatus.map(item => item.count) ?? [0]));

  return (
    <section className="reports-workspace">
      <div className="reports-toolbar">
        <div><span className="reports-eyebrow">{t("SHOP PERFORMANCE")}</span><h2>{t("Operations report")}</h2><p>{t("Live figures from this shop’s recorded print jobs and verified collections.")}</p></div>
        <div className="reports-controls">
          <label className="reports-period">{t("Period")}
            <select aria-label={t("Reporting period")} value={days} onChange={event => setDays(Number(event.target.value) as 7 | 30 | 90)}>
              <option value={7}>{t("Last 7 days")}</option><option value={30}>{t("Last 30 days")}</option><option value={90}>{t("Last 90 days")}</option>
            </select>
          </label>
          <button className="secondary-button reports-refresh" type="button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}><Activity size={14}/> {t("Refresh")}</button>
        </div>
      </div>
      {error && <div className="inline-error" role="alert">{error}</div>}
      {loading && !summary ? <div className="panel reports-loading"><span className="button-spinner"/> {t("Loading shop activity…")}</div> : null}
      {!loading && !summary && !error ? <div className="panel reports-loading">{t("No report data is available for this period yet.")}</div> : null}
      {summary && <>
        <div className="stat-grid reports-stat-grid">
          <Metric icon={<FileText size={18}/>} label={t("Print jobs")} value={number(summary.totalJobs)} note={t("Created in the last {days} days").replace("{days}", String(summary.periodDays))} accent="blue"/>
          <Metric icon={<Check size={18}/>} label={t("Output confirmed")} value={number(summary.completedJobs)} note={t("Physically confirmed by shop operators")} accent="mint"/>
          <Metric icon={<Printer size={18}/>} label={t("Pages requested")} value={number(summary.pagesRequested)} note={t("Page count × requested copies")} accent="violet"/>
          <Metric icon={<Wallet size={18}/>} label={t("Payments to review")} value={number(summary.pendingPaymentCount)} note={t("Open UPI/cash attempts across all dates")} accent="amber"/>
        </div>
        <div className="reports-grid">
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>{t("Job status breakdown")}</h2><p>{t("Current status of jobs created during this period")}</p></div><span className="neutral-chip">{summary.totalJobs} {t("jobs")}</span></div>
            {summary.jobsByStatus.length === 0 ? <div className="reports-empty">{t("No print jobs were created in this period.")}</div> :
              <div className="report-status-list">{summary.jobsByStatus.map(item => <div className="report-status-row" key={item.status}>
                <div className="report-status-label"><span>{prettyStatus(item.status)}</span><strong>{number(item.count)}</strong></div>
                <div className="report-status-track"><span style={{ width: `${Math.max(2, item.count / statusMaximum * 100)}%` }}/></div>
              </div>)}</div>}
          </section>
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>{t("Verified collections")}</h2><p>{t("Shop-confirmed payments, grouped separately by currency")}</p></div><span className="neutral-chip">{t("Verified only")}</span></div>
            {summary.verifiedCollectionsByMethod.length === 0 ? <div className="reports-empty">{t("No payments have been verified in this period. Unverified UPI references are not included.")}</div> :
              <div className="report-collection-list">{summary.verifiedCollectionsByMethod.map(item => <div className="report-collection-row" key={`${item.currency}-${item.method}`}>
                <div><strong>{item.method === "UPI" ? "UPI" : t("Cash at counter")}</strong><small>{item.paymentCount} {t(item.paymentCount === 1 ? "payment" : "payments")} {t("verified")}</small></div>
                <strong className="report-collection-amount">{money(item.amountMinor, item.currency)}</strong>
              </div>)}</div>}
          </section>
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>{t("Refunds recorded")}</h2><p>{t("Refunds the shop confirmed sending during this period")}</p></div><span className="neutral-chip">{t("Recorded as sent")}</span></div>
            {summary.refundsRecordedByMethod.length === 0 ? <div className="reports-empty">{t("No refunds were recorded as sent during this period.")}</div> :
              <div className="report-collection-list">{summary.refundsRecordedByMethod.map(item => <div className="report-collection-row" key={`refund-${item.currency}-${item.method}`}>
                <div><strong>{t(item.method === "UPI" ? "UPI refund" : "Cash refund")}</strong><small>{item.refundCount} {t(item.refundCount === 1 ? "refund" : "refunds")} {t("recorded")}</small></div>
                <strong className="report-collection-amount">{money(item.amountMinor, item.currency)}</strong>
              </div>)}</div>}
          </section>
        </div>
        {summary.outputUnconfirmedJobs > 0 && <div className="report-caution"><ShieldCheck size={16}/><span><strong>{summary.outputUnconfirmedJobs} output-unconfirmed {summary.outputUnconfirmedJobs === 1 ? "job" : "jobs"}.</strong> {t("Check the actual pages before any manual retry. These jobs are not counted as completed.")}</span></div>}
        <div className="reports-footnote">{t("Report window: {from} – {to}. Job counts use creation time, gross collections use payment verification time, and refunds use the date the shop recorded sending them. Pages requested are not a claim of pages physically printed. A UPI reference is excluded until the shop verifies the receipt.", {from: date(summary.fromUtc), to: date(summary.toUtc)})}</div>
      </>}
    </section>
  );
}

function Overview({ profile, printers, stations, printJobs, pricing, agentDevices, paymentSettings, onNavigate }: {
  profile: ShopProfile | null; printers: PrinterDevice[]; stations: QrStation[];
  printJobs: ShopPrintJob[]; pricing: ShopPricingProfile | null;
  agentDevices: AgentDevice[]; paymentSettings: ShopPaymentSettings | null;
  onNavigate: (section: Section) => void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const activePrinters = printers.filter(printer =>
    printer.isActive && (printer.outputMode === "PHYSICAL" || printer.outputMode === "PDF_EXPORT" ||
      (printer.outputMode === "UNSUPPORTED_VIRTUAL" && printer.virtualTestGrantActive === true))
  );
  const todayKey = istDateKey(new Date());
  const jobsToday = printJobs.filter(job => istDateKey(job.createdUtc) === todayKey);
  const quotesAwaiting = printJobs.filter(job => job.status === "AWAITING_SHOP_APPROVAL");
  const onlineAgentIds = new Set(agentDevices
    .filter(agent => !agent.revokedUtc && agent.status === "Online" &&
      !!agent.lastSeenUtc && Date.parse(agent.lastSeenUtc) > Date.now() - 2 * 60 * 1000)
    .map(agent => agent.id));
  const hasOnlineAgent = onlineAgentIds.size > 0;
  const hasReachablePrinter = printers.some(printer => isPrinterAvailableForShop(printer, agentDevices));
  const hasPaymentMethod = paymentSettings?.cashEnabled === true ||
    (paymentSettings?.upiEnabled === true &&
      !!paymentSettings.upiId?.trim() && !!paymentSettings.payeeName?.trim());
  const setupChecks: { complete: boolean; title: string; detail: string; target: Section }[] = [
    { complete: !!profile?.displayName, title: "Create your shop identity", detail: "Your shop name and customer-facing brand.", target: "Settings" },
    { complete: activePrinters.length > 0, title: "Select your printers", detail: "Windows queues are discovered automatically; choose which ones to use within your plan.", target: "Printers" },
    { complete: hasOnlineAgent, title: "Connect the shop computer", detail: "The Windows Agent must be paired and recently online.", target: "Print Agent" },
    { complete: hasReachablePrinter, title: "Verify a live printer", detail: "At least one enabled output must be visible to the connected computer.", target: "Printers" },
    { complete: stations.some(station => station.isEnabled), title: "Enable a QR station", detail: "At least one customer-facing QR station must be enabled.", target: "QR Stations" },
    { complete: !!pricing?.isConfigured, title: "Set your print prices", detail: "Configure mono, colour and duplex rates.", target: "Pricing" },
    { complete: hasPaymentMethod, title: "Enable a payment method", detail: "Configure cash at counter or a complete UPI payee profile.", target: "Payments" }
  ];
  return (
    <>
      <div className="stat-grid">
        <Metric icon={<Printer size={18}/>} label={t("Detected printer queues")} value={String(printers.length)} note={`${activePrinters.length} selected for use`} accent="blue"/>
        <Metric icon={<QrCode size={18}/>} label={t("QR stations")} value={String(stations.length)} note={`${stations.filter(station=>station.isEnabled).length} currently enabled`} accent="violet"/>
        <Metric icon={<FileText size={18}/>} label={t("Print jobs today")} value={String(jobsToday.length)} note="Real customer uploads" accent="amber"/>
        <Metric icon={<Wallet size={18}/>} label={t("Quotes awaiting review")} value={String(quotesAwaiting.length)} note="Quotes are not collected payments" accent="mint"/>
      </div>
      <div className="dashboard-grid">
        <section className="panel activity-empty-panel">
          <div className="panel-heading"><div><h2>{t("Customer activity")}</h2><p>{t("Recent uploads and quote requests from your QR stations")}</p></div><span className="neutral-chip">{printJobs.length} recent jobs</span></div>
          {printJobs.length===0 ? <div className="activity-empty-content"><div className="activity-empty-art"><div className="activity-art-sheet"><FileText size={25}/><i/><i/><i/></div><div className="activity-art-printer"><Printer size={27}/></div><span className="activity-art-spark">✦</span></div><h3>{t("Ready when your first customer is")}</h3><p>{t("Set your prices and share a QR station. Customer uploads will appear here as soon as they arrive.")}</p><button className="text-action" onClick={() => onNavigate(printers.length ? "QR Stations" : "Printers")}>{printers.length ? "Open QR stations" : "Add your first printer"} <span>→</span></button></div> :
            <div className="activity-job-list">
              {printJobs.slice(0,4).map(job=><div className="activity-job-row" key={job.id}><div className="activity-job-icon"><FileText size={16}/></div><div className="activity-job-copy"><strong>{job.jobNumber}</strong><span>{job.document.originalFileName} · {job.printKind==="PHOTO" ? `${job.photoCount??0} photos · ${job.photoSizeCode??""} · ${job.photosPerPage??"layout not recorded"} per A4 page · ${job.pageCount} A4 pages` : `${job.pageCount} pages`}</span></div><div className="activity-job-status"><Status label={prettyStatus(job.status)} tone={job.status==="AWAITING_CUSTOMER_APPROVAL"||job.status==="AWAITING_SHOP_APPROVAL"?"amber":"blue"}/><small>{formatIstTime(job.createdUtc)}</small></div></div>)}
              <button className="text-action" onClick={()=>onNavigate("Print Queue")}>{t("Open print queue")} <span>→</span></button>
            </div>}
        </section>
        <section className="panel printer-panel">
          <div className="panel-heading"><div><h2>{t("Printer setup")}</h2><p>{t("Registered shop devices")}</p></div><button className="text-action" onClick={() => onNavigate("Printers")}>{t("Manage")} <span>→</span></button></div>
          <div className="printer-summary"><div className="printer-summary-icon"><Printer size={19}/></div><div><strong>{printers.length} registered</strong><span>{t("Live hardware status awaits the local agent")}</span></div><div className="health-score neutral-score">—</div></div>
          <div className="device-list">{printers.slice(0,4).map(printer=><Device key={printer.id} name={printer.displayName} detail={`${printer.manufacturer} · ${printer.connectionType}`} state={printer.status} tone={printer.status.toLowerCase()==="ready"?"green":"slate"}/>)}{printers.length===0 && <div className="mini-empty">{t("No printers registered yet.")}</div>}</div>
          <button className="text-action full-action" onClick={() => onNavigate("Printers")}>{t("View all printers")} <span>→</span></button>
        </section>
      </div>
      <section className="panel setup-panel">
        <div className="panel-heading"><div><h2>{t("Shop setup checklist")}</h2><p>{t("Complete these steps before accepting customer print jobs.")}</p></div><span className="setup-count">{setupChecks.filter(check => check.complete).length} / {setupChecks.length} complete</span></div>
        <div className="setup-checks">
          {setupChecks.map(check => <SetupCheck key={check.title} complete={check.complete} title={check.title} detail={check.detail} onClick={() => onNavigate(check.target)}/>)}
        </div>
      </section>
    </>
  );
}

type PrinterCapabilitySnapshot = {
  source?: string;
  queueName?: string;
  driverName?: string | null;
  discoveredAtUtc?: string;
  color?: boolean | null;
  colorStatus?: string;
  duplex?: boolean | null;
  duplexStatus?: string;
  orientation?: { portrait?: boolean | null; landscape?: boolean | null } | null;
  paperSizes?: Array<{ name?: string | null; code?: number }> | null;
  pagesPerSheet?: number[] | null;
  pagesPerSheetStatus?: string;
  pagesPerSheetDirection?: string | null;
  pagesPerSheetDirectionStatus?: string;
  mediaTypes?: Array<{ name?: string | null; code?: number }> | null;
  mediaTypesStatus?: string;
  pageMediaTypes?: string[] | null;
  pageMediaTypesStatus?: string;
  resolutions?: Array<{ xDpi?: number; yDpi?: number }> | null;
};

function parsePrinterCapabilitySnapshot(json?:string|null):PrinterCapabilitySnapshot|null {
  if(!json?.trim()||json.trim()==="{}")return null;
  try {
    const value=JSON.parse(json) as unknown;
    return value&&typeof value==="object"&&!Array.isArray(value)
      ?value as PrinterCapabilitySnapshot:null;
  } catch {
    return null;
  }
}

function capabilityState(value:unknown,reported?:string):"supported"|"unsupported"|"unknown" {
  if(reported==="supported"||reported==="unsupported"||reported==="unknown")return reported;
  return typeof value==="boolean"?(value?"supported":"unsupported"):"unknown";
}

function capabilityStateLabel(state:"supported"|"unsupported"|"unknown"):string {
  return state==="supported"?"Supported":state==="unsupported"?"Not supported":"Not reported";
}

function formatCapabilityTimestamp(value?:string):string {
  if(!value)return "Not yet discovered";
  const date=new Date(value);
  return Number.isFinite(date.getTime())?date.toLocaleString():"Timestamp unavailable";
}

function PrinterCapabilityPanel({printer}:{printer:PrinterDevice}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const snapshot=parsePrinterCapabilitySnapshot(printer.capabilitiesJson);
  const pages=Array.isArray(snapshot?.pagesPerSheet)
    ?snapshot.pagesPerSheet.filter(value=>Number.isInteger(value)&&value>=1&&value<=500):[];
  const nupValues=pages.filter(value=>value>1).sort((a,b)=>a-b);
  const nupState=capabilityState(nupValues.length>0,snapshot?.pagesPerSheetStatus);
  const colourState=capabilityState(snapshot?.color,snapshot?.colorStatus);
  const duplexState=capabilityState(snapshot?.duplex,snapshot?.duplexStatus);
  const directionState=capabilityState(
    snapshot?.pagesPerSheetDirection,
    snapshot?.pagesPerSheetDirectionStatus
  );
  const paperSizes=(snapshot?.paperSizes??[]).map(item=>item.name?.trim()|| (item.code?"Driver code "+item.code:"")).filter(Boolean);
  const mediaTypes=(snapshot?.mediaTypes??[]).map(item=>item.name?.trim()|| (item.code?"Driver code "+item.code:"")).filter(Boolean);
  const pageMediaTypes=(snapshot?.pageMediaTypes??[]).map(value=>value?.trim()).filter((value):value is string=>!!value);
  const photoMedia=resolvePhotoMediaCapabilities(pageMediaTypes,mediaTypes,snapshot?.pageMediaTypesStatus,snapshot?.mediaTypesStatus);
  const photoMediaTypes=photoMedia.mediaTypes;
  const photoMediaLabels=photoMedia.labels;
  const photoMediaState=photoMedia.state;
  const resolutions=(snapshot?.resolutions??[]).map(item=>
    item.xDpi&&item.yDpi?item.xDpi+" × "+item.yDpi+" dpi":""
  ).filter(Boolean);
  const status=printer.status.toLowerCase();
  const statusIssue=status.includes("offline")||status.includes("stale")||status.includes("revoked")||
    status.includes("paused")||status.includes("error")||status==="unknown";
  const diagnostic=status.includes("revoked")
    ?t("The paired Print Agent has been revoked. Re-enrol the shop computer before using this queue.")
    :status.includes("offline")
      ?"The Print Agent is offline. Check that the shop computer is powered on, connected to the internet and running SmartPrint Agent."
      :status.includes("stale")
        ?"The last Windows printer inventory is stale. Reconnect the Agent or refresh printer discovery on the shop computer."
        :status.includes("paused")
          ?"Windows reports this queue as paused. Resume it in Windows Settings → Printers & scanners, then refresh discovery."
          :status.includes("error")
            ?"Windows reports a printer error. Open the queue in Windows, clear the blocked document or resolve the displayed driver/device warning, then reconnect the printer."
            :status==="unknown"
              ?"Windows could not confirm the live queue state. Check the printer, cable/network and Windows queue before retrying."
            :!snapshot
              ?"No capability snapshot has been received yet. Confirm the Agent is connected, then reconnect or rediscover this printer."
              :nupState==="unknown"||colourState==="unknown"||duplexState==="unknown"
                ?"Some driver capabilities were not reported. Update or reinstall the printer driver, then reconnect the queue to refresh its capability report."
                :"The Agent has reported this queue. Compare the requested print settings with the driver capabilities below before retrying a failed job.";
  const capabilityTiles=[
    {label:"Colour",value:snapshot?.color===true?"Colour":snapshot?.color===false?"Mono only":"Not reported",state:colourState},
    {label:"Duplex",value:snapshot?.duplex===true?"Available":snapshot?.duplex===false?"Simplex only":"Not reported",state:duplexState},
    {label:"N-up",value:nupValues.length?nupValues.map(value=>value+"-up").join(" · "):capabilityStateLabel(nupState),state:nupState},
    {label:"Photo media",value:photoMediaLabels.length?photoMediaLabels.join(" · "):capabilityStateLabel(photoMediaState),state:photoMediaState},
    {label:"Paper sizes",value:paperSizes.length?paperSizes.length+" reported":"Not reported",state:paperSizes.length?"supported" as const:"unknown" as const}
  ];
  return (
    <div className="printer-capability-panel">
      <div className="printer-capability-heading">
        <strong>{t("Driver capabilities")}</strong>
        <span>{snapshot?formatCapabilityTimestamp(snapshot.discoveredAtUtc):"Awaiting Agent discovery"}</span>
      </div>
      {snapshot&&<div className="printer-capability-tiles">
        {capabilityTiles.map(tile=><div className="printer-capability-tile" key={tile.label}>
          <span>{tile.label}</span>
          <strong className={"capability-state-"+tile.state}>{tile.value}</strong>
        </div>)}
      </div>}
      <details className="printer-diagnostics" open={statusIssue||!snapshot}>
        <summary>{snapshot?"Full capabilities & troubleshooting":"Capability diagnostics"}</summary>
        <div className="printer-diagnostic-content">
          <div className="printer-driver-identity">
            <span>{t("Installed driver")}</span>
            <strong>{snapshot?.driverName||"Driver name not reported by Agent"}</strong>
            <small>{snapshot?.source==="WINDOWS_DRIVER"?"Read from the Windows print driver":"Capability source not reported"}</small>
          </div>
          <div className="printer-capability-facts">
            <div><span>{t("Colour printing")}</span><strong>{capabilityStateLabel(colourState)}</strong></div>
            <div><span>{t("Duplex printing")}</span><strong>{capabilityStateLabel(duplexState)}</strong></div>
            <div><span>{t("Page order for N-up")}</span><strong>{snapshot?.pagesPerSheetDirection||capabilityStateLabel(directionState)}</strong></div>
            <div><span>{t("Orientation")}</span><strong>{snapshot?.orientation?[
              snapshot.orientation.portrait===true?"Portrait":null,
              snapshot.orientation.landscape===true?"Landscape":null
            ].filter(Boolean).join(" · ")||"Not reported":"Not reported"}</strong></div>
            <div><span>{t("Paper sizes")}</span><strong>{paperSizes.length?paperSizes.slice(0,10).join(", ")+(paperSizes.length>10?" +"+(paperSizes.length-10)+" more":""):"Not reported"}</strong></div>
            <div><span>{t("Photo media types")}</span><strong>{photoMediaTypes.length?photoMediaTypes.slice(0,8).join(", ")+(photoMediaTypes.length>8?" +"+(photoMediaTypes.length-8)+" more":""):photoMediaState==="unsupported"?"No matching glossy/matte stock":"Not reported"}</strong></div>
            <div><span>{t("Print Schema media")}</span><strong>{pageMediaTypes.length?pageMediaTypes.slice(0,8).join(", ")+(pageMediaTypes.length>8?" +"+(pageMediaTypes.length-8)+" more":""):"Not reported"}</strong></div>
            <div><span>{t("All driver media types")}</span><strong>{mediaTypes.length?mediaTypes.slice(0,8).join(", ")+(mediaTypes.length>8?" +"+(mediaTypes.length-8)+" more":""):"Not reported"}</strong></div>
            <div><span>{t("Resolutions")}</span><strong>{resolutions.length?resolutions.slice(0,8).join(", ")+(resolutions.length>8?" +"+(resolutions.length-8)+" more":""):"Not reported"}</strong></div>
          </div>
          <div className={"printer-diagnostic-advice"+(statusIssue||!snapshot?" warning":"")} role="note">
            <strong>{statusIssue||!snapshot?"What to check":"Troubleshooting guidance"}</strong>
            <span>{diagnostic}</span>
          </div>
          {printer.lastSeenUtc&&<small className="printer-last-seen">Agent last seen · {formatCapabilityTimestamp(printer.lastSeenUtc)}</small>}
        </div>
      </details>
    </div>
  );
}

function PrintersPage({
  printers, agents, token, maxPrinters, subscriptionActive, busyResourceId, onToggle, onOpenAgent
}: {
  printers: PrinterDevice[];
  agents: AgentDevice[];
  token: string;
  maxPrinters?: number | null;
  subscriptionActive?: boolean;
  busyResourceId: string | null;
  onToggle: (printer: PrinterDevice, replacePrinterId?: string) => Promise<boolean>;
  onOpenAgent: () => void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [pendingReplacement, setPendingReplacement] = useState<PrinterDevice | null>(null);
  const [replacePrinterId, setReplacePrinterId] = useState("");
  const selectedPrinters = printers.filter(printer =>
    printer.isActive && (printer.outputMode === "PHYSICAL" || printer.outputMode === "PDF_EXPORT" ||
      (printer.outputMode === "UNSUPPORTED_VIRTUAL" && printer.virtualTestGrantActive === true))
  );
  const selectablePrinters = printers.filter(printer =>
    !printer.isActive && isPrinterReadyForSelection(printer, agents)
  );
  const detectedCount = printers.filter(printer =>
    !!printer.agentDeviceId && !!printer.queueName
  ).length;
  const quotaFull = maxPrinters != null && selectedPrinters.length >= maxPrinters;
  const canUseSelection = subscriptionActive === true;
  const canReplaceSelection = selectedPrinters.length > 0 && selectablePrinters.length > 0;

  async function confirmReplacement() {
    if (!pendingReplacement || !replacePrinterId) return;
    const success = await onToggle(pendingReplacement, replacePrinterId);
    if (success) {
      setPendingReplacement(null);
      setReplacePrinterId("");
    }
  }

  return (
    <>
      <section className="panel collection-panel">
        <div className="collection-header">
          <div>
            <h2>{t("Detected printer connections and Windows queues")}</h2>
            <p>{t("USB, LAN, shared and IPP connections are discovered through Windows. Connection type tells you how the queue is reached; output type tells you whether SmartPrint sends a physical print job, creates a PDF, or keeps an unsupported virtual queue visible. Your plan limit applies only to selected output queues.")}</p>
          </div>
          <div className="printer-discovery-guidance" role="note">
            <strong>{t("Adding a new printer")}</strong>
            <span>{t("Connect the printer by USB or to the shop network. In Windows Settings → Printers & scanners, choose Add device and install the manufacturer's driver if Windows asks. Keep the SmartPrint Print Agent running; it lists the queue automatically, usually within 30 seconds. If the printer is connected to another PC, pair that PC's SmartPrint Print Agent to this shop from the Print Agent page. You do not manually register the printer here.")}</span>
          </div>
        </div>
        <div className="printer-selection-summary" role="status">
          <div>
            <strong>{detectedCount} {t("detected Windows queues")}</strong>
            <span>{selectedPrinters.length} {t("selected")}{maxPrinters == null ? " · " + t("no configured selection limit") : ` ${t("of")} ${maxPrinters} ${t("plan slots")}`}</span>
          </div>
          <p>{t("At the plan limit, choose another ready queue and replace any selected one. You can also replace a queue before the limit is reached; use Select to add a queue without replacing anything. The other discovered queues remain visible.")}</p>
        </div>
        {!canUseSelection && (
          <div className="printer-selection-warning" role={subscriptionActive === false ? "alert" : "status"}>
            {subscriptionActive === false
              ? t("Your shop subscription is inactive. Your queues and files remain visible, but new printer selections are paused until shop access is restored.")
             : t("Checking your shop plan before allowing printer selection. Your detected queues remain visible.")}
          </div>
        )}
        {maxPrinters === 0 && selectedPrinters.length === 0 && (
          <div className="printer-selection-warning" role="status">
            {t("This plan currently has no printer selection slots. All discovered queues remain visible, but selecting one requires a plan with at least one printer slot.")}
          </div>
        )}
        {printers.length === 0 ? (
          <EmptyModule
            icon={<Printer size={24}/>}
            title={t("No Windows printer queues reported yet")}
            text={t("Connect the printer and add it in Windows Settings → Printers & scanners. Install its driver if Windows asks, then keep the SmartPrint Print Agent running. If the printer is on another PC, pair that PC's Print Agent to this shop from the Print Agent page. New queues appear automatically after the next scan.")}
            actionLabel={t("Check Print Agent")}
            onAction={onOpenAgent}
          />
        ) : (
          <div className="printer-grid">
            {printers.map(printer => {
              const agent = agents.find(candidate => candidate.id === printer.agentDeviceId);
              const isUnsupportedVirtual = printer.outputMode === "UNSUPPORTED_VIRTUAL";
              const isAuthorisedVirtual = isUnsupportedVirtual && printer.virtualTestGrantActive === true;
              const isSelected = printer.isActive && (!isUnsupportedVirtual || isAuthorisedVirtual);
              const canSelect = isPrinterReadyForSelection(printer, agents);
              const agentLastSeen = agent?.lastSeenUtc ? Date.parse(agent.lastSeenUtc) : Number.NaN;
              const agentOnline = !!agent && !agent.revokedUtc && agent.status === "Online" &&
                Number.isFinite(agentLastSeen) && agentLastSeen > Date.now() - 2 * 60 * 1000;
              const printerLastSeen = printer.lastSeenUtc ? Date.parse(printer.lastSeenUtc) : Number.NaN;
              const queueFresh = printer.outputMode === "PDF_EXPORT" ||
                (Number.isFinite(printerLastSeen) && printerLastSeen > Date.now() - 2 * 60 * 1000);
              const queueReady = printer.outputMode === "PDF_EXPORT" ||
                printer.status === "Ready" || printer.status === "Busy";
              const selectionBlockReason = isUnsupportedVirtual && !isAuthorisedVirtual
                ? t("This virtual queue needs an active platform-admin test grant for this shop before it can be selected.")
                : !printer.agentDeviceId
                  ? t("Keep the Print Agent connected. This Windows queue has not been linked yet.")
                  : !agentOnline
                    ? t("Connect the shop Windows computer and wait for its Print Agent to report online.")
                    : !queueFresh
                      ? t("The Agent is online, but this queue has not reported recently. Wait for the next automatic printer scan.")
                      : !queueReady
                        ? t("This Windows queue is not ready. Clear its Windows printer error or paper/connection issue, then wait for the next automatic scan.")
                        : undefined;
              const possibleDuplicates = printer.outputMode === "PHYSICAL" && printer.agentDeviceId && printer.connectionAddress
                ? printers.filter(other => other.id !== printer.id && other.outputMode === "PHYSICAL" &&
                    other.agentDeviceId === printer.agentDeviceId && !!other.connectionAddress &&
                    other.connectionAddress.trim().toLowerCase() === printer.connectionAddress!.trim().toLowerCase())
                : [];
              const ownerStatus = isAuthorisedVirtual
                ? t("Platform-approved virtual test queue")
                : !printer.agentDeviceId
                ? t("Waiting for Print Agent discovery")
                : !agentOnline
                  ? t("Windows computer not currently connected")
                  : printer.outputMode === "PDF_EXPORT"
                    ? t("PDF export destination ready on this computer")
                    : !queueFresh
                      ? t("Waiting for a fresh Windows printer scan")
                      : printer.status === "Ready" || printer.status === "Busy"
                        ? t("Windows queue reported ready")
                        : `Windows queue reports ${printer.status.toLowerCase()}`;
              return (
                <article className="printer-card" key={printer.id}>
                  <div className="printer-card-top">
                    <div className="printer-card-icon"><Printer size={20}/></div>
                    <Status
                      label={t(isUnsupportedVirtual && !isAuthorisedVirtual ? "Needs approval" : isSelected ? (canSelect ? "Selected" : "Selected · not ready") : canSelect ? (isAuthorisedVirtual ? "Test queue approved" : "Available") : !agentOnline ? "Computer offline" : "Queue not ready")}
                      tone={isUnsupportedVirtual && !isAuthorisedVirtual ? "red" : isSelected ? (canSelect ? "green" : "amber") : canSelect ? "slate" : "amber"}
                    />
                  </div>
                  <h3>{isUnsupportedVirtual ? (isAuthorisedVirtual ? t("Virtual test · ") : t("Unsupported virtual · ")) : printer.outputMode === "PDF_EXPORT" ? t("PDF export · ") : printer.connectionType === "IPP" ? t("IPP printer · ") : ""}{printer.displayName}</h3>
                  <p>{isUnsupportedVirtual
                    ? isAuthorisedVirtual
                      ? t("Approved for this shop's non-production test use. Jobs use the existing Windows spooler pipeline and are recorded as test output, not physical fulfilment.")
                      : t("This virtual Windows queue is visible but requires platform-admin approval for this shop before it can be selected.")
                    : printer.outputMode === "PDF_EXPORT"
                      ? t("Supported Microsoft Print to PDF destination. SmartPrint creates the PDF directly on this connected computer; physical printer status is not used.")
                      : printer.connectionType === "IPP"
                        ? t("IPP-connected Windows queue · installed driver {model}").replace("{model}", printer.model)
                        : printer.manufacturer === "Windows"
                          ? t("Installed Windows driver · {model}").replace("{model}", printer.model)
                          : t("{manufacturer} · {model}").replace("{manufacturer}", printer.manufacturer).replace("{model}", printer.model)}</p>
                  <div className="printer-meta">
                    <span>{ownerStatus}{printer.isDefault ? " · " + t("Default Windows queue") : ""}</span>
                    <span>{printer.queueName || printer.connectionAddress || t("Connection details not reported")}</span>
                    <span>Print Agent · {agent?.deviceName || t("Not linked")}</span>
                  </div>
                  <PrinterCapabilityPanel printer={printer}/>
                  {(printer.outputMode === "PHYSICAL" ||
                    (printer.outputMode === "UNSUPPORTED_VIRTUAL" && isAuthorisedVirtual)) && (
                    <PrinterDiagnosticControls
                      printer={printer}
                      token={token}
                      disabled={!isSelected || !canSelect || !agentOnline}
                      onOpenAgent={onOpenAgent}
                    />
                  )}
                  {possibleDuplicates.length > 0 && (
                    <div className="printer-duplicate-warning" role="note">
                      <strong>{t("Same Windows port")}</strong>
                      <span>{possibleDuplicates.map(item => item.displayName).join(", ")} use the same Windows port. These are separate queues and can have different driver settings or output behaviour.</span>
                    </div>
                  )}
                  <div className="printer-card-footer">
                    <span className={isSelected ? "enabled-label" : "disabled-label"}>
                      {t(isSelected ? "Selected for new jobs" : isUnsupportedVirtual && !isAuthorisedVirtual ? "Visible · awaiting approval" : "Not selected")}
                    </span>
                    {isSelected ? (
                      <button
                        className="subtle-button"
                        disabled={!!busyResourceId}
                        onClick={() => void onToggle(printer)}
                      >
                        {busyResourceId === printer.id ? <span className="button-spinner"/> : t("Deselect")}
                      </button>
                    ) : (
                      <div className="printer-card-actions">
                        {!quotaFull && selectedPrinters.length > 0 && canSelect && canUseSelection && (
                          <button
                            className="subtle-button"
                            disabled={!!busyResourceId}
                            onClick={() => {
                              setPendingReplacement(printer);
                              setReplacePrinterId("");
                            }}
                          >
                            Replace
                          </button>
                        )}
                        <button
                          className="subtle-button"
                          disabled={!!busyResourceId || !canSelect || !canUseSelection || (quotaFull && !canReplaceSelection)}
                          title={!canSelect ? selectionBlockReason : !canUseSelection ? t("Shop plan access must be confirmed before selecting a printer.") : quotaFull && !canReplaceSelection ? t("No other ready queue is available to replace a selected printer. Connect and ready another printer, or upgrade your plan for another slot.") : undefined}
                          onClick={() => {
                            if (!canSelect || !canUseSelection) return;
                            if (quotaFull && canReplaceSelection) {
                              setPendingReplacement(printer);
                              setReplacePrinterId("");
                            } else if (!quotaFull) {
                              void onToggle(printer);
                            }
                          }}
                        >
                          {busyResourceId === printer.id ? <span className="button-spinner"/> : quotaFull ? t("Replace") : t("Select")}
                        </button>
                      </div>
                    )}
                  </div>
                </article>
              );
            })}
          </div>
        )}
      </section>
      {pendingReplacement && (
        <Modal
          title={t("Replace a selected printer")}
          subtitle={t("Select which current queue to replace with {printer}.").replace("{printer}", pendingReplacement.displayName)}
          onClose={() => { if (!busyResourceId) { setPendingReplacement(null); setReplacePrinterId(""); } }}
        >
          <div className="modal-form">
            <label>
              {t("Queue to replace")}
              <select value={replacePrinterId} onChange={event => setReplacePrinterId(event.target.value)}>
                <option value="">{t("Choose a selected queue")}</option>
                {selectedPrinters.map(printer => <option key={printer.id} value={printer.id}>{printer.displayName}</option>)}
              </select>
            </label>
            <div className="modal-note">
              <ShieldCheck size={15}/>
              <span>{t("The new queue takes the freed plan slot. QR station defaults using the old queue move to the replacement. Existing orders and files are kept; review the Print Queue and assign waiting files to the new printer. Jobs already active or awaiting output confirmation must be resolved first. Files stored only on the original Windows computer stay there; choose another queue on that same PC or finish those files before replacing the computer.")}</span>
            </div>
            <div className="modal-actions">
              <button type="button" className="secondary-button" disabled={!!busyResourceId} onClick={() => { setPendingReplacement(null); setReplacePrinterId(""); }}>{t("Cancel")}</button>
              <button type="button" className="primary-button" disabled={!!busyResourceId || !replacePrinterId} onClick={() => void confirmReplacement()}>
                {busyResourceId === pendingReplacement.id ? <span className="button-spinner"/> : t("Replace selected queue")}
              </button>
            </div>
          </div>
        </Modal>
      )}
    </>
  );
}


function PrinterDiagnosticControls({
  printer, token, disabled, onOpenAgent
}: {
  printer: PrinterDevice;
  token: string;
  disabled: boolean;
  onOpenAgent: () => void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [history, setHistory] = useState<PrinterDiagnosticAttempt[]>([]);
  const [preflight, setPreflight] = useState<PrinterDiagnosticProfile | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [notes, setNotes] = useState("");
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const idempotencyKeyRef = useRef<string | null>(null);
  const preparedRequestRef = useRef<{
    templateVersion: string;
    paperSize: string;
    colourMode: string;
    isDuplex: boolean;
    idempotencyKey: string;
  } | null>(null);
  const pendingAttempt = history.find(item => !item.confirmedUtc) ?? null;
  const pendingNeedsAttention = !!pendingAttempt &&
    Date.now() - new Date(pendingAttempt.createdUtc).getTime() > 10 * 60 * 1000;
  const currentAttempt = pendingAttempt ?? history[0] ?? null;
  const virtualTest = printer.outputMode === "UNSUPPORTED_VIRTUAL";

  async function refreshHistory() {
    const results = await getPrinterDiagnosticHistory(token, printer.id);
    setHistory(results);
  }

  useEffect(() => {
    let active = true;
    void getPrinterDiagnosticHistory(token, printer.id)
      .then(results => { if (active) setHistory(results); })
      .catch(reason => { if (active) setError(reason instanceof Error ? reason.message : t("Could not load printer diagnostic history.")); });
    return () => { active = false; };
  }, [token, printer.id]);

  useEffect(() => {
    if (!pendingAttempt) return;
    let active = true;
    const poll = window.setInterval(() => {
      void getPrinterDiagnosticHistory(token, printer.id)
        .then(results => { if (active) setHistory(results); })
        .catch(() => { /* Keep the last server-confirmed state and retry on the next interval. */ });
    }, 5000);
    return () => {
      active = false;
      window.clearInterval(poll);
    };
  }, [token, printer.id, pendingAttempt?.id, pendingAttempt?.confirmedUtc]);

  async function prepareDiagnostic() {
    if (busy || disabled || pendingAttempt) return;
    setBusy(true);
    setError("");
    try {
      const profile = await getPrinterDiagnosticProfile(token, printer.id);
      setPreflight(profile);
      setMessage("");
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("The printer test could not be prepared."));
    } finally {
      setBusy(false);
    }
  }

  async function runDiagnostic() {
    if (busy || disabled || pendingAttempt || !preflight) return;
    setBusy(true);
    setError("");
    setMessage("");
    try {
      let request = preparedRequestRef.current;
      if (!request) {
        const idempotencyKey = idempotencyKeyRef.current ??
          (typeof crypto !== "undefined" && "randomUUID" in crypto
            ? crypto.randomUUID()
            : `diag-${Date.now()}-${Math.random().toString(36).slice(2, 14)}`);
        idempotencyKeyRef.current = idempotencyKey;
        request = {
          templateVersion: preflight.templateVersion,
          paperSize: preflight.paperSize,
          colourMode: preflight.colourMode,
          isDuplex: preflight.isDuplex,
          idempotencyKey
        };
        preparedRequestRef.current = request;
      }
      const started = await startPrinterDiagnostic(token, printer.id, request);
      preparedRequestRef.current = null;
      idempotencyKeyRef.current = null;
      setPreflight(null);
      setHistory(current => [{
        id: started.id,
        printJobId: started.printJobId,
        templateVersion: started.templateVersion,
        ownerDecision: null,
        ownerNotes: null,
        createdUtc: started.createdUtc,
        confirmedUtc: null,
        job: {
          status: started.status,
          spoolerStatus: "AWAITING_AGENT",
          spoolerSubmittedUtc: null,
          spoolerObservedUtc: null,
          failureCode: null,
          outputType: started.outputType,
          copies: 1,
          pageCount: started.pageCount,
          localQueueReceivedUtc: null,
          printDispatchAttempts: 0,
          spoolerPagesPrinted: 0,
          spoolerTotalPages: 0,
          outputCompletedUtc: null
        }
      }, ...current.filter(item => item.id !== started.id)]);
      setMessage(started.duplicateRequest
        ? t("This test was already accepted. Checking its current result.")
        : t("One test copy has been sent to the paired Windows computer."));
      await refreshHistory();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("The printer test could not be started."));
    } finally {
      setBusy(false);
    }
  }

  async function confirmResult(decision: string) {
    if (!currentAttempt || confirming) return;
    setConfirming(true);
    setError("");
    try {
      await confirmPrinterDiagnostic(token, currentAttempt.id, decision, notes.trim() || undefined);
      setMessage(t("Test result saved."));
      setNotes("");
      await refreshHistory();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("The test result could not be saved."));
    } finally {
      setConfirming(false);
    }
  }

  const execution = currentAttempt?.job;
  const staged = !!execution?.localQueueReceivedUtc && execution.printDispatchAttempts > 0;
  const spoolerStatus = execution?.spoolerStatus?.toUpperCase() ?? "";
  const hasSpoolerReceipt = !!execution?.spoolerSubmittedUtc && !!execution.spoolerObservedUtc;
  const blockedBeforeSubmission = execution?.status === "LOCAL_QUEUE_READY" &&
    spoolerStatus === "BLOCKED_BEFORE_SUBMISSION" && !execution.spoolerSubmittedUtc;
  const verifiedPrinted = spoolerStatus === "PRINTED" && hasSpoolerReceipt &&
    (execution?.spoolerTotalPages === 0 || execution?.spoolerPagesPrinted >= execution?.spoolerTotalPages);
  const verifiedSpoolerAbsence = execution?.status === "OUTPUT_UNCONFIRMED" &&
    spoolerStatus === "OUTPUT_UNCONFIRMED" && hasSpoolerReceipt &&
    execution.failureCode === "SPOOLER_JOB_NO_LONGER_VISIBLE";
  const virtualArtifactReady = virtualTest && execution?.status === "TEST_OUTPUT_READY" &&
    !!execution.outputCompletedUtc && spoolerStatus === "PRINTED";
  const terminalOutput = blockedBeforeSubmission || verifiedPrinted || verifiedSpoolerAbsence || virtualArtifactReady;
  const canConfirm = !!currentAttempt && staged && terminalOutput && !currentAttempt.confirmedUtc;
  const stateLabel = (status?: string | null) => {
    const labels: Record<string, string> = {
      DIAGNOSTIC_QUEUED: "Test queued",
      LOCAL_QUEUE_READY: "Preparing test",
      PRINTING: "Sending test",
      TEST_OUTPUT_READY: "Test file ready",
      OUTPUT_UNCONFIRMED: "Output needs checking",
      DIAGNOSTIC_VERIFIED: "Test passed",
      DIAGNOSTIC_RECEIVED: "Test file received",
      DIAGNOSTIC_ISSUE: "Issue reported",
      DIAGNOSTIC_NO_OUTPUT: "No output"
    };
    return t(status ? labels[status] ?? "Test in progress" : "Waiting for the paired computer");
  };
  const decisionLabel = (decision: string | null) => {
    if (!decision) return "";
    const labels: Record<string, string> = {
      PRINTED_CORRECTLY: "Printed correctly",
      PRINTED_WITH_ISSUE: "Printed with an issue",
      NOTHING_PRINTED: "Nothing printed",
      OUTPUT_RECEIVED_CORRECTLY: "File received correctly",
      OUTPUT_RECEIVED_WITH_ISSUE: "File received with an issue",
      NO_OUTPUT_RECEIVED: "No file received"
    };
    return t(labels[decision] ?? "Result recorded");
  };
  const preflightSummary = preflight
    ? `${virtualTest ? t("1 test file") : t("1 copy")} · ${preflight.pageCount} ${preflight.pageCount === 1 ? t("page") : t("pages")} · ${preflight.paperSize} · ${preflight.colourMode}${virtualTest ? "" : ` · ${preflight.isDuplex ? t("Duplex") : t("Single-sided")}`}`
    : "";

  return (
    <div className="printer-diagnostic-panel">
      <div className="printer-diagnostic-heading">
        <div>
          <strong>{t("Printer test")}</strong>
          <span>{virtualTest
            ? t("Create a test file through this virtual printer.")
            : t("Send one test copy using the installed Windows printer settings.")}</span>
        </div>
        {!preflight && (
          <button type="button" className="subtle-button" disabled={busy || disabled || !!pendingAttempt} onClick={() => void prepareDiagnostic()}>
            {busy ? <span className="button-spinner"/> : <Activity size={15}/>}
            {t("Test printer")}
          </button>
        )}
      </div>

      {preflight && !pendingAttempt && (
        <div className="printer-diagnostic-preflight">
          <span>{preflightSummary}</span>
          <span>{virtualTest
            ? t("A test PDF will be created. Check that the file opens correctly.")
            : t("Load the selected paper before continuing.")}</span>
          <div>
            <button type="button" className="subtle-button" disabled={busy} onClick={() => setPreflight(null)}>{t("Cancel")}</button>
            <button type="button" className="subtle-button primary" disabled={busy || disabled} onClick={() => void runDiagnostic()}>
              {busy ? <span className="button-spinner"/> : null}
              {t(virtualTest ? "Create test file" : "Print one test copy")}
            </button>
          </div>
        </div>
      )}

      {currentAttempt && (
        <div className="printer-diagnostic-state" role="status">
          <strong>{stateLabel(execution?.status)}</strong>
          {currentAttempt.confirmedUtc && <span>{decisionLabel(currentAttempt.ownerDecision)}</span>}
        </div>
      )}

      {canConfirm && (
        <div className="printer-diagnostic-confirmation">
          <strong>{t(virtualTest ? "Check the test file, then confirm." : "Check the printed sheet, then confirm.")}</strong>
          <div className="printer-diagnostic-actions">
            {virtualTest ? (
              virtualArtifactReady ? (
                <>
                  <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("OUTPUT_RECEIVED_CORRECTLY")}>{t("File received correctly")}</button>
                  <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("OUTPUT_RECEIVED_WITH_ISSUE")}>{t("File has an issue")}</button>
                </>
              ) : blockedBeforeSubmission ? (
                <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("NO_OUTPUT_RECEIVED")}>{t("No file received")}</button>
              ) : (
                <>
                  <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("OUTPUT_RECEIVED_WITH_ISSUE")}>{t("File has an issue")}</button>
                  <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("NO_OUTPUT_RECEIVED")}>{t("No file received")}</button>
                </>
              )
            ) : verifiedPrinted ? (
              <>
                <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("PRINTED_CORRECTLY")}>{t("Printed correctly")}</button>
                <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("PRINTED_WITH_ISSUE")}>{t("Printed with an issue")}</button>
              </>
            ) : blockedBeforeSubmission ? (
              <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("NOTHING_PRINTED")}>{t("Nothing printed")}</button>
            ) : (
              <>
                <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("PRINTED_WITH_ISSUE")}>{t("Printed with an issue")}</button>
                <button type="button" className="subtle-button" disabled={confirming} onClick={() => void confirmResult("NOTHING_PRINTED")}>{t("Nothing printed")}</button>
              </>
            )}
          </div>
          <label className="printer-diagnostic-notes">
            <span>{t("Optional note")}</span>
            <input value={notes} maxLength={500} onChange={event => setNotes(event.target.value)} placeholder={t("e.g. faint text or paper jam")} />
          </label>
          {confirming && <span className="field-hint">{t("Saving result…")}</span>}
        </div>
      )}

      {pendingAttempt && !canConfirm && (
        <div className={pendingNeedsAttention ? "printer-diagnostic-pending is-stale" : "printer-diagnostic-pending"}>
          <p className="field-hint">{pendingNeedsAttention
            ? t("This test is taking longer than expected. Reconnect the Windows computer so the pending job can report its final result before another test.")
            : staged
              ? t("Waiting for Windows to finish this test. Keep the printer and paired computer on.")
              : t("Test queued. Keep the paired Windows computer and Print Agent online; SmartPrint will check automatically.")}</p>
          {pendingNeedsAttention && <button type="button" className="text-action" onClick={onOpenAgent}>{t("Open Print Agent")}</button>}
        </div>
      )}
      {message && <p className="inline-success" role="status">{message}</p>}
      {error && <p className="inline-error" role="alert">{error}</p>}

      {history.length > 0 && (
        <details className="printer-diagnostic-details">
          <summary>{t("Test details and history")}</summary>
          {currentAttempt?.job && (
            <p>{t("Windows status")}: {currentAttempt.job.spoolerStatus || t("Not reported")}
              {currentAttempt.job.failureCode ? ` · ${t("Reason")}: ${currentAttempt.job.failureCode}` : ""}</p>
          )}
          <ul>
            {history.slice(0, 5).map(item => (
              <li key={item.id}>
                <span>{new Date(item.createdUtc).toLocaleString()}</span>
                <strong>{item.confirmedUtc ? decisionLabel(item.ownerDecision) : stateLabel(item.job?.status)}</strong>
                {item.ownerNotes && <small>{item.ownerNotes}</small>}
              </li>
            ))}
          </ul>
          {pendingAttempt && <button type="button" className="text-action" onClick={() => void refreshHistory()}>{t("Refresh test status")}</button>}
        </details>
      )}
    </div>
  );
}

function QrStationArtworkPreview({station,profile}:{station:QrStation;profile:ShopProfile|null}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [posterSrc,setPosterSrc]=useState<string|null>(null);
  const previewQrHostRef=useRef<HTMLDivElement|null>(null);

  useEffect(()=>{
    let cancelled=false;
    const qrSvg=previewQrHostRef.current?.querySelector<SVGSVGElement>(".qr-code-frame svg");
    if(!qrSvg){setPosterSrc(null);return;}
    const qrSvgMarkup=new XMLSerializer().serializeToString(qrSvg);
    void loadQrStationLogoDataUri(profile?.logoUrl).then(logoDataUri=>{
      if(cancelled)return;
      try{
        const poster=buildQrStationArtworkSvg({
          shopName:profile?.displayName?.trim()||station.displayName,
          stationName:station.displayName,
          isEnabled:station.isEnabled,
          logoDataUri
        },qrSvgMarkup);
        setPosterSrc("data:image/svg+xml;charset=utf-8,"+encodeURIComponent(poster));
      }catch{
        setPosterSrc(null);
      }
    });
    return ()=>{cancelled=true;};
  },[station.id,station.publicUrl,station.displayName,station.isEnabled,profile?.displayName,profile?.logoUrl]);

  return <div id={`qr-station-${station.id}`} className="station-qr-area qr-station-preview">
    {posterSrc
      ?<img className="station-poster-preview" src={posterSrc} alt={`${station.displayName} QR poster preview for ${profile?.displayName?.trim()||station.displayName}`}/>
      :<div className="station-poster-preview-loading" role="status">{t("Preparing the matching QR poster preview…")}</div>}
    <div className="qr-preview-source" ref={previewQrHostRef} aria-hidden="true">
      <div className="qr-code-frame"><QRCode value={station.publicUrl??""} size={172} level="H" fgColor="#101c35" bgColor="#ffffff"/></div>
    </div>
  </div>;
}

function StationsPage({ stations, profile, busyResourceId, onAdd, onToggle, onGenerate }: {
  stations:QrStation[]; profile:ShopProfile|null; busyResourceId:string|null; onAdd:()=>void; onToggle:(station:QrStation)=>void; onGenerate:(station:QrStation)=>void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [printingStationId,setPrintingStationId]=useState<string|null>(null);
  const [printingArtwork,setPrintingArtwork]=useState<{stationId:string;artwork:QrStationArtworkSource}|null>(null);
  const [formatByStation,setFormatByStation]=useState<Record<string,QrStationImageFormat>>({});
  const [exportingStationId,setExportingStationId]=useState<string|null>(null);
  const [copyFeedback,setCopyFeedback]=useState<Record<string,{kind:"success"|"error";message:string}>>({});
  const [exportFeedback,setExportFeedback]=useState<Record<string,{kind:"success"|"info"|"error";message:string}>>({});
  const [preparedShareImage,setPreparedShareImage]=useState<{stationId:string;signature:string;file:File}|null>(null);
  const imageFormatFor=(station:QrStation)=>formatByStation[station.id]??"png";
  const stationImageSignature=(station:QrStation,format=imageFormatFor(station))=>JSON.stringify([
    station.publicUrl??"",station.displayName,station.isEnabled,
    profile?.displayName??"",profile?.logoUrl??"",format
  ]);
  const copyStationUrl=async(station:QrStation)=>{
    if(!station.publicUrl)return;
    const copied=await copyText(station.publicUrl);
    setCopyFeedback(current=>({...current,[station.id]:copied
      ?{kind:"success",message:"Station link copied."}
      :{kind:"error",message:"Could not copy automatically. Open the link and copy it manually."}}));
  };
  const createStationArtwork=async(station:QrStation):Promise<QrStationArtworkSource>=>{
    if(!station.publicUrl)throw new Error("This station does not have a public QR link yet.");
    const root=document.getElementById("qr-station-"+station.id);
    const qrSvg=root?.querySelector<SVGSVGElement>(".qr-code-frame svg");
    if(!qrSvg)throw new Error("The station QR code is not ready. Refresh the page and try again.");
    const logoDataUri=await loadQrStationLogoDataUri(profile?.logoUrl);
    return {
      options:{
        shopName:profile?.displayName?.trim()||station.displayName,
        stationName:station.displayName,
        isEnabled:station.isEnabled,
        logoDataUri
      },
      qrSvgMarkup:new XMLSerializer().serializeToString(qrSvg)
    };
  };
  const createStationImage=async(station:QrStation,format=imageFormatFor(station))=>{
    const artwork=await createStationArtwork(station);
    return createQrStationImageFile(artwork.options,artwork.qrSvgMarkup,format);
  };
  const prepareStationPrint=async(station:QrStation)=>{
    setPrintingStationId(station.id);
    setPrintingArtwork(null);
    try{
      const artwork=await createStationArtwork(station);
      setPrintingArtwork({stationId:station.id,artwork});
    }catch(exception){
      setPrintingStationId(null);
      setExportFeedback(current=>({...current,[station.id]:{
        kind:"error",
        message:exception instanceof Error?exception.message:"Could not prepare this QR poster."
      }}));
    }
  };
  const exportStationImage=async(station:QrStation)=>{
    setExportingStationId(station.id);
    setExportFeedback(current=>({...current,[station.id]:{kind:"info",message:"Preparing your QR poster download…"}}));
    try{
      const file=await createStationImage(station);
      downloadQrStationImage(file);
      setExportFeedback(current=>({...current,[station.id]:{kind:"success",message:"Your "+imageFormatFor(station).toUpperCase()+" poster is ready."}}));
    }catch(exception){
      setExportFeedback(current=>({...current,[station.id]:{kind:"error",message:exception instanceof Error?exception.message:"Could not prepare this QR poster."}}));
    }finally{setExportingStationId(null);}
  };
  const prepareStationShare=async(station:QrStation,format=imageFormatFor(station))=>{
    const signature=stationImageSignature(station,format);
    if(preparedShareImage?.stationId===station.id&&preparedShareImage.signature===signature)return;
    setExportingStationId(station.id);
    setPreparedShareImage(null);
    setExportFeedback(current=>({...current,[station.id]:{kind:"info",message:"Preparing the poster so it is ready to share…"}}));
    try{
      const file=await createStationImage(station,format);
      setPreparedShareImage({stationId:station.id,signature,file});
      setExportFeedback(current=>({...current,[station.id]:{kind:"success",message:"Poster ready. Share will open your device’s sharing options."}}));
    }catch(exception){
      setPreparedShareImage(null);
      setExportFeedback(current=>({...current,[station.id]:{kind:"error",message:exception instanceof Error?exception.message:"Could not prepare this QR poster for sharing."}}));
    }finally{setExportingStationId(null);}
  };
  const sharePreparedStationImage=(station:QrStation)=>{
    const prepared=preparedShareImage;
    if(!prepared||prepared.stationId!==station.id||prepared.signature!==stationImageSignature(station)){
      setPreparedShareImage(null);
      setExportFeedback(current=>({...current,[station.id]:{kind:"info",message:"Station details changed. Prepare the image again before sharing."}}));
      return;
    }
    setExportingStationId(station.id);
    const file=prepared.file;
    // Invoke navigator.share directly from this click. Image generation has
    // already finished, so the browser's transient user activation is intact.
    void shareQrStationImage(file).then(result=>{
      if(result==="shared"){
        setPreparedShareImage(null);
        setExportFeedback(current=>({...current,[station.id]:{kind:"success",message:"QR poster shared successfully."}}));
      }else if(result==="cancelled"){
        setExportFeedback(current=>({...current,[station.id]:{kind:"info",message:"Sharing was cancelled. The image was not sent."}}));
      }else{
        downloadQrStationImage(file);
        setPreparedShareImage(null);
        setExportFeedback(current=>({...current,[station.id]:{kind:"info",message:"File sharing is not supported here, so the QR poster was downloaded instead."}}));
      }
    }).catch(exception=>{
      setExportFeedback(current=>({...current,[station.id]:{kind:"error",message:exception instanceof Error?exception.message:"Could not share this QR image."}}));
    }).finally(()=>setExportingStationId(null));
  };
  useEffect(()=>{
    if(!printingStationId||!printingArtwork||printingArtwork.stationId!==printingStationId)return;
    let cancelled=false;
    const onAfterPrint=()=>{
      setPrintingStationId(null);
      setPrintingArtwork(null);
    };
    const printSelectedStation=async()=>{
      const image=document.querySelector<HTMLImageElement>(".qr-print-document img.qr-print-poster");
      if(!image)throw new Error("The QR poster is not ready to print.");
      await image.decode();
      await document.fonts.ready.catch(()=>undefined);
      await new Promise<void>(resolve=>{
        window.requestAnimationFrame(()=>window.requestAnimationFrame(()=>resolve()));
      });
      if(!cancelled)window.print();
    };
    window.addEventListener("afterprint",onAfterPrint);
    void printSelectedStation().catch(exception=>{
      if(cancelled)return;
      setExportFeedback(current=>({...current,[printingStationId]:{
        kind:"error",
        message:exception instanceof Error?exception.message:"Could not print this QR poster."
      }}));
      setPrintingStationId(null);
      setPrintingArtwork(null);
    });
    return ()=>{
      cancelled=true;
      window.removeEventListener("afterprint",onAfterPrint);
    };
  },[printingStationId,printingArtwork]);

  return (
    <>
    <section className="panel collection-panel">
      <div className="collection-header"><div><h2>{t("Customer QR stations")}</h2><p>{t("Each station has a permanent public link. Every scan creates a separate short-lived customer session.")}</p></div><button className="primary-button" onClick={onAdd}><Plus size={16}/> {t("Create station")}</button></div>
      {stations.length===0 ? <EmptyModule icon={<QrCode size={24}/>} title={t("Your first QR station starts here")} text={t("Create a station for a counter or printer. The QR code identifies the station; every scan gets a separate expiring customer session.")} actionLabel={t("Create QR station")} onAction={onAdd}/> :
      <div className="station-grid">{stations.map(station=><article className="station-card" key={station.id}><div className="station-card-head"><div className="station-icon"><QrCode size={18}/></div><div className="station-head-copy"><strong>{station.displayName}</strong><span>{station.isEnabled?t("Accepting new sessions"):t("Disabled")}</span></div><Status label={station.isEnabled?t("Enabled"):t("Disabled")} tone={station.isEnabled?"green":"slate"}/></div>
        {station.publicUrl
          ?<QrStationArtworkPreview station={station} profile={profile}/>
          :<div className="station-qr-area"><div className="qr-missing"><QrCode size={34}/><strong>{t("QR code not stored")}</strong><span>{t("This older station needs a one-time QR replacement. Its existing printed code cannot be recovered from the old hash.")}</span><button className="secondary-button" disabled={!!busyResourceId} onClick={()=>onGenerate(station)}>{busyResourceId===station.id?<span className="button-spinner"/>:"Reissue permanent QR"}</button></div></div>}
        <div className="station-foot"><span>Code ending ···{station.publicCodeHint}</span><button className="subtle-button" disabled={!!busyResourceId} onClick={()=>onToggle(station)}>{busyResourceId===station.id?<span className="button-spinner"/>:station.isEnabled?"Disable station":"Enable station"}</button></div>
        {station.publicUrl && <>
           <div className="station-actions"><button className="secondary-button" onClick={()=>void copyStationUrl(station)}> <Copy size={14}/> {t("Copy link")}</button></div>
           {copyFeedback[station.id]&&<small className={"qr-share-feedback "+copyFeedback[station.id].kind} role="status" aria-live="polite">{copyFeedback[station.id].message}</small>}
           <details className="qr-poster-actions" onToggle={event=>{if(event.currentTarget.open)void prepareStationShare(station);}}>
             <summary><Printer size={15}/> {t("Print / download QR poster")} <span>{t("Print · PNG / JPG · Share")}</span></summary>
             <div className="qr-poster-format-row">
               <label htmlFor={`qr-poster-format-${station.id}`}>
                 <span>{t("File format")} <small>{t("Download and Share only · Print uses the poster as shown")}</small></span>
                 <select id={`qr-poster-format-${station.id}`} value={imageFormatFor(station)} onChange={event=>{const format=event.currentTarget.value as QrStationImageFormat;setFormatByStation(current=>({...current,[station.id]:format}));setPreparedShareImage(current=>current?.stationId===station.id?null:current);void prepareStationShare(station,format);}} disabled={!!exportingStationId}>
                   <option value="png">{t("PNG · best QR clarity")}</option>
                   <option value="jpg">{t("JPG · smaller file")}</option>
                 </select>
               </label>
             </div>
             <div className="qr-poster-action-buttons">
               <button className="primary-button" disabled={!!printingStationId||!!exportingStationId||!!busyResourceId} onClick={()=>void prepareStationPrint(station)}>{printingStationId===station.id?<span className="button-spinner"/>:<Printer size={15}/>} Print poster</button>
               <button className="secondary-button" disabled={!!exportingStationId||!!busyResourceId} onClick={()=>void exportStationImage(station)}>{exportingStationId===station.id?<span className="button-spinner"/>:<Download size={15}/>} Download poster</button>
               {preparedShareImage?.stationId===station.id&&preparedShareImage.signature===stationImageSignature(station)
                 ?<button className="secondary-button" disabled={!!exportingStationId||!!busyResourceId} onClick={()=>sharePreparedStationImage(station)}><Share2 size={15}/> {t("Share poster")}</button>
                 :<button className="secondary-button" disabled={!!busyResourceId||exportingStationId===station.id} onClick={()=>void prepareStationShare(station)}>{exportingStationId===station.id?<span className="button-spinner"/>:exportFeedback[station.id]?.kind==="error"?<RefreshCw size={15}/>:<Share2 size={15}/>} {exportingStationId===station.id?"Preparing share…":exportFeedback[station.id]?.kind==="error"?"Retry share preparation":"Refresh share"}</button>}
             </div>
             {!station.isEnabled&&<small className="qr-share-paused-note">{t("This station is disabled. The poster is marked “Temporarily paused” so customers are not led to an inactive service.")}</small>}
             {exportFeedback[station.id]&&<small className={"qr-share-feedback "+exportFeedback[station.id].kind} role="status" aria-live="polite">{exportFeedback[station.id].message}</small>}
             <small className="qr-poster-actions-help">{t("The preview above is the exact poster used for printing, downloading and sharing. We prepare the selected file format when you open this panel, so Share is ready as a single action. The QR uses this station’s permanent public link, not a customer session token. If your device cannot share files, SmartPrint downloads the poster instead.")}</small>
           </details>
         </>}
      </article>)}</div>}
    </section>
    <QrStationPrintDocument artwork={printingArtwork?.stationId===printingStationId?printingArtwork.artwork:null}/>
    </>
  );
}



function PrintAgentPage({agents,enrollmentCode,busy,onGenerate,onRevoke,desktop,localAgentStatus,localAgentConflict,localAgentRevoked,localAgentNeedsRepair,localAgentEnrollmentIssue,localAgentOwnershipChecking,onConnect,onReconnectAfterRevocation}:{
  agents:AgentDevice[];
  enrollmentCode:{code:string;expiresUtc:string}|null;
  busy:boolean;
  onGenerate:()=>void;
  onRevoke:(agent:AgentDevice)=>void;
  desktop:boolean;
  localAgentStatus:DesktopAgentStatus|null;
  localAgentConflict:boolean;
  localAgentRevoked:boolean;
  localAgentNeedsRepair:boolean;
  localAgentEnrollmentIssue:{code:string|null;message:string}|null;
  localAgentOwnershipChecking:boolean;
  onConnect:()=>void;
  onReconnectAfterRevocation:()=>void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const { release, state: releaseState, refresh } = usePublicRelease();
  const windowsInstallerUrl = release?.windowsAvailable && release.version
    ? getPublicDownloadPath("windows", release.version, release.downloadBaseUrl)
    : null;
  const windowsInstallerFallbackUrl = release?.windowsAvailable && release.version
    ? getPublicDownloadPath("windows", release.version)
    : null;
  const connected = Boolean(localAgentStatus?.paired && agents.some(agent =>
    !agent.revokedUtc && agent.id === localAgentStatus.agentDeviceId
  ));
  return <div className="agent-page">
    <section className="panel agent-pairing-panel">
      {desktop ? <div className="desktop-agent-connect">
        <div className="agent-intro">
          <div className="agent-hero-icon"><Monitor size={22}/></div>
          <div><div className="eyebrow"><span className="eyebrow-line"/>{t("THIS COMPUTER")}</div><h2>{t("Your print computer")}</h2><p>{t("SmartPrint connects this computer to your shop automatically. The background print service stays active even when you close this window.")}</p></div>
        </div>
        <div className="desktop-agent-status">
          <span className={connected ? "desktop-agent-indicator connected" : localAgentConflict ? "desktop-agent-indicator conflict" : "desktop-agent-indicator"} />
          <div><strong>{connected ? t("Connected to this shop") : localAgentRevoked ? t("Disconnected by the shop owner") : localAgentNeedsRepair ? t("Connection needs repair") : localAgentEnrollmentIssue ? (localAgentConflict ? t("Existing shop pairing needs revocation") : t("Connection needs attention")) : localAgentConflict ? t("Connected to another shop") : localAgentOwnershipChecking ? t("Checking shop registration") : localAgentStatus?.serviceReachable ? t("Ready to connect") : t("Print service not responding")}</strong>
            <small>{connected ? (localAgentStatus?.localApiReachable === false ? "Connected to your shop. Cloud printing remains available; local-network uploads are reconnecting automatically." : "Printer discovery and job hand-off are managed by the SmartPrint service.") : localAgentRevoked ? t("This computer was revoked and will not reconnect automatically. Select Reconnect this computer when your shop is ready; no pairing code is needed.") : localAgentNeedsRepair ? t("Windows could not read this computer's saved pairing. Check the paired-computer list below; revoke its old entry if it is still active, then select Repair connection. No code entry is needed.") : localAgentEnrollmentIssue ? localAgentEnrollmentIssue.message : localAgentConflict ? "This computer is still connected to another shop. Ask its previous owner to disconnect it before connecting it here." : localAgentOwnershipChecking ? "SmartPrint is verifying this computer's saved registration with your shop. It will not transfer or re-enroll it while ownership is uncertain." : localAgentStatus?.serviceReachable ? "Your owner session will authorize a one-time connection. No pairing code is shown." : "SmartPrint is starting or reconnecting its background service. It will retry automatically."}</small>
          </div>
        </div>
        <div className="desktop-agent-actions">
          {localAgentConflict ? (
            <button className="primary-button" disabled={busy} onClick={onReconnectAfterRevocation}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> {t("I’ve had the previous shop revoke this computer")}</>}
            </button>
          ) : localAgentOwnershipChecking ? (
            <span role="status" aria-live="polite">{t("Checking shop registration…")}</span>
          ) : localAgentRevoked ? (
            <button className="primary-button" disabled={busy} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> {t("Reconnect this computer")}</>}
            </button>
          ) : localAgentNeedsRepair ? (
            <button className="primary-button" disabled={busy} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> {t("Repair connection")}</>}
            </button>
          ) : (
            <button className="primary-button" disabled={busy || connected || Boolean(localAgentStatus?.paired)} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><Monitor size={15}/>{localAgentStatus?.serviceReachable ? "Connect this computer" : "Retry connection"}</>}
            </button>
          )}
          <span>{t("This computer will handle print orders for your shop while connected.")}</span>
        </div>
      </div> : <>
      <div className="agent-intro">
        <div className="agent-hero-icon"><Monitor size={22}/></div>
        <div><div className="eyebrow"><span className="eyebrow-line"/>{t("LOCAL PRINT CONNECTIVITY")}</div><h2>{t("Connect your shop computer")}</h2><p>{t("Pair the Windows computer connected to your printers. SmartPrint will keep it ready to receive customer print orders.")}</p></div>
      </div>
      <div className="agent-steps">
        <div><span>01</span><strong>{t("Generate a pairing code")}</strong><small>{t("One-time code, valid for 10 minutes.")}</small></div>
        <div><span>02</span><strong>{t("Run the guided Windows installer")}</strong><small>{t("Enter the code in the setup wizard. No PowerShell commands are required.")}</small></div>
        <div><span>03</span><strong>{t("Confirm the connection")}</strong><small>{t("Return here and wait for the computer heartbeat and printer discovery.")}</small></div>
      </div>
      {enrollmentCode&&<div className="agent-code-panel">
        <div><span>{t("ONE-TIME PAIRING CODE")}</span><strong>{enrollmentCode.code}</strong><small>Expires {formatIstTime(enrollmentCode.expiresUtc)}</small></div>
        <button className="secondary-button" onClick={()=>void copyText(enrollmentCode.code)}><Copy size={14}/> {t("Copy code")}</button>
      </div>}
      <div className="agent-command-panel">
        <div><span>{t("GUIDED WINDOWS INSTALLER")}</span><strong>{t("Download and run SmartPrint Windows Setup on the shop computer.")}</strong><small>{t("Run setup as administrator, enter the one-time pairing code in its wizard, and let it install and start the background service.")}</small></div>
        <div className="agent-installer-download">
          <DownloadAction
            platform="windows"
            label="Download Windows installer"
            available={Boolean(windowsInstallerUrl)}
            downloadUrl={windowsInstallerUrl}
            fallbackDownloadUrl={windowsInstallerFallbackUrl}
            releaseState={releaseState}
            onRefresh={refresh}
          />
        </div>
      </div>
      <div className="agent-security-note"><ShieldCheck size={16}/><span>{t("Your setup code works once and expires after 10 minutes. You can disconnect a computer from this page if it is replaced or lost.")}</span></div>
      <div className="agent-generate-row"><span>{t("Pairing codes are single use. Generating a new one does not revoke already paired computers.")}</span><button className="primary-button" disabled={busy} onClick={onGenerate}>{busy?<span className="button-spinner"/>:<><Plus size={15}/> {t("Generate pairing code")}</>}</button></div>
      </>}
    </section>
    {desktop && <ShopConnectionPanel connected={connected} localApiReady={localAgentStatus?.localApiReachable===true}/>}
    {!desktop && <section className="panel local-network-hint"><div className="local-network-mark"><Wifi size={20}/></div><div><h2>{t("Shop connection")}</h2><p>{t("Open SmartPrint on the Windows computer connected to your printers to choose and test the shop Wi-Fi or hotspot.")}</p></div></section>}
    <section className="panel agent-device-panel">
      <div className="collection-header"><div><h2>{t("Paired Windows computers")}</h2><p>{t("Agent connection history and the number of discovered printer queues.")}</p></div><span className="setup-count">{agents.filter(agent=>!agent.revokedUtc).length} paired</span></div>
      {agents.length===0?<div className="agent-empty"><Monitor size={25}/><strong>{t("No Windows computer paired yet")}</strong><span>{t("Generate a pairing code above and run the agent on the computer connected to your printers.")}</span></div>:
        <div className="agent-device-list">{agents.map(agent=><article className="agent-device-card" key={agent.id}>
          <div className="agent-device-icon"><Monitor size={19}/></div>
          <div className="agent-device-copy"><strong>{agent.deviceName}</strong><span>{agent.printerCount} printer {agent.printerCount===1?"queue":"queues"} discovered · Paired {formatIstDate(agent.createdUtc)}</span><small>{agent.lastSeenUtc?("Last contact "+formatIstDateTime(agent.lastSeenUtc)):"No heartbeat received yet"}</small>{agent.localEndpoints?.length?<small>{t("Shop connection found · run the phone check on the shop computer")}</small>:<small>{t("Connect this computer to the shop Wi-Fi or hotspot to enable phone uploads")}</small>}</div>
          <Status label={agent.revokedUtc?t("Revoked"):t(agent.status)} tone={agent.revokedUtc?"red":agent.status==="Online"?"green":"slate"}/>
          {!agent.revokedUtc&&<button className="subtle-button reject-action" disabled={busy} onClick={()=>onRevoke(agent)}>{t("Revoke")}</button>}
        </article>)}</div>}
    </section>
  </div>;
}

function ShopConnectionPanel({connected,localApiReady}:{connected:boolean;localApiReady:boolean}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [snapshot,setSnapshot]=useState<DesktopLocalNetworkSnapshot|null>(null);
  const [agentStatus,setAgentStatus]=useState<DesktopAgentStatus|null>(null);
  const [loading,setLoading]=useState(true);
  const [saving,setSaving]=useState(false);
  const [error,setError]=useState("");
  const [test,setTest]=useState<DesktopLocalPhoneTest|null>(null);
  const [verified,setVerified]=useState(false);
  const [expired,setExpired]=useState(false);

  async function refresh() {
    try {
      const [next,status]=await Promise.all([getLocalNetworkSnapshot(),getLocalAgentStatus()]);
      setSnapshot(next);
      setAgentStatus(status);
      setVerified(Boolean(next.phoneVerifiedUtc));
      setError("");
      return next;
    } catch(exception) {
      setError(exception instanceof Error?t(exception.message):t("Could not check the shop connection."));
      return null;
    } finally { setLoading(false); }
  }

  useEffect(()=>{void refresh();},[]);

  useEffect(()=>{
    if(!connected||!localApiReady) {
      setTest(null);
      setVerified(false);
      setExpired(false);
      return;
    }
    void refresh();
  },[connected,localApiReady]);

  useEffect(()=>{
    if(!test) {
      const timer=window.setInterval(()=>void refresh(),30000);
      return ()=>window.clearInterval(timer);
    }
    let active=true;
    let polling=false;
    const started=Date.parse(test.startedUtc);
    const poll=async()=>{
      if(polling||!active)return;
      if(Date.now()>=Date.parse(test.expiresUtc)){
        setExpired(true);
        setTest(null);
        return;
      }
      polling=true;
      try {
        const next=await getLocalNetworkSnapshot();
        if(!active)return;
        setSnapshot(next);
        if(next.phoneVerifiedUtc&&Date.parse(next.phoneVerifiedUtc)>=started){
          setVerified(true);setTest(null);
        }
      }catch{
        // The QR remains visible while the Agent briefly restarts or reconnects.
        // Expiry is checked before the pipe call, so a stopped service cannot
        // leave a disabled test button on screen indefinitely.
      }finally{polling=false;}
    };
    const timer=window.setInterval(()=>void poll(),2000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[test?.startedUtc]);

  async function changeNetwork(interfaceId:string) {
    if(!interfaceId)return;
    setSaving(true);setError("");setTest(null);setVerified(false);setExpired(false);
    try {setSnapshot(await selectLocalNetwork(interfaceId));}
    catch(exception){setError(exception instanceof Error?t(exception.message):t("Could not select this connection."));}
    finally{setSaving(false);}
  }

  async function beginPhoneTest() {
    setSaving(true);setError("");setExpired(false);setVerified(false);
    try {setTest(await startLocalPhoneTest());}
    catch(exception){setError(exception instanceof Error?t(exception.message):t("Could not start the phone check."));}
    finally{setSaving(false);}
  }

  const selected=snapshot?.networks.find(network=>network.id===snapshot.selectedNetworkId)??null;
  const hasSelectedNetwork=Boolean(snapshot?.selectedNetworkId && selected);
  const phoneTestPassed=verified&&localApiReady;
  const readiness=agentStatus?.readiness??null;
  const pipelineReady=readiness?.localUploadReady===true;
  const statusTone=phoneTestPassed&&pipelineReady?"green":hasSelectedNetwork||snapshot?.networks.length?"amber":"slate";
  const statusLabel=loading?"Checking…":error&&!snapshot?"Connection unavailable":connected&&!localApiReady?"Print service starting":localApiReady&&!readiness?"Agent update needed":localApiReady&&!pipelineReady?"Print pipeline recovering":phoneTestPassed?"Phone test passed":hasSelectedNetwork?"Phone check needed":snapshot?.networks.length?"Choose connection":"No connection found";

  return <section className="panel local-network-panel">
    <div className="local-network-heading">
      <div><span className="eyebrow"><span className="eyebrow-line"/>{t("SHOP CONNECTION")}</span><h2>{t("Customer upload connection")}</h2><p>{t("Choose the Wi-Fi or hotspot customers will use to send documents.")}</p></div>
      <Status label={loading?t("Checking…"):t(statusLabel)} tone={loading?"slate":statusTone}/>
    </div>
    {loading&&!snapshot?<div className="local-network-loading"><span className="button-spinner"/> {t("Checking available connections…")}</div>:error&&!snapshot
      ? <div className="local-network-empty"><Wifi size={20}/><div><strong>{t("Could not check this computer")}</strong><p>{t("SmartPrint is not responding yet. Make sure the desktop app is open, then try again.")}</p></div><button className="secondary-button" type="button" onClick={()=>void refresh()}>{t("Try again")}</button></div>
      : <>
      {snapshot?.networks.length?<>
        <label className="local-network-choice">Shop Wi-Fi or hotspot
          <select value={snapshot.selectedNetworkId??""} onChange={event=>void changeNetwork(event.target.value)} disabled={saving||!connected}>
            <option value="">{t("Choose the connection customers use")}</option>
            {snapshot.networks.map(network=><option key={network.id} value={network.id}>{network.displayName}{network.kind&&network.displayName.toLowerCase()!==network.kind.toLowerCase()?" · "+network.kind:""}</option>)}
          </select>
        </label>
        {selected
          ? <div className="local-network-selected"><Wifi size={17}/><span><strong>{selected.displayName}</strong><small>{selected.kind} · Used for customer document uploads</small></span><Check size={17}/></div>
          : <p className="local-network-copy">{t("Connect this computer to the Wi-Fi or hotspot customers will use, then select it here.")}</p>}
        {!connected?<p className="local-network-copy">{t("Connect this computer to your shop first. The phone check will be available when setup is complete.")}</p>:!localApiReady?<p className="local-network-copy">{t("SmartPrint is preparing the shop connection. The phone check will be available when the print service is ready.")}</p>:<>
          <div className="local-network-actions">
            <button className="primary-button" type="button" onClick={()=>void beginPhoneTest()} disabled={saving||!!test||!hasSelectedNetwork||!localApiReady}>
              {saving?<span className="button-spinner"/>:<><QrCode size={16}/> {t("Check with another phone")}</>}
            </button>
            <button className="secondary-button" type="button" onClick={()=>void refresh()} disabled={saving}><RotateCcw size={15}/> {t("Refresh")}</button>
          </div>
          {test&&<div className="local-phone-test">
            <div className="local-phone-qr"><QRCode value={test.phoneTestUrl} size={190} level="M" bgColor="#ffffff" fgColor="#111827"/></div>
            <div><strong>{t("Use a second phone")}</strong><p>{t("Connect another phone to this same Wi-Fi or hotspot. In SmartPrint, open Your space → connected shop → Check shop Wi-Fi, then scan this QR.")}</p><small>{t("The phone sharing the hotspot cannot join it. No SmartPrint? Scan with your camera and open the link.")}</small><p>{t("This screen updates automatically when the phone connects.")}</p></div>
          </div>}
          {phoneTestPassed&&<div className="local-network-result success"><Check size={17}/><span><strong>{t("Phone test passed")}</strong><small>{t("A phone reached this shop connection recently. The check expires after ten minutes.")}</small></span></div>}
          {localApiReady&&readiness?.localUploadReady===true&&<div className="local-network-result success"><Check size={17}/><span><strong>{t("Local print pipeline ready")}</strong><small>{t("Protected queue, print worker, cloud heartbeat and printer inventory are healthy. The assigned printer is checked separately for each QR station upload.")}</small></span></div>}
          {localApiReady&&!readiness&&<div className="local-network-result warning"><Clock3 size={17}/><span><strong>{t("Agent update required")}</strong><small>{t("This Agent version does not report queue and print-worker readiness. Update the Windows Print Agent to verify local upload readiness.")}</small></span></div>}
          {localApiReady&&readiness&&!readiness.localUploadReady&&<div className="local-network-result warning"><Clock3 size={17}/><span><strong>{t("Print pipeline recovering")}</strong><small>{[
            readiness.queueInitialized?"":"Protected document queue is not ready",
            readiness.printWorkerRunning?"":"Print worker is not running",
            readiness.heartbeatFresh?"":"Cloud heartbeat is stale",
            readiness.printerInventoryFresh?"":"Printer inventory is stale"
          ].filter(Boolean).join(" · ")} {readiness.lastFailureCode?"(Last issue: "+readiness.lastFailureCode+") ":""}Retry after the shop computer/Agent has recovered.</small></span></div>}
          {expired&&<div className="local-network-result warning"><Clock3 size={17}/><span><strong>{t("Phone not connected yet")}</strong><small>{t("Check that the second phone is on the selected Wi-Fi or hotspot. Some hotspots block devices from communicating; try shop Wi-Fi instead.")}</small></span></div>}
        </>}
      </>:<div className="local-network-empty"><Wifi size={20}/><div><strong>{t("No shop connection found")}</strong><p>{t("Connect this computer to the shop Wi-Fi or hotspot, then refresh.")}</p></div><button className="secondary-button" type="button" onClick={()=>void refresh()}>{t("Refresh")}</button></div>}
    </>}
    {error&&<div className="local-network-error" role="alert">{error}</div>}
  </section>;
}

function PricingPage({ pricing, onSave, busy }: {
  pricing: ShopPricingProfile;
  onSave: (values: {
    monoPerSideMinor: number;
    colourPerSideMinor: number;
    monoDuplexPerSheetMinor: number;
    colourDuplexPerSheetMinor: number;
    minimumChargeMinor: number;
    photo35x45Minor:number; photo25x35Minor:number; photo2x2Minor:number; photo4x6Minor:number;
    idDocumentPriceMinor:number;
  }) => void;
  busy: boolean;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [mono,setMono]=useState((pricing.monoPerSideMinor/100).toFixed(2));
  const [colour,setColour]=useState((pricing.colourPerSideMinor/100).toFixed(2));
  const [monoDuplex,setMonoDuplex]=useState((pricing.monoDuplexPerSheetMinor/100).toFixed(2));
  const [colourDuplex,setColourDuplex]=useState((pricing.colourDuplexPerSheetMinor/100).toFixed(2));
  const [minimum,setMinimum]=useState((pricing.minimumChargeMinor/100).toFixed(2));
  const [photo35x45,setPhoto35x45]=useState((pricing.photo35x45Minor/100).toFixed(2));const [photo25x35,setPhoto25x35]=useState((pricing.photo25x35Minor/100).toFixed(2));const [photo2x2,setPhoto2x2]=useState((pricing.photo2x2Minor/100).toFixed(2));const [photo4x6,setPhoto4x6]=useState((pricing.photo4x6Minor/100).toFixed(2));
  const [idDocumentPrice,setIdDocumentPrice]=useState((pricing.idDocumentPriceMinor/100).toFixed(2));
  useEffect(()=>{
    setMono((pricing.monoPerSideMinor/100).toFixed(2));
    setColour((pricing.colourPerSideMinor/100).toFixed(2));
    setMonoDuplex((pricing.monoDuplexPerSheetMinor/100).toFixed(2));
    setColourDuplex((pricing.colourDuplexPerSheetMinor/100).toFixed(2));
    setMinimum((pricing.minimumChargeMinor/100).toFixed(2));
    setPhoto35x45((pricing.photo35x45Minor/100).toFixed(2));setPhoto25x35((pricing.photo25x35Minor/100).toFixed(2));setPhoto2x2((pricing.photo2x2Minor/100).toFixed(2));setPhoto4x6((pricing.photo4x6Minor/100).toFixed(2));
    setIdDocumentPrice((pricing.idDocumentPriceMinor/100).toFixed(2));
  },[pricing]);
  const toMinor=(value:string)=>Math.round(Number(value)*100);
  const valid=[mono,colour,monoDuplex,colourDuplex,minimum,photo35x45,photo25x35,photo2x2,photo4x6,idDocumentPrice].every(value=>value.trim()!==""&&Number.isFinite(Number(value))&&Number(value)>=0);
  return <section className="panel settings-panel pricing-page">
    <div className="settings-intro"><div className="settings-icon"><Wallet size={20}/></div><div><h2>{t("Print rate card")}</h2><p>{t("Set the prices your customers see before they authorise a job.")}</p></div><span className={pricing.isConfigured?"pricing-status configured":"pricing-status"}>{pricing.isConfigured?"Active rate card":"Not configured"}</span></div>
    <div className="pricing-disclaimer"><ShieldCheck size={16}/><span>{t("Prices are stored in paise and calculated by the server. Updating this rate card affects new quotes; existing quotes retain their saved price and pricing version.")}</span></div>
    <form className="settings-form" onSubmit={event=>{
      event.preventDefault();
      if(!valid||toMinor(mono)<=0||toMinor(colour)<=0)return;
      onSave({
        monoPerSideMinor:toMinor(mono),
        colourPerSideMinor:toMinor(colour),
        monoDuplexPerSheetMinor:toMinor(monoDuplex),
        colourDuplexPerSheetMinor:toMinor(colourDuplex),
        minimumChargeMinor:toMinor(minimum),photo35x45Minor:toMinor(photo35x45),photo25x35Minor:toMinor(photo25x35),photo2x2Minor:toMinor(photo2x2),photo4x6Minor:toMinor(photo4x6),idDocumentPriceMinor:toMinor(idDocumentPrice)
      });
    }}>
      <div className="pricing-group-heading"><strong>{t("Single-sided printing")}</strong><span>{t("Price per A4 printed side")}</span></div>
      <div className="pricing-input-grid">
        <label htmlFor="pricing-mono-per-side">{t("Black & white")} <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0.01" max="1000" step="0.01" aria-label={t("Black & white")} id="pricing-mono-per-side" value={mono} onChange={e=>setMono(e.target.value)} required/></span></label>
        <label htmlFor="pricing-colour-per-side">{t("Colour")} <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0.01" max="1000" step="0.01" aria-label={t("Colour")} id="pricing-colour-per-side" value={colour} onChange={e=>setColour(e.target.value)} required/></span></label>
      </div>
      <div className="pricing-group-heading"><strong>{t("Double-sided printing")}</strong><span>{t("Price per physical A4 sheet (two sides)")}</span></div>
      <div className="pricing-input-grid">
        <label htmlFor="pricing-mono-duplex">{t("Black & white duplex")} <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" max="1000" step="0.01" aria-label={t("Black & white duplex")} id="pricing-mono-duplex" value={monoDuplex} onChange={e=>setMonoDuplex(e.target.value)}/></span></label>
        <label htmlFor="pricing-colour-duplex">{t("Colour duplex")} <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" max="1000" step="0.01" aria-label={t("Colour duplex")} id="pricing-colour-duplex" value={colourDuplex} onChange={e=>setColourDuplex(e.target.value)}/></span></label>
      </div>
      <div className="pricing-group-heading"><strong>{t("Minimum order")}</strong><span>{t("Applied if the calculated print total is lower")}</span></div>
      <label htmlFor="pricing-minimum-charge">{t("Minimum job charge")} <span className="price-input-wrap minimum-price"><span aria-hidden="true">₹</span><input type="number" min="0" max="100000" step="0.01" aria-label={t("Minimum job charge")} id="pricing-minimum-charge" value={minimum} onChange={e=>setMinimum(e.target.value)}/></span></label>
      <div className="pricing-group-heading"><strong>{t("Photo printing")}</strong><span>{t("Price per finished photo · ₹0 disables a size")}</span></div><div className="pricing-input-grid photo-pricing-grid"><label htmlFor="pricing-photo-35x45">35 × 45 mm <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" step="0.01" aria-label={t("35 × 45 mm")} id="pricing-photo-35x45" value={photo35x45} onChange={e=>setPhoto35x45(e.target.value)}/></span></label><label htmlFor="pricing-photo-25x35">25 × 35 mm <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" step="0.01" aria-label={t("25 × 35 mm")} id="pricing-photo-25x35" value={photo25x35} onChange={e=>setPhoto25x35(e.target.value)}/></span></label><label htmlFor="pricing-photo-2x2">2 × 2 inch <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" step="0.01" aria-label={t("2 × 2 inch")} id="pricing-photo-2x2" value={photo2x2} onChange={e=>setPhoto2x2(e.target.value)}/></span></label><label htmlFor="pricing-photo-4x6">4 × 6 inch <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" step="0.01" aria-label={t("4 × 6 inch")} id="pricing-photo-4x6" value={photo4x6} onChange={e=>setPhoto4x6(e.target.value)}/></span></label></div><div className="settings-summary"><ShieldCheck size={16}/><span>{t("Photo rates are per finished photo, not per A4 sheet. The quote records the rate used.")}</span></div>
       <div className="pricing-group-heading"><strong>{t("ID card printing")}</strong><span>{t("Price per prepared ID card · ₹0 disables ID card quotes")}</span></div>
       <label htmlFor="pricing-id-document">{t("Prepared ID card (one A4 sheet)")} <span className="price-input-wrap minimum-price"><span aria-hidden="true">₹</span><input type="number" min="0" max="1000" step="0.01" aria-label={t("Prepared ID card")} id="pricing-id-document" value={idDocumentPrice} onChange={e=>setIdDocumentPrice(e.target.value)}/></span></label>
       <div className="settings-summary"><ShieldCheck size={16}/><span>{t("ID cards use this dedicated flat rate, independent of ordinary document page prices and minimum job charge.")}</span></div>
      <div className="settings-summary"><ShieldCheck size={16}/><span>{t("Customer payment gateway setup is separate and is not active yet. This screen configures print quotations only; no money is collected here.")}</span></div>
      <div className="settings-actions"><button className="primary-button" disabled={busy||!valid||toMinor(mono)<=0||toMinor(colour)<=0}>{busy?<span className="button-spinner"/>:"Save print prices"}</button></div>
    </form>
    <div className="pricing-version-row"><span>{t("Rate card version")}</span><strong>v{pricing.version}</strong><span>{t("Last updated")}</span><strong>{formatIstDateTime(pricing.updatedUtc)}</strong></div>
  </section>;
}


function PaymentsPage({settings,payments,refunds,onSave,onVerify,onReject,busy,busyJobId}:{
  settings:ShopPaymentSettings;
  payments:ShopPayment[];
  refunds:ShopRefund[];
  onSave:(values:{upiEnabled:boolean;upiId:string|null;payeeName:string|null;cashEnabled:boolean;instantPaymentEnabled:boolean})=>void;
  onVerify:(payment:ShopPayment)=>void;
  onReject:(payment:ShopPayment)=>void;
  busy:boolean;
  busyJobId:string|null;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [upiEnabled,setUpiEnabled]=useState(settings.upiEnabled);
  const [upiId,setUpiId]=useState(settings.upiId||"");
  const [payeeName,setPayeeName]=useState(settings.payeeName||"");
  const [cashEnabled,setCashEnabled]=useState(settings.cashEnabled);
  const [instantPaymentEnabled,setInstantPaymentEnabled]=useState(settings.instantPaymentEnabled);
  useEffect(()=>{
    setUpiEnabled(settings.upiEnabled);
    setUpiId(settings.upiId||"");
    setPayeeName(settings.payeeName||"");
    setCashEnabled(settings.cashEnabled);
    setInstantPaymentEnabled(settings.instantPaymentEnabled);
  },[settings.upiEnabled,settings.upiId,settings.payeeName,settings.cashEnabled,settings.instantPaymentEnabled]);

  const pending=payments.filter(payment=>payment.status==="AWAITING_CUSTOMER_PAYMENT"||payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT");
  return <div className="payments-workspace">
    <section className="panel payment-settings-panel">
      <div className="collection-header"><div><h2>{t("Shop payment methods")}</h2><p>{t("Customer print payments go directly to your own UPI account or are received at your counter.")}</p></div><span className="setup-count">{(upiEnabled?1:0)+(cashEnabled?1:0)} enabled</span></div>
      <div className="payment-method-card">
        <div className="payment-method-heading"><div className="payment-method-icon"><Wallet size={19}/></div><div><strong>{t("UPI · QR and payment app")}</strong><small>{t("Configure your shop's own UPI ID. Payments are not auto-verified.")}</small></div><label className="switch"><input id="payment-upi-enabled" type="checkbox" aria-label={t("Enable UPI QR and payment app")} checked={upiEnabled} onChange={event=>setUpiEnabled(event.target.checked)}/><span aria-hidden="true"/></label></div>
        {upiEnabled&&<div className="payment-settings-fields">
          <label>{t("Shop UPI ID")}<input value={upiId} onChange={event=>setUpiId(event.target.value)} placeholder="yourshop@bank"/></label>
          <label>{t("Payee / business name")}<input value={payeeName} onChange={event=>setPayeeName(event.target.value)} maxLength={120} placeholder={t("Name shown in customer's UPI app")}/></label>
        </div>}
      </div>
      <div className="payment-method-card">
        <div className="payment-method-heading"><div className="payment-method-icon"><Printer size={19}/></div><div><strong>{t("Cash at counter")}</strong><small>{cashEnabled?"Enabled · customers can pay at the counter.":"Disabled · customers will not see cash at the counter."}</small></div><label className="switch"><input id="payment-cash-enabled" type="checkbox" aria-label={t("Enable cash at counter")} checked={cashEnabled} onChange={event=>setCashEnabled(event.target.checked)}/><span aria-hidden="true"/></label></div>
      </div>
      <div className="payment-method-card instant-payment-setting">
        <div className="payment-method-heading"><div className="payment-method-icon"><Zap size={19}/></div><div><strong>{t("Instant payment after upload")}</strong><small>{t("Customers can pay as soon as the rate-card quote is calculated, without a separate shop pre-approval. Once payment starts, file decisions are locked; after payment is verified, refund rejected files before printer handoff.")}</small></div><label className="switch"><input id="payment-instant-enabled" type="checkbox" aria-label={t("Enable instant payment after upload")} checked={instantPaymentEnabled} onChange={event=>setInstantPaymentEnabled(event.target.checked)}/><span aria-hidden="true"/></label></div>
        <p className="instant-payment-setting-note">{t("Requires your print rate card and at least one payment method. UPI references and cash are still verified by the shop before printing. Leave this off when you want to review and approve every quote before customers can pay.")}</p>
      </div>
      <div className="payment-security-note"><ShieldCheck size={16}/><span>{t("SmartPrint by QuantaEdge does not receive, hold or route your customer print revenue. Verify every UPI reference against your own bank/UPI app; cash must be physically received. SmartPrint platform subscription charges are billed separately.")}</span></div>
      <div className="payment-settings-footer"><span>Last updated {formatIstDateTime(settings.updatedUtc)}</span><button className="primary-button" disabled={busy||(!upiEnabled&&!cashEnabled)||(upiEnabled&&(!upiId.trim()||!payeeName.trim()))} onClick={()=>onSave({upiEnabled,upiId:upiEnabled?upiId.trim():null,payeeName:upiEnabled?payeeName.trim():null,cashEnabled,instantPaymentEnabled})}>{busy?<span className="button-spinner"/>:"Save payment methods"}</button></div>
    </section>
    <section className="panel payment-review-panel">
      <div className="collection-header"><div><h2>{t("Payment verification")}</h2><p>{t("Review UPI references and confirm cash collected before a job becomes payment-authorised.")}</p></div><span className="setup-count">{pending.length} need review</span></div>
      {payments.length===0?<EmptyModule icon={<Wallet size={24}/>} title={t("No print payments yet")} text={t("When customers choose UPI or cash for a shop-approved job, the payment attempt will appear here.")}/>:
      <div className="payment-record-list">{payments.map(payment=><article className="payment-record" key={payment.id}>
        <div className="payment-record-head"><div><strong>{payment.batchPaymentId?(`Batch checkout · ${payment.batchFileCount??1} files`):payment.jobNumber}</strong><small>{payment.documentName}</small></div><div className="payment-record-amount-wrap"><div className="payment-record-amount">{formatMoney(payment.amountMinor,payment.currency)}</div>{(payment.refundedAmountMinor??0)>0&&<small className="payment-record-refund-summary">Refund recorded: {formatMoney(payment.refundedAmountMinor??0,payment.currency)} · Net retained: {formatMoney(Math.max(0,payment.amountMinor-(payment.refundedAmountMinor??0)),payment.currency)}</small>}</div></div>
        {payment.origin==="SHOP_OWNER"&&<div className="payment-reference-details"><span>{t("Shop-created order recipient")}</span><strong>{payment.recipientName||"Walk-in"}{payment.recipientPhone?" · "+payment.recipientPhone:""}</strong><small>{t("This photo order was created in the shop workspace, not uploaded through a customer QR session.")}</small></div>}
        <div className="payment-record-meta"><span>{payment.method}</span><span>{formatIstDateTime(payment.createdUtc)}</span><Status label={payment.status.replaceAll("_"," ")} tone={payment.status==="VERIFIED"?"green":payment.status==="REJECTED"?"red":payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT"?"amber":"slate"}/></div>
        {payment.method==="UPI"&&<div className="payment-reference-details"><span>{t("Transaction reference")}</span><strong>{payment.paymentReference||"Customer has not submitted a reference"}</strong><small>Payee snapshot: {payment.payeeNameSnapshot||"Not recorded"} · {payment.upiIdSnapshot||"No UPI ID snapshot"}</small></div>}
        {payment.rejectionReason&&<div className="payment-rejection-note">Reason: {payment.rejectionReason}</div>}
        {(payment.status==="AWAITING_CUSTOMER_PAYMENT"||payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT")&&<div className="payment-review-actions">{payment.status==="AWAITING_CUSTOMER_PAYMENT"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>{t("Cancel attempt")}</button>}{payment.status==="CASH_AWAITING_PAYMENT"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>{payment.batchPaymentId?"Cancel batch cash attempt":"Cancel cash attempt"}</button>}{payment.status==="CUSTOMER_REPORTED"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>{payment.batchPaymentId?"Reject batch report":"Reject report"}</button>}<button className="primary-button" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onVerify(payment)}>{busyJobId===payment.printJobId?<span className="button-spinner"/>:payment.method==="UPI"?(payment.batchPaymentId?"Verify full batch in bank app":"Verify in bank app"):(payment.batchPaymentId?"Confirm full batch cash":"Confirm cash received")}</button></div>}
        {payment.status==="VERIFIED"&&<div className="payment-verified-note"><Check size={14}/> Verified {payment.verifiedUtc?formatIstDateTime(payment.verifiedUtc):"by shop"}</div>}
      </article>)}</div>}
    </section>
    <section className="panel refund-audit-panel">
      <div className="collection-header"><div><h2>{t("Refund audit log")}</h2><p>{t("Every shopkeeper-recorded file refund, with its exact amount, return method, reason and operator.")}</p></div><span className="setup-count">{refunds.length} recorded</span></div>
      {refunds.length===0?<EmptyModule icon={<RotateCcw size={24}/>} title={t("No refunds recorded")} text={t("When a shopkeeper rejects a paid file and records its return, the audit entry will appear here.")}/>:
      <div className="payment-record-list">{refunds.map(refund=><article className="payment-record refund-record" key={refund.id}>
        <div className="payment-record-head"><div><strong>{refund.jobNumber}</strong><small>{refund.documentName}</small></div><div className="payment-record-amount">{formatMoney(refund.amountMinor,refund.currency)}</div></div>
        <div className="payment-record-meta"><span>{refund.method} refund</span><span>{formatIstDateTime(refund.refundedUtc)}</span><Status label={t("Recorded as sent")} tone="green"/></div>
        <div className="refund-record-details"><span>{t("Shopkeeper")}</span><strong>{refund.refundedByName}</strong><span>{t("Reason")}</span><strong>{refund.reason}</strong>{refund.reference&&<><span>{t("Refund reference")}</span><strong>{refund.reference}</strong></>}</div>
      </article>)}</div>}
    </section>
  </div>;
}

function PrintQueuePage({ jobs, printers, agentDevices, searchQuery, loadingBatchId, onCreatePhotoOrder, onSearch, onPreview, onPreviewOutput, onApprove, onReject, onRefund, onReviewBatch, onHistory, onAssignPrinter, onUpdateSettings, onDispatch, onConfirmOutput, onRetryPdfExport, onRefresh, onLoadOlder, hasMoreJobs, loadingOlderJobs, busyJobId }: {
  jobs: ShopPrintJob[];
  printers: PrinterDevice[];
  agentDevices: AgentDevice[];
  searchQuery: string;
  loadingBatchId: string | null;
  onCreatePhotoOrder:()=>void;
  onSearch: (query: string) => Promise<void>;
  onPreview: (job: ShopPrintJob) => void;
  onPreviewOutput: (job: ShopPrintJob) => void;
  onApprove: (job: ShopPrintJob) => void;
  onReject: (job: ShopPrintJob) => void;
  onRefund: (job: ShopPrintJob) => void;
  onReviewBatch: (batchId: string) => void;
  onHistory: (job: ShopPrintJob) => void;
  onAssignPrinter: (job: ShopPrintJob, printerDeviceId: string) => void;
  onUpdateSettings: (job:ShopPrintJob,settings:CustomerPrintRequestSettings) => Promise<void> | void;
  onDispatch: (job: ShopPrintJob) => void;
  onConfirmOutput: (job: ShopPrintJob) => void;
  onRetryPdfExport: (job: ShopPrintJob) => void;
  onRefresh: () => Promise<void> | void;
  onLoadOlder: () => Promise<void> | void;
  hasMoreJobs: boolean;
  loadingOlderJobs: boolean;
  busyJobId: string | null;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [autoRefresh,setAutoRefresh]=useState(true);
  const [refreshSeconds,setRefreshSeconds]=useState(10);
  const [searchInput,setSearchInput]=useState(searchQuery);
  const [expandedBatchKeys,setExpandedBatchKeys]=useState<Set<string>>(()=>new Set());
  const [editingSettingsJobId,setEditingSettingsJobId]=useState<string|null>(null);
  const [editingPrintSettings,setEditingPrintSettings]=useState<CustomerPrintRequestSettings|null>(null);
  useEffect(()=>setSearchInput(searchQuery),[searchQuery]);
  const refreshRunning=useRef(false);
  const refreshCallback=useRef(onRefresh);
  refreshCallback.current=onRefresh;

  async function refreshQueue() {
    if(refreshRunning.current)return;
    refreshRunning.current=true;
    try { await refreshCallback.current(); }
    finally { refreshRunning.current=false; }
  }

  useEffect(()=>{
    if(!autoRefresh)return;
    const timer=window.setInterval(()=>void refreshQueue(),Math.max(5,refreshSeconds)*1000);
    return ()=>window.clearInterval(timer);
  },[autoRefresh,refreshSeconds]);

  const reviewableBatchFileStatuses=new Set(["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED","REJECTED"]);
  const reviewBatches=Array.from(new Map(jobs.filter(job=>job.batchId&&reviewableBatchFileStatuses.has(job.status)).map(job=>[job.batchId!,job])).keys());
  const dispatchablePrinters = printers.filter(printer =>
    isPrinterAvailableForShop(printer, agentDevices)
  );
  const batchMap=new Map<string,{key:string;batchId:string|null;jobs:ShopPrintJob[]}>();
  for(const job of jobs) {
    const key=job.batchId??`single:${job.id}`;
    const group=batchMap.get(key)??{key,batchId:job.batchId??null,jobs:[]};
    group.jobs.push(job);
    batchMap.set(key,group);
  }
  const queueBatches=Array.from(batchMap.values());

  return <section className="panel queue-panel live-queue-panel">
    <div className="collection-header"><div><h2>{t("Print jobs")}</h2><p>{t("Customer QR uploads and shop-created photo orders use the same review, payment and printing queue.")}</p></div><div className="queue-header-actions"><span className="setup-count">{queueBatches.length} batches shown</span><button className="primary-button" type="button" onClick={onCreatePhotoOrder}><ImageIcon size={14}/> {t("New photo order")}</button><form className="queue-search-form" onSubmit={event=>{event.preventDefault();void onSearch(searchInput);}}><input aria-label={t("Search all print jobs")} type="search" maxLength={120} placeholder={t("Job, customer, file or printer")} value={searchInput} onChange={event=>setSearchInput(event.target.value)}/><button className="secondary-button" type="submit"><Search size={13}/> {t("Search")}</button></form><button className="secondary-button queue-refresh-button" onClick={()=>void refreshQueue()} disabled={refreshRunning.current}><Activity size={13}/> {t("Refresh queue")}</button></div></div>
    <div className="queue-refresh-controls">
      <label className="queue-auto-toggle"><input type="checkbox" checked={autoRefresh} onChange={event=>setAutoRefresh(event.target.checked)}/><span className="queue-toggle-track"/><strong>{t("Auto refresh")}</strong><small>{autoRefresh?"On":"Off"}</small></label>
      <label className="queue-refresh-interval">{t("Refresh every")} <input type="number" min={5} max={3600} step={1} value={refreshSeconds} onChange={event=>setRefreshSeconds(Math.max(5,Math.min(3600,Number(event.target.value)||5)))}/> {t("seconds")}</label>
      <span className="queue-refresh-note">{autoRefresh?t("Queue refreshes automatically while this page is open."):t("Automatic refresh is off. Use Refresh queue whenever needed.")}</span>
    </div>
    {reviewBatches.length>0&&<div className="batch-review-strip"><div><strong>{reviewBatches.length} {reviewBatches.length===1?"batch":"batches"} need review</strong><span>{t("Review includes every file in the batch. New files start accepted; rejected files stay excluded until restored.")}</span></div>{reviewBatches.map(batchId=><button key={batchId} className="secondary-button" disabled={loadingBatchId===batchId||!!loadingBatchId} onClick={()=>onReviewBatch(batchId)}>{loadingBatchId===batchId?<><span className="button-spinner"/>{t("Loading full batch…")}</>:<>Review batch · {jobs.filter(job=>job.batchId===batchId&&reviewableBatchFileStatuses.has(job.status)).length} shown</>}</button>)}</div>}
    {jobs.length===0 ? <EmptyModule icon={<FileText size={24}/>} title={t("Your queue is clear")} text={t("When a customer uploads a document through one of your QR stations, the job and its current state will appear here.")}/> :
      <div className="table-scroll queue-table-scroll"><table className="queue-jobs-table"><thead><tr><th>{t("JOB")}</th><th>{t("DOCUMENT")}</th><th>{t("PRINT SETTINGS")}</th><th>{t("QUOTE")}</th><th>{t("PRINTER / LOCAL QUEUE")}</th><th>{t("STATUS")}</th><th>{t("CREATED")}</th><th>{t("ACTIONS")}</th></tr></thead><tbody>
        {queueBatches.map(batch=>{
          const first=batch.jobs[0];
          const photoJobs=batch.jobs.filter(job=>job.printKind==="PHOTO");
          const documentJobs=batch.jobs.filter(job=>job.printKind!=="PHOTO");
          const waiting=batch.jobs.filter(job=>reviewableBatchFileStatuses.has(job.status));
          const folders=Array.from(new Set(batch.jobs.map(job=>job.batchGroupName??"Loose files")));
          const fileCountLabel=`${batch.jobs.length} ${batch.jobs.length===1?"file":"files"}`;
          const folderGroupLabel=`${folders.length} ${folders.length===1?"folder group":"folder groups"}`;
          const statusLabels=Array.from(new Set(batch.jobs.map(job=>job.status)));
          const payableJobs=batch.jobs.filter(job=>job.status!=="REJECTED"&&job.status!=="REFUNDED");
          const totalRequestedPages=payableJobs.reduce((sum,job)=>sum+job.pageCount*job.copies,0);
          const monoRequestedPages=payableJobs.reduce((sum,job)=>sum+(job.colourMode==="Mono"?job.pageCount*job.copies:0),0);
          const colourRequestedPages=payableJobs.reduce((sum,job)=>sum+(job.colourMode==="Colour"?job.pageCount*job.copies:0),0);
          const totalAmount=payableJobs.reduce((sum,job)=>sum+Math.max(0,job.quotedAmountMinor),0);
          const activeBatchJobs=batch.jobs.filter(job=>job.status!=="REJECTED"&&job.status!=="REFUNDED");
          const localStorageJobs=activeBatchJobs.filter(job=>job.storageMode==="LOCAL_AGENT");
          const localAgentIds=Array.from(new Set(localStorageJobs.map(job=>job.documentAgentDeviceId).filter((id):id is string=>Boolean(id))));
          const hasUnidentifiedLocalStorage=localStorageJobs.some(job=>!job.documentAgentDeviceId);
          const requiredLocalAgentId=localAgentIds.length===1&&!hasUnidentifiedLocalStorage?localAgentIds[0]:null;
          const localComputerName=localStorageJobs.find(job=>job.documentAgentDeviceName)?.documentAgentDeviceName;
          const assignedPrinterIds=Array.from(new Set(activeBatchJobs.map(job=>job.printerDeviceId).filter((id):id is string=>Boolean(id))));
          const batchHasUnassignedFiles=activeBatchJobs.some(job=>!job.printerDeviceId);
          const lockedBatchJobs=activeBatchJobs.filter(job=>
            ["PRINT_AUTHORISED","PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING","OUTPUT_UNCONFIRMED","COMPLETED"].includes(job.status) ||
            (job.localQueueReceivedUtc!=null && job.status!=="LOCAL_QUEUE_READY") ||
            (job.status==="LOCAL_QUEUE_CLAIMED"&&!!job.agentLeaseExpiresUtc&&Date.parse(job.agentLeaseExpiresUtc)>Date.now())
          );
          const lockedPrinterIds=Array.from(new Set(lockedBatchJobs.map(job=>job.printerDeviceId).filter((id):id is string=>Boolean(id))));
          const batchPrinterConflict=lockedPrinterIds.length>1;
          const lockedBatchPrinter=lockedPrinterIds.length===1?printers.find(printer=>printer.id===lockedPrinterIds[0]):undefined;
          const multipleOrUnknownLocalComputers=localAgentIds.length>1||hasUnidentifiedLocalStorage;
          const lockedPrinterOnWrongComputer=Boolean(requiredLocalAgentId&&lockedBatchPrinter&&lockedBatchPrinter.agentDeviceId!==requiredLocalAgentId);
          const localComputerIdentityConflict=multipleOrUnknownLocalComputers||lockedPrinterOnWrongComputer;
          const batchAssignmentIncomplete=assignedPrinterIds.length!==1||batchHasUnassignedFiles;
          const currentBatchPrinterId=!batchAssignmentIncomplete&&assignedPrinterIds.length===1?assignedPrinterIds[0]:"";
          const batchPrinter=printers.find(printer=>printer.id===currentBatchPrinterId);
          const batchPrinterDisplayName=batchPrinter?.outputMode==="PDF_EXPORT"?"Save as PDF":batchPrinter?.displayName||activeBatchJobs.find(job=>job.printerDeviceId===currentBatchPrinterId)?.printerName||"Assigned printer";
          const assignmentJob=batch.jobs.find(job=>
            job.status==="PAID_AUTHORISED" ||
            (job.status==="LOCAL_QUEUE_CLAIMED"&&!job.localQueueReceivedUtc&&
              (!job.agentLeaseExpiresUtc||Date.parse(job.agentLeaseExpiresUtc)<=Date.now())) ||
            (job.status==="LOCAL_QUEUE_READY"&&!!job.localQueueReceivedUtc&&!job.spoolerJobId&&!job.spoolerJobName)
          );
          const availableBatchPrinters=localComputerIdentityConflict
            ?[]
            :lockedPrinterIds.length===1
              ?dispatchablePrinters.filter(printer=>printer.id===lockedPrinterIds[0])
              :requiredLocalAgentId
                ?dispatchablePrinters.filter(printer=>printer.agentDeviceId===requiredLocalAgentId)
                :dispatchablePrinters;
          const expanded=expandedBatchKeys.has(batch.key);
          const customerBatchName=batch.jobs.find(job=>job.batchName?.trim())?.batchName?.trim()??null;
          const allPdfsSaved = batch.jobs.length > 0 && batch.jobs.every(job => !!verifiedPdfExportPath(job));
          const singleSavedPdfPath = batch.jobs.length === 1 ? verifiedPdfExportPath(first) : null;
          const label=statusLabels.length===1?(allPdfsSaved?"PDF saved":prettyStatus(statusLabels[0])):`${statusLabels.length} states`;
          const tone=statusLabels.every(status=>status==="COMPLETED"||status==="LOCAL_QUEUE_READY")?"green":statusLabels.some(status=>reviewableBatchFileStatuses.has(status)||status==="AWAITING_CUSTOMER_APPROVAL"||status==="OUTPUT_UNCONFIRMED")?"amber":"blue";
          return <Fragment key={batch.key}>
            <tr className="queue-batch-summary-row">
              <td><strong className="job-id">{customerBatchName??(batch.batchId?"Batch · "+first.jobNumber:first.jobNumber)}</strong><small className="batch-summary-sub">{batch.batchId?"First job · "+first.jobNumber+" · "+fileCountLabel:fileCountLabel}</small><div className="queue-print-kind-summary">{photoJobs.length>0&&<PrintKindTag printKind="PHOTO" count={photoJobs.length}/>}{documentJobs.length>0&&<PrintKindTag printKind="DOCUMENT" count={documentJobs.length}/>}</div></td>
              <td><div className="customer-cell"><strong>{folderGroupLabel}</strong><span>{folders.slice(0,3).join(" · ")}{folders.length>3?" · +"+(folders.length-3):""}</span></div></td>
              <td><div className="detail-cell"><strong>{totalRequestedPages} pages requested</strong><span>{monoRequestedPages} B&W · {colourRequestedPages} Colour</span></div></td>
              <td className="amount-cell">{totalAmount>0?formatMoney(totalAmount,first.currency):"Quote pending"}</td>
              <td><div className="queue-printer-cell">
                {singleSavedPdfPath ? <>
                  <strong>{t("PDF saved")}</strong>
                  <small className="queue-pdf-path">{singleSavedPdfPath}</small>
                </> : <>
                  <strong>{batchPrinterConflict?"Multiple printer assignments":batchAssignmentIncomplete?(assignedPrinterIds.length?"Choose one printer":"Printer not assigned"):batchPrinterDisplayName}</strong>
                  <small>{lockedPrinterIds.length===1?"Locked to the printer already used for this batch":waiting.length===0?"No files awaiting review":`${waiting.length} ${waiting.length===1?"file":"files"} awaiting review`}</small>
                  {batchPrinter?.outputMode==="PDF_EXPORT"&&<small>{t("Saves a PDF, not paper")}</small>}
                  {batchPrinter?.outputMode==="UNSUPPORTED_VIRTUAL"&&batchPrinter.virtualTestGrantActive!==true&&<small className="queue-batch-printer-lock">{t("This virtual queue needs an active platform-admin test grant for this shop before it can receive jobs.")}</small>}
                </>}
                {assignmentJob&&<select
                  aria-label={t("Assign one printer for all files in batch")}
                  value={currentBatchPrinterId}
                  disabled={!!busyJobId||batchPrinterConflict||availableBatchPrinters.length===0}
                  onChange={event=>onAssignPrinter(assignmentJob,event.target.value)}>
                  <option value="">{availableBatchPrinters.length===0?"No available printer":"Choose one printer for this batch"}</option>
                  {availableBatchPrinters.map(printer=><option key={printer.id} value={printer.id}>{printer.outputMode==="PDF_EXPORT"?"Save as PDF · ":""}{printer.displayName} · {agentDevices.find(agent=>agent.id===printer.agentDeviceId)?.deviceName||"Shop computer"} · {printer.status}</option>)}
                </select>}
                {assignmentJob&&<small className="queue-batch-assignment-note">{t("One printer is used for all active files.")}</small>}
                {requiredLocalAgentId&&!localComputerIdentityConflict&&<small className="queue-batch-assignment-note"><span>Stored on {localComputerName||"the original shop computer"}.</span> <InfoTooltip label={t("Shop computer storage details")}><span>{t("Files stay on this computer. Reconnect it to use its printers.")}</span><span>{t("To switch computers, upload a new Cloud-assisted batch.")}</span></InfoTooltip></small>}
                {multipleOrUnknownLocalComputers&&<small className="queue-batch-printer-lock">{t("Files are stored on different or unknown computers. Do not start output. Check for existing output, then upload all files in a new Cloud-assisted batch.")}</small>}
                {lockedPrinterOnWrongComputer&&<small className="queue-batch-printer-lock">Files are on {localComputerName||"the original shop computer"}, but this printer is on another computer. Stop; do not retry or switch printers. Check existing output before resubmitting.</small>}
                {batchPrinterConflict&&<small className="queue-batch-printer-lock">{t("This batch uses multiple printers. New output is blocked. Review the affected files.")}</small>}
              </div></td>
              <td><Status label={label} tone={tone}/></td>
              <td className="time-cell">{formatIstDateTime(first.createdUtc)}</td>
              <td><div className="queue-actions">
                {waiting.length>0&&batch.batchId&&<button className="subtle-button approve-action" disabled={busyJobId==="batch-review"||!!loadingBatchId} onClick={()=>onReviewBatch(batch.batchId!)}>{loadingBatchId===batch.batchId?"Loading…":"Preview & review"}</button>}
                <button className="subtle-button" onClick={()=>setExpandedBatchKeys(current=>{const next=new Set(current);if(next.has(batch.key))next.delete(batch.key);else next.add(batch.key);return next;})}>{expanded?"Hide files":"Files"}</button>
              </div></td>
            </tr>
            {expanded&&<tr className="queue-batch-detail-row"><td colSpan={8}><div className="queue-batch-files">{batch.jobs.map(job=><article className="queue-batch-file-card" key={job.id}>
              <div className="queue-batch-file-main">
                <div><div className="queue-batch-file-title"><strong>{job.document.originalFileName}</strong>{job.privatePrinting&&<span className="private-printing-tag" title={job.outputType==="TEST_VIRTUAL"?(job.outputMode==="UNSUPPORTED_VIRTUAL"?"Non-production virtual printer test only. Not physical fulfilment. The protected source is erased after the Agent verifies the spooler output.":"Non-production test PDF only. Not physical fulfilment. The protected source is erased after the Agent verifies the test artifact; the test PDF is retained locally for up to 24 hours."):"The paired shop computer erases the private working copy after physical output is confirmed. The original file on the customer device is not deleted."}>{t("Private · auto-delete")}</span>}<PrintKindTag printKind={job.printKind}/>{job.origin==="SHOP_OWNER"&&<span className="print-kind-tag print-kind-tag--photo">{t("Shop-created")}</span>}</div><small>{job.origin==="SHOP_OWNER"?`Recipient: ${job.recipientName||"Walk-in"}${job.recipientPhone?" · "+job.recipientPhone:""} · Shop-originated order`:"Customer QR order"} · {job.printKind==="PHOTO" ? `${job.photoCount??0} photos · ${job.photoSizeCode??""} · ${job.photoPaperFinish==="GLOSSY"?"Glossy":job.photoPaperFinish==="MATTE"?"Matte":"Plain"} · ${job.photoOutputLayout==="PHOTO_PAPER"?"individual photo paper":"A4 photo paper"} · ${job.pageCount} ${job.photoOutputLayout==="PHOTO_PAPER"?"photo-paper pages":"A4 pages"}` : `${job.batchGroupName??"Loose files"} · ${customerDocumentOutputSummary(job.pageCount,job.copies,job.pagesPerSheet??1,job.isDuplex)} · ${job.colourMode} · ${job.isDuplex?"Double-sided":"Single-sided"} · ${job.paperSize||"A4"} · ${job.orientation??"AUTO"}`}</small></div>
                <Status label={prettyStatus(job.status)} tone={job.status==="REJECTED"?"red":job.status==="COMPLETED"||job.status==="LOCAL_QUEUE_READY"?"green":job.status==="AWAITING_SHOP_APPROVAL"?"amber":"blue"}/>
                <strong className="amount-cell">{job.quotedAmountMinor>0?formatMoney(job.quotedAmountMinor,job.currency):"—"}</strong>
              </div>
              {job.printerName&&<div className="queue-batch-file-printer">
                <Printer size={13}/>
                <strong>{job.outputType==="TEST_VIRTUAL"?(job.outputMode==="UNSUPPORTED_VIRTUAL"?"Virtual printer test":"Private test PDF"):job.outputMode==="PDF_EXPORT"?"Save as PDF":job.outputConfirmedUtc||job.status==="COMPLETED"?"Printed on":job.spoolerJobId?"Sent to":"Assigned printer"}</strong>
                <span>{job.outputMode==="PDF_EXPORT"?job.printerAgentName||"Shop computer":job.printerName}</span>
                {job.outputMode==="PDF_EXPORT"&&<small>{job.outputType==="TEST_VIRTUAL"?"Test PDF destination: ":"PDF destination: "}{job.printerName}{job.outputType==="TEST_VIRTUAL"?" · Not physical fulfilment":""}</small>}
                {job.outputMode==="UNSUPPORTED_VIRTUAL"&&job.outputType==="TEST_VIRTUAL"&&<small>Test queue: {job.printerName} · Not physical fulfilment</small>}
                {job.queueName&&job.queueName!==job.printerName&&<small>Windows queue: {job.queueName}</small>}
                {job.printerAgentName&&job.outputMode!=="PDF_EXPORT"&&<small>Shop computer: {job.printerAgentName}</small>}
                {job.printerSnapshotUtc&&<small>Output destination recorded: {formatIstDateTime(job.printerSnapshotUtc)}</small>}
                {job.outputConfirmedUtc&&job.outputType!=="PDF_EXPORT"&&<small>Paper output manually confirmed by {job.outputConfirmedByName||"shop operator"} at {formatIstDateTime(job.outputConfirmedUtc)}</small>}
                {job.spoolerStatus?.toUpperCase()==="RETAINED"&&<small className="queue-print-warning">{spoolerStatusWarning(job.spoolerStatus,job.printerStatus)}</small>}
              </div>}
              {!job.printerName&&job.storageMode==="LOCAL_AGENT"&&job.documentAgentDeviceName&&<div className="queue-batch-file-printer"><Printer size={13}/><strong>{t("Stored on")}</strong><span>{job.documentAgentDeviceName}</span></div>}
              {batch.jobs.length > 1 && verifiedPdfExportPath(job) && <div className="queue-batch-file-output">
                <span>{t("Saved PDF location")}</span>
                <code>{verifiedPdfExportPath(job)}</code>
              </div>}
              {job.status==="LOCAL_QUEUE_READY"&&job.failureCode&&<div className="printer-duplicate-warning" role="status">
                 <strong>{t("Printing paused before submission")}</strong>
                 <span>{printBlockMessage(job.failureCode)} {t("The original document and requested settings remain unchanged. Reassign this batch to a compatible printer, or resolve the issue with the customer before changing any setting. A settings change must be sent as a revised quote and explicitly approved before output.")}</span>
               </div>}
               <div className="queue-batch-file-actions">
                {job.printKind==="DOCUMENT"&&["AWAITING_CONFIGURATION","AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","QUOTE_EXPIRED","PAYMENT_REJECTED"].includes(job.status)&&<button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>{
                  setEditingSettingsJobId(job.id);
                  setEditingPrintSettings({
                    copies:job.copies,
                    colourMode:job.colourMode==="Colour"?"Colour":"Mono",
                    paperSize:(["A4","A3","A5","LETTER","LEGAL"].includes(job.paperSize)?job.paperSize:"A4") as CustomerPrintRequestSettings["paperSize"],
                    isDuplex:job.isDuplex,
                    orientation:(["AUTO","PORTRAIT","LANDSCAPE"].includes(job.orientation??"AUTO")?job.orientation??"AUTO":"AUTO") as CustomerPrintRequestSettings["orientation"]
                  });
                }}>{t("Adjust options")}</button>}
                <button className="subtle-button" disabled={busyJobId===job.id||job.privatePrinting} onClick={()=>onPreview(job)}>{job.privatePrinting?t("Private source · preview disabled"):t("Preview original")}</button>
                {job.status==="COMPLETED"&&job.outputType==="PDF_EXPORT"&&verifiedPdfExportPath(job)&&job.outputFileName&&job.outputSha256?.length===64&&(job.outputSizeBytes??0)>0&&(job.outputSizeBytes??0)<=100*1024*1024&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onPreviewOutput(job)}>{t("Preview saved PDF")}</button>}
                <button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>onHistory(job)}>{t("History")}</button>
                {job.status==="AWAITING_SHOP_APPROVAL"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onApprove(job)}>{t("Accept")}</button>}
                {["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&<button className="subtle-button reject-action" disabled={busyJobId===job.id} onClick={()=>onReject(job)}>{t("Reject file")}</button>}
                {(job.status==="PAID_AUTHORISED"||job.status==="REJECTED")&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId&&<button className="subtle-button reject-action" disabled={busyJobId===job.id} onClick={()=>onRefund(job)}>{job.status==="REJECTED"?"Refund rejected file":"Review pages / refund"}</button>}
                {job.status==="LOCAL_QUEUE_READY"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onDispatch(job)}>{job.outputMode==="UNSUPPORTED_VIRTUAL"?"Run virtual test":job.privatePrinting&&job.outputMode==="PDF_EXPORT"?"Create test PDF":job.outputMode==="PDF_EXPORT"?"Save PDF":"Start printing"}</button>}
                {job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode==="PDF_EXPORT"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onRetryPdfExport(job)}>{job.privatePrinting&&job.outputType==="TEST_VIRTUAL"?"Retry test PDF":"Save PDF again"}</button>}
                {job.outputType!=="TEST_VIRTUAL"&&isPhysicalOutputConfirmationStatus(job.status)&&(job.status!=="OUTPUT_UNCONFIRMED"||job.outputMode!=="PDF_EXPORT")&&<button className="subtle-button approve-action" disabled={busyJobId===job.id||hasIncompleteSpoolerPageProgress(job.spoolerPagesPrinted,job.spoolerTotalPages)||!(canConfirmPhysicalOutput(job.status,job.spoolerStatus,job.spoolerSubmittedUtc,job.spoolerObservedUtc,job.printerStatus,job.failureCode,job.spoolerJobId,Date.now(),job.spoolerPagesPrinted??0,job.spoolerTotalPages??0)||canManuallyReconcileUncertainOutput(job.status,job.spoolerStatus,job.failureCode,job.spoolerJobId,job.queueName,job.spoolerJobName))} title={hasIncompleteSpoolerPageProgress(job.spoolerPagesPrinted,job.spoolerTotalPages)?t("Windows recorded {printed} of {total} pages for this job.",{printed:job.spoolerPagesPrinted??0,total:job.spoolerTotalPages}):canManuallyReconcileUncertainOutput(job.status,job.spoolerStatus,job.failureCode,job.spoolerJobId,job.queueName,job.spoolerJobName)?t("The Agent restarted before Windows returned a spooler ID. Check the selected Windows queue for this exact job name and physically inspect all paper output before continuing."):!canConfirmPhysicalOutput(job.status,job.spoolerStatus,job.spoolerSubmittedUtc,job.spoolerObservedUtc,job.printerStatus,job.failureCode,job.spoolerJobId,Date.now(),job.spoolerPagesPrinted??0,job.spoolerTotalPages??0)?(isRetainedSpoolerJob(job.spoolerStatus)?t("Windows still holds this job. Confirmation remains blocked until the Agent verifies that the retained entry has cleared."):!hasVerifiedAgentSpoolerObservation(job.spoolerStatus,job.spoolerSubmittedUtc,job.spoolerObservedUtc,job.failureCode,job.spoolerJobId)?t("Waiting for a fresh Agent observation from after submission before confirming output."):(["QUEUED","SPOOLING","PRINTING"].includes((job.spoolerStatus??"").toUpperCase())?t("Windows still reports this job in its queue or printing. Wait for a terminal state before confirming output."):t(spoolerStatusWarning(job.spoolerStatus,job.printerStatus)||"Confirm output only after Windows reports that the job printed or its queue entry was verified absent."))):undefined} onClick={()=>onConfirmOutput(job)}>{canManuallyReconcileUncertainOutput(job.status,job.spoolerStatus,job.failureCode,job.spoolerJobId,job.queueName,job.spoolerJobName)?"Reconcile & confirm":"Confirm output"}</button>}
              </div>
              {editingSettingsJobId===job.id&&editingPrintSettings&&<div className="queue-settings-editor">
                <div><strong>{t("Review print options")}</strong><small>{t("The revised quote must be approved by the customer before payment or printing.")}</small></div>
                <label>{t("Colour")} <select value={editingPrintSettings.colourMode} onChange={event=>{
                  const colourMode=event.currentTarget.value as CustomerPrintRequestSettings["colourMode"];
                  setEditingPrintSettings(current=>current?{...current,colourMode}:current);
                }}><option value="Mono">{t("B&W")}</option><option value="Colour">{t("Colour")}</option></select></label>
                <label>{t("Sides")} <select value={editingPrintSettings.isDuplex?"DOUBLE":"SINGLE"} onChange={event=>{
                  const isDuplex=event.currentTarget.value==="DOUBLE";
                  setEditingPrintSettings(current=>current?{...current,isDuplex}:current);
                }}><option value="SINGLE">{t("Single-sided")}</option><option value="DOUBLE">{t("Double-sided")}</option></select></label>
                <label>{t("Copies")} <input type="number" min={1} max={100} value={editingPrintSettings.copies} onChange={event=>{
                  const copies=Math.max(1,Math.min(100,Number(event.currentTarget.value)||1));
                  setEditingPrintSettings(current=>current?{...current,copies}:current);
                }}/></label>
                <label>{t("Paper size")} <select value={editingPrintSettings.paperSize} onChange={event=>{
                  const paperSize=event.currentTarget.value as CustomerPrintRequestSettings["paperSize"];
                  setEditingPrintSettings(current=>current?{...current,paperSize}:current);
                }}><option value="A4">A4</option><option value="A3">A3</option><option value="A5">A5</option><option value="LETTER">{t("Letter")}</option><option value="LEGAL">{t("Legal")}</option></select></label>
                <label>{t("Orientation")} <select value={editingPrintSettings.orientation} onChange={event=>{
                  const orientation=event.currentTarget.value as CustomerPrintRequestSettings["orientation"];
                  setEditingPrintSettings(current=>current?{...current,orientation}:current);
                }}><option value="AUTO">{t("Auto · follow document")}</option><option value="PORTRAIT">{t("Portrait")}</option><option value="LANDSCAPE">{t("Landscape")}</option></select></label>
                <div className="queue-settings-actions">
                  <button className="secondary-button" type="button" onClick={()=>{setEditingSettingsJobId(null);setEditingPrintSettings(null);}}>{t("Cancel")}</button>
                  <button className="primary-button" type="button" disabled={busyJobId===job.id||!editingPrintSettings} onClick={()=>{if(editingPrintSettings)void onUpdateSettings(job,editingPrintSettings);setEditingSettingsJobId(null);setEditingPrintSettings(null);}}>{t("Send revised quote")}</button>
                </div>
              </div>}
            </article>)}</div></td></tr>}
          </Fragment>;
        })}
      </tbody></table></div>}
    {hasMoreJobs && <div className="queue-load-older"><button className="secondary-button" type="button" onClick={() => void onLoadOlder()} disabled={loadingOlderJobs}>{loadingOlderJobs ? <span className="button-spinner"/> : <ArrowRight size={14}/>} Load older jobs</button></div>}
  </section>;
}



function BatchReviewModal({jobs,busy,decisions,retrying,onDecision,onClose,onPreview,onApply}:{
  jobs:ShopPrintJob[];
  busy:boolean;
  decisions:Record<string,boolean>;
  retrying:boolean;
  onDecision:(jobId:string,accepted:boolean)=>void;
  onClose:()=>void;
  onPreview:(job:ShopPrintJob)=>void;
  onApply:(acceptedJobIds:string[],pdfBrandingEnabled:boolean)=>void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const hasIdDocument=jobs.some(job=>job.printKind==="ID_DOCUMENT");
  const [pdfBrandingEnabled,setPdfBrandingEnabled]=useState(()=>!hasIdDocument&&jobs.some(job=>job.pdfBrandingEnabled===true));
  const acceptedIds=jobs.filter(job=>decisions[job.id]??true).map(job=>job.id);
  const acceptedCount=acceptedIds.length;
  const rejectedCount=jobs.length-acceptedCount;
  const acceptedJobs=jobs.filter(job=>decisions[job.id]??true);
  const quoteExpired=(job:ShopPrintJob)=>!job.quoteExpiresUtc||
    !Number.isFinite(Date.parse(job.quoteExpiresUtc))||Date.parse(job.quoteExpiresUtc)<=Date.now();
  const quoteRefreshRequiredCount=acceptedJobs.filter(quoteExpired).length;
  const acceptedTotal=acceptedJobs.filter(job=>!quoteExpired(job))
    .reduce((sum,job)=>sum+Math.max(0,job.quotedAmountMinor),0);
  const acceptedDocumentJobs=acceptedJobs.filter(job=>job.printKind!=="PHOTO"&&job.printKind!=="ID_DOCUMENT");
  const acceptedIdCardJobs=acceptedJobs.filter(job=>job.printKind==="ID_DOCUMENT");
  const acceptedPhotoJobs=acceptedJobs.filter(job=>job.printKind==="PHOTO");
  const acceptedOutputSides=acceptedDocumentJobs.reduce((sum,job)=>sum+printJobOutputSides(job.pageCount,job.pagesPerSheet??1,job.copies),0);
  const acceptedIdSheets=acceptedIdCardJobs.reduce((sum,job)=>sum+job.pageCount,0);
  const acceptedPhotoPages=acceptedPhotoJobs.reduce((sum,job)=>sum+job.pageCount,0);
  const acceptedMonoPages=acceptedDocumentJobs.reduce((sum,job)=>sum+(job.colourMode==="Mono"?printJobOutputSides(job.pageCount,job.pagesPerSheet??1,job.copies):0),0);
  const acceptedColourPages=acceptedDocumentJobs.reduce((sum,job)=>sum+(job.colourMode==="Colour"?printJobOutputSides(job.pageCount,job.pagesPerSheet??1,job.copies):0),0);
  const folders=Array.from(new Set(jobs.map(job=>job.batchGroupName??"Loose files")));
  return <Modal title={t(retrying?"Retry batch review":"Review batch")} subtitle={t(retrying?"Retry review. Rejected files stay excluded until restored.":"Select the files that should remain in the customer's batch checkout. Excluded files will not be charged.")} onClose={onClose}>
    <section className="batch-review-overview" aria-label={t("Batch review summary")}>
      <div className="batch-review-overview-heading"><div><span>{t("REVIEW SUMMARY")}</span><strong>{jobs.length} {jobs.length===1?"file":"files"} in this batch</strong><small>{t("Included files stay in one checkout. Excluded files will not be charged.")}</small></div><div className="batch-review-total"><small>{t("Accepted value · valid quotes only")}</small><strong>{formatMoney(acceptedTotal,jobs[0]?.currency??"INR")}</strong></div></div>
      <div className="batch-review-stats">
        <span className="included"><b>{acceptedCount}</b> {t("included")}</span>
        <span className="excluded"><b>{rejectedCount}</b> {t("excluded")}</span>
        <span className="output"><b>{acceptedOutputSides}</b> {t("document output sides")}</span>
        {acceptedIdSheets>0&&<span className="id-cards"><b>{acceptedIdSheets}</b> {t("ID-card A4 sheets")}</span>}
        {acceptedPhotoPages>0&&<span className="photos"><b>{acceptedPhotoPages}</b> {t("photo A4 pages")}</span>}
        {quoteRefreshRequiredCount>0&&<span className="refresh"><b>{quoteRefreshRequiredCount}</b> {t("need a fresh quote")}</span>}
      </div>
      <div className="batch-review-output-note"><span><strong>{t("Document output mix")}</strong></span><span><i className="mono-dot"/> {t("B&W")} <strong>{acceptedMonoPages}</strong></span><span><i className="colour-dot"/> {t("Colour")} <strong>{acceptedColourPages}</strong></span></div>
    </section>
    <div className="batch-review-list">{folders.map(folderName=>{
      const folderJobs=jobs.filter(job=>(job.batchGroupName??"Loose files")===folderName);
      const folderAccepted=folderJobs.filter(job=>decisions[job.id]??true).length;
      return <details className="batch-review-folder" key={folderName} open>
        <summary className="batch-review-folder-head"><FolderOpen size={15}/><strong>{folderName}</strong><small>{folderAccepted} of {folderJobs.length} included</small></summary>
        {folderJobs.map((job,index)=>{
          const accepted=decisions[job.id]??true;
          const needsFreshQuote=accepted&&quoteExpired(job);
          const outputSides=printJobOutputSides(job.pageCount,job.pagesPerSheet??1,job.copies);
          const fileName=job.document.originalFileName;
          const printDetail=job.printKind==="PHOTO"
            ? `${job.photoCount??0} finished photos · ${job.photoSizeCode??"Size not recorded"} · ${job.pageCount} A4 pages`
            : job.printKind==="ID_DOCUMENT"
              ? `${job.pageCount} prepared A4 ${job.pageCount===1?"sheet":"sheets"} · ${job.copies} ${job.copies===1?"copy":"copies"} · ID card layout`
              : `${job.pageCount} source pages → ${outputSides} output sides · ${job.copies} ${job.copies===1?"copy":"copies"} · ${job.pagesPerSheet??1} per sheet · ${job.colourMode} · ${job.isDuplex?"Double-sided":"Single-sided"}`;
          return <article className={"batch-review-row "+(accepted?"accepted":"rejected")} key={job.id}>
            <label className="batch-review-toggle" title={t("Include this file in the customer's batch checkout")}>
              <input type="checkbox" checked={accepted} disabled={busy} aria-label={t("Include {name} in batch checkout",{name:fileName})} onChange={event=>onDecision(job.id,event.currentTarget.checked)}/>
              <span>{t(accepted?"Included":"Excluded")}</span>
            </label>
            <span className="batch-review-index">{index+1}</span>
            <div className="batch-review-file">
              <div className="batch-review-file-heading"><strong>{fileName}</strong>{job.privatePrinting&&<span className="private-printing-tag">{t("Private · auto-delete")}</span>}<PrintKindTag printKind={job.printKind}/></div>
              <small>{printDetail}</small>
              <div className="batch-review-file-status"><Status label={t(!accepted?"Will be excluded":needsFreshQuote?"Fresh quote required":"Included in checkout")} tone={!accepted?"red":needsFreshQuote?"amber":"green"}/></div>
            </div>
            <div className="batch-review-file-finance"><strong>{!accepted?"Not charged":needsFreshQuote?"—":formatMoney(job.quotedAmountMinor,job.currency)}</strong><small>{!accepted?"Excluded":needsFreshQuote?"Recalculate before checkout":"Current quote"}</small></div>
            <button className="subtle-button batch-review-preview" type="button" disabled={busy||job.privatePrinting} onClick={()=>onPreview(job)}>{job.privatePrinting?"Private source":"Preview"}</button>
          </article>;
        })}
      </details>;
    })}</div>
    <div className="batch-branding-setting">
      <div className="batch-branding-copy">
        <strong>{t("SmartPrint PDF footer")} <InfoTooltip label={t("PDF footer details")}><span>{t("Footer only appears on PDFs with a verified blank bottom margin.")}</span><span>{t("Digitally signed or interactive PDFs remain unchanged.")}</span><span>{t("Original uploads and physical prints are never altered.")}</span><span>{t("Image-to-PDF exports currently remain unbranded.")}</span><span>{t("Branding off preserves a single-copy full-document PDF byte-for-byte.")}</span><span>{t("Multi-copy or reformatted exports may be flattened.")}</span></InfoTooltip></strong>
        <small>{t("Add a footer only to eligible PDFs with clear bottom margins. New batches start off.")}</small>
      </div>
      <label className="batch-branding-toggle">
        <input type="checkbox" checked={pdfBrandingEnabled&&!hasIdDocument} disabled={busy||hasIdDocument} onChange={event=>setPdfBrandingEnabled(event.currentTarget.checked)} aria-label={t("Add SmartPrint footer to PDF exports in this batch")}/>
        <span>{hasIdDocument?"Unavailable for ID card sheets":pdfBrandingEnabled?"On for this batch":"Off · default"}</span>
      </label>
    </div>
    <p className="batch-review-next-step">{quoteRefreshRequiredCount>0?quoteRefreshRequiredCount+" accepted file(s) need a new quote before customer checkout. ":""}Applying this review saves the file decisions. It does not collect payment or send files to a printer.</p>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose} disabled={busy}>{t("Keep reviewing")}</button><button className="primary-button" disabled={busy||jobs.length===0} onClick={()=>onApply(acceptedIds,pdfBrandingEnabled&&!hasIdDocument)}>{busy?<span className="button-spinner"/>:"Save batch review"}</button></div>
  </Modal>;
}

function JobHistoryModal({job,events,onClose}:{job:ShopPrintJob;events:ShopJobEvent[];onClose:()=>void}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  return <Modal title={job.jobNumber} subtitle={job.privatePrinting?"Private print · audit history":`Audit history · ${job.document.originalFileName}`} onClose={onClose}>
    <div className="shop-history-list">{events.length===0?<p>{t("No recorded events yet.")}</p>:events.map(event=><div key={event.id}><i/><span><strong>{prettyStatus(event.newStatus)}</strong><small>{event.actorType==="ShopUser"?"Shop team":event.actorType==="Customer"?"Customer":"System"} · {formatIstDateTime(event.createdUtc)}</small></span></div>)}</div>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose}>{t("Close history")}</button></div>
  </Modal>;
}

function DocumentPreviewModal({name,url,mediaType,job,readOnly=false,isSavedOutput=false,focusEditOnMount=false,onClose,onApprove,onReject,onRefund,onEditPhoto}:{
  name:string;url:string;mediaType:string;job:ShopPrintJob;readOnly?:boolean;isSavedOutput?:boolean;focusEditOnMount?:boolean;onClose:()=>void;onApprove:()=>void;onReject:()=>void;onRefund:()=>void;onEditPhoto?:()=>void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const editPhotoButtonRef=useRef<HTMLButtonElement|null>(null);
  const isImage=mediaType==="image/jpeg"||mediaType==="image/png";
  useEffect(()=>{
    if(focusEditOnMount)editPhotoButtonRef.current?.focus();
  },[focusEditOnMount]);
  useEffect(()=>{
    if(!isDesktopApp()||(!isImage&&job.printKind!=="PHOTO"))return;
    void setDesktopContentProtection(true);
    return ()=>{void setDesktopContentProtection(false);};
  },[isImage,job.printKind]);
  const photoDimensions:Record<string,string>={"35x45":"35 × 45 mm","25x35":"25 × 35 mm","2x2":"50.8 × 50.8 mm","4x6":"101.6 × 152.4 mm"};
  const previewSummary = isSavedOutput
    ? "Final PDF export saved by this shop. This is the actual output file; the uploaded original remains separate and unchanged."
    : job.printKind==="PHOTO"
      ? `${job.photoCount??0} ${photoDimensions[job.photoSizeCode??""]??job.photoSizeCode??""} photos · ${job.photosPerPage??"Layout density not recorded"} per A4 page · ${job.pageCount} A4 PDF page(s). Preview displays the complete prepared file.`
      : `${customerDocumentOutputSummary(job.pageCount,job.copies,job.pagesPerSheet??1,job.isDuplex)}. Preview displays the uploaded original once; the assigned printer driver applies the N-up layout at print time.`;
  const subtitle = isSavedOutput ? "Saved PDF output · Read-only preview" : readOnly ? `Read-only preview · ${previewSummary}` : job.batchId ? `File review · Batch ${job.batchId.slice(0,8).toUpperCase()} · ${previewSummary}` : previewSummary;
  return <Modal title={name} subtitle={subtitle} onClose={onClose}>
    <ShopDocumentPreviewSurface name={name} url={url} mediaType={mediaType} printKind={job.printKind}/>
    {onEditPhoto&&<div className="file-review-note"><ImageIcon size={16}/><span>{t("Open the same Photo Studio used by customers to crop, improve the background, add form details and prepare this photo for shop printing.")}</span><button ref={editPhotoButtonRef} className="secondary-button" onClick={onEditPhoto}>{t("Enhance photo for printing")}</button></div>}
    {!readOnly&&isImage&&job.status==="AWAITING_SHOP_APPROVAL"&&job.storageMode==="LOCAL_AGENT"&&<div className="file-review-note"><ShieldCheck size={16}/><span>{t("This original is stored only on the paired shop computer. It remains local; this version does not copy it to cloud storage for editing.")}</span></div>}
    {["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&<div className="file-review-note"><Check size={16}/><span>{readOnly ? t("This preview is part of the batch review. Keep or change this file's decision in the batch panel after closing the preview.") : t("Review this file independently. Accepting or rejecting it will not change any other file in the batch.")}</span></div>}
    {job.status==="PAID_AUTHORISED"&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&<div className="file-refund-note"><Wallet size={16}/><span>{t("Paid amount:")} <strong>{formatMoney(job.payment.amountMinor,job.payment.currency)}</strong>. {t("A refund can be recorded while this file has not yet been handed to the shop computer.")}</span></div>}
    <div className="modal-actions">
      {!readOnly&&job.status==="AWAITING_SHOP_APPROVAL"&&<button className="primary-button" onClick={onApprove}><Check size={15}/> {t("Accept this file")}</button>}
      {!readOnly&&["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&<button className="secondary-button reject-action" onClick={onReject}><X size={15}/> {t("Reject this file")}</button>}
      {!readOnly&&(job.status==="PAID_AUTHORISED"||job.status==="REJECTED")&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId&&<button className="secondary-button reject-action" onClick={onRefund}><RotateCcw size={15}/> {job.status==="REJECTED"?"Refund rejected file":"Review pages & refund"}</button>}
      <button className="secondary-button" onClick={onClose}>{t("Close preview")}</button>
    </div>
  </Modal>;
}

function RefundModal({job,token,busy,onClose,onSubmit}:{
  job:ShopPrintJob; token:string; busy:boolean; onClose:()=>void;
  onSubmit:(values:{refundMethod:"UPI"|"CASH";refundReference:string|null;reason:string;rejectedPageNumbers:number[];idempotencyKey:string})=>void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [rejectedPages,setRejectedPages]=useState<Set<number>>(()=>{
    if(job.status==="REJECTED")return new Set<number>(Array.from({length:job.pageCount},(_,index)=>index+1));
    try{return new Set<number>(JSON.parse(job.rejectedPageNumbersJson||"[]") as number[]);}catch{return new Set<number>();}
  });
  const [estimate,setEstimate]=useState<Awaited<ReturnType<typeof previewShopPrintRefund>>|null>(null);
  const [estimating,setEstimating]=useState(false);
  const [estimateError,setEstimateError]=useState("");
  const [method,setMethod]=useState<"UPI"|"CASH">(job.payment?.method==="CASH"?"CASH":"UPI");
  const [reference,setReference]=useState("");
  const [reason,setReason]=useState(job.status==="REJECTED"?"Shop rejected the entire file after preview.":"Customer rejected selected pages after shop preview.");
  const [confirmed,setConfirmed]=useState(false);
  const refundRequest=useRef<{signature:string;key:string}|null>(null);
  const selectedPages=Array.from(rejectedPages).sort((a,b)=>a-b);
  const selectionKey=selectedPages.join(",");
  const alreadyRejected=(()=>{try{return new Set<number>(JSON.parse(job.rejectedPageNumbersJson||"[]") as number[]);}catch{return new Set<number>();}})();

  useEffect(()=>{
    let active=true;
    setEstimating(true);setEstimateError("");setEstimate(null);
    void previewShopPrintRefund(token,job.id,selectedPages).then(value=>{if(active)setEstimate(value);})
      .catch(error=>{if(active)setEstimateError(error instanceof Error?error.message:"Could not calculate the refund.");})
      .finally(()=>{if(active)setEstimating(false);});
    return ()=>{active=false;};
  },[token,job.id,selectionKey]);

  const additional=estimate?.additionalRefundAmountMinor??0;
  const hasNewPages=selectedPages.some(page=>!alreadyRejected.has(page));
  const canSubmit=!!estimate&&hasNewPages&&reason.trim().length>=3&&confirmed&&
    (method==="CASH"||reference.trim().length>=4);
  const togglePage=(page:number,checked:boolean)=>{
    if(alreadyRejected.has(page))return;
    setRejectedPages(current=>{const next=new Set(current);if(checked)next.add(page);else next.delete(page);return next;});
  };

  return <Modal title={t(job.status==="REJECTED"?"Refund rejected file":"Review pages and refund")} subtitle={t(job.status==="REJECTED"?"All pages are selected because the shop rejected this entire file. Confirm the full-file refund before handoff.":"Every page starts accepted. Tick only pages to reject; selections can be undone before recording. Already refunded pages stay excluded.")} onClose={onClose}>
    <div className="refund-summary-card">
      <div><span>{t("FILE")}</span><strong>{job.document.originalFileName}</strong></div>
      <div><span>{t("VERIFIED PAYMENT")}</span><strong>{formatMoney(job.payment?.amountMinor||job.quotedAmountMinor,job.payment?.currency||job.currency)}</strong></div>
    </div>
    <div className="refund-selection-actions">
      <span>{t("Reject specific pages or the entire file.")}</span>
      <button type="button" className="secondary-button reject-action" disabled={busy} onClick={()=>setRejectedPages(new Set(Array.from({length:job.pageCount},(_,index)=>index+1)))}>{t("Reject entire file · refund all pages")}</button>
    </div>
    <div className="page-refund-grid" role="group" aria-label={t("Pages to reject")}>
      {Array.from({length:job.pageCount},(_,index)=>index+1).map(page=>{
        const rejected=rejectedPages.has(page), locked=alreadyRejected.has(page);
        return <label className={locked?"page-refund-chip already-rejected":rejected?"page-refund-chip selected":"page-refund-chip"} key={page}>
          <input type="checkbox" checked={rejected} disabled={locked||busy} onChange={event=>togglePage(page,event.target.checked)}/>
          <span>Page {page}{locked?" · refunded":rejected?" · reject":""}</span>
        </label>;
      })}
    </div>
    <div className="page-refund-estimate">
      {estimating?<span>{t("Calculating the exact refund from the original quote…")}</span>:
       estimateError?<span className="inline-error">{estimateError}</span>:
       estimate?<><span>{t("{count} pages will remain in the print job.").replace("{count}", String(estimate.acceptedPageCount))}</span><strong>{t("Return {amount}").replace("{amount}", formatMoney(additional,estimate.currency))}</strong><small>{t("Cumulative returned: {returned} · remaining payment: {remaining}").replace("{returned}", formatMoney(estimate.cumulativeRefundAmountMinor,estimate.currency)).replace("{remaining}", formatMoney(estimate.remainingPaidAmountMinor,estimate.currency))}</small></>:<span>{t("Select page(s) to see the refund amount.")}</span>}
    </div>
    {estimate&&additional>0&&<form className="refund-form" onSubmit={event=>{
      event.preventDefault();
      if(!canSubmit)return;
      const payload={refundMethod:method,refundReference:method==="UPI"?reference.trim():null,reason:reason.trim(),rejectedPageNumbers:selectedPages};
      const signature=JSON.stringify(payload);
      if(refundRequest.current?.signature!==signature)refundRequest.current={signature,key:crypto.randomUUID()};
      onSubmit({...payload,idempotencyKey:refundRequest.current.key});
    }}>
      <label>{t("Refund returned by")}<select value={method} onChange={event=>setMethod(event.target.value as "UPI"|"CASH")}><option value="UPI">{t("UPI transfer")}</option><option value="CASH">{t("Cash returned at counter")}</option></select></label>
      {method==="UPI"&&<label>{t("Refund transaction reference")}<input value={reference} onChange={event=>setReference(event.target.value)} maxLength={100} placeholder={t("Enter the refund UPI reference")} required/></label>}
      <label>{t("Reason for rejecting these pages")}<textarea rows={3} maxLength={300} value={reason} onChange={event=>setReason(event.target.value)} required/></label>
      <label className="refund-confirm-line"><input type="checkbox" checked={confirmed} onChange={event=>setConfirmed(event.target.checked)}/><span>{t("I confirm that I have already returned the exact additional amount shown above. SmartPrint records the refund; it does not send money automatically.")}</span></label>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>{t("Cancel")}</button><button className="primary-button" disabled={busy||estimating||!canSubmit}>{busy?<span className="button-spinner"/>:t("Record refund and reject selected pages")}</button></div>
    </form>}
    {estimate&&additional===0&&<div className="refund-form"><p>{t("Select at least one additional page that produces a positive refund from the original quote. No page is rejected until a refund can be recorded.")}</p><div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>{t("Close")}</button></div></div>}
  </Modal>;
}

function rejectedPageList(json?:string) {
  try {
    const pages=JSON.parse(json||"[]");
    return Array.isArray(pages)&&pages.length>0?pages.join(", "):"selected pages";
  } catch {
    return "selected pages";
  }
}

function spoolerStatusWarning(status?: string | null, printerStatus?: string | null) {
  const spooler = (status || "").toUpperCase();
  const printer = (printerStatus || "").toUpperCase();
  if (spooler === "OFFLINE" || printer === "OFFLINE")
    return "Printer is offline. Reconnect it; Windows may resume this same queued job. Do not start a duplicate.";
  if (spooler === "RETAINED")
    return "Windows is holding this print job. SmartPrint will release it only after Windows confirms printing is complete. Confirm output only after the physical pages have printed.";
  if (spooler === "PAPEROUT")
    return "The printer reports no paper. Refill it and check the same Windows queue before continuing.";
  if (spooler === "PAUSED" || printer === "PAUSED")
    return "The printer or Windows print queue is paused. Resume the existing job in Windows; do not submit another copy.";
  if (spooler === "PRINTER_UNAVAILABLE")
    return "The Agent cannot currently open this printer queue. Reconnect or restore the queue, then refresh this job.";
  if (spooler === "ERROR")
    return "Windows reports a printer error. Resolve it in the printer queue and inspect any pages already produced.";
  if (spooler === "USERINTERVENTION")
    return "The printer requires attention. Resolve the prompt at the device before checking the same queued job.";
  return "";
}
function prettyStatus(status:string) {
  const names:Record<string,string>={
    AWAITING_CONFIGURATION:"Being prepared",
    AWAITING_CUSTOMER_APPROVAL:"Customer approval needed",
    QUOTE_EXPIRED:"Price needs refreshing",
    AWAITING_SHOP_APPROVAL:"Needs your review",
    REJECTED:"Not accepted",
    REFUNDED:"Refunded",
    PARTIALLY_REFUNDED:"Partly refunded",
    UPLOADED:"Received",
    VALIDATING:"Checking files",
    ANALYSING:"Checking files",
    AWAITING_PAYMENT:"Ready for payment",
    AWAITING_CUSTOMER_PAYMENT:"Ready for payment",
    AWAITING_COUNTER_PAYMENT:"Pay at shop",
    CASH_AWAITING_PAYMENT:"Cash payment due",
    CUSTOMER_REPORTED:"Payment being checked",
    PAYMENT_REPORTED:"Payment being checked",
    PAYMENT_REJECTED:"Payment not confirmed",
    AUTHORISED:"Approved",
    PAID_AUTHORISED:"Payment received",
    VERIFIED:"Payment received",
    LOCAL_QUEUE_CLAIMED:"Shop computer preparing",
    LOCAL_QUEUE_READY:"Ready for print",
    PRINT_AUTHORISED:"Waiting for shop computer",
    PRINT_SUBMITTING:"Sending to printer",
    SPOOLER_ACCEPTED:"Sent to printer",
    SUBMITTING_TO_SPOOLER:"Sent to printer",
    QUEUED:"Waiting to print",
    PRINTING:"Printing",
    OUTPUT_UNCONFIRMED:"Shop checking print",
    TEST_OUTPUT_READY:"Test complete",
    PDF_EXPORTING:"Preparing PDF",
    PDF_EXPORTED:"PDF ready",
    COMPLETED:"Ready for collection",
    PARTIALLY_COMPLETED:"Some files complete",
    MIXED_OUTCOME:"Some files need attention",
    MIXED_PROGRESS:"In progress",
    FAILED:"Could not print",
    CANCELLED:"Cancelled"
  };
  return names[status] || "In progress";
}
function printBlockMessage(code:string):string {
  switch(code) {
    case "PAPER_SIZE_UNSUPPORTED":
    case "PAPER_SIZE_REJECTED":
      return "The selected printer did not confirm the requested paper size.";
    case "LANDSCAPE_UNSUPPORTED":
    case "ORIENTATION_UNCONFIRMED":
      return "The selected printer did not confirm the requested page orientation.";
    case "COLOUR_UNSUPPORTED":
    case "COLOUR_REJECTED":
      return "The selected printer did not confirm colour output.";
    case "DUPLEX_UNSUPPORTED":
    case "DUPLEX_REJECTED":
      return "The selected printer did not confirm double-sided output.";
    default:
      return "The Windows Agent could not confirm the requested settings with this output device.";
  }
}

function isPrinterReadyForSelection(printer: PrinterDevice, agents: readonly AgentDevice[], now = Date.now()) {
  return getPrinterReadinessReason(printer, agents, now) === null;
}

function formatMoney(minor:number,currency:string) {
  return new Intl.NumberFormat("en-IN",{style:"currency",currency,minimumFractionDigits:2}).format(minor/100);
}
function formatBytes(bytes:number) {
  return bytes<1024*1024?`${(bytes/1024).toFixed(0)} KB`:`${(bytes/(1024*1024)).toFixed(2)} MB`;
}

function SettingsPage({ profile, onSave, busy }: { profile:ShopProfile; onSave:(v:{displayName:string;logoUrl:string|null;accentColor:string})=>void; busy:boolean }) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [displayName,setDisplayName]=useState(profile.displayName);
  const [logoUrl,setLogoUrl]=useState(profile.logoUrl || "");
  const [accentColor,setAccentColor]=useState(profile.accentColor || "#7662E8");
  useEffect(()=>{setDisplayName(profile.displayName);setLogoUrl(profile.logoUrl||"");setAccentColor(profile.accentColor||"#7662E8");},[profile]);
  return <section className="panel settings-panel"><div className="settings-intro"><div className="settings-icon"><Settings size={20}/></div><div><h2>{t("Shop profile & branding")}</h2><p>{t("Update the shop name and brand customers see when they connect through your QR stations.")}</p></div></div>
    <form className="settings-form" onSubmit={event=>{event.preventDefault();onSave({displayName:displayName.trim(),logoUrl:logoUrl.trim()||null,accentColor});}}>
      <label>{t("Customer-facing shop name")}<input value={displayName} onChange={e=>setDisplayName(e.target.value)} required maxLength={180}/></label>
      <label>{t("Logo URL")} <span className="field-hint">{t("Optional · secure HTTPS image URL")}</span><input type="url" value={logoUrl} onChange={e=>setLogoUrl(e.target.value)} placeholder="https://example.com/logo.png"/></label>
      <label>{t("Brand accent colour")}<div className="colour-picker-row"><input type="color" value={accentColor} onChange={e=>setAccentColor(e.target.value)}/><input value={accentColor} onChange={e=>setAccentColor(e.target.value)} pattern="^#[0-9A-Fa-f]{6}$" required/></div></label>
      <div className="settings-summary"><ShieldCheck size={16}/><span>{t("Standard Smart Print branding is active. Full white-label branding is a paid plan upgrade.")}</span></div>
      <div className="settings-actions"><button className="primary-button" disabled={busy}>{busy?<span className="button-spinner"/>:t("Save shop identity")}</button></div>
    </form>
    <div className="settings-meta"><span>{t("Shop slug")}</span><strong>{profile.slug}</strong><span>{t("Currency")}</span><strong>{profile.currency}</strong><span>{t("Time zone")}</span><strong>{profile.timeZone}</strong></div>
  </section>;
}



function ShopOwnerAccountModal({ profile, busy, onClose, onSave }: {
  profile: ShopProfile;
  busy: boolean;
  onClose: () => void;
  onSave: (values: { ownerName: string; email: string; currentPassword?: string }) => Promise<void>;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [ownerName, setOwnerName] = useState(profile.ownerName);
  const [email, setEmail] = useState(profile.ownerEmail);
  const [currentPassword, setCurrentPassword] = useState("");
  const [saveError, setSaveError] = useState("");
  const emailChanged = email.trim().toLowerCase() !== profile.ownerEmail.trim().toLowerCase();
  return (
    <Modal title={t("Personal details")} subtitle={t("Update the name and sign-in email for your shop owner account.")} onClose={onClose}>
      <form className="modal-form account-edit-form" onSubmit={async event => {
        event.preventDefault();
        setSaveError("");
        try {
          await onSave({
            ownerName: ownerName.trim(),
            email: email.trim(),
            currentPassword: emailChanged ? currentPassword : undefined
          });
          onClose();
        } catch (exception) {
          setSaveError(exception instanceof Error ? exception.message : "Could not update your personal details.");
        }
      }}>
        <label>{t("Your name")}
          <input value={ownerName} onChange={event => setOwnerName(event.target.value)}
            autoComplete="name" maxLength={160} required/>
        </label>
        <label>{t("Sign-in email")}
          <input type="email" value={email}
            onChange={event => setEmail(event.currentTarget.value)}
            onInput={event => setEmail(event.currentTarget.value)}
            autoComplete="email" maxLength={256} required/>
        </label>
        {emailChanged && <>
          <div className="modal-note account-email-change-note">
            <ShieldCheck size={16}/>
            <span>{t("This address becomes your sign-in email immediately. Make sure it is correct. Your current password is required to confirm the change.")}</span>
          </div>
          <label>{t("Current password")}
            <input type="password" value={currentPassword}
              onChange={event => setCurrentPassword(event.target.value)}
              autoComplete="current-password" required/>
          </label>
        </>}
        {saveError && <div className="inline-error account-edit-error" role="alert">{saveError}</div>}
        <div className="modal-actions">
          <button type="button" className="secondary-button" onClick={onClose} disabled={busy}>{t("Cancel")}</button>
          <button className="primary-button" disabled={busy || !ownerName.trim() || !email.trim() || (emailChanged && !currentPassword)}>
            {busy ? <span className="button-spinner"/> : "Save personal details"}
          </button>
        </div>
      </form>
    </Modal>
  );
}

function HelpSupportModal({ onClose, onNavigate }: { onClose: () => void; onNavigate: (section: Section) => void }) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const topics: { section: Section; title: string; description: string; icon: typeof FileText }[] = [
    { section: "Print Queue", title: "Print jobs and approvals", description: "Review incoming jobs, decisions, print status and refunds.", icon: FileText },
    { section: "Printers", title: "Printers and connections", description: "Check printer setup, availability and device details.", icon: Printer },
    { section: "Print Agent", title: "Shop computer and Print Agent", description: "Pair or reconnect the Windows computer that runs printing.", icon: Monitor },
    { section: "QR Stations", title: "QR stations", description: "Manage counter QR codes and their assigned printers.", icon: QrCode },
    { section: "Pricing", title: "Pricing", description: "Review the rates customers see before they submit a job.", icon: Wallet }
  ];

  return (
    <Modal title={t("Help & support")} subtitle={t("Choose the part of your shop workspace you need help with.")} onClose={onClose}>
      <div className="help-topic-list">
        {topics.map(({ section, title, description, icon: Icon }) => (
          <button type="button" className="help-topic" key={section} onClick={() => onNavigate(section)}>
            <span className="help-topic-icon"><Icon size={17}/></span>
            <span className="help-topic-copy"><strong>{title}</strong><small>{description}</small></span>
            <ArrowRight size={16}/>
          </button>
        ))}
      </div>
      <p className="help-modal-note">{t("These shortcuts open the existing shop tools. They do not change your settings or print jobs.")}</p>
    </Modal>
  );
}

function StationModal({ busy, printers, onClose, onSubmit }: {
  busy:boolean; printers:PrinterDevice[]; onClose:()=>void; onSubmit:(v:{displayName:string;printerDeviceId?:string})=>void;
}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [displayName,setDisplayName]=useState("Front counter");
  const [printerDeviceId,setPrinterDeviceId]=useState("");
  return <Modal title={t("Create a QR station")} subtitle={t("Give customers a clear entry point for this counter or printer.")} onClose={onClose}>
    <form className="modal-form" onSubmit={e=>{e.preventDefault();onSubmit({displayName,printerDeviceId:printerDeviceId||undefined});}}>
      <label>{t("Station name")}<input value={displayName} onChange={e=>setDisplayName(e.target.value)} placeholder={t("e.g. Front counter")} required maxLength={120}/></label>
      <label>{t("Default printer")} <span className="field-hint">{t("Optional · only approved virtual test queues are included")}</span><select value={printerDeviceId} onChange={e=>setPrinterDeviceId(e.target.value)}><option value="">{t("No default printer")}</option>{printers.filter(printer=>printer.isActive&&(printer.outputMode!=="UNSUPPORTED_VIRTUAL"||printer.virtualTestGrantActive===true)).map(printer=><option key={printer.id} value={printer.id}>{printer.outputMode==="PDF_EXPORT"?"Microsoft Print to PDF · ":""}{printer.displayName} · {printer.manufacturer}</option>)}</select></label>
      <div className="modal-note"><QrCode size={15}/> The QR contains a public station identifier, not a permanent print authorisation. Every scan creates an expiring customer session. Only enabled printers are offered as defaults; you can also assign a printer to each paid job in the queue.</div>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>{t("Cancel")}</button><button className="primary-button" disabled={busy||!displayName.trim()}>{busy?<span className="button-spinner"/>:"Create station"}</button></div>
    </form>
  </Modal>;
}

function Modal({title,subtitle,children,onClose}:{title:string;subtitle:string;children:ReactNode;onClose:()=>void}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  return <div className="modal-scrim" onMouseDown={e=>{if(e.target===e.currentTarget)onClose();}}><section className="modal-card" role="dialog" aria-modal="true" aria-label={title}><div className="modal-heading"><div><h2>{title}</h2><p>{subtitle}</p></div><button className="icon-button" onClick={onClose} aria-label={t("Close")}><X size={18}/></button></div>{children}</section></div>;
}

function EmptyModule({icon,title,text,actionLabel,onAction}:{icon:ReactNode;title:string;text:string;actionLabel?:string;onAction?:()=>void}) {
  return <section className="panel empty-module"><div className="empty-module-icon">{icon}</div><h2>{title}</h2><p>{text}</p>{actionLabel&&onAction&&<button className="primary-button" onClick={onAction}>{actionLabel}<ArrowRight size={15}/></button>}</section>;
}

function SetupCheck({complete,title,detail,onClick}:{complete:boolean;title:string;detail:string;onClick:()=>void}) {
  return <button className="setup-check" onClick={onClick}><span className={complete?"check-circle complete":"check-circle"}>{complete?<Check size={14}/>:null}</span><span className="setup-check-copy"><strong>{title}</strong><small>{detail}</small></span><ArrowRight size={16}/></button>;
}
function Metric({icon,label,value,note,accent}:{icon:ReactNode;label:string;value:string;note:string;accent:string}) {
  return <section className="metric-card"><div className="metric-top"><div className={"metric-icon "+accent}>{icon}</div><span className="metric-dots"><i/><i/><i/></span></div><div className="metric-label">{label}</div><div className="metric-value">{value}</div><div className="metric-foot"><span>{note}</span></div></section>;
}
function Device({name,detail,state,tone}:{name:string;detail:string;state:string;tone:string}) {
  return <div className="device-row"><div className="device-glyph"><Printer size={16}/></div><div className="device-copy"><strong>{name}</strong><span>{detail}</span></div><Status label={state} tone={tone}/></div>;
}
function Status({label,tone}:{label:string;tone:string}) { return <span className={"status-badge "+tone}><i/>{label}</span>; }
function PrintKindTag({printKind,count}:{printKind?:string|null;count?:number}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const isPhoto=printKind==="PHOTO";
  const isIdDocument=printKind==="ID_DOCUMENT";
  const label=isPhoto?"Photo":isIdDocument?"ID card":"Document";
  const countLabel=typeof count==="number"&&count>1?` · ${count}`:"";
  return <span className={`print-kind-tag print-kind-tag--${isPhoto?"photo":"document"}`} title={t(isPhoto?"Photo printing job":isIdDocument?"Private ID card print job":"Document printing job")}>{t(label)}{countLabel}</span>;
}
function normalizedCustomerPrintKind(value:string|null|undefined):"DOCUMENT"|"PHOTO"|"ID_DOCUMENT" {
  return value==="PHOTO"||value==="ID_DOCUMENT"?value:"DOCUMENT";
}

function initials(value:string) { return value.trim().split(/\s+/).slice(0,2).map(part=>part[0]?.toUpperCase()||"").join("") || "SP"; }
function SubscriptionPage({ summary, token, onUpdated }: { summary: ShopSubscriptionSummary; token: string; onUpdated: (summary: ShopSubscriptionSummary) => void }) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const { subscription, plan, entitlements, usage } = summary;
  const status = subscription.status.toLowerCase();
  const statusTone = status === "active" ? "tone-success" : status === "trial" ? "tone-warning" : status === "expired" ? "tone-danger" : "tone-neutral";
  const planName = plan?.name ?? (subscription.planCode === "TRIAL" ? "Trial plan" : subscription.planCode);
  const periodStart = formatIstDate(subscription.periodStartUtc);
  const periodEnd = formatIstDate(subscription.periodEndUtc);
  const price = (amount: number | null | undefined, currency: string) =>
    amount == null ? "Not configured" : `${currency} ${(amount / 100).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

  return (
    <section className="subscription-workspace">
      <div className="subscription-hero-grid">
        <section className="panel subscription-current-panel">
          <div className="subscription-panel-top">
            <span className="subscription-eyebrow">{t("CURRENT SUBSCRIPTION")}</span>
            <span className={`subscription-status ${statusTone}`}>{subscription.status}</span>
          </div>
          <h2>{planName}</h2>
          <p className="subscription-plan-meta">
            {plan ? `Plan ${plan.code} · version ${plan.version} · ${plan.status}` : "A versioned plan has not yet been assigned to this shop."}
          </p>
          <div className="subscription-period">
            <div><span>{t("Billing interval")}</span><strong>{subscription.billingInterval}</strong></div>
            <div><span>{t("Current period")}</span><strong>{periodStart} – {periodEnd}</strong></div>
          </div>
          <div className="subscription-prices">
            <div><span>{t("Monthly plan price")}</span><strong>{price(plan?.monthlyPriceMinor, plan?.currency ?? "INR")}</strong></div>
            <div><span>{t("Annual plan price")}</span><strong>{price(plan?.annualPriceMinor, plan?.currency ?? "INR")}</strong></div>
          </div>
          {!entitlements.isAccessActive && (
            <div className="subscription-legacy-note subscription-expired-note"><ShieldCheck size={15}/><span>{status === "expired" ? t("Your subscription expired. Renew using the plans below to restore access. Existing customer payment records and completed jobs remain available.") : t("Shop access is currently restricted. Review your account status or contact SmartPrint support for assistance.")}</span></div>
          )}
          {entitlements.isLegacyUnconfigured && entitlements.isAccessActive && (
            <div className="subscription-legacy-note"><ShieldCheck size={15}/><span>{t("This subscription predates plan assignment. Existing shop access is being preserved until its terms are explicitly assigned.")}</span></div>
          )}
        </section>

        <section className="panel subscription-feature-panel">
          <div className="panel-heading"><div><h2>{t("Included capabilities")}</h2><p>{t("Resolved from the version assigned to your subscription")}</p></div></div>
          <div className="subscription-feature-list">
            <FeatureLine label={t("Cloud-assisted printing")} enabled={plan?.cloudAssistedPrintingEnabled ?? entitlements.cloudAssistedPrintingEnabled}/>
            <FeatureLine label={t("White-label experience")} enabled={plan?.whiteLabelEnabled ?? entitlements.whiteLabelEnabled}/>
            <FeatureLine label={t("Advanced reports")} enabled={plan?.advancedReportsEnabled ?? entitlements.advancedReportsEnabled}/>
          </div>
          <div className="subscription-upgrade-note">
            <CreditCard size={15}/>
            <span>{t("Manage your SmartPrint subscription securely with Razorpay. Customer print payments remain separate and continue to be handled by your shop.")}</span>
          </div>
        </section>
      </div>

      <ShopSubscriptionCheckout token={token} onSubscriptionUpdated={onUpdated} currentSubscription={subscription}/>

      <section className="panel subscription-usage-panel">
        <div className="subscription-usage-heading">
          <div><h2>{t("Usage and limits")}</h2><p>{t("Live counts for this shop. Monthly print jobs use the UTC calendar month.")}</p></div>
          <span className="subscription-updated">Updated {formatIstTime(summary.generatedUtc)}</span>
        </div>
        <div className="subscription-usage-grid">
          <UsageMeter label={t("Selected printers")} used={usage.printerCount} maximum={entitlements.maxPrinters}/>
          <UsageMeter label={t("Paired computers")} used={usage.agentDeviceCount} maximum={entitlements.maxAgentDevices}/>
          <UsageMeter label={t("QR stations")} used={usage.qrStationCount} maximum={entitlements.maxQrStations}/>
          <UsageMeter label={t("Print jobs this month")} used={usage.monthlyPrintJobCount} maximum={entitlements.maxMonthlyPrintJobs}/>
        </div>
        <div className="subscription-document-limit">
          <span>{t("Maximum cloud document size")}</span>
          <strong>{entitlements.maxDocumentBytes == null ? "No configured cap" : `${(entitlements.maxDocumentBytes / (1024 * 1024)).toLocaleString("en-IN", { maximumFractionDigits: 2 })} MB`}</strong>
        </div>
        <div className="subscription-usage-footnote">{t("Printer quota applies only to selected supported output queues. All Windows queues discovered by your Print Agent remain visible, including unselected alternatives, and you can replace a selected queue without losing its discovery record. QR stations and paired computers follow their own plan limits.")}</div>
      </section>
    </section>
  );
}

function SubscriptionExpiryReminder({
  summary,
  onNavigate
}: {
  summary: ShopSubscriptionSummary;
  onNavigate: () => void;
}) {
  const reminderState = getSubscriptionReminderState(
    summary.subscription.status,
    summary.entitlements.isAccessActive,
    summary.subscription.periodEndUtc
  );
  const expired = reminderState === "expired";
  const dueSoon = reminderState === "due-soon";
  if (!expired && !dueSoon) return null;

  const isTrial = summary.subscription.status.toLowerCase() === "trial";
  return (
    <div className={expired ? "subscription-expiry-reminder expired" : "subscription-expiry-reminder"}>
      <div>
        <strong>{expired ? "Your SmartPrint subscription has expired" : isTrial ? "Your SmartPrint trial is ending soon" : "Your SmartPrint subscription is ending soon"}</strong>
        <span>{expired
          ? "New print requests are paused. Renew below to restore access; your existing customer payment records remain available."
          : `Your current access ends on ${formatIstDate(summary.subscription.periodEndUtc)}. Renew before then to keep accepting new print requests.`}</span>
      </div>
      <button type="button" className="secondary-button small" onClick={onNavigate}>
        {expired ? "Renew subscription" : "Review renewal"}
      </button>
    </div>
  );
}

function UsageMeter({ label, used, maximum }: { label: string; used: number; maximum: number | null }) {
  const boundedMaximum = maximum == null ? null : Math.max(0, maximum);
  const percentage = boundedMaximum == null ? 0 : boundedMaximum > 0 ? Math.min(100, used / boundedMaximum * 100) : 100;
  return (
    <div className="subscription-usage-item">
      <div className="subscription-usage-item-top"><span>{label}</span><strong>{used.toLocaleString("en-IN")} <small>{boundedMaximum == null ? "/ unlimited" : `/ ${boundedMaximum.toLocaleString("en-IN")}`}</small></strong></div>
      <div className="subscription-progress-track"><span style={{ width: boundedMaximum == null ? "0%" : `${percentage}%` }}/></div>
      <small className="subscription-usage-caption">{boundedMaximum == null ? "No configured plan limit" : boundedMaximum === 0 ? "Not available on this plan" : `${Math.max(0, boundedMaximum - used).toLocaleString("en-IN")} remaining`}</small>
    </div>
  );
}

function FeatureLine({ label, enabled }: { label: string; enabled: boolean }) {
  return (
    <div className="subscription-feature-line">
      <span className={enabled ? "subscription-feature-indicator enabled" : "subscription-feature-indicator"}>{enabled ? <Check size={13}/> : <X size={13}/>}</span>
      <span>{label}</span>
      <strong>{enabled ? "Included" : "Not included"}</strong>
    </div>
  );
}

function pageDescription(section:Section) {
  const descriptions:Record<Section,string> = {
    "Order Journey":"See every customer order move from upload through review, payment and printing to handover.",
    "Overview":"Your shop setup and operations, all in one place.",
    "Print Queue":"Review real customer uploads and server-calculated print quotes.",
    "Printers":"Manage connected devices, capabilities and health.",
    "QR Stations":"Create secure customer entry points for each counter.",
    "Print Agent":"Pair the Windows computer that hosts your installed printers.",
    "Pricing":"Set your mono, colour and duplex rates.",
    "Subscription":"Review your assigned plan, entitlement limits and current usage.",
    "Payments":"Configure your shop's payment methods and verify customer print payments.",
    "Customers":"View customer activity and print history.",
    "Reports":"Understand print volume, shop revenue and performance.",
    "Settings":"Update the shop name and branding customers see when they connect."
  };
  return descriptions[section];
}
async function copyText(value:string):Promise<boolean> {
  try {
    if(navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value);
      return true;
    }
  } catch {
    // Fall through to the in-page clipboard fallback.
  }
  const helper=document.createElement("textarea");
  helper.value=value;
  helper.setAttribute("readonly","");
  helper.style.position="fixed";
  helper.style.opacity="0";
  document.body.appendChild(helper);
  helper.select();
  try { return document.execCommand("copy"); } catch { return false; }
  finally { helper.remove(); }
}

function CustomerInlineNotice({kind,title,children}:{kind:"info"|"warning";title:string;children:ReactNode}) {
  const Icon=kind==="warning"?AlertTriangle:Info;
  return <div className={"customer-inline-notice "+kind} role={kind==="warning"?"alert":"status"}>
    <Icon size={16} aria-hidden="true"/>
    <div><strong>{title}</strong><span>{children}</span></div>
  </div>;
}

function InfoTooltip({label,children}:{label:string;children:ReactNode}) {
  const tooltipId=useId();
  const rootRef=useRef<HTMLSpanElement>(null);
  const tooltipRef=useRef<HTMLSpanElement>(null);
  const [open,setOpen]=useState(false);
  const [tooltipPosition,setTooltipPosition]=useState({left:12,top:12});
  const updateTooltipPosition=useCallback(()=>{
    const anchor=rootRef.current?.getBoundingClientRect();
    const tooltip=tooltipRef.current;
    if(!anchor||!tooltip)return;
    const gutter=12;
    const tooltipBounds=tooltip.getBoundingClientRect();
    const availableWidth=Math.max(0,window.innerWidth-gutter*2);
    const tooltipWidth=Math.min(tooltipBounds.width||320,availableWidth);
    const maxLeft=Math.max(gutter,window.innerWidth-tooltipWidth-gutter);
    const left=Math.max(gutter,Math.min(anchor.left,maxLeft));
    const below=anchor.bottom+8;
    const top=below+tooltipBounds.height<=window.innerHeight-gutter
      ?below
      :Math.max(gutter,anchor.top-tooltipBounds.height-8);
    const maxTop=Math.max(gutter,window.innerHeight-tooltipBounds.height-gutter);
    setTooltipPosition({left,top:Math.min(top,maxTop)});
  },[]);
  const openTooltip=()=>{
    updateTooltipPosition();
    setOpen(true);
  };
  useEffect(()=>{
    if(!open)return;
    const reposition=()=>updateTooltipPosition();
    window.addEventListener("resize",reposition);
    window.addEventListener("scroll",reposition,true);
    return ()=>{
      window.removeEventListener("resize",reposition);
      window.removeEventListener("scroll",reposition,true);
    };
  },[open,updateTooltipPosition]);
  useEffect(()=>{
    if(!open)return;
    const dismissOutside=(event:PointerEvent)=>{
      if(!rootRef.current?.contains(event.target as Node))setOpen(false);
    };
    document.addEventListener("pointerdown",dismissOutside);
    return ()=>document.removeEventListener("pointerdown",dismissOutside);
  },[open]);
  return <span ref={rootRef}
    className={open?"context-info context-info-open":"context-info"}
    onMouseEnter={openTooltip}
    onMouseLeave={()=>setOpen(false)}
    onBlur={event=>{
      const nextTarget=event.relatedTarget;
      if(!(nextTarget instanceof Node)||!event.currentTarget.contains(nextTarget))setOpen(false);
    }}
  >
    <button type="button" className="context-info-trigger" aria-label={label}
      aria-expanded={open} aria-describedby={tooltipId}
      onFocus={openTooltip} onClick={openTooltip}
      onKeyDown={event=>{if(event.key==="Escape"){setOpen(false);event.currentTarget.blur();}}}
    ><Info size={14} strokeWidth={2}/></button>
    <span ref={tooltipRef} id={tooltipId} className="context-info-tooltip" role="tooltip"
      aria-hidden={!open} style={tooltipPosition}>{children}</span>
  </span>;
}

function CustomerEntry({publicCode,dark,onToggleTheme}:{publicCode:string;dark:boolean;onToggleTheme:()=>void}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const [result,setResult]=useState<Awaited<ReturnType<typeof createCustomerSession>>|null>(null);
  const [error,setError]=useState("");
  const [loading,setLoading]=useState(true);
  const [localReady,setLocalReady]=useState(false);
  const [localFallbackSuggested,setLocalFallbackSuggested]=useState(false);
  const handleLocalReady=useCallback((ready:boolean)=>{
    setLocalReady(ready);
    if(ready)setLocalFallbackSuggested(false);
  },[]);
  const handleLocalUnavailable=useCallback(()=>setLocalFallbackSuggested(true),[]);
  useEffect(()=>{
    setLocalReady(false);
    setLocalFallbackSuggested(false);
  },[result?.localAgent?.agentDeviceId]);
  useEffect(()=>{
    let mounted=true;
    const stationKey=`qrtoprint.customer-session:${publicCode}`;
    const restore=async()=>{
      const storedToken=sessionStorage.getItem(stationKey) ||
        await getNativeCustomerPrintSession(publicCode);
      let existing:Awaited<ReturnType<typeof createCustomerSession>>|null=null;
      if(storedToken){
        try {
          existing=await resumeCustomerSession(storedToken,publicCode);
        } catch(exception) {
          if(exception instanceof ApiError&&exception.status===401){
            // Retain the expired capability only in this tab, for read-only
            // restoration of its own order. It is never saved to localStorage.
            sessionStorage.setItem("qrtoprint.customer-previous-session:"+publicCode,storedToken);
            sessionStorage.removeItem(stationKey);
            sessionStorage.removeItem("qrtoprint.customer-session");
            await clearNativeCustomerPrintSession(publicCode);
          } else throw exception;
        }
      }
      const value=existing??await createCustomerSession(publicCode);
      // Wait for native encrypted persistence before presenting the connected session.
      // In a regular browser the bridge is absent and this safely becomes a no-op.
      const storedNatively=await saveNativeCustomerPrintSession(publicCode,value.sessionToken);
      await saveNativeCustomerShopProfile(publicCode,value.shop);
      if(!mounted)return;
      setResult(value);
      if(storedNatively)sessionStorage.removeItem(stationKey);
      else sessionStorage.setItem(stationKey,value.sessionToken);
      sessionStorage.removeItem("qrtoprint.customer-session");
    };
    void restore().catch(exception=>{
      if(mounted)setError(exception instanceof Error?t(exception.message):t("This QR station is unavailable."));
    }).finally(()=>{if(mounted)setLoading(false);});
    return ()=>{mounted=false;};
  },[publicCode]);

  const startNewSession=useCallback(async()=>{
    setLoading(true);
    setError("");
    setLocalReady(false);
    setLocalFallbackSuggested(false);
    try{
      sessionStorage.removeItem("qrtoprint.customer-previous-session:"+publicCode);
      const value=await createCustomerSession(publicCode);
      const storedNatively=await saveNativeCustomerPrintSession(publicCode,value.sessionToken);
      await saveNativeCustomerShopProfile(publicCode,value.shop);
      const stationKey=`qrtoprint.customer-session:${publicCode}`;
      if(storedNatively)sessionStorage.removeItem(stationKey);
      else sessionStorage.setItem(stationKey,value.sessionToken);
      sessionStorage.removeItem("qrtoprint.customer-session");
      setResult(value);
    }catch(exception){
      setError(exception instanceof Error?t(exception.message):t("Could not start a new print session."));
    }finally{
      setLoading(false);
    }
  },[publicCode]);

  useEffect(()=>{
    if(!result)return;
    let active=true;
    let renewing=false;
    const renew=()=>{
      if(renewing)return;
      renewing=true;
      void renewCustomerPrintSession(result.sessionToken)
        .then(session=>{
          if(active){
            setResult(current=>current?.sessionToken===result.sessionToken
              ? {...current,expiresUtc:session.expiresUtc,localAgent:session.localAgent,paymentOptions:session.paymentOptions,
                  privatePrintingVirtualOutputEnabled:session.privatePrintingVirtualOutputEnabled}
              : current);
          }
        })
        .catch(()=>{/* A temporary offline period is retried on the next foreground check. */})
        .finally(()=>{renewing=false;});
    };
    const checkExpiry=()=>{
      const expiry=Date.parse(result.expiresUtc);
      if(!Number.isFinite(expiry)||expiry<=Date.now()+10*60*1000)renew();
    };
    const timer=window.setInterval(renew,5*60*1000);
    const onVisibility=()=>{if(document.visibilityState==="visible")checkExpiry();};
    window.addEventListener("focus",checkExpiry);
    document.addEventListener("visibilitychange",onVisibility);
    return ()=>{
      active=false;
      window.clearInterval(timer);
      window.removeEventListener("focus",checkExpiry);
      document.removeEventListener("visibilitychange",onVisibility);
    };
  },[result?.sessionToken,result?.expiresUtc]);

  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  return <div className={nativeAppMode ? "customer-entry smartprint-native" : "customer-entry"}><div className="customer-entry-glow"/>
    <header className="customer-entry-brand"><BrandLockup className="customer-entry-brand-lockup" /><div style={{display:"flex",alignItems:"center",gap:8}}><LanguageSwitcher compact/><ThemeToggleButton dark={dark} onToggle={onToggleTheme}/></div></header>
    <main className={result ? "customer-entry-card customer-entry-card--upload" : "customer-entry-card"}>
      {loading?<><div className="customer-entry-spinner"><span className="button-spinner"/></div><h1>{nativeAppMode?translateUi(locale,"common.loading"):t("Connecting to your print shop")}</h1>{!nativeAppMode&&<p>{t("Creating a secure, time-limited session for this QR station.")}</p>}</>:
      error?<><div className="entry-error-icon"><X size={23}/></div><div className="entry-eyebrow">{t("STATION UNAVAILABLE")}</div><h1>{nativeAppMode?t("Shop unavailable"):t("This QR station can't be reached")}</h1>{!nativeAppMode&&<p>{error}</p>}<button className="primary-button" onClick={()=>window.location.reload()}>{t("Try again")}</button></>:
      result?<><div className="entry-success-icon"><Check size={23}/></div><div className="entry-eyebrow">{t("SECURE SESSION CREATED")}</div><h1>{nativeAppMode?result.shop.displayName:translateUi(locale,"customer.welcome",{shopName:result.shop.displayName})}</h1><p>{nativeAppMode?t("Shop connected."):t("Your phone is connected to this shop's print station.")}</p>
        <div className="entry-printer-card"><div className="entry-printer-icon"><Printer size={20}/></div><div><strong>{result.printer?.displayName||t("Shop print counter")}</strong><span>{nativeAppMode?(result.printer?.status||t("Printer not selected")):result.printer?result.printer.manufacturer+" · "+result.printer.model : t("The shop will select a printer for your job.")}</span></div><Status label={result.printer?.status||t("Session ready")} tone="blue"/></div>
        <div className="entry-session-note"><ShieldCheck size={16}/><span>{nativeAppMode?t("Secure session · Expires {expires}",{expires:formatIstTime(result.expiresUtc)}):t("This session expires at {expires}. Your document access stays limited to this shop session.",{expires:formatIstTime(result.expiresUtc)})}</span></div>
        <CustomerLocalConnection localAgent={result.localAgent} onReady={handleLocalReady} onUnavailable={handleLocalUnavailable} cloudAssistedPrintingEnabled={result.cloudAssistedPrintingEnabled}/>
        <CustomerPrintFlow dark={dark} publicCode={publicCode} sessionToken={result.sessionToken} paymentOptions={result.paymentOptions} localAgent={result.localAgent} localReady={localReady} localFallbackSuggested={localFallbackSuggested} cloudAssistedPrintingEnabled={result.cloudAssistedPrintingEnabled} privatePrintingVirtualOutputEnabled={result.privatePrintingVirtualOutputEnabled} photoPricing={result.photoPricing} hasAssignedPrinter={Boolean(result.printer)} printerCapabilitiesJson={result.printer?.capabilitiesJson} key={result.sessionToken} onStartNewSession={()=>void startNewSession()}/>
      </>:null}
    </main>
    <footer className="customer-entry-footer">© 2026 SmartPrint by QuantaEdge · {t("Secure customer printing")}</footer>
  </div>;
}


type CustomerLocalAgent = NonNullable<Awaited<ReturnType<typeof createCustomerSession>>["localAgent"]>;
type LocalAgentCheckStatus = "idle" | "checking" | "connected" | "unverified" | "recovering" | "unavailable" | "denied" | "insecure";

function isPrivateIpv4Hostname(hostname:string):boolean {
  const octets=hostname.split(".").map(Number);
  return octets.length===4 &&
    octets.every(part=>Number.isInteger(part)&&part>=0&&part<=255) &&
    (octets[0]===10 ||
     (octets[0]===172&&octets[1]>=16&&octets[1]<=31) ||
     (octets[0]===192&&octets[1]===168));
}

function isLocalOrLoopbackPortalOrigin(hostname:string):boolean {
  const value=hostname.toLowerCase().replace(/^\[|\]$/g,"");
  return value==="localhost" ||
    value.endsWith(".localhost") ||
    value==="127.0.0.1" ||
    value==="::1" ||
    isPrivateIpv4Hostname(value) ||
    value.endsWith(".local");
}

function buildSecureLocalPortalUrl():string|null {
  try {
    const url=new URL(window.location.href);
    if(url.protocol!=="http:"||url.port!=="3000"||!isPrivateIpv4Hostname(url.hostname))return null;
    url.protocol="https:";
    url.port="3443";
    return url.toString();
  } catch {
    return null;
  }
}

async function requestSmartPrintNativeLocalNetworkAccess():Promise<"granted"|"denied"|"not_required"|"unavailable"> {
  const bridge=(window as Window & {
    SmartPrintNative?: {
      postMessage:(message:string)=>void;
      addEventListener:(type:string,listener:(event:MessageEvent)=>void)=>void;
      removeEventListener:(type:string,listener:(event:MessageEvent)=>void)=>void;
    };
  }).SmartPrintNative;
  if(!bridge?.postMessage||!bridge.addEventListener||!bridge.removeEventListener)return "unavailable";

  return new Promise(resolve=>{
    let settled=false;
    let timeout:number|undefined;
    const finish=(result:"granted"|"denied"|"not_required"|"unavailable")=>{
      if(settled)return;
      settled=true;
      if(timeout!==undefined)window.clearTimeout(timeout);
      bridge.removeEventListener("message",handleReply);
      resolve(result);
    };
    const handleReply=(event:MessageEvent)=>{
      const value=typeof event.data==="string"?event.data:"";
      if(value==="localNetworkAccess:granted")finish("granted");
      else if(value==="localNetworkAccess:denied")finish("denied");
      else if(value==="localNetworkAccess:not_required")finish("not_required");
    };
    timeout=window.setTimeout(()=>finish("unavailable"),30000);
    bridge.addEventListener("message",handleReply);
    try{bridge.postMessage("requestLocalNetworkAccess");}
    catch{finish("unavailable");}
  });
}

function CustomerLocalConnection({localAgent,onReady,onUnavailable,cloudAssistedPrintingEnabled}:{localAgent:CustomerLocalAgent|null;onReady:(ready:boolean)=>void;onUnavailable:()=>void;cloudAssistedPrintingEnabled:boolean}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  const [permissionState,setPermissionState]=useState<PermissionState|"unknown">("unknown");
  const [checkStatus,setCheckStatus]=useState<LocalAgentCheckStatus>("idle");
  const [message,setMessage]=useState("");
  const checkGeneration=useRef(0);
  const securePortalUrl=buildSecureLocalPortalUrl();
  const localOriginDoesNotNeedLnaPermission=isLocalOrLoopbackPortalOrigin(window.location.hostname);

  function requireSecureContext() {
    setCheckStatus("insecure");
    onReady(false);
    if(cloudAssistedPrintingEnabled)onUnavailable();
    setMessage(nativeAppMode ? "Secure HTTPS required." : securePortalUrl
      ? "This shop page is open over HTTP. Chrome blocks local-network requests from insecure pages without showing a permission prompt. Open the secure shop link below; the certificate must be trusted on this phone."
      : "This shop page is not a secure HTTPS context. Open the shop's HTTPS customer portal before checking local printing.");
  }

  async function checkConnection() {
    const generation=++checkGeneration.current;
    if(!localAgent)return;
    if(!window.isSecureContext) {
      requireSecureContext();
      return;
    }
    setCheckStatus("checking");
    onReady(false);
    setMessage(nativeAppMode ? "Checking shop connection." : "Checking the shop computer. No document is sent by this check.");
    let nativeLocalNetworkPermissionDenied=false;
    if(nativeAppMode) {
      const access=await requestSmartPrintNativeLocalNetworkAccess();
      if(generation!==checkGeneration.current)return;
      // Android 16 only enforces local-network permission when its protection is
      // active. A denied Nearby devices prompt must not block a connection on
      // devices where the OS still grants local access implicitly.
      nativeLocalNetworkPermissionDenied=access==="denied";
    }

    let validEndpointCount=0;
    for(const value of localAgent.endpoints.slice(0,8)) {
      let endpoint:URL;
      try {
        endpoint=new URL(value);
        if(endpoint.protocol!=="http:" ||
           endpoint.port!=="17842" ||
           endpoint.username!=="" ||
           endpoint.password!=="" ||
           endpoint.pathname!=="/" ||
           endpoint.search!=="" ||
           endpoint.hash!=="" ||
           !isPrivateIpv4Hostname(endpoint.hostname))continue;
      } catch {
        continue;
      }

      validEndpointCount++;
      const controller=new AbortController();
      const timeout=window.setTimeout(()=>controller.abort(),3500);
      try {
        // Start from the user's click when a browser permission prompt is required.
        const response=await fetch(`${endpoint.origin}/api/v1/local/status`,{
          method:"GET",
          mode:"cors",
          cache:"no-store",
          credentials:"omit",
          signal:controller.signal,
          targetAddressSpace:"local"
        } as RequestInit & {targetAddressSpace:"local"});
        if(generation!==checkGeneration.current)return;
        if(!response.ok)continue;

        const status=await response.json() as {
          agentDeviceId?:string;
          protocolVersion?:number;
          status?:string;
          localUploadReady?:boolean;
          readiness?: {
            queueInitialized?:boolean;
            printWorkerRunning?:boolean;
            heartbeatFresh?:boolean;
            printerInventoryFresh?:boolean;
            discoveredPrinterCount?:number;
            localUploadReady?:boolean;
          };
        };
        if(generation!==checkGeneration.current)return;
        if(status.agentDeviceId===localAgent.agentDeviceId &&
           status.protocolVersion===1 &&
           status.status==="LOCAL_ENDPOINT_READY") {
          if(!status.readiness) {
            // Existing installed Agents do not yet publish the detailed readiness
            // payload. Keep their working local route available; the ticket API
            // and Agent must still validate the assigned printer and queue.
            setCheckStatus("unverified");
            onReady(true);
            setMessage("This shop computer is reachable, but its Agent has not reported detailed queue health yet. SmartPrint will verify the assigned printer and upload with the shop before confirming the file.");
            return;
          }
          if(status.localUploadReady!==true || status.readiness?.localUploadReady!==true) {
            setCheckStatus("recovering");
            onReady(false);
            const readiness=status.readiness;
            const agentHeartbeatOffline=readiness?.heartbeatFresh!==true;
            if(cloudAssistedPrintingEnabled||agentHeartbeatOffline)onUnavailable();
            const blockers=[
              readiness?.queueInitialized!==true?"protected document queue is not ready":null,
              readiness?.printWorkerRunning!==true?"print worker is not running":null,
              readiness?.heartbeatFresh!==true?"cloud heartbeat is not fresh":null,
              readiness?.printerInventoryFresh!==true?"printer inventory is not fresh":null
            ].filter((value):value is string=>value!==null);
            const detail=blockers.length?blockers.join(", "):"the Agent has not reported full pipeline readiness";
            setMessage("The shop computer is reachable, but local printing is recovering ("+detail+"). "+
              (cloudAssistedPrintingEnabled
                ? "Choose Cloud-assisted upload while the shop operator restores the local print pipeline."
                : agentHeartbeatOffline
                  ? "The Agent cloud heartbeat is offline. Choose Secure queue · HTTPS to save this request while the shop reconnects."
                  : "Ask the shop operator to restore the protected local queue, then check again."));
            return;
          }
          setCheckStatus("connected");
          onReady(true);
          setMessage(nativeAppMode ? "Shop connection ready. Standard local uploads use HTTP; Private Printing encrypts document contents end-to-end." : "This phone can reach the shop computer. Standard local uploads use HTTP and are not encrypted in transit; Private Printing encrypts document contents end-to-end.");
          return;
        }
      } catch {
        // Continue across the Agent's advertised private addresses.
      } finally {
        window.clearTimeout(timeout);
      }
    }

    if(generation!==checkGeneration.current)return;
    setCheckStatus("unavailable");
    onReady(false);
    // A customer session can outlive the Agent heartbeat. Surface the existing
    // HTTPS queue path after the local Agent endpoint is confirmed unreachable;
    // the API still verifies the live Agent/plan boundary before accepting bytes.
    onUnavailable();
    setMessage(nativeAppMode
      ? nativeLocalNetworkPermissionDenied
        ? "Could not reach the shop. Make sure this phone is on the same Wi-Fi or hotspot as the shop computer. If Android 16 local-network access is restricted, allow SmartPrint under Android Settings → Permissions → Nearby devices. Cloud-assisted upload remains available."
        : "Shop offline · Secure HTTPS queue available while the shop Agent is offline."
      : validEndpointCount===0
        ? "The shop Agent has not announced a usable local address. Secure HTTPS queue fallback is available only while the shop Agent is offline."
        : "Could not reach the shop computer. Connect this phone to the same shop Wi-Fi or hotspot, then ask the shop owner to check the connection. Secure HTTPS queue fallback is available only while the shop Agent is offline.");
  }

  useEffect(()=>{
    // Invalidate an older request immediately when the shop endpoint list changes.
    checkGeneration.current++;
    if(!localAgent?.endpoints.length) {
      onReady(false);
      onUnavailable();
      setPermissionState("unknown");
      setCheckStatus("unavailable");
      setMessage(cloudAssistedPrintingEnabled
        ? "The shop computer isn't reachable on this Wi-Fi. Choose Cloud-assisted · HTTPS to continue."
        : "The shop computer isn't reachable on this Wi-Fi. Choose Secure queue · HTTPS to continue.");
      return ()=>{checkGeneration.current++;};
    }
    onReady(false);
    setCheckStatus("idle");
    setMessage("");
    if(!window.isSecureContext) {
      requireSecureContext();
      return ()=>{checkGeneration.current++;};
    }

    // Android WebView does not expose Chrome's site-level Local Network Access
    // prompt. Keep this check user-initiated and let the native app request the
    // Android 16 transitional permission only when the customer chooses Shop Wi-Fi.
    if(nativeAppMode) {
      setPermissionState("unknown");
      setCheckStatus("idle");
      setMessage("Tap Check Wi-Fi to connect to this shop.");
      return ()=>{checkGeneration.current++;};
    }

    if(localOriginDoesNotNeedLnaPermission) {
      setPermissionState("unknown");
      return ()=>{checkGeneration.current++;};
    }
    if(!navigator.permissions?.query)return ()=>{checkGeneration.current++;};

    let active=true;
    let permissionStatus:PermissionStatus|null=null;
    const updatePermission=()=>{
      if(!active||!permissionStatus)return;
      setPermissionState(permissionStatus.state);
      if(permissionStatus.state==="denied") {
        setCheckStatus("denied");
        onReady(false);
        if(cloudAssistedPrintingEnabled)onUnavailable();
        setMessage(nativeAppMode ? "Allow local network access." : "Chrome has blocked Local network access for this public HTTPS site. On Android, open the site information icon beside the address bar → Permissions → Local network access → Allow, then reload this QR page.");
      } else if(permissionStatus.state==="prompt") {
        setCheckStatus("idle");
        setMessage(nativeAppMode ? "Tap Check Wi-Fi." : "Tap Check shop Wi-Fi to request local-network access from Chrome.");
      }
    };

    void navigator.permissions.query({name:"local-network" as PermissionName})
      .then(status=>{
        if(!active)return;
        permissionStatus=status;
        status.addEventListener("change",updatePermission);
        updatePermission();
        if(status.state==="granted")void checkConnection();
      })
      .catch(()=>{if(active)setPermissionState("unknown");});

    return ()=>{
      active=false;
      checkGeneration.current++;
      permissionStatus?.removeEventListener("change",updatePermission);
    };
  },[localAgent?.agentDeviceId,localAgent?.endpoints.join("|"),onReady,onUnavailable]);

  const label=!localAgent?.endpoints.length?"Temporarily unavailable":
    checkStatus==="checking"?"Checking…":
    checkStatus==="connected"?(nativeAppMode?"Connected":"Shop computer reachable"):
    checkStatus==="unverified"?"Connected · health details unavailable":
    checkStatus==="recovering"?"Print pipeline recovering":
    checkStatus==="denied"?(nativeAppMode?"Access blocked":"Browser permission blocked"):
    checkStatus==="insecure"?"HTTPS required":
    checkStatus==="unavailable"?"Not reachable":"Not checked";
  const tone=checkStatus==="connected"?"green":
    checkStatus==="unverified"?"amber":
    checkStatus==="recovering"?"amber":
    checkStatus==="unavailable"||checkStatus==="denied"?"red":"blue";

  return <section className="entry-local-agent">
    <div className="entry-local-agent-head">
      <div><strong>{t(nativeAppMode?"Shop Wi-Fi":"Shop Wi-Fi connection")}</strong><span>{t("Optional local connection check")}</span></div>
      <Status label={label} tone={tone}/>
    </div>
    {!nativeAppMode&&<p>{t("Check whether this phone can reach the shop's local print computer. This check does not upload your document.")}</p>}
    {!localAgent?.endpoints.length
      ? <div className="entry-local-agent-message" role="status"><strong>{t("Local transfer unavailable.")}</strong> {message}</div>
      : checkStatus==="insecure"
      ? <div className="entry-local-agent-message">
          <small>{message}</small>
          {securePortalUrl&&<a className="customer-flow-button" href={securePortalUrl}>{nativeAppMode?"Open HTTPS":"Open secure HTTPS shop page"}</a>}
        </div>
      : permissionState==="denied"&&!localOriginDoesNotNeedLnaPermission
        ? <small className="entry-local-agent-message">{message}</small>
        : <button type="button" className="customer-flow-button" disabled={checkStatus==="checking"} onClick={()=>void checkConnection()}>
            {checkStatus==="checking"?"Checking…":checkStatus==="connected"?"Check again":nativeAppMode?"Check Wi-Fi":"Check shop Wi-Fi"}
          </button>}
    {Boolean(localAgent?.endpoints.length)&&message&&checkStatus!=="insecure"&&!(permissionState==="denied"&&!localOriginDoesNotNeedLnaPermission)&&<small className="entry-local-agent-message">{message}</small>}
  </section>;
}


type CustomerGroupSettings = {
  copies: string;
  colourMode: "Mono" | "Colour";
  isDuplex: boolean;
  advancedEnabled: boolean;
  paperSize: CustomerPrintRequestSettings["paperSize"];
  orientation: CustomerPrintRequestSettings["orientation"];
  pagesPerSheet: number;
};
type CustomerFileSettings = CustomerGroupSettings & { custom: boolean };

function consumeSmartPrintNativePickerGroups():string[]|undefined {
  const target=window as Window & {__smartPrintNativePickerGroups?:string[]};
  const groups=target.__smartPrintNativePickerGroups;
  delete target.__smartPrintNativePickerGroups;
  return Array.isArray(groups)?groups:undefined;
}
type CustomerSavedPrintSettings = CustomerPrintRequestSettings & { advancedEnabled?: boolean };
function customerPhotoSizeLabel(sizeCode?:string|null):string {
  switch(sizeCode){
    case "35x45": return "35 × 45 mm";
    case "25x35": return "25 × 35 mm";
    case "2x2": return "2 × 2 in";
    case "4x6": return "4 × 6 in";
    default: return "photo";
  }
}

function printJobOutputSides(pageCount:number,pagesPerSheet=1,copies=1):number {
  const density=Number.isInteger(pagesPerSheet)&&pagesPerSheet>1?pagesPerSheet:1;
  return Math.ceil(pageCount/density)*copies;
}

function customerDocumentOutputSummary(
  pageCount:number,
  copies:number,
  pagesPerSheet=1,
  isDuplex=false
):string {
  const density=Number.isInteger(pagesPerSheet)&&pagesPerSheet>1?pagesPerSheet:1;
  const outputSidesPerCopy=Math.ceil(pageCount/density);
  if(density===1)return `${pageCount} pages × ${copies} copies = ${pageCount*copies} pages requested`;
  const outputSheetsPerCopy=isDuplex?Math.ceil(outputSidesPerCopy/2):outputSidesPerCopy;
  return `${pageCount} PDF pages · ${density} per sheet = ${outputSidesPerCopy} output sides per copy · ${outputSidesPerCopy*copies} output sides total${isDuplex?` · ${outputSheetsPerCopy} duplex sheets per copy`:""}`;
}

type StoredCustomerDraft = {
  sessionMarker: string;
  printKindsByJobId?: Record<string,"DOCUMENT"|"PHOTO"|"ID_DOCUMENT">;
  privateFileNamesByJobId?: Record<string,string>;
  uploaded?: UploadedPrintJob;
  batchName?: string;
  uploadedBatch?: UploadedPrintJob[];
  uploadedFileFingerprints?: string[];
  batchId?: string;
  quote: PrintQuoteResult | null;
  fileGroupNames?: Record<string,string>;
  groupSettings?: Record<string,CustomerGroupSettings>;
  fileSettings?: Record<string,CustomerFileSettings>;
  printSettingsByJobId?: Record<string,CustomerSavedPrintSettings>;
  quotesByJobId?: Record<string,PrintQuoteResult>;
  photoSettingsByJobId?: Record<string,CustomerPhotoSettings>;
};

function isPrivateFilenamePlaceholder(value:string|undefined|null):boolean {
  const name=value?.trim()??"";
  return !name||/^private document$/i.test(name)||/^private file$/i.test(name)||/^private-document(?:\.[a-z0-9]+)?$/i.test(name);
}

function storedCustomerPrivateFileNames(draft:StoredCustomerDraft|null):Record<string,string> {
  const names={...(draft?.privateFileNamesByJobId??{})};
  const items=[draft?.uploaded,...(draft?.uploadedBatch??[])];
  for(const item of items) {
    if(item?.job.privatePrinting&&!isPrivateFilenamePlaceholder(item.document.originalFileName)&&isPrivateFilenamePlaceholder(names[item.job.id])) {
      names[item.job.id]=item.document.originalFileName;
    }
  }
  return names;
}

function resolveCustomerDocumentName(item:UploadedPrintJob,privateFileNamesByJobId:Record<string,string>):string {
  if(!item.job.privatePrinting)return item.document.originalFileName?.trim()||"Document";
  const savedName=privateFileNamesByJobId[item.job.id]?.trim();
  if(savedName&&!isPrivateFilenamePlaceholder(savedName))return savedName;
  const draftName=item.document.originalFileName?.trim();
  return draftName&&!isPrivateFilenamePlaceholder(draftName)?draftName:"Private file";
}

function isCustomerImage(file:File):boolean {
  const identified=identifiedCustomerDocumentTypes.get(file);
  if(identified)return identified.mediaType==="image/jpeg"||identified.mediaType==="image/png";
  return isSupportedPhotoSource(file);
}

function customerFileFingerprint(file:File):string {
  const relativePath=(file as File & {webkitRelativePath?:string}).webkitRelativePath??"";
  const nativeGroup=(file as File & {__smartPrintBatchGroup?:string}).__smartPrintBatchGroup??"";
  return `${relativePath||nativeGroup}\u0000${file.name}\u0000${file.size}\u0000${file.lastModified}\u0000${file.type}`;
}

function isSmartPrintIdDocument(file:File):boolean {
  return (file as File & {__smartPrintPrintKind?:string}).__smartPrintPrintKind==="ID_DOCUMENT";
}

function isSmartPrintPhotoDocument(file:File):boolean {
  return (file as File & {__smartPrintPrintKind?:string}).__smartPrintPrintKind==="PHOTO";
}
type CustomerNupDirection =
  | "RightBottom" | "BottomRight" | "LeftBottom" | "BottomLeft"
  | "RightTop" | "TopRight" | "LeftTop" | "TopLeft";
type NativeCustomerPdfPreview = {
  token:string; requestedPageIndex:number; pageIndex:number; pageCount:number;
  pagesPerSheet:number; pagesPerSheetDirection:CustomerNupDirection; paperSize:string; orientation:string;
  firstSourcePage:number; lastSourcePage:number;
  image:string; thumbnails:Array<{index:number;image:string}>; loading:boolean;
};
type BrowserCustomerNupPreview = {
  url:string;
  sheetIndex:number;
  sheetCount:number;
  pagesPerSheet:number;
  pagesPerSheetDirection:CustomerNupDirection;
  firstSourcePage:number;
  lastSourcePage:number;
  loading:boolean;
  failed:boolean;
};
function customerStatusTone(status:string):"green"|"red"|"amber"|"blue"|"slate" {
  if(["COMPLETED","PDF_EXPORTED","PAID_AUTHORISED","REFUNDED"].includes(status))return "green";
  if(["REJECTED","PAYMENT_REJECTED","FAILED","CANCELLED"].includes(status))return "red";
  if(["AWAITING_PAYMENT","AWAITING_CUSTOMER_PAYMENT","AWAITING_COUNTER_PAYMENT","PAYMENT_REPORTED","QUOTE_EXPIRED"].includes(status))return "amber";
  if(["AWAITING_SHOP_APPROVAL","LOCAL_QUEUE_CLAIMED","LOCAL_QUEUE_READY","PRINT_AUTHORISED","PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING","OUTPUT_UNCONFIRMED"].includes(status))return "blue";
  return "slate";
}
function customerStatusLabel(status:string):string {
  return status==="AWAITING_SHOP_APPROVAL"?"Waiting for shop review":prettyStatus(status);
}
const isValidNativePhotoSettings = isValidCustomerPhotoSettings;

function parseCustomerPagesPerSheetDirection(
  capabilitiesJson?:string|null
):CustomerNupDirection|null {
  if(!capabilitiesJson)return null;
  try {
    const capabilities=JSON.parse(capabilitiesJson) as {pagesPerSheetDirection?:unknown};
    const direction=capabilities.pagesPerSheetDirection;
    return direction==="RightBottom"||direction==="BottomRight"||direction==="LeftBottom"||
      direction==="BottomLeft"||direction==="RightTop"||direction==="TopRight"||
      direction==="LeftTop"||direction==="TopLeft"
      ?direction:null;
  } catch {
    return null;
  }
}

function parseCustomerPagesPerSheetStatus(capabilitiesJson?:string|null):"supported"|"unsupported"|"unknown" {
  if(!capabilitiesJson?.trim()||capabilitiesJson.trim()==="{}")return "unknown";
  try {
    const capabilities=JSON.parse(capabilitiesJson) as {pagesPerSheetStatus?:unknown;pagesPerSheet?:unknown};
    if(capabilities.pagesPerSheetStatus==="supported"||capabilities.pagesPerSheetStatus==="unsupported"||capabilities.pagesPerSheetStatus==="unknown")
      return capabilities.pagesPerSheetStatus;
    return Array.isArray(capabilities.pagesPerSheet)&&capabilities.pagesPerSheet.some(value=>
      typeof value==="number"&&Number.isInteger(value)&&value>1&&value<=500)?"supported":"unknown";
  } catch {
    return "unknown";
  }
}

function parseCustomerPagesPerSheetCapabilities(capabilitiesJson?:string|null):number[] {
  if(!capabilitiesJson)return [1];
  try {
    const capabilities=JSON.parse(capabilitiesJson) as {pagesPerSheet?:unknown};
    const values=Array.isArray(capabilities.pagesPerSheet)?capabilities.pagesPerSheet:[];
    const supported=values.filter((value):value is number=>
      typeof value==="number"&&Number.isInteger(value)&&value>1&&value<=500);
    return [1,...new Set(supported)].sort((left,right)=>left-right);
  } catch {
    return [1];
  }
}

const identifiedCustomerDocumentTypes=new WeakMap<File,SupportedDocumentType>();
function isCustomerPdf(file:File):boolean {
  const identified=identifiedCustomerDocumentTypes.get(file);
  if(identified)return identified.mediaType==="application/pdf";
  return file.type.toLowerCase()==="application/pdf"||/\.pdf$/i.test(file.name);
}

const smartPrintNativePdfPreviewTokens=new WeakMap<File,string>();

function postSmartPrintNativeMessage(message:string):boolean {
  const bridge=(window as Window & {SmartPrintNative?:{postMessage:(value:string)=>void}}).SmartPrintNative;
  if(!bridge?.postMessage)return false;
  try {bridge.postMessage(message);return true;}
  catch {return false;}
}

function CustomerNativePdfPreview({token,preview,onPage}:{token:string;preview:NativeCustomerPdfPreview|null;onPage:(sheet:number)=>void}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const current=preview?.token===token?preview:null;
  return <div className="customer-native-pdf-preview">
    {current?.image
      ? <img className="customer-native-pdf-page" src={"data:image/jpeg;base64,"+current.image} alt={t("Print sheet {page} of {pages}, containing PDF pages {first} to {last}",{page:current.pageIndex+1,pages:current.pageCount,first:current.firstSourcePage,last:current.lastSourcePage})}/>
      : <div className="customer-native-pdf-empty">
          {current===null||current.loading
            ? <><span className="button-spinner"/><span>{t("Preparing print layout preview…")}</span></>
            : <><FileText size={22}/><strong>{t("Preview unavailable")}</strong><span>{t("The print layout could not be previewed on this device. You can still upload the original PDF.")}</span></>}
        </div>}
    {current?.image&&<div className="customer-native-pdf-layout-note">
      <strong>{current.pagesPerSheet}-up print layout · {current.pagesPerSheetDirection}</strong>
      <span>PDF pages {current.firstSourcePage}–{current.lastSourcePage} appear on this output sheet. Page order follows the selected driver direction; final margins and page fitting are controlled by the printer driver. The original PDF is sent to the printer driver unchanged.</span>
    </div>}
    {current&&current.pageCount>1&&<div className="customer-native-pdf-controls">
      <button type="button" disabled={current.loading||current.pageIndex<=0} onClick={()=>onPage(current.pageIndex-1)}>{t("Previous sheet")}</button>
      <span>Sheet {current.pageIndex+1} of {current.pageCount}</span>
      <button type="button" disabled={current.loading||current.pageIndex>=current.pageCount-1} onClick={()=>onPage(current.pageIndex+1)}>{t("Next sheet")}</button>
    </div>}
  </div>;
}

function consumeSmartPrintNativePrintKinds():Array<string|null>|undefined {
  const target=window as Window & {__smartPrintNativePrintKinds?:Array<string|null>};
  const kinds=target.__smartPrintNativePrintKinds;
  delete target.__smartPrintNativePrintKinds;
  return Array.isArray(kinds)?kinds:undefined;
}

function consumeSmartPrintNativePhotoSettings():Array<CustomerPhotoSettings|null>|undefined {
  const target=window as Window & {__smartPrintNativePhotoSettings?:Array<CustomerPhotoSettings|null>};
  const settings=target.__smartPrintNativePhotoSettings;
  delete target.__smartPrintNativePhotoSettings;
  return Array.isArray(settings)?settings:undefined;
}

function consumeSmartPrintNativePdfPreviews():Array<string|null>|undefined {
  const target=window as Window & {__smartPrintNativePdfPreviews?:Array<string|null>};
  const previews=target.__smartPrintNativePdfPreviews;
  delete target.__smartPrintNativePdfPreviews;
  return Array.isArray(previews)?previews:undefined;
}

function customerBatchGroupName(file:File):string {
  const relativePath=(file as File & {webkitRelativePath?:string}).webkitRelativePath??"";
  const pathParts=relativePath.split("/").filter(Boolean);
  return pathParts.length>1?pathParts[0]:"Loose files";
}

function normalizeCustomerCopies(value:string):string {
  const trimmed=value.trim();
  if(!/^\d+$/.test(trimmed))return "1";
  const copies=Number(trimmed);
  if(!Number.isFinite(copies))return "100";
  return String(Math.min(100,Math.max(1,Math.trunc(copies))));
}

function readStoredCustomerDraft(publicCode:string,sessionToken:string):StoredCustomerDraft|null {
  const marker=sessionToken.slice(-16);
  const keys=[
    `qrtoprint.customer-active-job:${publicCode}`,
    `qrtoprint.customer-previous-job:${publicCode}`
  ];
  for(const key of keys){
    try {
      const raw=localStorage.getItem(key);
      if(!raw)continue;
      const draft=JSON.parse(raw) as StoredCustomerDraft;
      if(draft.sessionMarker===marker&&
        (draft.uploaded?.job?.id||draft.uploadedBatch?.length||draft.batchName?.trim()))return draft;
    } catch {
      // A corrupt draft must not stop the customer from starting a new upload.
    }
  }
  return null;
}

function smartPrintNativeHeadline(status:string):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Shop reviewing your files";
    case "AWAITING_PAYMENT": return "Ready for payment";
    case "AWAITING_CUSTOMER_PAYMENT": return "Complete payment";
    case "AWAITING_COUNTER_PAYMENT": return "Pay at the shop";
    case "PAYMENT_REPORTED": return "Shop checking payment";
    case "PAYMENT_REJECTED": return "Payment not confirmed";
    case "PAID_AUTHORISED": return "Payment received";
    case "LOCAL_QUEUE_CLAIMED":
    case "LOCAL_QUEUE_READY": return "Preparing your print";
    case "PRINT_AUTHORISED": return "Waiting for shop computer";
    case "PRINT_SUBMITTING": return "Starting print";
    case "SPOOLER_ACCEPTED":
    case "PRINTING": return "Printing in progress";
    case "OUTPUT_UNCONFIRMED": return "Shop checking your print";
    case "TEST_OUTPUT_READY": return "Test complete";
    case "COMPLETED": return "Ready for collection";
    case "PDF_EXPORTED": return "PDF ready";
    case "REFUNDED": return "Refunded";
    case "CANCELLED": return "Cancelled";
    case "FAILED": return "Could not print";
    case "REJECTED": return "Not accepted";
    case "QUOTE_EXPIRED": return "Price refresh needed";
    default: return "Request in progress";
  }
}

function CustomerPrintFlow({dark,publicCode,sessionToken,paymentOptions,localAgent,localReady,localFallbackSuggested,cloudAssistedPrintingEnabled,privatePrintingVirtualOutputEnabled,photoPricing,hasAssignedPrinter,printerCapabilitiesJson,onStartNewSession}:{dark:boolean;publicCode:string;sessionToken:string;paymentOptions:{upiEnabled:boolean;upiId?:string|null;payeeName?:string|null;cashEnabled:boolean;instantPaymentEnabled:boolean};localAgent:CustomerLocalAgent|null;localReady:boolean;localFallbackSuggested:boolean;cloudAssistedPrintingEnabled:boolean;privatePrintingVirtualOutputEnabled:boolean;photoPricing:CustomerPhotoPricing|null;hasAssignedPrinter:boolean;printerCapabilitiesJson?:string|null;onStartNewSession:()=>void}) {
  const { locale } = useLocale();
  const t = (value: string, values: MessageValues = {}) => translateUiText(locale, value, values);
  const money = (minor: number, currency: string) => formatUiCurrency(minor / 100, locale, currency);
  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  // Private Printing is available only with a live paired Agent/key and a ready
  // physical queue, or an explicitly approved non-production virtual test queue.
  const supportedPagesPerSheet=hasAssignedPrinter
    ?parseCustomerPagesPerSheetCapabilities(printerCapabilitiesJson)
    :[1];
  const pagesPerSheetDirection=hasAssignedPrinter
    ?parseCustomerPagesPerSheetDirection(printerCapabilitiesJson)
    :null;
  // A direction must be reported too, otherwise a customer preview could
  // show a different page reading order from the eventual physical output.
  const nupCapabilityStatus=parseCustomerPagesPerSheetStatus(printerCapabilitiesJson);
  const nupOptions=pagesPerSheetDirection
    ?supportedPagesPerSheet.filter(value=>value>1)
    :[];
  const nupUnavailableReason=!hasAssignedPrinter
    ?"No printer is assigned to this QR station. Ask the shop owner to assign an active printer."
    :nupCapabilityStatus==="unsupported"
      ?"The installed driver explicitly reports that multiple pages per sheet is not supported."
      :nupCapabilityStatus==="supported"
        ?"The driver reports N-up counts but not a reliable page-reading direction. SmartPrint keeps this option hidden to prevent the preview and printed page order from disagreeing."
        :"The printer has not reported its N-up capabilities yet. Ask the shop owner to reconnect the printer or refresh the Print Agent.";
  const photoRatesAvailable=Boolean(photoPricing&&[photoPricing.photo35x45Minor,photoPricing.photo25x35Minor,photoPricing.photo2x2Minor,photoPricing.photo4x6Minor].some(rate=>Number.isFinite(rate)&&rate>0));
  const initialDraft=readStoredCustomerDraft(publicCode,sessionToken);
  const previousSessionToken=sessionStorage.getItem("qrtoprint.customer-previous-session:"+publicCode);
  const previousDraft=useState(()=>previousSessionToken&&previousSessionToken!==sessionToken
    ?readStoredCustomerDraft(publicCode,previousSessionToken):null)[0];
  const previousItems=previousDraft?.uploadedBatch?.length
    ?previousDraft.uploadedBatch
    :previousDraft?.uploaded?[previousDraft.uploaded]:[];
  const previousHistoryKey=previousItems.map(item=>item.job.id).join("|");
  const [previousHistoryStatuses,setPreviousHistoryStatuses]=useState<Record<string,CustomerJobStatus>>({});
  const [previousHistoryLoading,setPreviousHistoryLoading]=useState(previousItems.length>0);
  const [previousHistoryUnavailable,setPreviousHistoryUnavailable]=useState(false);
  useEffect(()=>{
    if(!previousSessionToken||!previousHistoryKey){setPreviousHistoryLoading(false);return;}
    let active=true;
    let running=false;
    const refreshPreviousHistory=async()=>{
      if(running)return;
      running=true;
      try{
        const results=await Promise.all(previousItems.map(async item=>{
          try{return [item.job.id,await getCustomerJobStatus(previousSessionToken,item.job.id)] as const;}
          catch{return [item.job.id,null] as const;}
        }));
        if(!active)return;
        const recovered=results.reduce<Record<string,CustomerJobStatus>>((map,[id,status])=>{
          if(status)map[id]=status;
          return map;
        },{});
        setPreviousHistoryStatuses(recovered);
        setPreviousHistoryUnavailable(results.some(([,status])=>status===null));
        setPreviousHistoryLoading(false);
      }finally{running=false;}
    };
    void refreshPreviousHistory();
    const timer=window.setInterval(()=>void refreshPreviousHistory(),60000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[previousSessionToken,previousHistoryKey]);
  const [files,setFiles]=useState<File[]>([]);
  const selectedFilesRef=useRef<File[]>([]);
  useEffect(()=>{selectedFilesRef.current=files;},[files]);
  const [selectedFileIndex,setSelectedFileIndex]=useState(0);
  const file=files[selectedFileIndex]??null;
  const [previewUrl,setPreviewUrl]=useState<string|null>(null);
  const [nativePdfPreview,setNativePdfPreview]=useState<NativeCustomerPdfPreview|null>(null);
  const [browserNupPreview,setBrowserNupPreview]=useState<BrowserCustomerNupPreview|null>(null);
  const [browserNupPreviewSheetIndex,setBrowserNupPreviewSheetIndex]=useState(0);
  // Retain only the currently selected PDF's parsed source and serialize sheet renders.
  // This avoids rereading and reparsing a large PDF on every N-up sheet navigation.
  const browserNupSourceCacheRef=useRef<{
    file:File;
    document:Promise<import("./customer-nup-preview").CustomerNupPreviewSource>|null;
  }|null>(null);
  // One active render plus one replaceable pending task: rapid page/settings
  // changes never accumulate an unbounded queue of obsolete PDF jobs.
  const browserNupRenderQueueRef=useRef<{
    running:boolean;
    pending:(()=>Promise<void>)|null;
  }>({running:false,pending:null});
  const enqueueBrowserNupRender=(task:()=>Promise<void>)=>{
    const queue=browserNupRenderQueueRef.current;
    queue.pending=task;
    if(queue.running)return;
    queue.running=true;
    void (async()=>{
      while(queue.pending){
        const next=queue.pending;
        queue.pending=null;
        try{await next();}catch{/* Each task owns its visible error state. */}
      }
      queue.running=false;
    })();
  };
  const [uploaded,setUploaded]=useState<UploadedPrintJob|null>(()=>initialDraft?.uploaded??initialDraft?.uploadedBatch?.[0]??null);
  const [uploadedBatch,setUploadedBatch]=useState<UploadedPrintJob[]>(()=>initialDraft?.uploadedBatch??(initialDraft?.uploaded?[initialDraft.uploaded]:[]));
  const [uploadedFileFingerprints,setUploadedFileFingerprints]=useState<string[]>(()=>initialDraft?.uploadedFileFingerprints??[]);
  const [privateFileNamesByJobId,setPrivateFileNamesByJobId]=useState<Record<string,string>>(()=>storedCustomerPrivateFileNames(initialDraft));
  const [batchId,setBatchId]=useState(()=>initialDraft?.batchId??"");
  const [batchName,setBatchName]=useState(()=>initialDraft?.batchName??initialDraft?.uploadedBatch?.[0]?.job?.batchName??initialDraft?.uploaded?.job?.batchName??"");
  const [fileGroupNames,setFileGroupNames]=useState<Record<string,string>>(()=>initialDraft?.fileGroupNames??{});
  const [groupSettings,setGroupSettings]=useState<Record<string,CustomerGroupSettings>>(()=>initialDraft?.groupSettings??{});
  const [fileSettings,setFileSettings]=useState<Record<string,CustomerFileSettings>>(()=>initialDraft?.fileSettings??{});
  const [printSettingsByJobId,setPrintSettingsByJobId]=useState<Record<string,CustomerSavedPrintSettings>>(()=>initialDraft?.printSettingsByJobId??{});
  const [printKindsByJobId,setPrintKindsByJobId]=useState<Record<string,"DOCUMENT"|"PHOTO"|"ID_DOCUMENT">>(()=>initialDraft?.printKindsByJobId??{});
  const [quotesByJobId,setQuotesByJobId]=useState<Record<string,PrintQuoteResult>>(()=>initialDraft?.quotesByJobId??(initialDraft?.quote&&initialDraft?.uploaded?{[initialDraft.uploaded.job.id]:initialDraft.quote}:{}));
  const [photoSettingsByFileFingerprint,setPhotoSettingsByFileFingerprint]=useState<Record<string,CustomerPhotoSettings>>({});
  const [photoPreviewPagesByFingerprint,setPhotoPreviewPagesByFingerprint]=useState<Record<string,string[]>>({});
  const [selectedPreparedPhotoPage,setSelectedPreparedPhotoPage]=useState(0);
  const photoPreviewUrlsRef=useRef<Set<string>>(new Set());
  const [photoSettingsByJobId,setPhotoSettingsByJobId]=useState<Record<string,CustomerPhotoSettings>>(()=>initialDraft?.photoSettingsByJobId??{});
  const [photoStudioSource,setPhotoStudioSource]=useState<File|null>(null);
  const [photoStudioSourceFingerprint,setPhotoStudioSourceFingerprint]=useState<string|null>(null);
  const [uploadProgress,setUploadProgress]=useState<CustomerUploadProgressState|null>(null);
  const [quote,setQuote]=useState<PrintQuoteResult|null>(()=>initialDraft?.quote??null);
  const [jobStatus,setJobStatus]=useState<CustomerJobStatus|null>(null);
  const [batchStatuses,setBatchStatuses]=useState<Record<string,CustomerJobStatus>>({});
  const [payment,setPayment]=useState<CustomerPayment|null>(null);
  const [batchPayment,setBatchPayment]=useState<CustomerBatchPayment|null>(null);

  function paymentTimestamp(payment: { updatedUtc?: string | null; createdUtc?: string | null }): number {
    const timestamp=Date.parse(payment.updatedUtc??payment.createdUtc??"");
    return Number.isFinite(timestamp)?timestamp:0;
  }

  function rememberBatchPayment(candidate?:CustomerBatchPayment|null) {
    if(!candidate)return;
    setBatchPayment(current=>
      !current||paymentTimestamp(candidate)>=paymentTimestamp(current)
        ?candidate:current);
  }

  function rememberCustomerPayment(candidate?:CustomerPayment|null) {
    if(!candidate)return;
    setPayment(current=>
      !current||current.id!==candidate.id||paymentTimestamp(candidate)>=paymentTimestamp(current)
        ?candidate:current);
  }

  function rememberBatchStatuses(candidates:Record<string,CustomerJobStatus>) {
    setBatchStatuses(current=>{
      const next={...current};
      for(const [id,candidate] of Object.entries(candidates)){
        const existing=current[id];
        if(!existing||Date.parse(candidate.updatedUtc)>=Date.parse(existing.updatedUtc))next[id]=candidate;
      }
      return next;
    });
  }

  function rememberCustomerStatus(candidate:CustomerJobStatus) {
    rememberBatchStatuses({[candidate.id]:candidate});
    // Background responses for a previously selected file update its batch row,
    // but must never replace the currently selected file's status panel.
    if(uploaded?.job.id===candidate.id){
      setJobStatus(current=>
        !current||current.id!==candidate.id||Date.parse(candidate.updatedUtc)>=Date.parse(current.updatedUtc)
          ?candidate:current);
    }
    if(candidate.pricingVersion&&candidate.quotedAmountMinor>0) {
      const paperSize=(["A4","A3","A5","LETTER","LEGAL"].includes(candidate.paperSize)
        ?candidate.paperSize:"A4") as CustomerPrintRequestSettings["paperSize"];
      const orientation=(["AUTO","PORTRAIT","LANDSCAPE"].includes(candidate.orientation??"AUTO")
        ?candidate.orientation??"AUTO":"AUTO") as CustomerPrintRequestSettings["orientation"];
      // A shop-side quote revision is authoritative. Keep the settings shown
      // to the customer in step with the revised amount before approval.
      setPrintSettingsByJobId(current=>{
        const previous=current[candidate.id];
        return {
          ...current,
          [candidate.id]:{
            ...previous,
            copies:candidate.copies,
            colourMode:candidate.colourMode==="Colour"?"Colour":"Mono",
            paperSize,
            isDuplex:candidate.isDuplex,
            orientation,
            advancedEnabled:Boolean(previous?.advancedEnabled)||paperSize!=="A4"||orientation!=="AUTO"
          }
        };
      });
      const revisedQuote:PrintQuoteResult={
        id:candidate.id,
        batchId:uploadedBatch.find(item=>item.job.id===candidate.id)?.job.batchId??uploaded?.job.batchId??null,
        jobNumber:candidate.jobNumber,
        status:candidate.status,
        pageCount:candidate.pageCount,
        copies:candidate.copies,
        colourMode:candidate.colourMode as "Mono"|"Colour",
        paperSize:candidate.paperSize as CustomerPrintRequestSettings["paperSize"],
        isDuplex:candidate.isDuplex,
        orientation:(candidate.orientation??"AUTO") as CustomerPrintRequestSettings["orientation"],
        printKind:(candidate.printKind??"DOCUMENT") as "DOCUMENT"|"PHOTO"|"ID_DOCUMENT",
        photoSizeCode:candidate.photoSizeCode??null,
        photoCount:candidate.photoCount,
        photoUnitPriceMinor:candidate.photoUnitPriceMinorSnapshot,
        currency:candidate.currency,
        quotedAmountMinor:candidate.quotedAmountMinor,
        amountMajor:candidate.quotedAmountMinor/100,
        pricingVersion:candidate.pricingVersion,
        quoteExpiresUtc:candidate.quoteExpiresUtc??""
      };
      setQuotesByJobId(current=>({...current,[candidate.id]:revisedQuote}));
      setQuote(revisedQuote);
    }
    rememberCustomerPayment(candidate.payment);
    rememberBatchPayment(candidate.payment?.batchPayment);
  }
  const [paymentReference,setPaymentReference]=useState("");
  const [paymentMethod,setPaymentMethod]=useState<"UPI"|"CASH">(paymentOptions.upiEnabled?"UPI":"CASH");
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notices,setNotices]=useState<Array<{kind:"info"|"warning";message:string}>>([]);
  const [uploadMode,setUploadMode]=useState<"LOCAL"|"CLOUD">(
    localAgent?.endpoints.length ? "LOCAL" : "CLOUD"
  );
  const [uploadModeManuallySelected,setUploadModeManuallySelected]=useState(false);
  const privatePrintingStorageKey="smartprint.private-printing:"+sessionToken;
  const privacyChoiceManualStorageKey="smartprint.privacy-choice-manual:"+sessionToken;
  const [privatePrinting,setPrivatePrinting]=useState(()=>sessionStorage.getItem("smartprint.private-printing:"+sessionToken)==="true");
  const [privacyChoiceManuallySelected,setPrivacyChoiceManuallySelected]=useState(
    ()=>sessionStorage.getItem("smartprint.privacy-choice-manual:"+sessionToken)==="true"
  );
  const privateVirtualTestMode = localAgent?.printerOutputMode === "PDF_EXPORT";
  const privateOutputModeAllowed = localAgent?.printerOutputMode === "PHYSICAL" ||
    (privateVirtualTestMode && privatePrintingVirtualOutputEnabled);
  const privateQueueReady = localAgent?.printerStatus === "Ready" || localAgent?.printerStatus === "Busy";
  const canOfferPrivatePrinting=Boolean(
    hasAssignedPrinter &&
    localAgent?.agentDeviceId &&
    localAgent.documentEncryptionPublicKey &&
    privateQueueReady &&
    privateOutputModeAllowed
  );

  useEffect(()=>{
    if(privatePrinting)sessionStorage.setItem(privatePrintingStorageKey,"true");
    else sessionStorage.removeItem(privatePrintingStorageKey);
  },[privatePrinting,privatePrintingStorageKey]);

  useEffect(()=>{
    if(privacyChoiceManuallySelected)sessionStorage.setItem(privacyChoiceManualStorageKey,"true");
    else sessionStorage.removeItem(privacyChoiceManualStorageKey);
  },[privacyChoiceManuallySelected,privacyChoiceManualStorageKey]);

  const secureQueueFallbackEnabled = cloudAssistedPrintingEnabled || !localAgent?.endpoints.length || localFallbackSuggested;

  useEffect(()=>{
    if(privatePrinting){setUploadMode("LOCAL");return;}
    setUploadMode(current=>resolveCustomerUploadMode(
      current,
      Boolean(localAgent?.endpoints?.length),
      secureQueueFallbackEnabled,
      uploadModeManuallySelected
    ));
  },[secureQueueFallbackEnabled,localAgent?.endpoints.join("|"),uploadModeManuallySelected,privatePrinting]);

  useEffect(()=>{
    if(privatePrinting){setUploadMode("LOCAL");return;}
    if(uploadModeManuallySelected)return;
    if((localFallbackSuggested||!localReady)&&secureQueueFallbackEnabled) {
      setUploadMode("CLOUD");
    } else if(!localFallbackSuggested&&localReady&&localAgent?.endpoints.length) {
      setUploadMode("LOCAL");
    }
  },[localFallbackSuggested,localReady,localAgent?.endpoints.length,secureQueueFallbackEnabled,uploadModeManuallySelected,privatePrinting]);

  const selectedBatchRequiresPrivatePrinting=privatePrinting||files.some(isSmartPrintIdDocument);
  const pendingPrivateRetryFiles=files.filter(candidate=>
    !uploadedFileFingerprints.includes(customerFileFingerprint(candidate))
  );
  const hasPendingPrivateUploadIdentity=selectedBatchRequiresPrivatePrinting&&pendingPrivateRetryFiles.some(candidate=>
    hasPendingLocalCustomerUploadAttempt(sessionToken,candidate,batchId||undefined)
  );
  const customerUploadUnavailable = selectedBatchRequiresPrivatePrinting
    ? !canOfferPrivatePrinting&&!hasPendingPrivateUploadIdentity
    : !secureQueueFallbackEnabled && !localAgent?.endpoints.length;

  useEffect(()=>{
    if(!paymentOptions.upiEnabled&&paymentOptions.cashEnabled)setPaymentMethod("CASH");
    else if(paymentOptions.upiEnabled)setPaymentMethod("UPI");
  },[paymentOptions.upiEnabled,paymentOptions.cashEnabled]);

  useEffect(()=>{
    const storageKey=`qrtoprint.customer-active-job:${publicCode}`;
    const previousStorageKey=`qrtoprint.customer-previous-job:${publicCode}`;
    const currentMarker=sessionToken.slice(-16);
    if(!uploaded&&uploadedBatch.length===0&&!batchName.trim()){
      const existingRaw=localStorage.getItem(storageKey);
      if(existingRaw){
        try{
          const existing=JSON.parse(existingRaw) as StoredCustomerDraft;
          // A fresh QR session must not erase the previous session's order.
          if(existing.sessionMarker&&existing.sessionMarker!==currentMarker)return;
        }catch{
          // Corrupt local metadata can be cleared safely.
        }
      }
      localStorage.removeItem(storageKey);
      return;
    }
    const existingRaw=localStorage.getItem(storageKey);
    if(existingRaw){
      try{
        const existing=JSON.parse(existingRaw) as StoredCustomerDraft;
        if(existing.sessionMarker&&existing.sessionMarker!==currentMarker&&
          (existing.uploaded?.job?.id||existing.uploadedBatch?.length||existing.batchName?.trim())){
          // Keep one bounded prior-order snapshot; never persist a token or file bytes.
          localStorage.setItem(previousStorageKey,existingRaw);
        }
      }catch{
        // Replace malformed metadata with this session's valid snapshot.
      }
    }
    // Store only job metadata, never the bearer session capability.
    localStorage.setItem(storageKey,JSON.stringify({
      sessionMarker:currentMarker,
      uploaded: uploaded??uploadedBatch[0],
      uploadedBatch,
      uploadedFileFingerprints,
      privateFileNamesByJobId,
      batchId,
      batchName,
      quote,
      fileGroupNames,
      groupSettings,
      fileSettings,
      printSettingsByJobId,
      printKindsByJobId,
      quotesByJobId,
      photoSettingsByJobId
    } satisfies StoredCustomerDraft));
  },[publicCode,sessionToken,uploaded,uploadedBatch,uploadedFileFingerprints,privateFileNamesByJobId,batchId,batchName,quote,fileGroupNames,groupSettings,fileSettings,printSettingsByJobId,printKindsByJobId,quotesByJobId,photoSettingsByJobId]);

  const batchItemsForStatus=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
  const batchStatusKey=batchItemsForStatus.map(item=>item.job.id).join("|");
  const canStartNewSession=!!uploaded&&isCustomerBatchFinished(batchItemsForStatus,batchStatuses,uploaded.job.id,jobStatus?.status);
  const groupNameForFile=(candidate:File)=>fileGroupNames[customerFileFingerprint(candidate)]??customerBatchGroupName(candidate);
  const defaultsForGroup=(name:string):CustomerGroupSettings=>{
    const baseline:CustomerGroupSettings={
      copies:"1",colourMode:"Mono",isDuplex:false,advancedEnabled:false,paperSize:"A4",orientation:"AUTO",pagesPerSheet:1
    };
    return {...baseline,...(groupSettings[name]??{})};
  };
  const settingsForFile=(candidate:File):CustomerSavedPrintSettings=> {
    if(isSmartPrintIdDocument(candidate)) {
      const defaults=defaultsForGroup(groupNameForFile(candidate));
      return {
        copies:1,colourMode:defaults.colourMode,isDuplex:false,paperSize:"A4",orientation:"PORTRAIT",pagesPerSheet:1,advancedEnabled:false
      };
    }
    const fingerprint=customerFileFingerprint(candidate);
    const defaults=defaultsForGroup(groupNameForFile(candidate));
    const own=fileSettings[fingerprint];
    if(photoSettingsByFileFingerprint[fingerprint])return {
      copies:1,colourMode:"Colour",paperSize:"A4",isDuplex:false,orientation:"AUTO",pagesPerSheet:1,advancedEnabled:false
    };
    const selected=own?.custom?{...defaults,...own}:defaults;
    return {
      copies:Number(normalizeCustomerCopies(selected.copies)),
      colourMode:selected.colourMode,
      isDuplex:selected.isDuplex,
      paperSize:selected.advancedEnabled?selected.paperSize:"A4",
      orientation:selected.advancedEnabled?selected.orientation:"AUTO",
      pagesPerSheet:selected.advancedEnabled&&isCustomerPdf(candidate)&&!isSmartPrintPhotoDocument(candidate)&&
        nupOptions.includes(selected.pagesPerSheet??1)
        ?(selected.pagesPerSheet??1):1,
      advancedEnabled:selected.advancedEnabled
    };
  };

  useEffect(()=>{
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    if(items.length===0)return;
    let active=true;
    let refreshing=false;
    const refreshAll=async()=>{
      if(refreshing)return;
      refreshing=true;
      try{
        const selectedJobId=uploaded?.job.id;
        const results=await Promise.all(items.filter(item=>item.job.id!==selectedJobId).map(async item=>{
          try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
          catch{return null;}
        }));
        if(!active)return;
        const fresh=results.reduce<Record<string,Awaited<ReturnType<typeof getCustomerJobStatus>>>>((map,item)=>{
          if(item)map[item[0]]=item[1];
          return map;
        },{});
        rememberBatchStatuses(fresh);
        const latestBatchPayment=results
          .map(item=>item?.[1].payment?.batchPayment??null)
          .filter((item):item is CustomerBatchPayment=>item!==null)
          .sort((left,right)=>paymentTimestamp(right)-paymentTimestamp(left))[0];
        rememberBatchPayment(latestBatchPayment);
      } finally {refreshing=false;}
    };
    void refreshAll();
    const timer=window.setInterval(()=>void refreshAll(),30000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[sessionToken,batchStatusKey,uploaded?.job.id]);

  useEffect(()=>{
    if(!uploaded)return;
    let active=true;
    void getCustomerJobStatus(sessionToken,uploaded.job.id).then(refreshed=>{
      if(!active)return;
      // A newly uploaded job starts in AWAITING_CONFIGURATION on the API.
      // Treat that state like the legacy Uploaded value so the customer can
      // choose print settings and request the first quote after a reload.
      if(refreshed.status==="Uploaded"||refreshed.status==="UPLOADED"||refreshed.status==="AWAITING_CONFIGURATION"){
        setJobStatus(null);
        setPayment(null);
        return;
      }
      rememberCustomerStatus(refreshed);
    }).catch(exception=>{
      if(active)setError(exception instanceof ApiError?t(exception.message):t("Could not restore this print request."));
    });
    return ()=>{active=false;};
  },[sessionToken,uploaded?.job.id]);

  useEffect(()=>{
    if(!nativeAppMode)return;
    const onNativePdfPreview=(event:MessageEvent)=>{
      const message=typeof event.data==="string"?event.data:"";
      if(message.startsWith("CUSTOMER_PDF_PREVIEW|")){
        try{
          const payload=JSON.parse(message.slice("CUSTOMER_PDF_PREVIEW|".length)) as {
            token:string;pageIndex:number;pageCount:number;pagesPerSheet:number;
            pagesPerSheetDirection:CustomerNupDirection;
            paperSize:string;orientation:string;firstSourcePage:number;lastSourcePage:number;image:string;
            thumbnails:Array<{index:number;image:string}>;
          };
          setNativePdfPreview(current=>current&&current.token===payload.token&&
            current.requestedPageIndex===payload.pageIndex&&current.pagesPerSheet===payload.pagesPerSheet&&
            current.pagesPerSheetDirection===payload.pagesPerSheetDirection&&
            current.paperSize===payload.paperSize&&current.orientation===payload.orientation
            ?{...payload,requestedPageIndex:payload.pageIndex,loading:false}:current);
        }catch{
          setError(t("The PDF preview could not be displayed. You can still upload the document."));
        }
      }else if(message.startsWith("CUSTOMER_PDF_PREVIEW_FAILED|")){
        const token=message.slice("CUSTOMER_PDF_PREVIEW_FAILED|".length).split("|",1)[0];
        setNativePdfPreview(current=>current?.token===token
          ?{...current,pageCount:0,image:"",thumbnails:[],loading:false}
          :current);
        setError(t("The PDF preview could not be prepared. You can still upload the document."));
      }
    };
    const bridge=(window as Window & {
      SmartPrintNative?:{
        addEventListener?:(type:string,listener:(event:MessageEvent)=>void)=>void;
        removeEventListener?:(type:string,listener:(event:MessageEvent)=>void)=>void;
      };
    }).SmartPrintNative;
    if(!bridge?.addEventListener||!bridge.removeEventListener)return;
    bridge.addEventListener("message",onNativePdfPreview);
    return ()=>bridge.removeEventListener!("message",onNativePdfPreview);
  },[nativeAppMode]);

  useEffect(()=>{
    if(!nativeAppMode)return;
    setNativePdfPreview(null);
    return ()=>{postSmartPrintNativeMessage("closeCustomerPdfPreview");};
  },[file,nativeAppMode]);

  useEffect(()=>{
    if(!nativeAppMode||!file||!isCustomerPdf(file))return;
    if(photoSettingsByFileFingerprint[customerFileFingerprint(file)])return;
    // Re-render the sheet preview whenever pages-per-sheet, paper or orientation changes.
    openCustomerPdfPreview(file);
  },[file,nativeAppMode,photoSettingsByFileFingerprint,groupSettings,fileSettings]);

  useEffect(()=>{
    if(!nativeAppMode)return;
    const tokens=files
      .map(candidate=>smartPrintNativePdfPreviewTokens.get(candidate))
      .filter((token):token is string=>Boolean(token));
    postSmartPrintNativeMessage("retainCustomerPdfPreviews|"+tokens.join(","));
  },[files,nativeAppMode]);

  useEffect(()=>{
    if(!nativeAppMode)return;
    return ()=>{
      postSmartPrintNativeMessage("retainCustomerPdfPreviews|none");
      // Leaving the customer upload flow ends the lifetime of native plaintext
      // staging files. Failed uploads remain available for retry while this
      // component stays mounted.
      postSmartPrintNativeMessage("releaseStudioUploadFiles");
    };
  },[nativeAppMode]);

  useEffect(()=>()=>{photoPreviewUrlsRef.current.forEach(url=>URL.revokeObjectURL(url));photoPreviewUrlsRef.current.clear();},[]);

  useEffect(()=>{
    if(!file){setPreviewUrl(null);return;}
    // Android providers and renamed files can expose a MIME type that disagrees
    // with the bytes. The content analysis performed during selection is the
    // authority for both PDF and image previews.
    const identified=identifiedCustomerDocumentTypes.get(file);
    const browserMediaType=file.type.split(";")[0].trim().toLowerCase();
    const previewSource=identified&&browserMediaType!==identified.mediaType
      ?new Blob([file],{type:identified.mediaType})
      :file;
    const url=URL.createObjectURL(previewSource);
    setPreviewUrl(url);
    return ()=>URL.revokeObjectURL(url);
  },[file]);

  useEffect(()=>{
    setBrowserNupPreviewSheetIndex(0);
  },[file,groupSettings,fileSettings]);

  useEffect(()=>{
    if(nativeAppMode||!file||!isCustomerPdf(file)){
      browserNupSourceCacheRef.current=null;
      setBrowserNupPreview(null);
      return;
    }

    const settings=settingsForFile(file);
    const pagesPerSheet=settings.pagesPerSheet??1;
    if(pagesPerSheet<=1||!pagesPerSheetDirection||photoSettingsByFileFingerprint[customerFileFingerprint(file)]){
      browserNupSourceCacheRef.current=null;
      setBrowserNupPreview(null);
      return;
    }

    let cancelled=false;
    let generatedUrl:string|null=null;
    const sheetIndex=Math.max(0,browserNupPreviewSheetIndex);
    setBrowserNupPreview({
      url:"",
      sheetIndex,
      sheetCount:0,
      pagesPerSheet,
      pagesPerSheetDirection,
      firstSourcePage:sheetIndex*pagesPerSheet+1,
      lastSourcePage:(sheetIndex+1)*pagesPerSheet,
      loading:true,
      failed:false
    });

    // Create/retain one parsed source per currently selected File. The promise is
    // lazy so a superseded request does not read a PDF it will never render.
    let sourceCache=browserNupSourceCacheRef.current;
    if(!sourceCache||sourceCache.file!==file){
      sourceCache={file,document:null};
      browserNupSourceCacheRef.current=sourceCache;
    }

    enqueueBrowserNupRender(async()=>{
      try{
        if(cancelled)return;
        const {loadCustomerNupPreviewSource,createCustomerNupPreviewFromDocument}=await import("./customer-nup-preview");
        if(cancelled)return;
        if(!sourceCache!.document){
          const loadingDocument=(async()=>{
            const sourceBytes=new Uint8Array(await file.arrayBuffer());
            if(cancelled)throw new Error("PDF preview superseded.");
            return loadCustomerNupPreviewSource(sourceBytes);
          })();
          // A cancelled/failed first parse must not poison the cached promise.
          // Clear only this exact in-flight promise so a newer parse is preserved.
          sourceCache!.document=loadingDocument;
          void loadingDocument.catch(()=>{
            if(sourceCache!.document===loadingDocument)sourceCache!.document=null;
          });
        }
        const source=await sourceCache!.document;
        if(cancelled)return;
        const preview=await createCustomerNupPreviewFromDocument(source,{
          pagesPerSheet,
          paperSize:settings.paperSize,
          orientation:settings.orientation,
          pagesPerSheetDirection,
          sheetIndex
        });
        if(cancelled)return;
        const outputBuffer=preview.bytes.buffer.slice(
          preview.bytes.byteOffset,
          preview.bytes.byteOffset+preview.bytes.byteLength
        ) as ArrayBuffer;
        generatedUrl=URL.createObjectURL(new Blob([outputBuffer],{type:"application/pdf"}));
        setBrowserNupPreview({
          url:generatedUrl,
          sheetIndex:preview.sheetIndex,
          sheetCount:preview.sheetCount,
          pagesPerSheet,
          pagesPerSheetDirection,
          firstSourcePage:preview.firstSourcePage,
          lastSourcePage:preview.lastSourcePage,
          loading:false,
          failed:false
        });
      }catch{
        if(!cancelled){
          setBrowserNupPreview({
            url:"",
            sheetIndex,
            sheetCount:0,
            pagesPerSheet,
            pagesPerSheetDirection,
            firstSourcePage:sheetIndex*pagesPerSheet+1,
            lastSourcePage:(sheetIndex+1)*pagesPerSheet,
            loading:false,
            failed:true
          });
        }
      }
    });

    return ()=>{
      cancelled=true;
      if(generatedUrl)URL.revokeObjectURL(generatedUrl);
      // Release the parsed document when a different File becomes selected.
      // Sheet/settings changes for the same File continue to reuse the cache.
      if(browserNupSourceCacheRef.current?.file!==file)browserNupSourceCacheRef.current=null;
    };
  },[file,nativeAppMode,groupSettings,fileSettings,photoSettingsByFileFingerprint,browserNupPreviewSheetIndex]);

  async function upload() {
    if(batchLockedForPayment){
      setError(t("This batch already has a payment attempt. Start a new batch for additional files."));
      return;
    }
    if(files.length===0)return;
    const containsIdDocument=files.some(isSmartPrintIdDocument);
    const effectivePrivatePrinting=privatePrinting||containsIdDocument;
    // The browser's cached job DTO can be stale or incomplete while a Private
    // relay ticket is being reconciled. The API validates the authoritative
    // persisted batch mode (jobs and outstanding upload tickets) before issuing a ticket.
    // Do not reject an ID sheet from cached job.privatePrinting metadata here.
    if(containsIdDocument&&!privatePrinting)setPrivatePrinting(true);
    const effectiveUploadMode: "LOCAL"|"CLOUD"=effectivePrivatePrinting?"LOCAL":uploadMode;
    if(effectivePrivatePrinting&&!canOfferPrivatePrinting&&!hasPendingPrivateUploadIdentity) {
      setError(t("Private Printing is not ready at this shop yet. No new document has been uploaded. Ask the shop operator to reconnect the paired computer."));
      return;
    }
    if(effectivePrivatePrinting&&uploadMode!=="LOCAL")setUploadMode("LOCAL");
    if(effectiveUploadMode==="LOCAL"&&!effectivePrivatePrinting&&!localReady) {
      setError(nativeAppMode ? t("Check shop Wi-Fi.") : t("Check shop Wi-Fi access above before starting a direct local upload."));
      return;
    }
    let currentBatchId=batchId||crypto.randomUUID();
    let currentBatchName=batchName.trim();
    setBatchId(currentBatchId);
    const completedFingerprints=new Set(uploadedFileFingerprints);
    const results=[...uploadedBatch];
    const savedSettings={...printSettingsByJobId};
    const savedPhotoSettings={...photoSettingsByJobId};
    const savedPrintKinds={...printKindsByJobId};
    const savedPrivateFileNames={...privateFileNamesByJobId};
    for(const item of results) {
      if(item.job.privatePrinting&&!isPrivateFilenamePlaceholder(item.document.originalFileName)) {
        savedPrivateFileNames[item.job.id]??=item.document.originalFileName;
      }
    }
    const pending=files.filter(candidate=>!completedFingerprints.has(customerFileFingerprint(candidate)));
    if(pending.length===0){setError(nativeAppMode ? t("Files already uploaded. Choose more.") : t("All selected files are already uploaded. Choose another document or folder."));return;}
    const currentBatchBytes=results.reduce((sum,item)=>sum+item.document.sizeBytes,0);
    const uploadCapacityError=validateCustomerUploadBatchLimits(results.length,currentBatchBytes,pending.map(candidate=>candidate.size));
    if(uploadCapacityError){setError(uploadCapacityError.code==="FILE_SIZE"?t("One selected file exceeds the 50 MB per-file upload limit."):uploadCapacityError.code==="FILE_COUNT"?t("A print batch can contain up to 20 files."):t("A print batch can contain up to 500 MB in total."));return;}
    setBusy(true);setError("");setNotices([]);setQuote(null);setQuotesByJobId({});setJobStatus(null);
    setUploadProgress({
      completed:0,
      total:pending.length,
      files:pending.map(candidate=>({
        name:candidate.name,
        sizeBytes:candidate.size,
        bytesSent:0,
        totalBytes:candidate.size,
        status:"QUEUED",
        message:"Waiting for an available upload slot"
      }))
    });
    const updateFileProgress=(index:number,patch:Partial<CustomerUploadFileProgress>)=>{
      setUploadProgress(current=>{
        if(!current||!current.files[index])return current;
        const next=[...current.files];
        const previous=next[index];
        next[index]={
          ...previous,
          ...patch,
          ...(patch.status==="COMPLETED"
            ?{bytesSent:previous.totalBytes||previous.sizeBytes}
            :{})
        };
        return {...current,files:next};
      });
    };
    const reportTransferProgress=(index:number,progress:{bytesSent:number;totalBytes:number;stage:string})=>{
      if(progress.stage==="WAITING_FOR_SHOP") {
        updateFileProgress(index,{
          bytesSent:progress.bytesSent,
          totalBytes:progress.totalBytes,
          status:"WAITING_FOR_SHOP",
          message:t("Encrypted file received; waiting for shop confirmation")
        });
        return;
      }
      updateFileProgress(index,{
        bytesSent:progress.bytesSent,
        totalBytes:progress.totalBytes,
        status:progress.stage==="STREAMING_ENCRYPTED"?"STREAMING_ENCRYPTED":"TRANSFERRING",
        message:progress.stage==="STREAMING_ENCRYPTED"
          ?"Encrypting and streaming authenticated records"
          :"Transferring file bytes"
      });
    };
    const reportPrivateStage=(index:number,stage:string,message:string)=>{
      const status=stage==="WAITING_FOR_AGENT"
        ?"WAITING_FOR_SHOP"
        :stage==="WAITING_FOR_UPLOAD"?"PREPARING"
          :stage==="RECEIVING_ENCRYPTED_PAYLOAD"?"STREAMING_ENCRYPTED"
            :stage==="READY"?"COMPLETED"
              :stage==="FAILED"||stage==="EXPIRED"?"FAILED"
                :"PROCESSING";
      updateFileProgress(index,{status,message});
    };
    try {
      const initialResults=[...results];
      const resultsByPendingIndex=new Map<number,UploadedPrintJob>();
      const outcomes=await mapConcurrentSettled(
        pending,
        CUSTOMER_UPLOAD_CONCURRENCY,
        async (candidate,index)=>{
          updateFileProgress(index,{
            status:"PREPARING",
            message:effectivePrivatePrinting?"Preparing encrypted Private Printing transfer":"Preparing secure upload"
          });
          const fingerprint=customerFileFingerprint(candidate);
          const batchGroupName=groupNameForFile(candidate);
          const settings=settingsForFile(candidate);
          const idDocument=isSmartPrintIdDocument(candidate);
          const photoSettings=photoSettingsByFileFingerprint[fingerprint];
          const printKind=idDocument?"ID_DOCUMENT":photoSettings?"PHOTO":"DOCUMENT";
          if(idDocument&&!effectivePrivatePrinting) throw new ApiError("Prepared ID cards must use Private Printing. No normal cloud upload is allowed.",409);
          const uploadedItem=effectiveUploadMode==="LOCAL"&&(effectivePrivatePrinting||localAgent)
            ? await uploadCustomerDocumentLocally(
                sessionToken,candidate,localAgent,currentBatchId,batchGroupName,currentBatchName||undefined,
                cloudAssistedPrintingEnabled,settings,effectivePrivatePrinting,localReady,printKind,photoSettings,
                progress=>reportTransferProgress(index,progress),
                (stage,message)=>reportPrivateStage(index,stage,message)
              )
            : idDocument
              ? (()=>{throw new ApiError("The paired shop computer's encrypted transfer is not ready. No ID document was uploaded or sent to cloud storage.",409);})()
              : await uploadCustomerDocument(
                  sessionToken,candidate,currentBatchId,batchGroupName,currentBatchName||undefined,
                  settings,printKind,photoSettings,
                  progress=>reportTransferProgress(index,progress)
                );
          const created=effectivePrivatePrinting
            ? {...uploadedItem,document:{...uploadedItem.document,originalFileName:candidate.name}}
            : uploadedItem;
          if(created.job.batchId&&created.job.batchId!==currentBatchId) {
            throw new ApiError("This file belongs to an earlier batch. Refresh the original print request before adding more files; SmartPrint will not mix two batches.",409);
          }
          if(created.job.batchName)currentBatchName=created.job.batchName;
          return created;
        },
        progress=>{
          setUploadProgress(current=>current?{...current,completed:progress.completed}:current);
        },
        (index,outcome)=>{
          if(outcome.status==="fulfilled") {
            updateFileProgress(index,{status:"COMPLETED",message:"Upload confirmed"});
          } else if(outcome.reason instanceof LocalUploadOutcomeUnknownError) {
            updateFileProgress(index,{status:"WAITING_FOR_SHOP",message:outcome.reason.message});
          } else {
            updateFileProgress(index,{
              status:"FAILED",
              message:outcome.reason instanceof Error?outcome.reason.message:"Upload failed"
            });
          }
          if(outcome.status!=="fulfilled")return;
          const candidate=pending[index];
          const created=outcome.value;
          const fingerprint=customerFileFingerprint(candidate);
          resultsByPendingIndex.set(index,created);
          if(effectivePrivatePrinting)savedPrivateFileNames[created.job.id]=candidate.name;
          savedSettings[created.job.id]=settingsForFile(candidate);
          const photoSettings=photoSettingsByFileFingerprint[fingerprint];
          if(photoSettings)savedPhotoSettings[created.job.id]=photoSettings;
          savedPrintKinds[created.job.id]=isSmartPrintIdDocument(candidate)?"ID_DOCUMENT":photoSettings?"PHOTO":"DOCUMENT";
          completedFingerprints.add(fingerprint);

          const orderedResults=[
            ...initialResults,
            ...pending.flatMap((_,pendingIndex)=>{
              const result=resultsByPendingIndex.get(pendingIndex);
              return result?[result]:[];
            })
          ];
          results.splice(0,results.length,...orderedResults);
          setPrivateFileNamesByJobId({...savedPrivateFileNames});
          setPrintSettingsByJobId({...savedSettings});
          setPhotoSettingsByJobId({...savedPhotoSettings});
          setPrintKindsByJobId({...savedPrintKinds});
          setUploadedBatch(orderedResults);
          setUploaded(orderedResults[0]??null);
          setUploadedFileFingerprints([...completedFingerprints]);
        }
      );
      const firstFailure=outcomes.find(outcome=>outcome.status==="rejected");
      if(firstFailure?.status==="rejected")throw firstFailure.reason;
      setPrintSettingsByJobId(savedSettings);
      setPhotoSettingsByJobId(savedPhotoSettings);
      setPrintKindsByJobId(savedPrintKinds);
      setPrivateFileNamesByJobId(savedPrivateFileNames);
      setUploadedBatch(results);
      setUploaded(results[0]??null);
      setQuote(null);setJobStatus(null);setPayment(null);setPaymentReference("");
      setSelectedFileIndex(0);
      setFiles([]);
      // Upload succeeded; release both studios' temporary decrypted picker copies.
      // The saved encrypted Photo Studio and ID Studio vault entries remain untouched.
      postSmartPrintNativeMessage("releaseStudioUploadFiles");
      if(paymentOptions.instantPaymentEnabled)await getQuote(results,savedSettings,savedPhotoSettings,savedPrintKinds);
    } catch(exception) {
      setPrintSettingsByJobId(savedSettings);
      setPhotoSettingsByJobId(savedPhotoSettings);
      setPrintKindsByJobId(savedPrintKinds);
      setPrivateFileNamesByJobId(savedPrivateFileNames);
      setUploadedBatch(results);
      setUploadedFileFingerprints([...completedFingerprints]);
      if(results.length>0)setUploaded(results[0]);
      const confirmedPreTicketRejection=effectiveUploadMode==="LOCAL"&&exception instanceof LocalUploadPreflightError;
      const agentUnavailable=exception instanceof LocalUploadPreflightError&&
        ["local_print_agent_unavailable","local_print_printer_unavailable"].includes(exception.code??"");
      const batchCheckoutLocked=exception instanceof LocalUploadPreflightError&&
        exception.code==="batch_upload_locked_after_payment_start";
      const canFallbackToCloud=!privatePrinting&&!batchCheckoutLocked&&canFallbackToCloudFromLocalFailure(
        confirmedPreTicketRejection,
        cloudAssistedPrintingEnabled||agentUnavailable
      );
      if(canFallbackToCloud){setUploadModeManuallySelected(true);setUploadMode("CLOUD");}
      if(exception instanceof LocalUploadOutcomeUnknownError&&effectiveUploadMode==="LOCAL"&&!privatePrinting&&secureQueueFallbackEnabled) {
        // Local ticket and grant are preserved. HTTPS recovery uses this same
        // PrintJob; an incomplete file may need to be sent again from the start.
        setUploadModeManuallySelected(true);
        setUploadMode("CLOUD");
      }
      const batchPrivacyMismatch=exception instanceof ApiError&&exception.code==="batch_privacy_mode_mismatch";
      if(batchPrivacyMismatch&&!containsIdDocument) {
        // The API confirms that this batch is already Private; restore the
        // customer control to the authoritative, non-downgradable mode.
        setPrivatePrinting(true);
        setUploadMode("LOCAL");
        setUploadModeManuallySelected(true);
      }
      const privatePromotionUnavailable=exception instanceof ApiError&&exception.code==="batch_private_promotion_unavailable";
      const uploadError=privatePromotionUnavailable
        ? exception instanceof ApiError ? exception.message : "Private Printing could not be enabled. No privacy mode was changed."
        : batchPrivacyMismatch
          ? containsIdDocument
            ? "This batch is already Private. Keep Private Printing selected for every additional file."
            : "This batch already contains a Private Printing file. Keep Private Printing selected for every additional file."
        : exception instanceof LocalUploadOutcomeUnknownError
        ? privatePrinting
          ? exception.message
          : "Your original print request is saved. Tap Retry to check its status. The file may need to be sent again from the beginning; SmartPrint will not create a duplicate print job."
        : batchCheckoutLocked
          ? "Checkout has already started for this batch, so this unfinished file was not included. Start a new batch for this file."
          : confirmedPreTicketRejection
          ? exception instanceof LocalUploadPreflightError && exception.code==="local_upload_ticket_expired"
            ? canFallbackToCloud
              ? "The earlier shop Wi-Fi attempt expired before a job was created. Cloud-assisted HTTPS is now selected; tap Retry to start a fresh transfer."
              : "The earlier shop Wi-Fi attempt expired before a job was created. Reconnect to the shop computer, check Wi-Fi, then tap Retry to start a fresh transfer."
            : exception instanceof LocalUploadPreflightError && exception.code==="batch_local_agent_mismatch"
              ? canFallbackToCloud
                ? "Some files in this batch are stored on another shop computer. Cloud-assisted upload is now selected; retry the remaining files in this same batch. The shop can route the complete batch to the computer holding the existing files."
                : "Some files in this batch are on another shop computer. Cloud-assisted upload is not included in this shop's plan. Ask the shop to restore the original computer before retrying the same batch."
              : canFallbackToCloud
                ? "The API rejected local upload before creating a ticket. Cloud-assisted HTTPS upload is now selected; retry the remaining files in this same batch."
                : "The shop cannot accept a local upload right now, and Cloud-assisted upload is not included in its plan. Ask the shop operator to restore the local Agent/printer connection, then retry the same batch."
          : exception instanceof ApiError ? exception.message
          : "Upload stopped after "+completedFingerprints.size+" file(s). Retry the selected files to continue; completed files will be skipped.";
      setError(uploadError);
    } finally {setBusy(false);}
  }

  function selectUploadedFile(item:UploadedPrintJob) {
    if(busy)return;
    setUploaded(item);setQuote(quotesByJobId[item.job.id]??null);setJobStatus(null);setPayment(null);setPaymentReference("");setError("");
  }

  async function getQuote(itemsOverride?:UploadedPrintJob[],settingsOverride?:Record<string,CustomerSavedPrintSettings>,photoSettingsOverride?:Record<string,CustomerPhotoSettings>,printKindsOverride?:Record<string,"DOCUMENT"|"PHOTO"|"ID_DOCUMENT">) {
    const items=itemsOverride??(uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]));
    if(items.length===0)return;
    setBusy(true);setError("");setJobStatus(null);
    const nextQuotes={...quotesByJobId};
    const failures:string[]=[];
    try {
      for(const item of items) {
        const existingQuote=nextQuotes[item.job.id];
        const status=uploaded?.job.id===item.job.id&&jobStatus?.status
          ?jobStatus.status
          :batchStatuses[item.job.id]?.status??item.job.status;
        const quoteExpired=quoteExpiredForBatchItem(item);
        if(!quoteExpired&&existingQuote&&existingQuote.status!=="QUOTE_EXPIRED")continue;
        if(quoteExpired
          ? !["QUOTE_EXPIRED","AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(status)
          : !["AWAITING_CONFIGURATION","UPLOADED","Uploaded"].includes(status))continue;
        const requestedSettings=settingsOverride?.[item.job.id]??printSettingsByJobId[item.job.id];
        const settings:CustomerSavedPrintSettings={
          copies:requestedSettings?.copies??1,
          colourMode:requestedSettings?.colourMode??"Mono",
          isDuplex:requestedSettings?.isDuplex??false,
          paperSize:requestedSettings?.paperSize??"A4",
          orientation:requestedSettings?.orientation??"AUTO",
          pagesPerSheet:requestedSettings?.pagesPerSheet??1,
          advancedEnabled:requestedSettings?.advancedEnabled??false
        };
        const photoSettings=photoSettingsOverride?.[item.job.id]??photoSettingsByJobId[item.job.id];
        const idDocument=(printKindsOverride?.[item.job.id]??printKindsByJobId[item.job.id]??item.job.printKind)==="ID_DOCUMENT";
        try {
          nextQuotes[item.job.id]=await calculateCustomerQuote(sessionToken,item.job.id,{
            colourMode:photoSettings?"Colour":settings.colourMode,
            paperSize:photoSettings||idDocument?"A4":settings.paperSize,
            isDuplex:photoSettings||idDocument?false:settings.isDuplex,
            copies:photoSettings?1:idDocument?1:settings.copies,
            orientation:photoSettings?"AUTO":idDocument?"PORTRAIT":settings.orientation,
            pagesPerSheet:photoSettings||idDocument?1:(settings.pagesPerSheet??1),
            ...(photoSettings
              ?{printKind:"PHOTO" as const,photoSizeCode:photoSettings.sizeCode,photoCount:photoSettings.photoCount,photosPerPage:photoSettings.photosPerPage??photoSettings.photoCount,photoOutputLayout:photoSettings.photoOutputLayout??"A4_SHEET",photoPaperFinish:photoSettings.photoPaperFinish??"PLAIN"}
              :idDocument?{printKind:"ID_DOCUMENT" as const}:{printKind:"DOCUMENT" as const})
          });
        } catch(exception) {
          failures.push(resolveCustomerDocumentName(item,privateFileNamesByJobId));
        }
      }
      setQuotesByJobId(nextQuotes);
      const statusResults=await Promise.all(items.map(async item=>{
        try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
        catch{return null;}
      }));
      const freshStatuses=statusResults.reduce<Record<string,CustomerJobStatus>>((map,item)=>{
        if(item)map[item[0]]=item[1];
        return map;
      },{});
      rememberBatchStatuses(freshStatuses);
      if(uploaded&&freshStatuses[uploaded.job.id]){
        const selected=freshStatuses[uploaded.job.id];
        rememberCustomerStatus(selected);
      }
      const selectedQuote=uploaded?nextQuotes[uploaded.job.id]??null:null;
      setQuote(selectedQuote??Object.values(nextQuotes)[0]??null);
      if(failures.length) {
        setError(t("Could not quote: {files}. Retry the batch quote.").replace("{files}", failures.join(", ")));
      }
      else if(!Object.keys(nextQuotes).length) setError(t("No files are waiting for a quote."));
    } catch(exception) {
      setError(exception instanceof ApiError?t(exception.message):t("The shop could not calculate the batch quote."));
    } finally {setBusy(false);}
  }

  async function acceptQuote() {
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    const pending=items.filter(item=>
      !["REJECTED","REFUNDED"].includes(statusForBatchItem(item))&&
      quotesByJobId[item.job.id]?.status==="AWAITING_CUSTOMER_APPROVAL");
    if(pending.length===0)return;
    setBusy(true);setError("");
    const nextQuotes={...quotesByJobId};
    const failures:string[]=[];
    try {
      for(const item of pending) {
        try {
          const accepted=await acceptCustomerQuote(sessionToken,item.job.id);
          const current=nextQuotes[item.job.id];
          if(current)nextQuotes[item.job.id]={...current,status:accepted.status};
        } catch {
          const fresh=await getCustomerJobStatus(sessionToken,item.job.id).catch(()=>null);
          if(fresh&&fresh.status!=="AWAITING_CUSTOMER_APPROVAL") {
            rememberBatchStatuses({[item.job.id]:fresh});
          } else failures.push(resolveCustomerDocumentName(item,privateFileNamesByJobId));
        }
      }
      setQuotesByJobId(nextQuotes);
      if(uploaded) {
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        rememberCustomerStatus(refreshed);
      }
      const refreshedBatch=await Promise.all(items.map(async item=>{
        try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
        catch{return null;}
      }));
      rememberBatchStatuses(Object.fromEntries(refreshedBatch.filter((entry):entry is NonNullable<typeof entry>=>entry!==null)));
      setQuote(uploaded?nextQuotes[uploaded.job.id]??null:null);
      if(failures.length)setError(t("Could not send: {files}. Refresh and retry.").replace("{files}", failures.join(", ")));
    } catch(exception) {
      setError(exception instanceof ApiError?t(exception.message):t("The batch could not be sent to the shop."));
    } finally {setBusy(false);}
  }

  async function refreshJobStatus() {
    if(!uploaded)return;
    setBusy(true);setError("");
    try {
      const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
      rememberCustomerStatus(refreshed);
    }
    catch(exception){setError(exception instanceof ApiError?t(exception.message):t("Could not refresh job status."));}
    finally {setBusy(false);}
  }


  // Keep the customer informed as the shop reviews, verifies payment and prints.
  // Private jobs keep polling after print completion until shop-computer deletion is acknowledged.
  useEffect(()=>{
    if(!uploaded||!jobStatus)return;
    const liveStates=new Set([
      "AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","AWAITING_COUNTER_PAYMENT",
      "AWAITING_CUSTOMER_PAYMENT","PAYMENT_REPORTED","PAYMENT_REJECTED",
      "PAID_AUTHORISED","LOCAL_QUEUE_CLAIMED","LOCAL_QUEUE_READY",
      "PRINT_AUTHORISED","PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING",
      "OUTPUT_UNCONFIRMED"
    ]);
    const waitingForPrivatePurge =
      (jobStatus.status==="COMPLETED" || jobStatus.status==="TEST_OUTPUT_READY") &&
      Boolean(jobStatus.privatePrinting) &&
      !jobStatus.privateDocumentPurgedUtc;
    if(!liveStates.has(jobStatus.status)&&!waitingForPrivatePurge)return;
    let active=true;
    const timer=window.setInterval(()=>{
      void getCustomerJobStatus(sessionToken,uploaded.job.id).then(refreshed=>{
        if(!active)return;
        rememberCustomerStatus(refreshed);
        rememberBatchPayment(refreshed.payment?.batchPayment);
      }).catch(()=>{/* Retry on the next interval without interrupting the flow. */});
    },8000);
    return ()=>{
      active=false;
      window.clearInterval(timer);
    };
  },[sessionToken,uploaded?.job.id,jobStatus?.status,jobStatus?.privatePrinting,jobStatus?.privateDocumentPurgedUtc]);

  async function refreshBatchStatuses() {
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    const results=await Promise.all(items.map(async item=>{
      try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
      catch{return null;}
    }));
    const fresh=results.reduce<Record<string,CustomerJobStatus>>((map,item)=>{
      if(item)map[item[0]]=item[1];
      return map;
    },{});
    rememberBatchStatuses(fresh);
    const latestBatchPayment=results
      .map(item=>item?.[1].payment?.batchPayment??null)
      .filter((item):item is CustomerBatchPayment=>item!==null)
      .sort((left,right)=>paymentTimestamp(right)-paymentTimestamp(left))[0];
    rememberBatchPayment(latestBatchPayment);
    const selected=uploaded?fresh[uploaded.job.id]:null;
    if(selected){
      rememberCustomerStatus(selected);
    }
  }

  async function beginPayment(method:"UPI"|"CASH",forceBatchCheckout=false) {
    if(!uploaded)return;
    setBusy(true);setError("");
    try {
      if(batchId&&(forceBatchCheckout||!hasLegacyIndividualPayment)){
        const attempt=await startCustomerBatchPayment(sessionToken,batchId,method);
        setBatchPayment(attempt);
        setPaymentMethod(method);
        await refreshBatchStatuses();
      } else {
        const attempt=await startCustomerPayment(sessionToken,uploaded.job.id,method);
        rememberCustomerPayment(attempt);
        setPaymentMethod(method);
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        rememberCustomerStatus(refreshed);
      }
    } catch(exception) {
      if(batchId)await refreshBatchStatuses();
      setError(exception instanceof ApiError && exception.code==="batch_uploads_still_processing"
        ? t("One of your files is still being prepared. Please wait a moment, then try again. No payment has been started.")
        : exception instanceof ApiError && exception.code==="batch_review_not_complete"
          ? t("The shop is still reviewing your files. Payment will be ready when the review is complete.")
          : exception instanceof ApiError
            ? exception.message
            : t("Could not start this payment method."));
    } finally {setBusy(false);}
  }

  async function submitPaymentReference() {
    setBusy(true);setError("");
    try {
      if(batchPayment&&batchPayment.status==="AWAITING_CUSTOMER_PAYMENT"){
        await reportCustomerBatchPayment(sessionToken,batchPayment.id,paymentReference.trim());
        setPaymentReference("");
        await refreshBatchStatuses();
      } else {
        if(!payment||!uploaded)return;
        await reportCustomerUpiPayment(sessionToken,payment.id,paymentReference.trim());
        setPaymentReference("");
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        rememberCustomerStatus(refreshed);
        rememberBatchPayment(refreshed.payment?.batchPayment);
      }
    } catch(exception) {
      setError(exception instanceof ApiError?t(exception.message):t("The payment reference could not be submitted."));
    } finally {setBusy(false);}
  }

  function applyPhotoSheets(sheets:PreparedPhotoSheet[]) {
    if(!photoStudioSourceFingerprint||!sheets.length)return;
    const index=files.findIndex(candidate=>customerFileFingerprint(candidate)===photoStudioSourceFingerprint);
    if(index<0){setError(t("The source photo is no longer in this batch. Select it again."));setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);return;}
    const generated=sheets.map(sheet=>sheet.file),nextFiles=[...files.slice(0,index),...generated,...files.slice(index+1)];
    const pending=nextFiles.filter(candidate=>!uploadedFileFingerprints.includes(customerFileFingerprint(candidate)));
    const existingBytes=uploadedBatch.reduce((sum,item)=>sum+item.document.sizeBytes,0);
    const capacityError=validateCustomerUploadBatchLimits(uploadedBatch.length,existingBytes,pending.map(candidate=>candidate.size));
    if(capacityError){setError(t("Prepared photo sheets exceed the 50 MB per-file, 20-file or 500 MB batch limit. Reduce the copies or start a new batch."));setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);return;}
    const group=groupNameForFile(files[index]),groups={...fileGroupNames},settings={...fileSettings},photos={...photoSettingsByFileFingerprint},previews={...photoPreviewPagesByFingerprint};
    delete groups[photoStudioSourceFingerprint];delete settings[photoStudioSourceFingerprint];delete photos[photoStudioSourceFingerprint];delete previews[photoStudioSourceFingerprint];
    for(const sheet of sheets){
      const fp=customerFileFingerprint(sheet.file);groups[fp]=group;
      settings[fp]={copies:"1",custom:true,colourMode:"Colour",isDuplex:false,advancedEnabled:false,paperSize:"A4",orientation:"AUTO",pagesPerSheet:1};
      photos[fp]={sizeCode:sheet.sizeCode,photoCount:sheet.photoCount,photosPerPage:sheet.photosPerPage,photoOutputLayout:sheet.photoOutputLayout,photoPaperFinish:sheet.photoPaperFinish};
      const urls=(sheet.previewPages??[]).map(page=>{const url=URL.createObjectURL(page);photoPreviewUrlsRef.current.add(url);return url;});
      if(urls.length)previews[fp]=urls;
    }
    // Photo Studio output inherits the customer's current batch privacy choice.
    // Do not silently enable Private Printing or Standard Printing here.
    setFiles(nextFiles);setSelectedFileIndex(index);setSelectedPreparedPhotoPage(0);setFileGroupNames(groups);setFileSettings(settings);setPhotoSettingsByFileFingerprint(photos);setPhotoPreviewPagesByFingerprint(previews);setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);setError("");
  }
  function openPhotoStudio(candidate:File){
    const fingerprint=customerFileFingerprint(candidate);
    if(uploadedFileFingerprints.includes(fingerprint)){
      setError(t("This photo has already been sent to the shop. The shop can prepare it from the review queue; reopening it here would create a duplicate print request."));
      return;
    }
    if(!photoRatesAvailable){setError(t("Photo printing is not configured at this shop yet. Please ask the shop owner to enable photo pricing."));return;}
    const identified=identifiedCustomerDocumentTypes.get(candidate);
    const mediaType=identified?.mediaType??resolvePhotoSourceMimeType(candidate);
    if(mediaType!=="image/jpeg"&&mediaType!=="image/png"){
      setError(t("The file contents are not a supported JPEG or PNG photo."));
      return;
    }
    const source=candidate.type.split(";")[0].trim().toLowerCase()===mediaType
      ?candidate
      :new File([candidate],candidate.name,{type:mediaType,lastModified:candidate.lastModified});
    setPhotoStudioSource(source);
    setPhotoStudioSourceFingerprint(fingerprint);
    setError("");
  }
  function postCustomerPdfPreviewRequest(token:string,sheetIndex:number,candidate:File):boolean {
    const settings=settingsForFile(candidate);
    const pagesPerSheet=settings.pagesPerSheet??1;
    return postSmartPrintNativeMessage(
      "previewCustomerPdf|"+token+"|"+sheetIndex+"|"+pagesPerSheet+"|"+settings.paperSize+"|"+settings.orientation+"|"+(pagesPerSheetDirection??"RightBottom")
    );
  }
  function openCustomerPdfPreview(candidate:File) {
    const token=smartPrintNativePdfPreviewTokens.get(candidate);
    if(!token){
      setNativePdfPreview(null);
      setError(t("The PDF preview is unavailable. You can still upload the document."));
      return;
    }
    const settings=settingsForFile(candidate);
    const pagesPerSheet=settings.pagesPerSheet??1;
    setNativePdfPreview({
      token,requestedPageIndex:0,pageIndex:0,pageCount:0,pagesPerSheet,
      pagesPerSheetDirection:pagesPerSheetDirection??"RightBottom",
      paperSize:settings.paperSize,orientation:settings.orientation,
      firstSourcePage:0,lastSourcePage:0,image:"",thumbnails:[],loading:true
    });
    if(!postCustomerPdfPreviewRequest(token,0,candidate)){
      setNativePdfPreview({
        token,requestedPageIndex:0,pageIndex:0,pageCount:0,pagesPerSheet,
        pagesPerSheetDirection:pagesPerSheetDirection??"RightBottom",
        paperSize:settings.paperSize,orientation:settings.orientation,
        firstSourcePage:0,lastSourcePage:0,image:"",thumbnails:[],loading:false
      });
      setError(t("The PDF preview is unavailable. You can still upload the document."));
      return;
    }
    setError("");
  }
  function requestCustomerPdfPreviewPage(token:string,sheetIndex:number) {
    const current=nativePdfPreview;
    if(!current||current.token!==token||current.loading||!file)return;
    const target=Math.max(0,Math.min(current.pageCount-1,sheetIndex));
    if(target===current.pageIndex)return;
    setNativePdfPreview({...current,requestedPageIndex:target,loading:true});
    if(!postCustomerPdfPreviewRequest(token,target,file)){
      setNativePdfPreview({...current,loading:false});
      setError(t("The print layout sheet could not be opened."));
    }
  }
  async function chooseFiles(next:File[], nativeGroupNames?:string[], nativePreviewTokens?:Array<string|null>, nativePrintKinds?:Array<string|null>, nativePhotoSettings?:Array<CustomerPhotoSettings|null>) {
    if(busy)return;
    setNotices([]);
    if(batchLockedForPayment){
      setError(t("This batch already has a payment attempt. Start a new batch for additional files."));
      return;
    }
    if(nativePrintKinds?.length===next.length) {
      next.forEach((candidate,index)=>{
        if(nativePrintKinds[index]==="ID_DOCUMENT"||nativePrintKinds[index]==="PHOTO") {
          (candidate as File & {__smartPrintPrintKind?:string}).__smartPrintPrintKind=nativePrintKinds[index]!;
        }
      });
    }
    if(nativePhotoSettings?.length===next.length) {
      next.forEach((candidate,index)=>{
        const settings=nativePhotoSettings[index];
        if(settings&&isValidNativePhotoSettings(settings)) {
          (candidate as File & {__smartPrintPhotoSettings?:CustomerPhotoSettings}).__smartPrintPhotoSettings=settings;
        }
      });
    }
    let nestedSkipped=0;
    const rootFiles=next.filter(candidate=>{
      const relativePath=(candidate as File & {webkitRelativePath?:string}).webkitRelativePath??"";
      const parts=relativePath.split("/").filter(Boolean);
      if(relativePath&&parts.length>2){nestedSkipped++;return false;}
      return true;
    });
    const identified=await Promise.all(rootFiles.map(async candidate=>{
      const contentType=await identifySupportedDocument(candidate);
      if(contentType)identifiedCustomerDocumentTypes.set(candidate,contentType);
      return {candidate,contentType};
    }));
    const supported=identified.filter(item=>item.contentType!==null).map(item=>item.candidate);
    if(nativePreviewTokens?.length===next.length) {
      supported.forEach(candidate=>{
        const token=nativePreviewTokens[next.indexOf(candidate)];
        if(token&&isCustomerPdf(candidate))smartPrintNativePdfPreviewTokens.set(candidate,token);
      });
    }
    const malformedPhotoDocument=supported.some(candidate=>
      (candidate as File & {__smartPrintPrintKind?:string}).__smartPrintPrintKind==="PHOTO"&&!isValidNativePhotoSettings(
        (candidate as File & {__smartPrintPhotoSettings?:CustomerPhotoSettings}).__smartPrintPhotoSettings
      )
    );
    if(malformedPhotoDocument){setError(t("This saved Photo Studio sheet is missing its print details. Refresh the private document library and select it again."));return;}
    const incomingIdDocument=supported.some(isSmartPrintIdDocument);
    const incomingPhotoDocument=supported.some(isSmartPrintPhotoDocument);
    if(incomingIdDocument&&uploadedBatch.some(item=>!item.job.privatePrinting)) {
      setError(t("This batch already contains Standard Printing uploads, so it can no longer be promoted safely. Start a new Private Printing batch for the ID document."));
      return;
    }
    // Selecting an ID document promotes all not-yet-uploaded files in this
    // batch to Private. The API can also promote only unclaimed, untransferred
    // local tickets; it rejects promotion after Standard content has moved.
    if(incomingIdDocument) {
      // ID Studio remains Private Printing only.
      setPrivatePrinting(true);
      setUploadMode("LOCAL");
      setUploadModeManuallySelected(true);
    }
    // Prepared Photo Studio documents use the same current batch privacy choice.
    // Only ID Studio imposes a mandatory Private Printing rule.
    for(const candidate of supported){
      const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim().slice(0,80);
      if(nativeGroup)(candidate as File & {__smartPrintBatchGroup?:string}).__smartPrintBatchGroup=nativeGroup;
    }
    if(incomingPhotoDocument){
      setPhotoSettingsByFileFingerprint(current=>{
        const updated={...current};
        supported.forEach(candidate=>{
          const settings=(candidate as File & {__smartPrintPhotoSettings?:CustomerPhotoSettings}).__smartPrintPhotoSettings;
          if(settings)updated[customerFileFingerprint(candidate)]=settings;
        });
        return updated;
      });
    }
    const unsupportedSkipped=rootFiles.length-supported.length;
    const mismatchedNames=supported.filter(candidate=>{
      const detected=identifiedCustomerDocumentTypes.get(candidate);
      if(!detected)return false;
      const ext=candidate.name.toLowerCase().match(/\.[^.]+$/)?.[0]??"";
      return ext!==detected.extension&&!(detected.extension===".jpg"&&ext===".jpeg");
    });
    if(supported.length===0){setError(t("Choose printable PDF, JPG or PNG files."));return;}
    const merged=new Map(selectedFilesRef.current.map(candidate=>[customerFileFingerprint(candidate),candidate]));
    for(const candidate of supported)merged.set(customerFileFingerprint(candidate),candidate);
    const selected=Array.from(merged.values());
    const alreadyUploaded=new Set(uploadedFileFingerprints);
    const pending=selected.filter(candidate=>!alreadyUploaded.has(customerFileFingerprint(candidate)));
    const existingBytes=uploadedBatch.reduce((sum,item)=>sum+item.document.sizeBytes,0);
    const capacityError=validateCustomerUploadBatchLimits(uploadedBatch.length,existingBytes,pending.map(candidate=>candidate.size));
    if(capacityError){
      if(capacityError.code==="FILE_SIZE"){
        const oversized=pending[capacityError.fileIndex]??supported.find(candidate=>candidate.size<=0||candidate.size>UPLOAD_LIMITS.maxFileBytes);
        setError(oversized?`“${oversized.name}” exceeds the ${UPLOAD_LIMITS.maxFileBytes / (1024 * 1024)} MB per-file limit.`:"Choose a valid, non-empty file.");
      } else if(capacityError.code==="FILE_COUNT"){
        setError(nativeAppMode ? t("{count} files maximum.").replace("{count}", String(UPLOAD_LIMITS.maxBatchFiles)) : t("This batch can contain up to {count} files.").replace("{count}", String(UPLOAD_LIMITS.maxBatchFiles)));
      } else {
        setError(nativeAppMode ? t("{size} MB batch limit.").replace("{size}", String(UPLOAD_LIMITS.maxBatchBytes / (1024 * 1024))) : t("This batch can contain up to {size} MB in total.").replace("{size}", String(UPLOAD_LIMITS.maxBatchBytes / (1024 * 1024))));
      }
      return;
    }
    setFileGroupNames(current=>{
      const updated={...current};
      for(const candidate of supported){
        const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim();
        updated[customerFileFingerprint(candidate)]=nativeGroup||customerBatchGroupName(candidate);
      }
      return updated;
    });
    setGroupSettings(current=>{
      const updated={...current};
      for(const candidate of supported) {
        const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim();
        const group=nativeGroup||customerBatchGroupName(candidate);
        if(!updated[group])updated[group]={copies:"1",colourMode:"Mono",isDuplex:false,advancedEnabled:false,paperSize:"A4",orientation:"AUTO",pagesPerSheet:1};
      }
      return updated;
    });
    setFileSettings(current=>{
      const updated={...current};
      for(const candidate of supported) {
        const key=customerFileFingerprint(candidate);
        if(!updated[key])updated[key]={...defaultsForGroup(fileGroupNames[key]??customerBatchGroupName(candidate)),custom:false};
      }
      return updated;
    });
    selectedFilesRef.current=selected;setFiles(selected);setSelectedFileIndex(0);
    const selectionNotices:Array<{kind:"info"|"warning";message:string}>=[];
    if(nestedSkipped||unsupportedSkipped) {
      const skippedMessages:string[]=[];
      if(nestedSkipped)skippedMessages.push("Files inside nested folders aren't included.");
      if(unsupportedSkipped)skippedMessages.push("Only PDF, JPG and PNG files are supported.");
      selectionNotices.push({kind:"warning",message:skippedMessages.join(" ")});
    }
    if(mismatchedNames.length)selectionNotices.push({
      kind:"info",
      message:`${mismatchedNames.length} file(s) were identified by content. Original filenames were kept.`
    });
    setError("");
    setNotices(selectionNotices);
  }

  function removeSelectedFile(fingerprint: string) {
    if (busy) return;
    const index = files.findIndex(candidate => customerFileFingerprint(candidate) === fingerprint);
    if (index < 0) return;
    const nextFiles = files.filter(candidate => customerFileFingerprint(candidate) !== fingerprint);
    if(nextFiles.length===0&&nativeAppMode)postSmartPrintNativeMessage("releaseStudioUploadFiles");
    setFiles(nextFiles);
    setSelectedFileIndex(current => current === index
      ? Math.min(index, Math.max(0, nextFiles.length - 1))
      : current > index ? current - 1 : current);
    setFileGroupNames(current => {
      const next = { ...current };
      delete next[fingerprint];
      return next;
    });
    setFileSettings(current => {
      const next = { ...current };
      delete next[fingerprint];
      return next;
    });
    setPhotoSettingsByFileFingerprint(current=>{const next={...current};delete next[fingerprint];return next;});
    setError("");
    setNotices([]);
  }

  const pendingFilesCount=files.filter(candidate=>!uploadedFileFingerprints.includes(customerFileFingerprint(candidate))).length;
  const uploadConfirmedCount=uploadProgress?.files.filter(file=>file.status==="COMPLETED").length??0;
  const uploadFailedCount=uploadProgress?.files.filter(file=>file.status==="FAILED").length??0;
  const uploadWaitingCount=uploadProgress?.files.filter(file=>file.status==="WAITING_FOR_SHOP").length??0;
  const uploadGroups=Array.from(new Set(files.map(candidate=>groupNameForFile(candidate)))).map(name=>({
    name, files:files.filter(candidate=>groupNameForFile(candidate)===name)
  }));
  const quoteItems=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
  const batchLockedForPayment=(batchPayment!==null&&batchPayment.status!=="REJECTED")||
    (payment!==null&&!payment.batchPayment&&["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED","PARTIALLY_REFUNDED"].includes(payment.status));
  const statusForBatchItem=(item:UploadedPrintJob)=>
    uploaded?.job.id===item.job.id&&jobStatus?.id===item.job.id&&jobStatus.status
      ? jobStatus.status
      : batchStatuses[item.job.id]?.status??item.job.status;
  const quoteExpiredForBatchItem=(item:UploadedPrintJob)=>{
    const status=statusForBatchItem(item);
    if(status==="QUOTE_EXPIRED")return true;
    const expiry=quotesByJobId[item.job.id]?.quoteExpiresUtc??batchStatuses[item.job.id]?.quoteExpiresUtc;
    if(!expiry)return ["AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(status);
    const expiresAt=Date.parse(expiry);
    return !Number.isFinite(expiresAt)||expiresAt<=Date.now();
  };
  const batchCheckoutStatuses=new Set([
    "REJECTED","REFUNDED","AWAITING_PAYMENT","PAYMENT_REJECTED",
    "AWAITING_CUSTOMER_PAYMENT","PAYMENT_REPORTED","AWAITING_COUNTER_PAYMENT",
    "PAID_AUTHORISED","LOCAL_QUEUE_CLAIMED","LOCAL_QUEUE_READY","PRINT_AUTHORISED",
    "PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING","OUTPUT_UNCONFIRMED","COMPLETED"
  ]);
  const batchCheckoutReady=quoteItems.length>0&&quoteItems.every(item=>batchCheckoutStatuses.has(statusForBatchItem(item)));
  const batchPayableItems=quoteItems.filter(item=>["AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(statusForBatchItem(item)));
  const batchPayableTotal=batchPayableItems.reduce((total,item)=>
    total+(batchStatuses[item.job.id]?.quotedAmountMinor??quotesByJobId[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor??0),0);
  const hasLegacyIndividualPayment=quoteItems.some(item=>{
    const itemPayment=uploaded?.job.id===item.job.id&&payment
      ?payment
      :batchStatuses[item.job.id]?.payment??null;
    return Boolean(itemPayment&&!itemPayment.batchPayment&&
      ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED","PARTIALLY_REFUNDED"].includes(itemPayment.status));
  });
  const hasSelectedLegacyIndividualPayment=payment!==null&&!payment.batchPayment&&
    ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED","PARTIALLY_REFUNDED"].includes(payment.status);
  const batchPaymentStarted=batchPayment!==null&&batchPayment.status!=="REJECTED";
  const acceptedQuoteExpired=shouldBlockBatchCheckoutForExpiredQuote(
    batchPayment?.status,
    quoteItems.some(item=>!["REJECTED","REFUNDED"].includes(statusForBatchItem(item))&&quoteExpiredForBatchItem(item))
  );
  const batchCheckoutVisible=Boolean(batchId)&&!hasLegacyIndividualPayment&&
    (batchPaymentStarted||(!acceptedQuoteExpired&&batchCheckoutReady&&batchPayableItems.length>0));
  const batchCheckoutUsesPayment=batchPayment!==null&&batchPayment.status!=="REJECTED";
  const legacyIndividualPayment=!batchId||hasSelectedLegacyIndividualPayment;
  const batchCheckoutAmount=batchCheckoutUsesPayment?batchPayment!.amountMinor:batchPayableTotal;
  const batchCheckoutFileCount=batchCheckoutUsesPayment?batchPayment!.fileCount:batchPayableItems.length;
  const batchCheckoutCurrency=batchCheckoutUsesPayment?batchPayment!.currency:(batchPayableItems[0]?.job.currency??"INR");
  const customerPaymentForBatchItem=(item:UploadedPrintJob)=>
    uploaded?.job.id===item.job.id&&payment
      ?payment
      :batchStatuses[item.job.id]?.payment??null;
  const batchPaymentItems=batchCheckoutUsesPayment
    ?quoteItems.filter(item=>customerPaymentForBatchItem(item)?.batchPaymentId===batchPayment!.id)
    :batchPayableItems;
  const batchCheckoutLineItems=batchCheckoutUsesPayment?batchPaymentItems:batchPayableItems;
  const batchPaymentAllocatedTotal=batchPaymentItems.reduce((total,item)=>
    total+(customerPaymentForBatchItem(item)?.amountMinor??0),0);
  const batchPaymentRefundedTotal=batchPaymentItems.reduce((total,item)=>
    total+(customerPaymentForBatchItem(item)?.refundedAmountMinor??0),0);
  const batchPaymentAllocationComplete=batchCheckoutUsesPayment&&
    batchPaymentItems.length===batchPayment!.fileCount&&
    batchPaymentAllocatedTotal===batchPayment!.amountMinor;
  const batchCheckoutHeading=batchCheckoutUsesPayment
    ?batchPayment!.status==="VERIFIED"?"BATCH PAYMENT VERIFIED"
      :batchPayment!.status==="CUSTOMER_REPORTED"?"PAYMENT REFERENCE SUBMITTED"
      :batchPayment!.status==="CASH_AWAITING_PAYMENT"?"PAY AT SHOP COUNTER"
      :"PAYMENT FOR ACCEPTED FILES"
    :"PAY FOR ACCEPTED FILES";
  const quoteRows=quoteItems.map(item=>({
    item,
    quote:quotesByJobId[item.job.id],
    excluded:["REJECTED","REFUNDED"].includes(statusForBatchItem(item))
  }));
  const quoteEntries=quoteRows.filter(entry=>!!entry.quote&&!quoteExpiredForBatchItem(entry.item));
  const acceptedQuoteEntries=quoteEntries.filter(entry=>!entry.excluded);
  const batchQuoteTotal=acceptedQuoteEntries.reduce((total,entry)=>
    total+(entry.quote?.quotedAmountMinor??0),0);
  const batchQuoteNeedsRefresh=quoteRows.filter(entry=>
    !entry.excluded&&!shouldBlockQuoteRefreshForStatus(statusForBatchItem(entry.item))&&
    (!entry.quote||quoteExpiredForBatchItem(entry.item))
  ).length;
  const batchQuoteCurrency=quoteEntries[0]?.quote?.currency??quoteItems[0]?.job.currency??"INR";
  const hasAcceptedQuoteLocked=quoteRows.some(entry=>
    !entry.excluded&&!!entry.quote&&quoteExpiredForBatchItem(entry.item)&&
    shouldBlockQuoteRefreshForStatus(statusForBatchItem(entry.item))
  );
  const expiredQuoteRefreshKey=quoteRows
    .filter(entry=>{
      const status=statusForBatchItem(entry.item);
      return !entry.excluded&&
        quoteExpiredForBatchItem(entry.item)&&
        ["QUOTE_EXPIRED","AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(status)&&
        !shouldBlockQuoteRefreshForStatus(status);
    })
    .map(entry=>entry.item.job.id)
    .sort()
    .join("|");
  const automaticQuoteRefreshAttemptRef=useRef("");
  useEffect(()=>{
    if(!expiredQuoteRefreshKey){
      automaticQuoteRefreshAttemptRef.current="";
      return;
    }
    if(busy||batchLockedForPayment||hasAcceptedQuoteLocked||
      automaticQuoteRefreshAttemptRef.current===expiredQuoteRefreshKey)return;
    // Renew an expired quote in the background. The API preserves the current
    // approval stage when the price is unchanged and requires fresh acceptance
    // when it changes; payment creation remains a separate customer action.
    automaticQuoteRefreshAttemptRef.current=expiredQuoteRefreshKey;
    void getQuote();
  },[expiredQuoteRefreshKey,busy,batchLockedForPayment,hasAcceptedQuoteLocked]);
  const batchQuoteTotalDisplay=acceptedQuoteEntries.length>0
    ?money(batchQuoteTotal,batchQuoteCurrency)
    :batchQuoteNeedsRefresh>0||hasAcceptedQuoteLocked?"—"
      :quoteRows.some(entry=>!entry.excluded)?"Pending":money(0,batchQuoteCurrency);
  const acceptedBatchRows=quoteRows.filter(entry=>!entry.excluded);
  const batchQuoteCounts={
    documents:acceptedBatchRows.filter(entry=>normalizedCustomerPrintKind(entry.quote?.printKind??printKindsByJobId[entry.item.job.id]??entry.item.job.printKind)==="DOCUMENT").length,
    idCards:acceptedBatchRows.filter(entry=>normalizedCustomerPrintKind(entry.quote?.printKind??printKindsByJobId[entry.item.job.id]??entry.item.job.printKind)==="ID_DOCUMENT").length,
    photos:acceptedBatchRows.filter(entry=>normalizedCustomerPrintKind(entry.quote?.printKind??printKindsByJobId[entry.item.job.id]??entry.item.job.printKind)==="PHOTO").length,
    excluded:quoteRows.filter(entry=>entry.excluded).length
  };
  const batchQuoteSummary=acceptedQuoteEntries.length>0
    ?acceptedQuoteEntries.length+" "+(acceptedQuoteEntries.length===1?"file has":"files have")+" a current quote."
    :"No accepted files have a current quote yet.";
  const batchQuoteBreakdown=calculateBatchQuoteBreakdown(acceptedQuoteEntries.map(entry=>({
    printKind:printKindsByJobId[entry.item.job.id]==="ID_DOCUMENT"
      ?"ID_DOCUMENT"
      :normalizedCustomerPrintKind(entry.quote!.printKind??printKindsByJobId[entry.item.job.id]??entry.item.job.printKind),
    pageCount:entry.quote!.pageCount,
    copies:entry.quote!.copies,
    colourMode:entry.quote!.colourMode==="Colour"?"Colour":"Mono",
    photoCount:entry.quote!.photoCount??0,
    pagesPerSheet:entry.quote!.pagesPerSheet??1,
    quotedAmountMinor:entry.quote!.quotedAmountMinor
  })));
  const quoteEligibleItems=quoteItems.filter(item=>!["REJECTED","REFUNDED"].includes(statusForBatchItem(item)));
  const canSendBatch=quoteEligibleItems.length>0&&
    acceptedQuoteEntries.length===quoteEligibleItems.length&&
    acceptedQuoteEntries.some(entry=>entry.quote?.status==="AWAITING_CUSTOMER_APPROVAL");
  const canCalculateBatch=!batchLockedForPayment&&!batchPaymentStarted&&quoteItems.some(item=>{
    const status=statusForBatchItem(item);
    if(shouldBlockQuoteRefreshForStatus(status))return false;
    if(quoteExpiredForBatchItem(item))return true;
    if(quotesByJobId[item.job.id]&&quotesByJobId[item.job.id].status!=="QUOTE_EXPIRED")return false;
    return !quotesByJobId[item.job.id]||quotesByJobId[item.job.id].status==="QUOTE_EXPIRED"||
      ["AWAITING_CONFIGURATION","UPLOADED","Uploaded"].includes(status);
  });

  const photoPriceMap={"35x45":photoPricing?.photo35x45Minor??0,"25x35":photoPricing?.photo25x35Minor??0,"2x2":photoPricing?.photo2x2Minor??0,"4x6":photoPricing?.photo4x6Minor??0};
  const photoAlreadyUploaded=Boolean(file&&uploadedFileFingerprints.includes(customerFileFingerprint(file)));
  return <>{photoStudioSource&&<PhotoStudio source={photoStudioSource} mode="customer" dark={dark} photoPrices={photoPriceMap} onCancel={()=>{setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);}} onApply={applyPhotoSheets}/>}<section className="customer-print-flow">
    {previousItems.length>0&&<section className="customer-previous-order" aria-label={t("Previous print request")}>
      <div className="customer-previous-order-heading"><div><span>{t("PREVIOUS REQUEST")}</span><strong>{t("From your earlier QR session")}</strong></div><span className="customer-previous-order-readonly">{t("Read only")}</span></div>
      <p>{t("This request is separate from your current session. Its status is restored for reference only; printing, payment and file changes are not available here.")}</p>
      <div className="customer-previous-order-list">
        {previousItems.map(item=>{
          const recovered=previousHistoryStatuses[item.job.id];
          const status=recovered?.status??(previousHistoryLoading?"Refreshing status":previousHistoryUnavailable?"Status unavailable · ask the shop before resubmitting":customerStatusLabel(item.job.status??"UPLOADED"));
          return <div className="customer-previous-order-item" key={item.job.id}>
            <span><FileText size={14}/></span>
            <div><strong>{resolveCustomerDocumentName(item,storedCustomerPrivateFileNames(previousDraft))}</strong><small>{item.job.jobNumber} · {status}</small></div>
          </div>;
        })}
      </div>
      {previousHistoryUnavailable&&<CustomerInlineNotice kind="warning" title={t("Earlier status unavailable")}>{t("Check with the shop before uploading the same document again.")}</CustomerInlineNotice>}
      <small className="customer-previous-order-footnote">{t("Your earlier order remains attached to its original shop session. Document contents stay private; this panel restores status metadata only.")}</small>
    </section>}
    <div className="customer-flow-heading"><div><span>{t("STEP 1")}</span><h2>{nativeAppMode?t("Add files"):t("Add documents or images")}</h2></div><span className="upload-limit">PDF/JPG/PNG · Up to {UPLOAD_LIMITS.maxBatchFiles} files / {UPLOAD_LIMITS.maxBatchBytes / (1024 * 1024)} MB</span></div>
    <section className="customer-privacy-choice" aria-label={t("Document privacy choice")}>
      <div className="customer-privacy-choice-heading"><div><span>{nativeAppMode?t("PRIVACY"):t("CHOOSE YOUR PRIVACY")}</span><strong>{t("Choose how files are handled")}</strong></div><small>{t("Applies to all files")}</small></div>
      <div className="customer-privacy-choice-grid">
        <button type="button" aria-pressed={privatePrinting} disabled={busy||uploadedBatch.length>0||!canOfferPrivatePrinting} className={privatePrinting?"customer-privacy-option private selected":"customer-privacy-option private"} onClick={()=>{setPrivacyChoiceManuallySelected(true);setPrivatePrinting(true);setUploadMode("LOCAL");setUploadModeManuallySelected(true);}}>
          <span className="customer-privacy-option-icon"><ShieldCheck size={22}/></span><span className="customer-privacy-option-copy"><strong>{t("Private Printing")}</strong><small>{privateVirtualTestMode&&privatePrintingVirtualOutputEnabled?t("Encrypted · development test PDF only"):t("Encrypted · shop-side source erased after verified output")}</small></span><span className="customer-privacy-option-state">{privatePrinting?t("Selected"):uploadedBatch.length>0?t("Unavailable for this batch"):canOfferPrivatePrinting?t("Choose private"):t("Shop setup needed")}</span>
        </button>
        <button type="button" aria-pressed={!privatePrinting} disabled={busy||uploadedBatch.length>0} className={!privatePrinting?"customer-privacy-option standard selected":"customer-privacy-option standard"} onClick={()=>{setPrivacyChoiceManuallySelected(true);setPrivatePrinting(false);}}>
          <span className="customer-privacy-option-icon"><FileText size={22}/></span><span className="customer-privacy-option-copy"><strong>{t("Standard Printing")}</strong><small>{t("Normal print queue. Files are deleted 72 hours after confirmed output.")}</small></span><span className="customer-privacy-option-state">{privatePrinting?t("Choose standard"):t("Selected")}</span>
        </button>
      </div>
      {uploadedBatch.some(item=>!item.job.privatePrinting)&&<CustomerInlineNotice kind="info" title={t("Private mode is locked for this batch")}>{t("Standard files have already been uploaded. To protect every document with Private Printing, start a new Private batch before uploading its files.")}</CustomerInlineNotice>}
      <div className="customer-upload-local-note"><InfoTooltip label={t("Private Printing details")}><span>{privateVirtualTestMode&&privatePrintingVirtualOutputEnabled?t("Encrypted on your device. The protected test PDF stays on this computer for up to 24 hours; it is not a physical print. Your original stays on your device."):t("Encrypted on your device and decrypted only by the paired Windows Agent. The shop-side copy is erased after confirmed output. Your original stays on your device.")}</span></InfoTooltip> {t("Private Printing details")} <InfoTooltip label={t("Standard Printing details")}><span>{t("Uses the shop's normal print queue. The shop-side copy is deleted 72 hours after confirmed output. Your original stays on your device.")}</span></InfoTooltip> {t("Standard Printing details")}</div>
      {!canOfferPrivatePrinting&&<CustomerInlineNotice kind="info" title={t("Private Printing unavailable")}>{!hasAssignedPrinter
        ?t("The shop needs to assign an active printer.")
        :!localAgent?.agentDeviceId||!localAgent.documentEncryptionPublicKey
          ?t("The paired shop computer must be online and publish its encryption key.")
          :!privateQueueReady
            ?t("The assigned printer queue must be Ready or Busy.")
            :privateVirtualTestMode
              ?t("The Company Main Admin must enable virtual test output.")
               : t("The shop needs an eligible physical printer.")}</CustomerInlineNotice>}
      {privatePrinting&&<div className="customer-private-confirmation"><ShieldCheck size={16}/><span><strong>{t("Private Printing is on.")}</strong></span></div>}
      {files.some(isSmartPrintPhotoDocument)&&<div className="customer-private-confirmation" role="status">
        <ShieldCheck size={16}/>
        <span>{privatePrinting
          ? <><strong>{t("Private Printing selected.")}</strong> <InfoTooltip label={t("Change printing method")}><span>{t("You can still choose Standard Printing above before uploading.")}</span></InfoTooltip></>
          : <strong>{t("Standard Printing selected.")}</strong>}</span>
      </div>}
    </section>
    {privatePrinting
      ? <div className="customer-private-transfer-note"><Lock size={16}/><span>{privateVirtualTestMode&&privatePrintingVirtualOutputEnabled?t("End-to-end encrypted · development test PDF only · not physical fulfilment"):t("End-to-end encrypted · physical printer required")}</span></div>
      : <div className="customer-upload-modes">
      <div className="customer-upload-mode-heading"><strong>{t("How your files reach the shop")}</strong><small>{t("Choose a connection method. Your privacy setting above stays the same.")}</small></div>
      {localAgent?.endpoints.length
        ? <button type="button" aria-pressed={uploadMode==="LOCAL"} className={uploadMode==="LOCAL"?"customer-upload-mode selected":"customer-upload-mode"} disabled={busy} onClick={()=>{setUploadModeManuallySelected(true);setUploadMode("LOCAL");}}><strong>{t("Shop Wi-Fi · local transfer")}</strong><small>{t("Direct transfer over shop Wi-Fi")}</small></button>
        : <button type="button" className="customer-upload-mode customer-upload-mode-unavailable" disabled aria-disabled="true"><strong>{t("Shop Wi-Fi · local transfer")}</strong><small>{t("Unavailable")}</small></button>}
      {secureQueueFallbackEnabled&&<button type="button" aria-pressed={uploadMode==="CLOUD"} className={uploadMode==="CLOUD"?"customer-upload-mode selected":"customer-upload-mode"} disabled={busy} onClick={()=>{setUploadModeManuallySelected(true);setUploadMode("CLOUD");}}><strong>{cloudAssistedPrintingEnabled?"Cloud-assisted · HTTPS":"Secure queue · HTTPS"}</strong><small>{t("Encrypted online transfer · print when ready")}</small></button>}
    </div>}
    {customerUploadUnavailable&&<CustomerInlineNotice kind="warning" title={t("Secure upload unavailable")}>{t("No file was uploaded. Ask the shop to reconnect its print service, then retry.")}</CustomerInlineNotice>}
    {privatePrinting&&!localReady&&canOfferPrivatePrinting&&<CustomerInlineNotice kind="info" title={t("Encrypted online relay")}>{t("No shop Wi-Fi is needed. SmartPrint uses its encrypted online relay.")}</CustomerInlineNotice>}
    {batchLockedForPayment&&<CustomerInlineNotice kind="warning" title={t("This batch is locked")}>{t("Payment has started. Add more documents in a new batch.")}</CustomerInlineNotice>}
    <div className="customer-upload-picker-grid" aria-label={t("Choose documents or folders")}
      onDragOver={event=>{if(!busy&&!batchLockedForPayment)event.preventDefault();}}
      onDrop={event=>{event.preventDefault();if(busy||batchLockedForPayment)return;void chooseFiles(Array.from(event.dataTransfer.files||[]));}}>
      <label className="customer-upload-zone">
        <input type="file" multiple disabled={busy||batchLockedForPayment} onChange={event=>{const selected=Array.from(event.currentTarget.files||[]);void chooseFiles(selected,consumeSmartPrintNativePickerGroups(),consumeSmartPrintNativePdfPreviews(),consumeSmartPrintNativePrintKinds(),consumeSmartPrintNativePhotoSettings());event.currentTarget.value="";}}/>
        <span className="customer-upload-icon"><FileUp size={21}/></span>
        <strong>{nativeAppMode?t("Select files"):t("Select documents or images")}</strong>
        <small>{t("PDF, JPG or PNG")}</small>
      </label>
      <label className="customer-upload-zone folder-upload-zone">
        <input type="file" multiple disabled={busy||batchLockedForPayment} accept={nativeAppMode?"application/x-smartprint-folder-selection":undefined} {...({webkitdirectory:"true",directory:"true"} as any)} onChange={event=>{const selected=Array.from(event.currentTarget.files||[]);void chooseFiles(selected,consumeSmartPrintNativePickerGroups(),consumeSmartPrintNativePdfPreviews(),consumeSmartPrintNativePrintKinds(),consumeSmartPrintNativePhotoSettings());event.currentTarget.value="";}}/>
        <span className="customer-upload-icon"><FolderOpen size={21}/></span>
        <strong>{nativeAppMode?t("Choose folders"):t("Add a folder")}</strong>
        <small>{nativeAppMode?t("Select multiple folders"):t("One folder at a time")}</small>
      </label>
    </div>
    {notices.length>0&&<div className="customer-inline-notices" aria-label={t("File selection messages")}>
      {notices.map((item,index)=><CustomerInlineNotice
        key={item.kind+"-"+index}
        kind={item.kind}
        title={item.kind==="warning"?t("Some files were skipped"):t("File details")}
      >{item.message}</CustomerInlineNotice>)}
    </div>}
    {files.length>0&&<div className="customer-selected-files" aria-label={t("Selected files and print settings")}>
      <div className="customer-selected-files-head"><strong>{files.length} {t("selected")}</strong><span>{formatBytes(files.reduce((total,item)=>total+item.size,0))} {t("in this selection")}</span></div>
      <div className="customer-batch-groups">{uploadGroups.map(group=>{
        const defaults=defaultsForGroup(group.name);
        const isIdStudioGroup=group.files.length>0&&group.files.every(isSmartPrintIdDocument);
        return <section className="customer-folder-group" key={group.name}>
          <div className="customer-folder-group-head">
            <div className="customer-folder-title"><FolderOpen size={16}/><span><strong>{group.name}</strong><small>{group.files.length} {t("file(s)")}</small></span></div>
            {isIdStudioGroup
              ? <div className="customer-folder-defaults">
                <div><span>{t("ID sheet")}</span><strong className="customer-paper-default">{t("A4 portrait · 1 copy · Single-sided")}</strong></div>
                <div><span>{t("Print mode")}</span><div className="customer-choice-row">
                  <button type="button" aria-pressed={defaults.colourMode==="Mono"} className={defaults.colourMode==="Mono"?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Mono"}}))}>{t("B&W")}</button>
                  <button type="button" aria-pressed={defaults.colourMode==="Colour"} className={defaults.colourMode==="Colour"?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Colour"}}))}>{t("Colour")}</button>
                </div></div>
              </div>
              : <div className="customer-folder-defaults">
              <div><span>{t("Colour")}</span><div className="customer-choice-row">
                <button type="button" aria-pressed={defaults.colourMode==="Mono"} className={defaults.colourMode==="Mono"?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Mono"}}))}>{t("B&W")}</button>
                <button type="button" aria-pressed={defaults.colourMode==="Colour"} className={defaults.colourMode==="Colour"?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Colour"}}))}>{t("Colour")}</button>
              </div></div>
              <div><span>{t("Sides")}</span><div className="customer-choice-row">
                <button type="button" aria-pressed={!defaults.isDuplex} className={!defaults.isDuplex?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,isDuplex:false}}))}>{t("Single")}</button>
                <button type="button" aria-pressed={defaults.isDuplex} className={defaults.isDuplex?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,isDuplex:true}}))}>{t("Double")}</button>
              </div></div>
              <label className="customer-folder-copies"><span>{t("Copies · all files")}</span><input type="text" disabled={busy} inputMode="numeric" pattern="[0-9]*" maxLength={3} value={defaults.copies}
                 onChange={event=>{
                   const copies=event.currentTarget.value.replace(/\D/g,"").slice(0,3);
                   setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),copies}}));
                 }}
                 onBlur={event=>{
                   const copies=normalizeCustomerCopies(event.currentTarget.value);
                   setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),copies}}));
                 }}/></label>
              <div><span>{t("Paper")}</span><strong className="customer-paper-default">{defaults.advancedEnabled?defaults.paperSize:"A4"}</strong></div>
              <label className="customer-advanced-toggle"><input type="checkbox" disabled={busy} checked={defaults.advancedEnabled} onChange={event=>{
                 const advancedEnabled=event.currentTarget.checked;
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),advancedEnabled}}));
               }}/><span>{t("Advanced settings")}</span></label>
            </div>}
            {!isIdStudioGroup&&defaults.advancedEnabled&&<div className="customer-file-overrides customer-folder-advanced">
              <label><span>{t("Paper size")}</span><select disabled={busy} value={defaults.paperSize} onChange={event=>{
                 const paperSize=event.currentTarget.value as CustomerGroupSettings["paperSize"];
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),paperSize}}));
               }}>
                <option value="A4">A4</option><option value="A3">A3</option><option value="A5">A5</option><option value="LETTER">{t("Letter")}</option><option value="LEGAL">{t("Legal")}</option>
              </select></label>
              <label><span>{t("Orientation")}</span><select disabled={busy} value={defaults.orientation} onChange={event=>{
                 const orientation=event.currentTarget.value as CustomerGroupSettings["orientation"];
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),orientation}}));
               }}>
                <option value="AUTO">{t("Auto · follow document")}</option><option value="PORTRAIT">{t("Portrait")}</option><option value="LANDSCAPE">{t("Landscape")}</option>
              </select></label>
              {nupOptions.length>0&&group.files.some(candidate=>
                isCustomerPdf(candidate)&&!isSmartPrintIdDocument(candidate)&&!isSmartPrintPhotoDocument(candidate)
              )&&<label><span>{t("Pages per sheet")}</span><select disabled={busy} value={defaults.pagesPerSheet??1} onChange={event=>{
                 const pagesPerSheet=Number(event.currentTarget.value)||1;
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),pagesPerSheet}}));
               }}>
                <option value={1}>{t("1 page per sheet")}</option>
                {nupOptions.map(value=><option key={value} value={value}>{t("{count} pages per sheet",{count:value})}</option>)}
              </select></label>}
              {nupOptions.length===0&&group.files.some(candidate=>
                isCustomerPdf(candidate)&&!isSmartPrintIdDocument(candidate)&&!isSmartPrintPhotoDocument(candidate)
              )&&<p className="customer-advanced-help">{t("Multiple pages per sheet is unavailable. {reason}",{reason:nupUnavailableReason})}</p>}
            </div>}
          </div>
          <div className="customer-folder-file-list">{group.files.map(selected=>{
            const fingerprint=customerFileFingerprint(selected);
            const savedOwn=fileSettings[fingerprint]??{...defaults,custom:false};
            const own={...defaults,...savedOwn};
            const index=files.findIndex(candidate=>customerFileFingerprint(candidate)===fingerprint);
            return <div className={selectedFileIndex===index?"customer-folder-file-row selected":"customer-folder-file-row"} key={fingerprint}>
              <button type="button" aria-pressed={selectedFileIndex===index} title={selected.name} className="customer-folder-file-name" disabled={busy} onClick={()=>setSelectedFileIndex(index)}><FileText size={15}/><span><strong>{selected.name}</strong><small>{formatBytes(selected.size)}</small></span><PrintKindTag printKind={photoSettingsByFileFingerprint[fingerprint]?"PHOTO":isSmartPrintIdDocument(selected)?"ID_DOCUMENT":"DOCUMENT"}/></button>
              {!isSmartPrintIdDocument(selected)&&<button type="button" aria-pressed={own.custom} aria-label={t("Use {setting} settings for {name}",{setting:own.custom?"folder":"custom",name:selected.name})} className={own.custom?"customer-file-custom active":"customer-file-custom"} disabled={busy} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...defaults,...(current[fingerprint]??{}),custom:!own.custom,...(!own.custom?{copies:defaults.copies,colourMode:defaults.colourMode,isDuplex:defaults.isDuplex,advancedEnabled:defaults.advancedEnabled,paperSize:defaults.paperSize,orientation:defaults.orientation,pagesPerSheet:defaults.pagesPerSheet}:{})}}))}>{own.custom?t("Custom"):t("Folder")}</button>}
              <button type="button" className="customer-file-remove" aria-label={t("Remove {name} from this selection",{name:selected.name})} title={t("Remove from this selection")} disabled={busy} onClick={()=>removeSelectedFile(fingerprint)}><X size={13}/><span>{t("Remove")}</span></button>
              {own.custom&&!isSmartPrintIdDocument(selected)&&<div className="customer-file-overrides">
                <label className="customer-folder-copies"><span>{t("Copies")}</span><input type="text" disabled={busy} inputMode="numeric" pattern="[0-9]*" maxLength={3} value={own.copies}
                   onChange={event=>{
                     const copies=event.currentTarget.value.replace(/\D/g,"").slice(0,3);
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,copies}}));
                   }}
                   onBlur={event=>{
                     const copies=normalizeCustomerCopies(event.currentTarget.value);
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,copies}}));
                   }}/></label>
                <div className="customer-choice-row">
                  <button type="button" aria-pressed={own.colourMode==="Mono"} className={own.colourMode==="Mono"?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,colourMode:"Mono"}}))}>{t("B&W")}</button>
                  <button type="button" aria-pressed={own.colourMode==="Colour"} className={own.colourMode==="Colour"?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,colourMode:"Colour"}}))}>{t("Colour")}</button>
                </div>
                <div className="customer-choice-row">
                  <button type="button" aria-pressed={!own.isDuplex} className={!own.isDuplex?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,isDuplex:false}}))}>{t("Single")}</button>
                  <button type="button" aria-pressed={own.isDuplex} className={own.isDuplex?"customer-choice selected":"customer-choice"} disabled={busy} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,isDuplex:true}}))}>{t("Double")}</button>
                </div>
                <div><span>{t("Paper")}</span><strong className="customer-paper-default">{own.advancedEnabled?own.paperSize:"A4"}</strong></div>
                <label className="customer-advanced-toggle"><input type="checkbox" disabled={busy} checked={own.advancedEnabled} onChange={event=>{
                   const advancedEnabled=event.currentTarget.checked;
                   setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,advancedEnabled}}));
                 }}/><span>{t("Advanced settings")}</span></label>
                {own.advancedEnabled&&<div className="customer-file-overrides customer-folder-advanced">
                  <label><span>{t("Paper size")}</span><select disabled={busy} value={own.paperSize} onChange={event=>{
                     const paperSize=event.currentTarget.value as CustomerFileSettings["paperSize"];
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,paperSize}}));
                   }}>
                    <option value="A4">A4</option><option value="A3">A3</option><option value="A5">A5</option><option value="LETTER">{t("Letter")}</option><option value="LEGAL">{t("Legal")}</option>
                  </select></label>
                  <label><span>{t("Orientation")}</span><select disabled={busy} value={own.orientation} onChange={event=>{
                     const orientation=event.currentTarget.value as CustomerFileSettings["orientation"];
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,orientation}}));
                   }}>
                    <option value="AUTO">{t("Auto · follow document")}</option><option value="PORTRAIT">{t("Portrait")}</option><option value="LANDSCAPE">{t("Landscape")}</option>
                  </select></label>
                  {nupOptions.length>0&&isCustomerPdf(selected)&&!isSmartPrintIdDocument(selected)&&!isSmartPrintPhotoDocument(selected)&&<label><span>{t("Pages per sheet")}</span><select disabled={busy} value={own.pagesPerSheet??1} onChange={event=>{
                     const pagesPerSheet=Number(event.currentTarget.value)||1;
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,pagesPerSheet}}));
                   }}>
                    <option value={1}>{t("1 page per sheet")}</option>
                    {nupOptions.map(value=><option key={value} value={value}>{value} pages per sheet</option>)}
                  </select></label>}
                  {nupOptions.length===0&&isCustomerPdf(selected)&&!isSmartPrintIdDocument(selected)&&!isSmartPrintPhotoDocument(selected)&&<p className="customer-advanced-help">Multiple pages per sheet is unavailable. {nupUnavailableReason}</p>}
                </div>}
              </div>}
            </div>;
          })}</div>
        </section>;
      })}</div>
    </div>}
    {files.length>0&&!privatePrinting&&<label className="customer-batch-name-field">
  <span>{t("Give this batch a name")} <small>{t("Optional · shown to the shop")}</small></span>
  <input type="text" value={batchName} maxLength={50}
    placeholder={t("e.g. Manjeet - College Notes")}
    disabled={busy||uploadedBatch.length>0}
    onChange={event=>setBatchName(event.target.value)}
  />
</label>}
{files.length>0&&<small className="customer-upload-local-note">{t("Folder defaults apply.")} <InfoTooltip label={t("Folder settings")}><span>{t("Settings apply to every file in this folder. Use Custom on a file to change its settings.")}</span></InfoTooltip></small>}
    {files.length>0&&!privatePrinting&&uploadMode==="LOCAL"&&localAgent&&(localReady
      ?<CustomerInlineNotice kind="warning" title={t("Local transfer is not encrypted")}>{secureQueueFallbackEnabled
          ?"Shop Wi-Fi uses HTTP. Choose HTTPS or Private Printing for an encrypted transfer."
          :canOfferPrivatePrinting
            ?"Shop Wi-Fi uses HTTP. Choose Private Printing for an encrypted transfer."
            :"Shop Wi-Fi uses HTTP. This transfer is not encrypted in transit."}</CustomerInlineNotice>
      :<CustomerInlineNotice kind="info" title={t("Check shop Wi-Fi")}>{t("Tap Check shop Wi-Fi above and allow local-network access.")}</CustomerInlineNotice>)}
    {files.length>0&&uploadMode==="CLOUD"&&<CustomerInlineNotice kind="info" title={t("Secure HTTPS transfer")}>{t("Your files are encrypted while being sent to the shop.")}</CustomerInlineNotice>}
    {previewUrl&&file&&<div className="customer-file-preview" role="region" aria-label={t("Preview for {name}",{name:file.name})}><div className="customer-file-label"><FileText size={15}/><span>{photoSettingsByFileFingerprint[customerFileFingerprint(file)]?"Prepared photo sheet":"Selected document"} · {file.name}</span>{isCustomerImage(file)&&photoRatesAvailable&&<button type="button" className="customer-file-studio-button" disabled={busy||batchLockedForPayment||photoAlreadyUploaded} title={photoAlreadyUploaded?t("This photo has already been submitted to the shop."):t("Open Photo Studio")} onClick={()=>openPhotoStudio(file)}><ImageIcon size={14}/> {photoAlreadyUploaded?t("Already sent to shop"):t("Open Photo Studio")}</button>}</div>{isCustomerImage(file)&&!photoRatesAvailable&&<CustomerInlineNotice kind="info" title={t("Photo printing unavailable")}>{t("Ask the shop to enable photo printing and set its rates.")}</CustomerInlineNotice>}{isCustomerImage(file)&&photoAlreadyUploaded&&photoRatesAvailable&&<CustomerInlineNotice kind="info" title={t("Photo already sent")}>{t("The shop can prepare it from the review queue. No second upload is needed.")}</CustomerInlineNotice>}{isCustomerPdf(file)?photoSettingsByFileFingerprint[customerFileFingerprint(file)]?(()=>{const pages=photoPreviewPagesByFingerprint[customerFileFingerprint(file)]??[];return pages.length?<div className="customer-edited-photo-preview"><div className="customer-edited-photo-frame"><img src={pages[Math.min(selectedPreparedPhotoPage,pages.length-1)]} alt={t("Prepared photo sheet page {page}",{page:selectedPreparedPhotoPage+1})}/><span>SmartPrint by QuantaEdge</span></div>{pages.length>1&&<div className="customer-edited-photo-pages" aria-label={t("Prepared A4 pages")}>{pages.map((url,index)=><button type="button" key={url} aria-pressed={selectedPreparedPhotoPage===index} onClick={()=>setSelectedPreparedPhotoPage(index)}><img src={url} alt={t("Page {page} thumbnail",{page:index+1})}/><span>Page {index+1}</span></button>)}</div>}<small>{t("Preview only.")} <InfoTooltip label={t("Prepared file")}><span>{t("The final prepared file stays in your print workflow.")}</span></InfoTooltip></small></div>:<div className="customer-private-photo-preview"><ShieldCheck size={20}/><strong>{t("Photo sheet prepared for this shop")}</strong><span>{t("Preview unavailable.")} <InfoTooltip label={t("Prepared PDF")}><span>{t("The prepared PDF stays in your print workflow.")}</span></InfoTooltip></span></div>})():nativeAppMode?smartPrintNativePdfPreviewTokens.has(file)?<CustomerNativePdfPreview token={smartPrintNativePdfPreviewTokens.get(file)!} preview={nativePdfPreview} onPage={page=>requestCustomerPdfPreviewPage(smartPrintNativePdfPreviewTokens.get(file)!,page)}/>:<small className="customer-upload-local-note">{t("Preview unavailable.")} <InfoTooltip label={t("Upload status")}><span>{t("You can still upload this file normally.")}</span></InfoTooltip></small>:<>
  <iframe src={browserNupPreview?.url||previewUrl} title={browserNupPreview?.url?file.name+" N-up print preview":file.name+" preview"}/>
  {browserNupPreview&&<div className="customer-nup-browser-preview" role="region" aria-label={t("Pages per sheet preview")}>
    {browserNupPreview.loading
      ? <span>{t("Preparing print layout preview…")}</span>
      : browserNupPreview.failed
        ? <span>{t("The N-up layout preview could not be prepared. Your original PDF remains available and will still be sent to the selected printer driver.")}</span>
        : <>
            <strong>{browserNupPreview.pagesPerSheet}-up preview · Output sheet {browserNupPreview.sheetIndex+1} of {browserNupPreview.sheetCount}</strong>
            <span>{t("PDF pages {first}–{last} appear on this output sheet.",{first:browserNupPreview.firstSourcePage,last:browserNupPreview.lastSourcePage})}</span>
            {browserNupPreview.sheetCount>1&&<div className="customer-nup-browser-preview-controls">
              <button type="button" disabled={browserNupPreview.sheetIndex<=0} onClick={()=>setBrowserNupPreviewSheetIndex(index=>Math.max(0,index-1))}>{t("Previous sheet")}</button>
              <button type="button" disabled={browserNupPreview.sheetIndex>=browserNupPreview.sheetCount-1} onClick={()=>setBrowserNupPreviewSheetIndex(index=>Math.min(browserNupPreview.sheetCount-1,index+1))}>{t("Next sheet")}</button>
            </div>}
            <small>{t("Page order follows {direction}. This visual preview does not replace driver-specific margins or page fitting. The original PDF is uploaded unchanged; the selected Windows printer driver applies the physical N-up layout.",{direction:browserNupPreview.pagesPerSheetDirection})}</small>
          </>}
  </div>}
</>:isCustomerImage(file)?<img className="customer-original-image-preview" src={previewUrl} alt={file.name}/>:<div className="customer-private-photo-preview"><ShieldCheck size={20}/><strong>{t("Preview unavailable.")}</strong><span>{t("This file remains attached to the print workflow.")}</span></div>}</div>}
    {files.length>0&&<button className="customer-flow-button" disabled={busy||customerUploadUnavailable||pendingFilesCount===0||(!privatePrinting&&uploadMode==="LOCAL"&&!localReady)} onClick={()=>void upload()}>{busy?<span className="button-spinner"/>:<><UploadIcon/>{privatePrinting&&uploadWaitingCount>0&&uploadFailedCount===0?t("Resume secure upload"):privatePrinting&&uploadFailedCount>0?t("Retry pending secure uploads"):privatePrinting?t("Upload {count} file(s) securely").replace("{count}",String(pendingFilesCount)):uploadMode==="LOCAL"&&localAgent?.endpoints?.length?t("Upload {count} file(s) to shop").replace("{count}",String(pendingFilesCount)):t("Upload {count} file(s)").replace("{count}",String(pendingFilesCount))}</>}</button>}
    {uploadProgress&&<section className="customer-upload-progress" aria-live="polite" aria-label={t("Upload progress")}>
      <div className="customer-upload-progress-header">
        <div><strong>{uploadConfirmedCount} of {uploadProgress.total} files uploaded</strong><span>{uploadFailedCount>0?`${uploadFailedCount} failed · Retry only failed files.`:uploadWaitingCount>0?`${uploadWaitingCount} waiting for the shop computer.`:uploadConfirmedCount===uploadProgress.total?"All files uploaded. Printing status is shown separately.":"Files upload in parallel."}</span></div>
        <span className="customer-upload-progress-count">{Math.round(
          uploadProgress.files.reduce((sum,file)=>sum+Math.min(file.bytesSent,file.totalBytes||file.sizeBytes),0)/
          Math.max(1,uploadProgress.files.reduce((sum,file)=>sum+(file.totalBytes||file.sizeBytes),0))*100
        )}%</span>
      </div>
      <div className="customer-upload-progress-list">
        {uploadProgress.files.map((file,index)=>{
          const isComplete=file.status==="COMPLETED";
          const isWaiting=file.status==="WAITING_FOR_SHOP"||file.status==="PROCESSING";
          const total=file.totalBytes||file.sizeBytes;
          const percent=isComplete?100:total>0?Math.min(100,file.bytesSent/total*100):0;
          const hasMeasuredBytes=file.totalBytes>0&&file.bytesSent>0;
          return <div className="customer-upload-progress-file" key={index}>
            <div className="customer-upload-progress-file-head">
              <span className="customer-upload-progress-file-icon">{isComplete?<Check size={14}/>:file.status==="FAILED"?<X size={14}/>:<FileText size={14}/>}</span>
              <div className="customer-upload-progress-file-main">
                <strong title={file.name}>{file.name}</strong>
                <span>{file.message}</span>
              </div>
              <span className={"customer-upload-progress-file-status "+(isComplete?"complete":file.status==="FAILED"?"failed":isWaiting?"waiting":"active")}>
                {isComplete?t("Done"):file.status==="FAILED"?t("Failed"):isWaiting?t("Processing"):file.status==="QUEUED"?t("Queued"):t("Uploading")}
              </span>
            </div>
            <div className="customer-upload-progress-track" role="progressbar" aria-label={t("Upload progress for {name}",{name:file.name})} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(percent)}><i className={hasMeasuredBytes||isComplete?"measured":"indeterminate"} style={{width:percent+"%"}}/></div>
            <div className="customer-upload-progress-file-foot">
              <span>{hasMeasuredBytes?formatBytes(Math.min(file.bytesSent,total))+" / "+formatBytes(total):formatBytes(file.sizeBytes)+" "+t("total")}</span>
              <span>{isComplete?"100%":hasMeasuredBytes?Math.floor(percent)+"%":file.status==="QUEUED"?t("Waiting"):isWaiting?t("Transfer sent"):t("Preparing")}</span>
            </div>
          </div>;
        })}
      </div>
      <small>{t("Progress shows file transfer, not printing. A file is complete only after SmartPrint confirms it. Successful files stay saved if another file fails.")}</small>
    </section>}
    {uploaded&&<>
      {uploadedBatch.length>0&&<div className="customer-uploaded-batch">
        <div className="customer-uploaded-batch-head"><strong>{batchName.trim() ? t("Batch · {name}", { name: batchName.trim() }) : t("Batch · {count} files", { count: uploadedBatch.length })}</strong><span>{uploadedBatch[0]?.job.jobNumber ? t("First job · {job}", { job: uploadedBatch[0].job.jobNumber }) + " · " : ""}{t("Select a file to view its status. Pay once for all accepted files.")}</span></div>
         {uploadedBatch.some(item=>item.job.privatePrinting&&resolveCustomerDocumentName(item,privateFileNamesByJobId)==="Private file")&&<p className="customer-payment-model-note">{t("Private filenames are kept only on this device. A filename that is not available locally is shown as “Private file”; the shop does not receive or restore the original name.")}</p>}

        {Array.from(new Set(uploadedBatch.map(item=>item.job.batchGroupName??"Loose files"))).map(groupName=><section className="customer-uploaded-folder" key={groupName}>
          <strong>{groupName}</strong>
          {uploadedBatch.filter(item=>(item.job.batchGroupName??"Loose files")===groupName).map(item=><button type="button" key={item.job.id} aria-pressed={uploaded.job.id===item.job.id} className={uploaded.job.id===item.job.id?"customer-uploaded-batch-item active":"customer-uploaded-batch-item"} disabled={busy} onClick={()=>selectUploadedFile(item)}>
            <span className="batch-file-index"><FileText size={14}/></span><span className="batch-file-copy"><span className="batch-file-title-row"><strong>{resolveCustomerDocumentName(item,privateFileNamesByJobId)}</strong><PrintKindTag printKind={normalizedCustomerPrintKind(printKindsByJobId[item.job.id]??batchStatuses[item.job.id]?.printKind??item.job.printKind)}/></span><small>{(photoSettingsByJobId[item.job.id]||batchStatuses[item.job.id]?.printKind==="PHOTO"||item.job.printKind==="PHOTO")?`${photoSettingsByJobId[item.job.id]?.photoCount??batchStatuses[item.job.id]?.photoCount??item.job.photoCount??0} photos · ${photoSettingsByJobId[item.job.id]?.sizeCode??batchStatuses[item.job.id]?.photoSizeCode??item.job.photoSizeCode??""} · ${photoSettingsByJobId[item.job.id]?.photosPerPage??batchStatuses[item.job.id]?.photosPerPage??item.job.photosPerPage??"layout not recorded"} per A4 page · ${batchStatuses[item.job.id]?.pageCount??item.job.pageCount} A4 pages`:`${customerDocumentOutputSummary(item.job.pageCount,printSettingsByJobId[item.job.id]?.copies??item.job.copies,printSettingsByJobId[item.job.id]?.pagesPerSheet??item.job.pagesPerSheet??1,printSettingsByJobId[item.job.id]?.isDuplex??item.job.isDuplex??false)} · ${printSettingsByJobId[item.job.id]?.colourMode??"Mono"} · ${(printSettingsByJobId[item.job.id]?.isDuplex??item.job.isDuplex??false)?"Double-sided":"Single-sided"} · ${printSettingsByJobId[item.job.id]?.paperSize??item.job.paperSize??"A4"} · ${(printSettingsByJobId[item.job.id]?.orientation??item.job.orientation??"AUTO")==="LANDSCAPE"?"Landscape":(printSettingsByJobId[item.job.id]?.orientation??item.job.orientation??"AUTO")==="PORTRAIT"?"Portrait":"Auto orientation"}`} · {(batchStatuses[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor)>0?money(batchStatuses[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor,item.job.currency):"Awaiting quote"}</small></span>
            <Status
              label={quoteExpiredForBatchItem(item)&&!["REJECTED","REFUNDED"].includes(statusForBatchItem(item))?"Quote expired":customerStatusLabel(batchStatuses[item.job.id]?.status??item.job.status??"UPLOADED")}
              tone={quoteExpiredForBatchItem(item)&&!["REJECTED","REFUNDED"].includes(statusForBatchItem(item))?"amber":customerStatusTone(statusForBatchItem(item))}
            />
          </button>)}
        </section>)}
      </div>}
      <div className="uploaded-document-card"><div className="uploaded-document-icon"><FileText size={18}/></div><div><div className="uploaded-document-title"><strong>{resolveCustomerDocumentName(uploaded,privateFileNamesByJobId)}</strong><PrintKindTag printKind={normalizedCustomerPrintKind(printKindsByJobId[uploaded.job.id]??batchStatuses[uploaded.job.id]?.printKind??uploaded.job.printKind)}/></div><span>{(photoSettingsByJobId[uploaded.job.id]||batchStatuses[uploaded.job.id]?.printKind==="PHOTO"||uploaded.job.printKind==="PHOTO")?`${photoSettingsByJobId[uploaded.job.id]?.photoCount??batchStatuses[uploaded.job.id]?.photoCount??uploaded.job.photoCount??0} photos · ${photoSettingsByJobId[uploaded.job.id]?.sizeCode??batchStatuses[uploaded.job.id]?.photoSizeCode??uploaded.job.photoSizeCode??""} · ${uploaded.job.pageCount} A4 pages`:`${customerDocumentOutputSummary(uploaded.job.pageCount,printSettingsByJobId[uploaded.job.id]?.copies??uploaded.job.copies,printSettingsByJobId[uploaded.job.id]?.pagesPerSheet??uploaded.job.pagesPerSheet??1,printSettingsByJobId[uploaded.job.id]?.isDuplex??uploaded.job.isDuplex??false)}`} · {formatBytes(uploaded.document.sizeBytes)} · Validated</span></div><Status label={t(customerStatusLabel(batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"UPLOADED"))} tone={customerStatusTone(batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"UPLOADED")}/></div>
      {batchId&&hasLegacyIndividualPayment&&<CustomerInlineNotice kind="warning" title={t("Individual payment already exists")}>{t("Resolve that payment before paying for this batch to avoid a duplicate charge.")}</CustomerInlineNotice>}
      {canCalculateBatch&&<div className="customer-batch-quote-action">
        <div className="customer-flow-heading customer-config-heading"><div><span>{t("STEP 2")}</span><h2>{t("Review batch settings")}</h2></div><small>{quoteItems.length} files</small></div>
        <button className="customer-flow-button" disabled={busy} onClick={()=>void getQuote()}>{busy?<span className="button-spinner"/>:"Calculate batch quote"}</button>
      </div>}
    </>}
    {error&&<div className="customer-flow-error" role="alert"><AlertTriangle size={16} aria-hidden="true"/><span>{error}</span></div>}
    {(quoteEntries.length>0||batchQuoteNeedsRefresh>0)&&<section className="customer-quote-card customer-batch-quote-card" aria-label={t("Batch quote summary")}>
      <div className="customer-batch-quote-heading">
        <div className="customer-batch-quote-title"><span>{t("BATCH QUOTE")}</span><strong>{batchQuoteTotalDisplay}</strong><small>{batchQuoteSummary} {batchQuoteNeedsRefresh>0?batchQuoteNeedsRefresh+" need a fresh quote. ":""}{t("Excluded files are not charged.")}</small></div>
        <span className="customer-batch-quote-validity">{batchQuoteNeedsRefresh>0?"Review required":"Quote for review"}</span>
      </div>
      <div className="customer-batch-quote-categories" aria-label={t("Files included in this quote")}>
        <span className="customer-batch-quote-chip included"><b>{acceptedQuoteEntries.length}</b> {t("priced")}</span>
        {batchQuoteNeedsRefresh>0&&<span className="customer-batch-quote-chip refresh"><b>{batchQuoteNeedsRefresh}</b> {t("quote needed")}</span>}
        {batchQuoteCounts.excluded>0&&<span className="customer-batch-quote-chip excluded"><b>{batchQuoteCounts.excluded}</b> {t("excluded")}</span>}
        {batchQuoteCounts.documents>0&&<span className="customer-batch-quote-chip document"><b>{batchQuoteCounts.documents}</b> {t("documents")}</span>}
        {batchQuoteCounts.idCards>0&&<span className="customer-batch-quote-chip id-card"><b>{batchQuoteCounts.idCards}</b> {t("ID cards")}</span>}
        {batchQuoteCounts.photos>0&&<span className="customer-batch-quote-chip photo"><b>{batchQuoteCounts.photos}</b> {t("photo jobs")}</span>}
      </div>
      <div className="customer-batch-quote-lines" aria-label={t("File-by-file prices")}>
        {quoteRows.map(entry=>{
          const status=statusForBatchItem(entry.item);
          const quoteExpired=Boolean(entry.quote)&&quoteExpiredForBatchItem(entry.item);
          const needsFreshQuote=!entry.excluded&&!shouldBlockQuoteRefreshForStatus(status)&&(!entry.quote||quoteExpired);
          const printKind=normalizedCustomerPrintKind(entry.quote?.printKind??printKindsByJobId[entry.item.job.id]??entry.item.job.printKind);
          const detail=entry.quote
            ? entry.quote.printKind==="PHOTO"
              ? (entry.quote.photoCount??0)+" × "+customerPhotoSizeLabel(entry.quote.photoSizeCode)+" · "+(entry.quote.photosPerPage??"layout not recorded")+" per A4 page · "+entry.quote.pageCount+" A4 pages · "+money(entry.quote.photoUnitPriceMinor??0,entry.quote.currency)+" per photo"
              : entry.quote.printKind==="ID_DOCUMENT"
                ? entry.quote.pageCount+" A4 portrait "+(entry.quote.pageCount===1?"sheet":"sheets")+" × "+entry.quote.copies+" copy · "+entry.quote.colourMode+" · Single-sided"
                : entry.quote.pageCount+" source pages · "+customerDocumentOutputSummary(entry.quote.pageCount,entry.quote.copies,entry.quote.pagesPerSheet??1,entry.quote.isDuplex)+" · "+entry.quote.colourMode+" · "+(entry.quote.isDuplex?"Double-sided":"Single-sided")+" · "+entry.quote!.paperSize+" · "+(entry.quote!.orientation==="LANDSCAPE"?"Landscape":entry.quote!.orientation==="PORTRAIT"?"Portrait":"Auto orientation")
            : entry.item.job.pageCount+" source "+(entry.item.job.pageCount===1?"page":"pages")+" · "+entry.item.job.copies+" "+(entry.item.job.copies===1?"copy":"copies")+" · quote not available yet";
          const lineState=entry.excluded
            ?"Excluded"
            :needsFreshQuote?(entry.quote?"Price refresh needed":"Quote needed")
              :!entry.quote?customerStatusLabel(status)
                :quoteExpired?"Quote locked · "+customerStatusLabel(status)
                  :"Included in total";
          const amountLabel=entry.excluded
            ?"Not charged"
            :needsFreshQuote?"—"
              :!entry.quote?"Pending"
                :quoteExpired?"See order status"
                  :money(entry.quote.quotedAmountMinor,entry.quote.currency);
          return <div className="customer-batch-quote-line" key={entry.item.job.id}>
            <span className="customer-batch-quote-line-copy">
              <span className="customer-batch-quote-line-top"><strong>{resolveCustomerDocumentName(entry.item,privateFileNamesByJobId)}</strong><PrintKindTag printKind={printKind}/><span className={"customer-batch-quote-line-state "+(entry.excluded?"excluded":needsFreshQuote?"refresh":"included")}>{lineState}</span></span>
              <small>{detail}</small>
            </span>
            <strong className={"customer-batch-quote-line-amount "+(entry.excluded||needsFreshQuote||quoteExpired?"excluded":"")}>{amountLabel}</strong>
          </div>;
        })}
      </div>
      {acceptedQuoteEntries.length>0&&<details className="customer-batch-quote-details">
        <summary><span>{t("See price breakdown")}</span><small>{t("Documents, ID cards and photos")}</small></summary>
        <div className="customer-batch-print-breakdown">
          <div><span><strong>{t("Normal documents")}</strong><small>{batchQuoteBreakdown.normalDocumentFileCount} files · {batchQuoteBreakdown.normalDocumentSourcePages} source pages → {batchQuoteBreakdown.normalDocumentOutputPages} output sides after N-up and copies</small><small>B&W · {batchQuoteBreakdown.normalDocumentMonoOutputPages} sides · {money(batchQuoteBreakdown.normalDocumentMonoAmountMinor,batchQuoteCurrency)} · Colour · {batchQuoteBreakdown.normalDocumentColourOutputPages} sides · {money(batchQuoteBreakdown.normalDocumentColourAmountMinor,batchQuoteCurrency)}</small></span><strong>{money(batchQuoteBreakdown.normalDocumentAmountMinor,batchQuoteCurrency)}</strong></div>
          <div><span><strong>{t("ID cards")}</strong><small>{batchQuoteBreakdown.idCardCount} jobs · {batchQuoteBreakdown.idCardSheets} prepared A4 sheets</small></span><strong>{money(batchQuoteBreakdown.idCardAmountMinor,batchQuoteCurrency)}</strong></div>
          <div><span><strong>{t("Photo printing")}</strong><small>{batchQuoteBreakdown.finishedPhotos} finished photos · {batchQuoteBreakdown.photoPages} A4 pages</small></span><strong>{money(batchQuoteBreakdown.photoAmountMinor,batchQuoteCurrency)}</strong></div>
          <div className="customer-batch-print-total"><span><strong>{t("Total output")}</strong><small>{t("Page sides and prepared A4 sheets, grouped by print type")}</small></span><strong>{batchQuoteBreakdown.totalOutputPagesAndSheets}</strong></div>
        </div>
      </details>}
      <p className="customer-payment-model-note customer-quote-explanation">{batchQuoteNeedsRefresh > 0 ? t("Only files with a current quote are included in this displayed total. Prices are being refreshed for the highlighted files. Review any change before continuing.") + " " : ""}{t("This is a quote, not a payment. You will pay once for the accepted files after the shop confirms them.")} <InfoTooltip label={t("How your quote is calculated")}><span>{t("Normal PDF/document pages are original pages. For N-up printing, pages per sheet groups source pages onto output sides; the quote counts those output sides after copies.")}</span><span>{t("Standalone JPG/PNG documents count as one page each.")}</span><span>{t("ID cards are counted as prepared A4 sheets and use the dedicated ID-card rate.")}</span><span>{t("Photo printing is priced per finished photo; the A4 page count is shown for planning.")}</span><span>{t("Duplex printing can place two output sides on one physical sheet.")}</span><span>{t("Rejected or refunded files are excluded from the total.")}</span></InfoTooltip></p>
    </section>}
    {canSendBatch&&<button className="customer-flow-button" disabled={busy} onClick={()=>void acceptQuote()}>{busy?<span className="button-spinner"/> : t("Send batch to shop")}</button>}
      {uploaded&&quoteExpiredForBatchItem(uploaded)&&!shouldBlockQuoteRefreshForStatus(statusForBatchItem(uploaded))&&!batchLockedForPayment&&!hasSelectedLegacyIndividualPayment&&<button className="customer-flow-button" disabled={busy} onClick={()=>{setQuote(null);setQuotesByJobId(current=>{const next={...current};delete next[uploaded.job.id];return next;});void getQuote(uploadedBatch.length?uploadedBatch:[uploaded]);}}>{t("Refresh price")}</button>}
    {jobStatus&&<div className="customer-job-state" role="status" aria-live="polite"><div className="customer-job-state-top"><Status label={customerStatusLabel(jobStatus.status)} tone={customerStatusTone(jobStatus.status)}/><button className="customer-refresh-status" disabled={busy} onClick={()=>void refreshJobStatus()}>{busy?<span className="button-spinner"/>:"Refresh status"}</button></div>
      <strong>{jobStatus.status==="TEST_OUTPUT_READY"?(jobStatus.outputMode==="UNSUPPORTED_VIRTUAL"?t("Virtual printer test completed · no physical print was made"):t("Development test PDF generated · no physical print was made")):jobStatus.privatePrinting&&jobStatus.status==="COMPLETED"&&jobStatus.privateDocumentPurgedUtc?t("Your print is ready — your document has been removed from the shop computer"):jobStatus.privatePrinting&&jobStatus.status==="COMPLETED"?t("Your print is ready — removing your document from the shop computer"):nativeAppMode?smartPrintNativeHeadline(jobStatus.status):jobStatus.status==="AWAITING_SHOP_APPROVAL"?t("Your request is with the shop"):jobStatus.status==="AWAITING_PAYMENT"?t("Your quote is ready for payment"):jobStatus.status==="LOCAL_QUEUE_CLAIMED"?t("Shop computer is preparing your file"):jobStatus.status==="LOCAL_QUEUE_READY"?t("Your file is ready for printing"):jobStatus.status==="PRINT_AUTHORISED"?t("Waiting for the shop computer"):jobStatus.status==="PRINT_SUBMITTING"?t("Your document is being sent to the printer"):jobStatus.status==="SPOOLER_ACCEPTED"?t("The printer queue accepted your document"):jobStatus.status==="PRINTING"?t("Your document is printing"):jobStatus.status==="OUTPUT_UNCONFIRMED"?t("The shop is checking your print"):jobStatus.status==="COMPLETED"?t("Your print is ready"):jobStatus.status==="PDF_EXPORTED"?t("Your PDF is ready"):jobStatus.status==="REFUNDED"?t("This file was fully refunded"):jobStatus.status==="CANCELLED"?t("This request was cancelled"):jobStatus.status==="FAILED"?t("This request could not be completed"):jobStatus.status==="PAID_AUTHORISED"?t("Your payment is verified"):jobStatus.status==="REJECTED"?t("The shop could not accept this request"):jobStatus.status==="QUOTE_EXPIRED"?t("Your price needs refreshing"):t("Your request is being reviewed")}</strong>
      <small className="customer-job-updated">{t("Last updated · {date}",{date:formatIstDateTime(jobStatus.updatedUtc)})}</small>
      <p>{jobStatus.status==="TEST_OUTPUT_READY"?(jobStatus.outputMode==="UNSUPPORTED_VIRTUAL"?t("The authorised virtual Windows queue completed the test through the normal spooler path. This is not physical fulfilment and is not ready for collection."):t("This was a test only. No physical pages were printed."))
        :jobStatus.status==="AWAITING_SHOP_APPROVAL"?t("The shop is reviewing your files. No payment has been collected.")
        :jobStatus.status==="AWAITING_PAYMENT"?t("Your files are accepted. Complete payment to continue.")
        :jobStatus.status==="AWAITING_COUNTER_PAYMENT"?t("Please pay at the shop counter.")
        :jobStatus.status==="AWAITING_CUSTOMER_PAYMENT"?t("Complete the payment using the details shown.")
        :jobStatus.status==="PAYMENT_REPORTED"?t("Your payment details were sent to the shop. The shop is checking the payment.")
        :jobStatus.status==="PAYMENT_REJECTED"?t("The payment could not be confirmed. Please check with the shop before paying again.")
        :jobStatus.status==="PAID_AUTHORISED"?t("Payment received. The shop is preparing your print.")
        :jobStatus.status==="LOCAL_QUEUE_CLAIMED"?t("The shop computer has picked up your request and is preparing the file.")
         :jobStatus.status==="LOCAL_QUEUE_READY"?t("Your file is safely stored on the shop computer and ready for printing.")
        :jobStatus.status==="PRINT_AUTHORISED"?t("The shop approved your print. The shop computer will receive it shortly."):["PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING"].includes(jobStatus.status)?t("Your print is in progress. The shop will confirm when the pages are ready.")
        :jobStatus.status==="OUTPUT_UNCONFIRMED"?t("The shop is checking the printer and will confirm whether your pages were printed. Please do not pay or upload the same file again.")
        :jobStatus.status==="COMPLETED"?t("Your pages are printed and ready for collection.")
        :jobStatus.status==="PDF_EXPORTED"?t("Your PDF is ready.")
        :jobStatus.status==="REFUNDED"?t("This file was not printed and the payment was returned.")
        :jobStatus.status==="CANCELLED"?t("This request was cancelled.")
        :jobStatus.status==="FAILED"?t("Printing could not be completed. Please speak to the shop before submitting again.")
        :jobStatus.status==="REJECTED"?t("The shop could not accept this file. Please ask the shop for help.")
        :jobStatus.status==="QUOTE_EXPIRED"?t("We are checking the current price. Review any change before continuing.")
        :t("Your request is being processed. Refresh to check again.")}</p>
      {jobStatus.privatePrinting&&jobStatus.status==="TEST_OUTPUT_READY"&&!jobStatus.privateDocumentPurgedUtc&&<div className="customer-private-purge-status customer-private-purge-pending" role="status" aria-live="polite"><Clock3 size={16}/><span><strong>{t("Removing the shop copy")}</strong>{t("The authorised test output is ready. The shop copy is being securely removed.")}</span></div>}
      {jobStatus.privatePrinting&&jobStatus.status==="TEST_OUTPUT_READY"&&jobStatus.privateDocumentPurgedUtc&&<div className="customer-private-purge-status customer-private-purge-complete" role="status" aria-live="polite"><ShieldCheck size={16}/><span><strong>{t("Shop copy removed")}</strong>{t("The test output is not physical fulfilment and is not ready for collection.")}</span></div>}
      {jobStatus.privatePrinting&&jobStatus.status==="COMPLETED"&&!jobStatus.privateDocumentPurgedUtc&&<div className="customer-private-purge-status customer-private-purge-pending" role="status" aria-live="polite"><Clock3 size={16}/><span><strong>{t("Removing the shop copy")}</strong>{t("Your pages are printed. SmartPrint is securely removing the shop copy.")}</span></div>}
      {jobStatus.privatePrinting&&jobStatus.status!=="TEST_OUTPUT_READY"&&jobStatus.privateDocumentPurgedUtc&&<div className="customer-private-purge-status customer-private-purge-complete" role="status" aria-live="polite"><ShieldCheck size={16}/><span><strong>{t("Shop copy removed")}</strong>{t("Your file has been removed from the shop computer. The original on your device is unchanged.")}</span></div>}
      {payment&&!!payment.refundedAmountMinor&&payment.refundedAmountMinor>0&&<div className="customer-refund-note"><Wallet size={16}/><span><strong>{money(payment.refundedAmountMinor,payment.currency)} returned</strong> for rejected pages ({rejectedPageList(jobStatus.rejectedPageNumbersJson)}). {money(Math.max(0,payment.amountMinor-payment.refundedAmountMinor),payment.currency)} remains paid; only accepted pages proceed to printing.</span></div>}
      {jobStatus.pdfBrandingEnabled&&<div className="customer-branding-note">
        <ShieldCheck size={16}/>
        <span><strong>{t("PDF footer selected by the shop.")}</strong> <InfoTooltip label={t("PDF footer details")}><span>{t("Footer only appears on PDFs with a verified blank bottom margin.")}</span><span>{t("Digitally signed or interactive PDFs remain unchanged.")}</span><span>{t("Original uploads and physical prints are never altered.")}</span><span>{t("Image-to-PDF exports currently remain unbranded.")}</span><span>{t("Branding off preserves a single-copy full-document PDF byte-for-byte.")}</span><span>{t("Reformatted or multi-copy exports may be flattened.")}</span><span>{t("Ask for an unbranded single-copy export when PDF structure matters.")}</span></InfoTooltip></span>
      </div>}
      {!!jobStatus.timeline?.filter(event=>event.previousStatus!==event.newStatus).length&&<div className="customer-state-timeline">{jobStatus.timeline.filter(event=>event.previousStatus!==event.newStatus).map((event,index)=><div key={event.createdUtc+"-"+index}><i/><span><strong>{customerStatusLabel(event.newStatus)}</strong><small>{event.actorType==="ShopUser"?"Shop team":event.actorType==="Customer"?"You":"System"} · {formatIstDateTime(event.createdUtc)}</small></span></div>)}</div>}
      {canStartNewSession&&<div className="customer-session-complete"><div><strong>{translateUi(locale,"customer.orderReceived")}</strong><span>{t("Your order history is saved. You can start another print session when ready.")}</span></div><button className="customer-flow-button" onClick={onStartNewSession}><RefreshCw size={16}/> {translateUi(locale,"customer.startNewSession")}</button></div>}

      {legacyIndividualPayment&&(jobStatus.status==="AWAITING_PAYMENT"||jobStatus.status==="PAYMENT_REJECTED")&&<div className="customer-pay-panel">
        <div className="customer-pay-heading"><span>{t("PAY THE SHOP")}</span><strong>{money(jobStatus.quotedAmountMinor,jobStatus.currency)}</strong></div>
        {!paymentOptions.cashEnabled&&paymentOptions.upiEnabled&&<CustomerInlineNotice kind="info" title={t("Cash at counter unavailable")}>{t("Choose UPI or ask the shop whether cash can be enabled.")}</CustomerInlineNotice>}
        {!paymentOptions.cashEnabled&&!paymentOptions.upiEnabled&&<CustomerInlineNotice kind="warning" title={t("No payment method available")}>{t("Ask the shop to enable UPI or cash at the counter.")}</CustomerInlineNotice>}
        {paymentOptions.upiEnabled&&<button type="button" aria-pressed={paymentMethod==="UPI"} className={paymentMethod==="UPI"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("UPI")}>{t("UPI payment")}</button>}
        {paymentOptions.cashEnabled&&<button type="button" aria-pressed={paymentMethod==="CASH"} className={paymentMethod==="CASH"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("CASH")}>{t("Cash at counter")}</button>}
        <button className="customer-flow-button" disabled={busy||(!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled)} onClick={()=>void beginPayment(paymentMethod)}>{busy?<span className="button-spinner"/>:nativeAppMode?t("Continue"):t("Continue with {method}",{method:paymentMethod==="UPI"?"UPI":"cash at counter"})}</button>
        
      </div>}
      {legacyIndividualPayment&&payment&&payment.status!=="REJECTED"&&<div className="customer-pay-panel">
        <div className="customer-pay-heading"><span>{t(payment.method==="UPI"?"UPI PAYMENT":"CASH AT COUNTER")}</span><strong>{money(Math.max(0,payment.amountMinor-(payment.refundedAmountMinor??0)),payment.currency)}</strong></div>
        {payment.method==="UPI"&&payment.status==="AWAITING_CUSTOMER_PAYMENT"&&<>
          <CustomerUpiQr
            uri={buildCustomerUpiIntent({
              serverUri:payment.upiUri,upiId:payment.upiIdSnapshot||paymentOptions.upiId,
              payeeName:payment.payeeNameSnapshot||paymentOptions.payeeName,amountMinor:payment.amountMinor,
              currency:payment.currency,transactionReference:payment.id,
              description:"SmartPrint "+(uploaded?.job.jobNumber||"print payment")
            })}
            payeeName={payment.payeeNameSnapshot||paymentOptions.payeeName||"Shop"}
            upiId={payment.upiIdSnapshot||paymentOptions.upiId||""}
            amount={money(payment.amountMinor,payment.currency)}
            amountMinor={payment.amountMinor}
            fileName={"smartprint-payment-"+payment.id.slice(0,12)+".png"}
          />
          <div className="customer-payment-reference">
            <label className="customer-reference-field">{t("UPI transaction ID (UTR)")}<input value={paymentReference} maxLength={100} onChange={event=>setPaymentReference(event.target.value)} placeholder={t("Enter transaction ID after paying")}/></label>
            <small className="customer-payment-reference-hint">{t("Find it in your UPI app under payment details or transaction history.")}</small>
            <button className="customer-flow-button" disabled={busy||paymentReference.trim().length<4} onClick={()=>void submitPaymentReference()}>{busy?<span className="button-spinner"/> :nativeAppMode?t("Submit UTR"):t("Submit payment reference")}</button>
            <CustomerInlineNotice kind="warning" title={t("Bank security checks still apply")}>{t("Downloading the QR does not bypass bank or UPI checks.")}</CustomerInlineNotice>
          </div>
        </>}
        {payment.method==="CASH"&&payment.status==="CASH_AWAITING_PAYMENT"&&<p>{nativeAppMode?t("Pay at counter · Await confirmation."):t("Pay at the shop counter. The shop will confirm receipt before authorising the job.")}</p>}
        {payment.status==="CUSTOMER_REPORTED"&&<p>{nativeAppMode?t("Reference received · Verification pending."):t("Your payment reference was received. The shop still needs to verify the transaction.")}</p>}
        {payment.status==="VERIFIED"&&<p>{nativeAppMode?t("Payment verified · Printing pending."):t("Payment verified by the shop. The print job is authorised, but not yet physically printed.")}</p>}
        {payment.status==="PARTIALLY_REFUNDED"&&<p>{nativeAppMode?t("Partial refund · Accepted pages only."):t("A partial return has been recorded. The remaining paid amount is for the pages the shop accepted.")}</p>}
      </div>}
    </div>}
    {batchCheckoutVisible&&<section className="customer-pay-panel customer-batch-checkout-panel">
      <div className="customer-pay-heading">
        <span>{batchCheckoutHeading} · {batchCheckoutFileCount} {t("FILE(S)")}</span>
        <strong>{money(batchCheckoutAmount,batchCheckoutCurrency)}</strong>
      </div>
      <div className="customer-batch-checkout-lines">
        {batchCheckoutLineItems.map(item=>{
          const allocation=customerPaymentForBatchItem(item);
          const amount=batchCheckoutUsesPayment
            ?allocation?.amountMinor??0
            :batchStatuses[item.job.id]?.quotedAmountMinor??quotesByJobId[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor??0;
          const refunded=batchCheckoutUsesPayment?(allocation?.refundedAmountMinor??0):0;
          const lineStatus=batchCheckoutUsesPayment?allocation?.status :t("Included in this checkout");
          return <div key={item.job.id}>
            <span>{resolveCustomerDocumentName(item,privateFileNamesByJobId)}<PrintKindTag printKind={normalizedCustomerPrintKind(printKindsByJobId[item.job.id]??item.job.printKind)}/><small>{lineStatus?String(lineStatus).replaceAll("_"," ") :t("Allocation loading")}{refunded>0? ` · ${money(refunded,item.job.currency)} refunded` : ""}</small></span>
            <strong>{money(amount,item.job.currency)}</strong>
          </div>;
        })}
      </div>
      {batchCheckoutUsesPayment&&!batchPaymentAllocationComplete&&<CustomerInlineNotice kind="info" title={t("Checking payment breakdown")}>{t("Review the full file list before paying or submitting a reference.")}</CustomerInlineNotice>}
      {batchCheckoutUsesPayment&&batchPaymentAllocationComplete&&batchPaymentRefundedTotal>0&&<CustomerInlineNotice kind="info" title={t("Refund summary")}>{t("Refunded: {refunded} · Remaining: {remaining}.",{refunded:money(batchPaymentRefundedTotal,batchCheckoutCurrency),remaining:money(Math.max(0,batchPayment!.amountMinor-batchPaymentRefundedTotal),batchCheckoutCurrency)})}</CustomerInlineNotice>}
      {!batchCheckoutUsesPayment&&<>
        {batchPayment?.status==="REJECTED"&&<p className="customer-flow-error">{t("The previous batch payment was rejected. {reason}",{reason:batchPayment.rejectionReason||t("Check with the shop before trying again.")})}</p>}
        {!paymentOptions.cashEnabled&&paymentOptions.upiEnabled&&<CustomerInlineNotice kind="info" title={t("Cash at counter unavailable")}>{t("Choose UPI or ask the shop whether cash can be enabled.")}</CustomerInlineNotice>}
        {!paymentOptions.cashEnabled&&!paymentOptions.upiEnabled&&<CustomerInlineNotice kind="warning" title={t("No payment method available")}>{t("Ask the shop to enable UPI or cash at the counter.")}</CustomerInlineNotice>}
        {paymentOptions.upiEnabled&&<button type="button" aria-pressed={paymentMethod==="UPI"} className={paymentMethod==="UPI"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("UPI")}>{t("UPI payment")}</button>}
        {paymentOptions.cashEnabled&&<button type="button" aria-pressed={paymentMethod==="CASH"} className={paymentMethod==="CASH"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("CASH")}>{t("Cash at counter")}</button>}
        <button className="customer-flow-button" disabled={busy||(!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled)||batchPayableItems.length===0} onClick={()=>void beginPayment(paymentMethod,true)}>
          {busy?<span className="button-spinner"/>:nativeAppMode?t("Pay accepted files"):t("Pay accepted files · {amount}",{amount:money(batchPayableTotal,batchCheckoutCurrency)})}
        </button>
        
      </>}
      {batchCheckoutUsesPayment&&batchPaymentAllocationComplete&&batchPayment?.method==="UPI"&&batchPayment.status==="AWAITING_CUSTOMER_PAYMENT"&&<>
        <CustomerUpiQr
          uri={buildCustomerUpiIntent({
            serverUri:batchPayment.upiUri,upiId:batchPayment.upiIdSnapshot||paymentOptions.upiId,
            payeeName:batchPayment.payeeNameSnapshot||paymentOptions.payeeName,amountMinor:batchPayment.amountMinor,
            currency:batchPayment.currency,transactionReference:batchPayment.id,
            description:"SmartPrint batch "+batchPayment.batchId.slice(0,8).toUpperCase()
          })}
          payeeName={batchPayment.payeeNameSnapshot||paymentOptions.payeeName||"Shop"}
          upiId={batchPayment.upiIdSnapshot||paymentOptions.upiId||""}
          amount={money(batchPayment.amountMinor,batchPayment.currency)}
          amountMinor={batchPayment.amountMinor}
          fileName={"smartprint-batch-payment-"+batchPayment.id.slice(0,12)+".png"}
        />
        <div className="customer-payment-reference">
          <label className="customer-reference-field">{t("UPI transaction ID (UTR)")}<input value={paymentReference} maxLength={100} onChange={event=>setPaymentReference(event.target.value)} placeholder={t("Enter transaction ID after paying")}/></label>
            <small className="customer-payment-reference-hint">{t("Find it in your UPI app under payment details or transaction history.")}</small>
          <button className="customer-flow-button" disabled={busy||paymentReference.trim().length<4} onClick={()=>void submitPaymentReference()}>{busy?<span className="button-spinner"/> :nativeAppMode?t("Submit UTR"):t("Submit batch payment reference")}</button>
          <CustomerInlineNotice kind="warning" title={t("Bank security checks still apply")}>{t("The shop verifies payment. A downloaded QR does not bypass bank or UPI checks.")}</CustomerInlineNotice>
        </div>
      </>}
      {batchCheckoutUsesPayment&&batchPaymentAllocationComplete&&batchPayment?.method==="CASH"&&batchPayment.status==="CASH_AWAITING_PAYMENT"&&<p>{t("Pay the displayed batch total at the shop counter. The shop will confirm receipt before authorising the accepted files.")}</p>}
      {batchCheckoutUsesPayment&&batchPayment?.status==="CUSTOMER_REPORTED"&&<p>{t("Your batch payment reference was received. The shop must verify the full amount before any accepted file is authorised.")}</p>}
      {batchCheckoutUsesPayment&&batchPayment?.status==="VERIFIED"&&<div className="payment-verified-note"><Check size={14}/> {t("Batch payment verified. Accepted files are authorised for the shop's print queue.")}</div>}
    </section>}
    <div className="customer-payment-boundary"><ShieldCheck size={15}/><span>{resolveCustomerUploadStatusMessage({
      jobStatus:jobStatus?.status??(uploaded?batchStatuses[uploaded.job.id]?.status??uploaded.job.status:null),
      filesSelected:files.length,
      uploadedFileCount:uploadedBatch.length||(uploaded?1:0),
      quoteAvailable:Boolean(quote),
      paymentStatus:payment?.status??batchPayment?.status??null,
      uploadProgress:uploadProgress?{files:uploadProgress.files}:null
    })}</span></div>
  </section></>;
}

function UploadIcon() {
  return <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M12 16V4m0 0L7 9m5-5 5 5"/><path d="M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3"/></svg>;
}
