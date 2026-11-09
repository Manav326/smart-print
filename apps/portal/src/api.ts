import { isUncompletedLocalUploadTicketExpired } from "./local-upload-routing";
import { getLocalPreviewEndpointCandidates, isPrivateLocalPreviewEndpoint } from "./local-preview-routing";
import { getLocalAgentStatus, isDesktopApp, writeDesktopSession } from "./desktop-bridge";
import { authStorageKey } from "./portal-routing";
export interface AuthTokens {
  tokenType: string;
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresUtc: string;
}

export function isPlatformAdminSession(tokens: AuthTokens | null): boolean {
  if (!tokens?.accessToken) return false;
  try {
    const segments = tokens.accessToken.split(".");
    if (segments.length !== 3) return false;
    const encoded = segments[1].replace(/-/g, "+").replace(/_/g, "/");
    const payload = JSON.parse(atob(encoded.padEnd(Math.ceil(encoded.length / 4) * 4, "="))) as {
      tenant_id?: string | null;
      platform_role?: string | string[];
    };
    const roles = Array.isArray(payload.platform_role)
      ? payload.platform_role
      : [payload.platform_role];
    return !Object.prototype.hasOwnProperty.call(payload, "tenant_id") && roles.includes("PlatformAdmin");
  } catch {
    return false;
  }
}

export type AuthScope = "admin" | "shop";

function currentAuthScope(): AuthScope {
  const path = window.location.pathname.replace(/\/+$/, "") || "/";
  return path === "/admin" || path.startsWith("/admin/") ? "admin" : "shop";
}

export interface PlatformOverview {
  generatedUtc: string;
  tenantCount: number;
  activeTenantCount: number;
  trialSubscriptionCount: number;
  activeSubscriptionCount: number;
  printerCount: number;
  activePrinterCount: number;
  agentCount: number;
  onlineAgentCount: number;
  jobsLast30Days: number;
  subscriptionsByStatus: { status: string; count: number }[];
}

export interface PlatformTenant {
  id: string;
  slug: string;
  businessName: string;
  status: string;
  createdUtc: string;
  planCode?: string | null;
  subscriptionStatus?: string | null;
  subscriptionPeriodEndUtc?: string | null;
  printerCount: number;
  activePrinterCount: number;
  agentCount: number;
  onlineAgentCount: number;
  offlineAgentCount: number;
  latestAgentHeartbeatUtc: string | null;
  jobsLast30Days: number;
}

export interface PlatformTenantsPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: PlatformTenant[];
}

export function updatePlatformTenantStatus(token: string, tenantId: string, status: "Active" | "Suspended" | "Inactive", reason: string) {
  return apiRequest<{ id: string; status: string; updatedUtc: string; duplicateRequest: boolean }>(
    `/api/v1/platform/tenants/${tenantId}/status`,
    { method: "PATCH", body: JSON.stringify({ status, reason }) },
    token
  );
}

export function getPlatformTenantStatusHistory(token: string, tenantId: string) {
  return apiRequest<Array<{
    id: string;
    previousStatus: string;
    newStatus: string;
    reason: string;
    actorUserId: string | null;
    createdUtc: string;
  }>>(`/api/v1/platform/tenants/${tenantId}/status-history`, {}, token);
}

export function getPlatformOverview(token: string) {
  return apiRequest<PlatformOverview>("/api/v1/platform/overview", {}, token);
}

export function getPlatformTenants(token: string, values: {
  search?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}) {
  const params = new URLSearchParams();
  if (values.search?.trim()) params.set("search", values.search.trim());
  if (values.status) params.set("status", values.status);
  params.set("page", String(values.page ?? 1));
  params.set("pageSize", String(values.pageSize ?? 25));
  return apiRequest<PlatformTenantsPage>(`/api/v1/platform/tenants?${params.toString()}`, {}, token);
}

export interface ShopProfile {
  id: string;
  slug: string;
  businessName: string;
  ownerName: string;
  ownerEmail: string;
  status: string;
  displayName: string;
  logoUrl?: string | null;
  accentColor: string;
  currency: string;
  timeZone: string;
  whiteLabelEnabled: boolean;
}

export interface PrinterDevice {
  id: string;
  displayName: string;
  manufacturer: string;
  model: string;
  connectionType: string;
  outputMode: "PHYSICAL" | "PDF_EXPORT" | string;
  connectionAddress?: string | null;
  queueName?: string | null;
  capabilitiesJson: string;
  status: string;
  isActive: boolean;
  isDefault: boolean;
  agentDeviceId?: string | null;
  lastSeenUtc?: string | null;
}

export interface QrStation {
  id: string;
  printerDeviceId?: string | null;
  displayName: string;
  publicCodeHint: string;
  publicUrl?: string | null;
  isEnabled: boolean;
  createdUtc: string;
}

export interface AgentDevice {
  id: string;
  deviceName: string;
  status: string;
  createdUtc: string;
  lastSeenUtc?: string | null;
  revokedUtc?: string | null;
  printerCount: number;
  localEndpoints?: string[];
}

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly code: string | null = null
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** A known API rejection with a safe next step; no print output was authorised. */
export class LocalUploadPreflightError extends ApiError {
  constructor(message: string, status: number, code: string | null) {
    super(message, status, code);
    this.name = "LocalUploadPreflightError";
  }
}

/** The Agent may have stored this file; the customer must retry the same ticket. */
export class LocalUploadOutcomeUnknownError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LocalUploadOutcomeUnknownError";
  }
}

const defaultApiBase = (import.meta.env.VITE_API_BASE_URL || "").replace(/\/$/, "");
let apiBase = defaultApiBase;
let desktopApiBaseRefresh: Promise<void> | null = null;
let lastDesktopApiBaseRefreshUtc = 0;

export function configureApiBaseUrl(value: string | null | undefined): boolean {
  if (!value) return false;
  try {
    const parsed = new URL(value);
    if (
      !(parsed.protocol === "https:" ||
        (parsed.protocol === "http:" && ["localhost", "127.0.0.1", "::1"].includes(parsed.hostname))) ||
      !parsed.hostname ||
      parsed.username ||
      parsed.password ||
      parsed.pathname !== "/" ||
      parsed.search ||
      parsed.hash
    ) return false;

    apiBase = parsed.origin;
    return true;
  } catch {
    // Keep the previously validated base URL if the local service returns invalid data.
    return false;
  }
}

async function refreshDesktopApiBaseUrl(force = false): Promise<void> {
  if (!isDesktopApp()) return;
  if (desktopApiBaseRefresh) return desktopApiBaseRefresh;
  if (!force && Date.now() - lastDesktopApiBaseRefreshUtc < 30_000) return;

  desktopApiBaseRefresh = getLocalAgentStatus()
    .then(status => {
      if (configureApiBaseUrl(status.apiBaseUrl)) {
        lastDesktopApiBaseRefreshUtc = Date.now();
      } else {
        // Retry shortly when the Windows service is still starting or not installed.
        lastDesktopApiBaseRefreshUtc = Date.now() - 25_000;
      }
    })
    .catch(() => {
      lastDesktopApiBaseRefreshUtc = Date.now() - 25_000;
    })
    .finally(() => {
      desktopApiBaseRefresh = null;
    });

  return desktopApiBaseRefresh;
}

let refreshInFlight: Promise<AuthTokens | null> | null = null;
let desktopSessionWrite: Promise<void> = Promise.resolve();

async function fetchApiResponse(path: string, options: RequestInit): Promise<Response> {
  const requestBase = apiBase;
  const method = (options.method || "GET").toUpperCase();

  try {
    return await fetch(`${requestBase}${path}`, options);
  } catch {
    // Refresh the destination after any desktop network failure so the next
    // explicit owner action uses the current URL. Only reads are safe to replay:
    // the server may have committed a write before its response was lost.
    const canRetry = ["GET", "HEAD", "OPTIONS"].includes(method);
    let destinationChanged = false;
    if (isDesktopApp()) {
      await refreshDesktopApiBaseUrl(true);
      destinationChanged = apiBase !== requestBase;
      if (canRetry && destinationChanged) {
        try {
          return await fetch(`${apiBase}${path}`, options);
        } catch {
          // Surface one stable network error below using the latest origin.
        }
      }
    }

    const message = !canRetry
      ? "The connection was lost before SmartPrint could confirm this action. Check the latest job or order status before trying again."
      : `Unable to reach Smart Print API at ${apiBase}. Check that the network is available.`;
    throw new ApiError(message, 0);
  }
}

async function refreshStoredSession(expectedAccessToken: string): Promise<AuthTokens | null> {
  const stored = readStoredTokens();
  if (!stored?.refreshToken) return null;

  // Another request may already have rotated the token while this request was
  // in flight. Reuse the newer access token instead of consuming its refresh
  // token again.
  if (stored.accessToken !== expectedAccessToken) {
    const expiresAt = Date.parse(stored.accessTokenExpiresUtc);
    if (Number.isFinite(expiresAt) && expiresAt > Date.now() + 15_000) {
      return stored;
    }
  }

  if (!refreshInFlight) {
    const source = stored;
    refreshInFlight = apiRequest<AuthTokens>("/api/v1/auth/refresh", {
      method: "POST",
      body: JSON.stringify({ refreshToken: source.refreshToken })
    }).then(async tokens => {
      const latest = readStoredTokens();
      if (!latest) return null;
      if (latest.refreshToken === source.refreshToken) {
        await storeTokens(tokens);
        return tokens;
      }
      return latest;
    }).catch(async error => {
      if (error instanceof ApiError && error.status === 401) {
        const latest = readStoredTokens();
        if (latest?.refreshToken === source.refreshToken) await storeTokens(null);
        return null;
      }
      throw error;
    }).finally(() => {
      refreshInFlight = null;
    });
  }

  return refreshInFlight;
}

export async function apiRequest<T>(
  path: string,
  options: RequestInit = {},
  accessToken?: string
): Promise<T> {
  await refreshDesktopApiBaseUrl();

  const send = async (bearerToken?: string): Promise<Response> => {
    const headers = new Headers(options.headers);
    headers.set("Accept", "application/json");
    if (bearerToken) headers.set("Authorization", `Bearer ${bearerToken}`);
    if (options.body && !(options.body instanceof FormData) && !headers.has("Content-Type")) {
      headers.set("Content-Type", "application/json");
    }

    return fetchApiResponse(path, { ...options, headers });
  };

  let response = await send(accessToken);
  if (response.status === 401 && accessToken) {
    const refreshed = await refreshStoredSession(accessToken);
    if (refreshed) {
      response = await send(refreshed.accessToken);
    }
  }

  const raw = await response.text();
  let payload: unknown = null;
  if (raw) {
    try { payload = JSON.parse(raw); } catch { payload = raw; }
  }

  if (!response.ok) {
    const body = payload as { code?: string; title?: string; detail?: string; message?: string; errors?: Record<string, string[]> } | null;
    const validation = body?.errors
      ? Object.values(body.errors).flat().join(" ")
      : "";
    throw new ApiError(
      validation || body?.detail || body?.message || body?.title || `Request failed (${response.status}).`,
      response.status,
      body?.code ?? null
    );
  }

  return payload as T;
}

function isSerializedPlatformAdmin(raw: string): boolean {
  try {
    const parsed = JSON.parse(raw) as AuthTokens;
    return isPlatformAdminSession(parsed);
  } catch {
    return false;
  }
}

export function readStoredTokens(scope: AuthScope = currentAuthScope()): AuthTokens | null {
  try {
    const scopedKey = authStorageKey(scope);
    const scoped = sessionStorage.getItem(scopedKey);
    if (scoped) return JSON.parse(scoped) as AuthTokens;

    // Migrate a pre-scope session once, but never copy it into the wrong
    // application. This keeps existing users signed in without allowing an
    // admin token to become a shop session (or vice versa).
    const legacy = sessionStorage.getItem("qrtoprint.auth");
    if (!legacy) return null;
    const legacyScope: AuthScope = isSerializedPlatformAdmin(legacy) ? "admin" : "shop";
    if (legacyScope !== scope) return null;
    sessionStorage.setItem(scopedKey, legacy);
    sessionStorage.removeItem("qrtoprint.auth");
    return JSON.parse(legacy) as AuthTokens;
  } catch {
    return null;
  }
}

export function storeTokens(tokens: AuthTokens | null, scope: AuthScope = currentAuthScope()): Promise<void> {
  const scopedKey = authStorageKey(scope);
  if (tokens) sessionStorage.setItem(scopedKey, JSON.stringify(tokens));
  else sessionStorage.removeItem(scopedKey);

  if (!isDesktopApp() || scope !== "shop") return Promise.resolve();

  // Serialize native writes so a late token-rotation write cannot overwrite a
  // newer sign-out or a newer session. Callers that rotate credentials can await
  // the write before allowing the next authenticated request.
  const serialized = tokens ? JSON.stringify(tokens) : null;
  desktopSessionWrite = desktopSessionWrite
    .catch(() => undefined)
    .then(() => writeDesktopSession(serialized));
  return desktopSessionWrite;
}

export const signIn = (email: string, password: string) =>
  apiRequest<AuthTokens>("/api/v1/auth/login", {
    method: "POST",
    body: JSON.stringify({ email, password })
  });

export async function registerShop(values: {
  businessName: string;
  ownerName: string;
  email: string;
  password: string;
}): Promise<AuthTokens> {
  const result = await apiRequest<{ tokens: AuthTokens }>("/api/v1/auth/register-shop", {
    method: "POST",
    body: JSON.stringify(values)
  });
  return result.tokens;
}

export interface ShopPaymentSettings {
  upiEnabled: boolean;
  upiId?: string | null;
  payeeName?: string | null;
  cashEnabled: boolean;
  instantPaymentEnabled: boolean;
  updatedUtc: string;
}

export interface ShopReportSummary {
  periodDays: number;
  fromUtc: string;
  toUtc: string;
  generatedUtc: string;
  totalJobs: number;
  completedJobs: number;
  outputUnconfirmedJobs: number;
  pagesRequested: number;
  pendingPaymentCount: number;
  jobsByStatus: { status: string; count: number }[];
  verifiedCollectionsByMethod: { method: string; currency: string; paymentCount: number; amountMinor: number }[];
  refundsRecordedByMethod: { method: string; currency: string; refundCount: number; amountMinor: number }[];
}
export interface ShopPayment {
  id: string;
  printJobId: string;
  jobNumber: string;
  documentName: string;
  origin?: "CUSTOMER_QR" | "SHOP_OWNER" | string;
  recipientName?: string | null;
  recipientPhone?: string | null;
  batchPaymentId?: string | null;
  batchFileCount?: number | null;
  method: "UPI" | "CASH";
  status: string;
  amountMinor: number;
  refundedAmountMinor?: number;
  currency: string;
  paymentReference?: string | null;
  upiIdSnapshot?: string | null;
  payeeNameSnapshot?: string | null;
  rejectionReason?: string | null;
  createdUtc: string;
  updatedUtc: string;
  verifiedUtc?: string | null;
}

export interface ShopSubscriptionPlan {
  id: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  version: number;
  status: string;
  currency: string;
  monthlyPriceMinor: number | null;
  annualPriceMinor: number | null;
  maxPrinters: number | null;
  maxAgentDevices: number | null;
  maxQrStations: number | null;
  maxMonthlyPrintJobs: number | null;
  maxDocumentBytes: number | null;
  cloudAssistedPrintingEnabled: boolean;
  whiteLabelEnabled: boolean;
  advancedReportsEnabled: boolean;
  publishedUtc: string | null;
}

export interface ShopSubscriptionSummary {
  generatedUtc: string;
  subscription: {
    id: string;
    planCode: string;
    status: string;
    billingInterval: string;
    periodStartUtc: string;
    periodEndUtc: string;
    createdUtc: string;
  };
  plan: ShopSubscriptionPlan | null;
  entitlements: {
    maxPrinters: number | null;
    maxAgentDevices: number | null;
    maxQrStations: number | null;
    maxMonthlyPrintJobs: number | null;
    maxDocumentBytes: number | null;
    cloudAssistedPrintingEnabled: boolean;
    whiteLabelEnabled: boolean;
    advancedReportsEnabled: boolean;
    isLegacyUnconfigured: boolean;
    isAccessActive: boolean;
  };
  usage: {
    printerCount: number;
    agentDeviceCount: number;
    qrStationCount: number;
    monthlyPrintJobCount: number;
    monthlyPeriodStartUtc: string;
    monthlyPeriodEndUtc: string;
  };
}

export interface ShopRefund {
  id: string;
  printJobId: string;
  printPaymentId: string;
  jobNumber: string;
  documentName: string;
  amountMinor: number;
  currency: string;
  method: "UPI" | "CASH";
  status: string;
  reference?: string | null;
  reason: string;
  refundedUtc: string;
  refundedByUserId: string;
  refundedByName: string;
}

export interface RecordShopRefundResult {
  id: string;
  printJobId: string;
  printPaymentId: string;
  jobNumber: string;
  amountMinor: number;
  currency: string;
  method: "UPI" | "CASH";
  status: string;
  reference?: string | null;
  reason: string;
  refundedUtc: string;
  refundedByUserId: string;
}

export interface ShopRefundPreview {
  id: string; pageCount: number; rejectedPageNumbers: number[];
  rejectedPageCount: number; acceptedPageCount: number;
  originalAmountMinor: number; previouslyRefundedAmountMinor: number;
  additionalRefundAmountMinor: number; cumulativeRefundAmountMinor: number;
  remainingPaidAmountMinor: number; currency: string;
}

export function previewShopPrintRefund(token: string, jobId: string, rejectedPageNumbers: number[]) {
  return apiRequest<ShopRefundPreview>(`/api/v1/shop/print-jobs/${jobId}/refund-preview`, {
    method: "POST", body: JSON.stringify({ rejectedPageNumbers })
  }, token);
}

export function recordShopPrintRefund(token: string, jobId: string, values: {
  refundMethod: "UPI" | "CASH";
  refundReference: string | null;
  reason: string;
  confirmedSent: true;
  idempotencyKey: string;
  rejectedPageNumbers: number[];
}) {
  return apiRequest<RecordShopRefundResult>(`/api/v1/shop/print-jobs/${jobId}/refund`, {
    method: "POST", body: JSON.stringify(values)
  }, token);
}

export function getShopSubscription(token: string) {
  return apiRequest<ShopSubscriptionSummary>("/api/v1/shop/subscription", {}, token);
}

export interface ShopSubscriptionCheckoutOptions {
  checkoutAvailable: boolean;
  plans: ShopSubscriptionPlan[];
}

export interface ShopSubscriptionOrder {
  paymentId: string;
  orderId: string;
  amountMinor: number;
  currency: string;
  keyId: string;
  planName: string;
  billingInterval: string;
  status: string;
}

export interface ShopSubscriptionPaymentStatus {
  paymentId: string;
  orderId: string;
  paymentIdAtGateway: string | null;
  amountMinor: number;
  currency: string;
  planCode: string;
  planName: string;
  billingInterval: string;
  status: string;
  createdUtc: string;
  capturedUtc: string | null;
  appliedUtc: string | null;
  appliedSubscriptionId: string | null;
}

export function getShopSubscriptionCheckoutOptions(token: string) {
  return apiRequest<ShopSubscriptionCheckoutOptions>("/api/v1/shop/subscription/plans", {}, token);
}

export function createShopSubscriptionOrder(token: string, values: {
  planVersionId: string;
  billingInterval: "Monthly" | "Annual";
  idempotencyKey: string;
}) {
  return apiRequest<ShopSubscriptionOrder>("/api/v1/shop/subscription/payments/orders", {
    method: "POST", body: JSON.stringify(values)
  }, token);
}

export function verifyShopSubscriptionPayment(token: string, values: {
  orderId: string;
  paymentId: string;
  signature: string;
}) {
  return apiRequest<ShopSubscriptionPaymentStatus>("/api/v1/shop/subscription/payments/verify", {
    method: "POST", body: JSON.stringify(values)
  }, token);
}

export function getShopSubscriptionPaymentStatus(token: string, paymentId: string) {
  return apiRequest<ShopSubscriptionPaymentStatus>(
    `/api/v1/shop/subscription/payments/${encodeURIComponent(paymentId)}`, {}, token
  );
}

export function getShopPayments(token: string) {
  return apiRequest<ShopPayment[]>("/api/v1/shop/payments", {}, token);
}

export async function loadShopData(token: string, queueSearch = "") {
  const [profile, printers, stations, pricing, printJobs, agentDevices, paymentSettings, payments, refunds, subscription] = await Promise.all([
    apiRequest<ShopProfile>("/api/v1/shop/profile", {}, token),
    getShopPrinters(token),
    apiRequest<QrStation[]>("/api/v1/shop/qr-stations", {}, token),
    apiRequest<ShopPricingProfile>("/api/v1/shop/pricing", {}, token),
    getShopPrintJobsPage(token, 0, 100, queueSearch),
    getShopAgentDevices(token),
    apiRequest<ShopPaymentSettings>("/api/v1/shop/payment-settings", {}, token),
    getShopPayments(token),
    apiRequest<ShopRefund[]>("/api/v1/shop/refunds", {}, token),
    getShopSubscription(token)
  ]);
  return { profile, printers, stations, pricing, printJobs, agentDevices, paymentSettings, payments, refunds, subscription };
}

export interface ShopCustomer {
  id: string;
  displayName: string;
  maskedEmail: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
  sessionCount: number;
  printJobCount: number;
  completedJobCount: number;
  lastPrintUtc?: string | null;
}

export interface ShopCustomerPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: ShopCustomer[];
}

export interface ShopCustomerJob {
  id: string;
  jobNumber: string;
  status: string;
  createdUtc: string;
  pageCount: number;
  copies: number;
  colourMode: string;
  paperSize: string;
  isDuplex: boolean;
  printKind?: string;
  photoSizeCode?: string | null;
  photoCount?: number;
  photosPerPage?: number | null;
  photoUnitPriceMinorSnapshot?: number;
  currency: string;
  quotedAmountMinor: number;
  rejectedPageNumbersJson: string;
  outputType: string;
  outputConfirmedUtc?: string | null;
  outputCompletedUtc?: string | null;
  document: { originalFileName: string; mediaType: string; sizeBytes: number };
  payment?: {
    status: string;
    method: string;
    amountMinor: number;
    refundedAmountMinor: number;
    currency: string;
    verifiedUtc?: string | null;
  } | null;
}

export interface ShopCustomerHistory {
  customer: { id: string; displayName: string; maskedEmail: string };
  totalJobs: number;
  jobs: ShopCustomerJob[];
}

export function getShopCustomers(token: string, values: {
  search?: string;
  page?: number;
  pageSize?: number;
}) {
  const params = new URLSearchParams();
  if (values.search?.trim()) params.set("search", values.search.trim());
  params.set("page", String(values.page ?? 1));
  params.set("pageSize", String(values.pageSize ?? 20));
  return apiRequest<ShopCustomerPage>(`/api/v1/shop/customers?${params.toString()}`, {}, token);
}

export function getShopCustomerHistory(token: string, customerId: string, take = 50) {
  return apiRequest<ShopCustomerHistory>(
    `/api/v1/shop/customers/${encodeURIComponent(customerId)}/jobs?take=${take}`,
    {},
    token
  );
}

export interface ShopPhotoJobResult {
  job: {
    id: string;
    jobNumber: string;
    status: string;
    origin: "SHOP_OWNER" | string;
    recipientName: string;
    recipientPhone?: string | null;
    customerUserId?: string | null;
    printKind: "PHOTO" | string;
    photoSizeCode: string;
    photoCount: number;
    photosPerPage: number;
    pageCount: number;
    photoUnitPriceMinorSnapshot: number;
    quotedAmountMinor: number;
    currency: string;
    quoteExpiresUtc: string;
    createdUtc: string;
  };
  payment: {
    id: string;
    method: "UPI" | "CASH";
    status: string;
    amountMinor: number;
    currency: string;
    upiIdSnapshot?: string | null;
    payeeNameSnapshot?: string | null;
  };
  document: {
    id: string;
    originalFileName: string;
    mediaType: string;
    sizeBytes: number;
    sha256: string;
    pageSizesJson: string;
  };
  duplicateRequest: boolean;
}

export async function createShopPhotoJob(token: string, file: File, values: {
  photoSizeCode: string;
  photoCount: number;
  photosPerPage: number;
  recipientType: "EXISTING_CUSTOMER" | "WALK_IN";
  customerUserId?: string;
  recipientName?: string;
  recipientPhone?: string;
  paymentMethod: "UPI" | "CASH";
  idempotencyKey: string;
}) {
  const form = new FormData();
  form.append("file", file, file.name);
  form.append("photoSizeCode", values.photoSizeCode);
  form.append("photoCount", String(values.photoCount));
  form.append("photosPerPage", String(values.photosPerPage));
  form.append("recipientType", values.recipientType);
  form.append("paymentMethod", values.paymentMethod);
  form.append("idempotencyKey", values.idempotencyKey);
  if (values.customerUserId) form.append("customerUserId", values.customerUserId);
  if (values.recipientName) form.append("recipientName", values.recipientName);
  if (values.recipientPhone) form.append("recipientPhone", values.recipientPhone);
  return apiRequest<ShopPhotoJobResult>("/api/v1/shop/photo-jobs", {
    method: "POST",
    body: form
  }, token);
}

export function getShopReportSummary(token: string, days: 7 | 30 | 90) {
  return apiRequest<ShopReportSummary>(`/api/v1/shop/reports/summary?days=${days}`, {}, token);
}
export function createPrinter(token: string, values: {
  displayName: string;
  manufacturer: string;
  model: string;
  connectionType: string;
  connectionAddress?: string;
  queueName?: string;
}) {
  return apiRequest<PrinterDevice>("/api/v1/shop/printers", {
    method: "POST",
    body: JSON.stringify({ ...values, capabilitiesJson: "{}" })
  }, token);
}

export function createStation(token: string, values: { displayName: string; printerDeviceId?: string }) {
  return apiRequest<QrStation & { publicUrl: string }>("/api/v1/shop/qr-stations", {
    method: "POST",
    body: JSON.stringify(values)
  }, token);
}

export function rotateStationCode(token: string, stationId: string) {
  return apiRequest<{ id: string; publicUrl: string; publicCodeHint: string }>(
    `/api/v1/shop/qr-stations/${stationId}/rotate-code`,
    { method: "POST" },
    token
  );
}

export function updateStationEnabled(token: string, stationId: string, isEnabled: boolean) {
  return apiRequest<{ id: string; isEnabled: boolean }>(
    `/api/v1/shop/qr-stations/${stationId}/enabled?isEnabled=${isEnabled}`,
    { method: "PATCH" },
    token
  );
}

export function updatePrinterActive(token: string, printerId: string, isActive: boolean) {
  return apiRequest<{ id: string; isActive: boolean }>(
    `/api/v1/shop/printers/${printerId}/active?isActive=${isActive}`,
    { method: "PATCH" },
    token
  );
}

export function updateShopProfile(token: string, values: { displayName: string; logoUrl: string | null; accentColor: string }) {
  return apiRequest<Partial<ShopProfile>>("/api/v1/shop/profile", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export interface ShopOwnerAccountUpdate {
  ownerName: string;
  ownerEmail: string;
  emailChanged: boolean;
}

export function updateShopOwnerAccount(token: string, values: {
  ownerName: string;
  email: string;
  currentPassword?: string;
}) {
  return apiRequest<ShopOwnerAccountUpdate>("/api/v1/shop/account", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function logout(token: string) {
  const stored = readStoredTokens();
  if (!stored) return Promise.resolve();
  return apiRequest<void>("/api/v1/auth/logout", {
    method: "POST",
    body: JSON.stringify({ refreshToken: stored.refreshToken })
  }, token);
}

async function nativeCustomerBridgeRequest(message: string): Promise<string> {
  try {
    const bridge = (window as Window & {
      SmartPrintNative?: {
        postMessage: (message: string) => void;
        addEventListener: (type: string, listener: (event: MessageEvent) => void) => void;
        removeEventListener: (type: string, listener: (event: MessageEvent) => void) => void;
      };
    }).SmartPrintNative;
    if (!bridge?.postMessage || !bridge.addEventListener || !bridge.removeEventListener) return "";
    return await new Promise<string>((resolve) => {
      let settled = false;
      const finish = (value: string) => {
        if (settled) return;
        settled = true;
        window.clearTimeout(timeout);
        bridge.removeEventListener("message", onMessage);
        resolve(value);
      };
      const onMessage = (event: MessageEvent) => {
        const value = typeof event.data === "string" ? event.data : "";
        // Native Wi-Fi permission replies use a reserved prefix and must never be
        // mistaken for an access token or station-session response.
        if (value.startsWith("localNetworkAccess:")) return;
        finish(value);
      };
      const timeout = window.setTimeout(() => finish(""), 10000);
      bridge.addEventListener("message", onMessage);
      bridge.postMessage(message);
    });
  } catch {
    return "";
  }
}

async function nativeCustomerAuthorizationHeaders(): Promise<Record<string, string>> {
  const token = await nativeCustomerBridgeRequest("getCustomerAccessToken");
  return token ? { Authorization: `Bearer ${token}` } : {};
}

export function getNativeCustomerPrintSession(publicCode: string): Promise<string> {
  return nativeCustomerBridgeRequest(`getCustomerPrintSession|${publicCode}`);
}

export async function saveNativeCustomerPrintSession(publicCode: string, token: string): Promise<boolean> {
  return (await nativeCustomerBridgeRequest(`saveCustomerPrintSession|${publicCode}|${token}`)) === "OK";
}

export async function saveNativeCustomerShopProfile(
  publicCode: string,
  shop: { displayName: string; logoUrl?: string | null; accentColor?: string | null }
): Promise<boolean> {
  const payload = encodeURIComponent(JSON.stringify({
    displayName: shop.displayName,
    logoUrl: shop.logoUrl ?? null,
    accentColor: shop.accentColor ?? null
  }));
  return (await nativeCustomerBridgeRequest(`setCustomerShopProfile|${publicCode}|${payload}`)) === "OK";
}

export async function clearNativeCustomerPrintSession(publicCode: string): Promise<void> {
  await nativeCustomerBridgeRequest(`clearCustomerPrintSession|${publicCode}`);
}

export async function createCustomerSession(publicCode: string) {
  return apiRequest<{
    id: string;
    status: string;
    expiresUtc: string;
    sessionToken: string;
    shop: { displayName: string; logoUrl?: string | null; accentColor: string; currency: string };
    printer: { displayName: string; manufacturer: string; model: string; status: string; capabilitiesJson: string } | null;
    photoPricing: CustomerPhotoPricing | null;
    paymentOptions: { upiEnabled: boolean; upiId?: string | null; payeeName?: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean };
    localAgent: { agentDeviceId: string; endpoints: string[] } | null;
    cloudAssistedPrintingEnabled: boolean;
  }>(`/api/v1/public/stations/${encodeURIComponent(publicCode)}/sessions`, {
    method: "POST",
    headers: await nativeCustomerAuthorizationHeaders()
  });
}


export async function renewCustomerPrintSession(sessionToken: string) {
  return apiRequest<{
    expiresUtc: string;
    localAgent: { agentDeviceId: string; endpoints: string[] } | null;
    paymentOptions: { upiEnabled: boolean; upiId?: string | null; payeeName?: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean };
  }>("/api/v1/public/sessions/current/renew", {
    method: "POST",
    headers: { "X-Print-Session": sessionToken, ...await nativeCustomerAuthorizationHeaders() }
  });
}

export function resumeCustomerSession(sessionToken: string, publicCode: string) {
  return apiRequest<{
    id: string;
    status: string;
    expiresUtc: string;
    sessionToken: string;
    shop: { displayName: string; logoUrl?: string | null; accentColor: string; currency: string };
    paymentOptions: { upiEnabled: boolean; upiId?: string | null; payeeName?: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean };
    printer: { displayName: string; manufacturer: string; model: string; status: string; capabilitiesJson: string } | null;
    photoPricing: CustomerPhotoPricing | null;
    localAgent: { agentDeviceId: string; endpoints: string[] } | null;
    cloudAssistedPrintingEnabled: boolean;
  }>("/api/v1/public/sessions/current/resume", {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ publicCode })
  });
}

export interface CustomerPhotoPricing { photo35x45Minor:number; photo25x35Minor:number; photo2x2Minor:number; photo4x6Minor:number; }
export interface ShopPricingProfile {
  isConfigured: boolean;
  monoPerSideMinor: number;
  colourPerSideMinor: number;
  monoDuplexPerSheetMinor: number;
  colourDuplexPerSheetMinor: number;
  minimumChargeMinor: number;
  photo35x45Minor: number;
  photo25x35Minor: number;
  photo2x2Minor: number;
  photo4x6Minor: number;
  version: number;
  updatedUtc: string;
}

export interface ShopPrintJob {
  id: string;
  batchId?: string | null;
  batchGroupName?: string | null;
  batchName?: string | null;
  rejectedPageNumbersJson?: string;
  origin?: "CUSTOMER_QR" | "SHOP_OWNER" | string;
  customerUserId?: string | null;
  recipientName?: string | null;
  recipientPhone?: string | null;
  jobNumber: string;
  status: string;
  pageCount: number;
  copies: number;
  colourMode: string;
  paperSize: string;
  isDuplex: boolean;
  orientation?: string;
  pdfBrandingEnabled?: boolean;
  pricingVersion?: number;
  printKind?: string;
  photoSizeCode?: string | null;
  photoCount?: number;
  photosPerPage?: number | null;
  photoUnitPriceMinorSnapshot?: number;
  currency: string;
  quotedAmountMinor: number;
  quoteExpiresUtc?: string | null;
  createdUtc: string;
  printerDeviceId?: string | null;
  printerName?: string | null;
  queueName?: string | null;
  printerAgentName?: string | null;
  printerSnapshotUtc?: string | null;
  outputConfirmedByName?: string | null;
  storageMode?: "LOCAL_AGENT" | "CLOUD" | string;
  documentAgentDeviceId?: string | null;
  documentAgentDeviceName?: string | null;
  printerStatus?: string | null;
  claimedByAgentDeviceId?: string | null;
  agentLeaseExpiresUtc?: string | null;
  localQueueReceivedUtc?: string | null;
  printDispatchLeaseExpiresUtc?: string | null;
  spoolerJobId?: number | null;
  spoolerJobName?: string | null;
  spoolerStatus?: string | null;
  spoolerSubmittedUtc?: string | null;
  spoolerObservedUtc?: string | null;
  outputConfirmedUtc?: string | null;
  failureCode?: string | null;
  outputMode?: "PHYSICAL" | "PDF_EXPORT" | string;
  outputType?: "PHYSICAL" | "PDF_EXPORT" | string;
  outputFilePath?: string | null;
  outputFileName?: string | null;
  outputSha256?: string | null;
  outputSizeBytes?: number | null;
  outputCompletedUtc?: string | null;
  payment?: { id: string; status: string; amountMinor: number; currency: string; method: string } | null;
  document: { originalFileName: string; mediaType: string; sizeBytes: number };
}

export interface UploadedPrintJob {
  job: {
    id: string;
    jobNumber: string;
    status: string;
    pageCount: number;
    copies: number;
    colourMode?: string;
    paperSize?: string;
    isDuplex?: boolean;
    orientation?: string;
    pricingVersion?: number;
    currency: string;
    quotedAmountMinor: number;
    createdUtc: string;
    batchGroupName?: string | null;
    batchName?: string | null;
    batchId?: string | null;
    printKind?: string;
    photoSizeCode?: string | null;
    photoCount?: number;
    photosPerPage?: number | null;
    photoUnitPriceMinorSnapshot?: number;
  };
  document: { id: string; originalFileName: string; mediaType: string; sizeBytes: number; sha256: string; pageSizesJson: string };
  duplicateRequest: boolean;
}

export interface PrintQuoteResult {
  id: string;
  batchId?: string | null;
  jobNumber: string;
  status: string;
  pageCount: number;
  copies: number;
  colourMode: string;
  paperSize: string;
  isDuplex: boolean;
  orientation?: string;
  printKind?: "DOCUMENT" | "PHOTO";
  photoSizeCode?: string | null;
  photoCount?: number;
  photosPerPage?: number | null;
  photoUnitPriceMinor?: number;
  currency: string;
  quotedAmountMinor: number;
  amountMajor: number;
  pricingVersion: number;
  quoteExpiresUtc: string;
}


export function updateShopPricing(token: string, values: {
  monoPerSideMinor: number;
  colourPerSideMinor: number;
  monoDuplexPerSheetMinor: number;
  colourDuplexPerSheetMinor: number;
  minimumChargeMinor: number;
  photo35x45Minor: number;
  photo25x35Minor: number;
  photo2x2Minor: number;
  photo4x6Minor: number;
}) {
  return apiRequest<ShopPricingProfile>("/api/v1/shop/pricing", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

function customerUploadAttemptStorageKey(sessionToken: string, file: File): string {
  // Hash file metadata rather than storing the original filename in the key.
  // This lets a re-selected file reuse its in-flight key after a page reload,
  // while a different file gets its own key.
  const relativePath = (file as File & { webkitRelativePath?: string }).webkitRelativePath ?? "";
  const nativeGroup = (file as File & { __smartPrintBatchGroup?: string }).__smartPrintBatchGroup ?? "";
  const identity = `${relativePath || nativeGroup}\u0000${file.name}\u0000${file.size}\u0000${file.lastModified}\u0000${file.type}`;
  let first = 2166136261;
  let second = 5381;
  for (let index = 0; index < identity.length; index++) {
    const code = identity.charCodeAt(index);
    first = Math.imul(first ^ code, 16777619);
    second = Math.imul(second ^ code, 33);
  }
  const fingerprint = `${(first >>> 0).toString(36)}${(second >>> 0).toString(36)}`;
  return `qrtoprint.customer-upload-attempt.${sessionToken.slice(0, 16)}.${fingerprint}`;
}

export interface CustomerPrintRequestSettings {
  copies: number;
  colourMode: "Mono" | "Colour";
  paperSize: "A4" | "A3" | "A5" | "LETTER" | "LEGAL";
  isDuplex: boolean;
  orientation: "AUTO" | "PORTRAIT" | "LANDSCAPE";
}

type CustomerLocalUploadTicket = {
  id: string;
  printJobId: string;
  batchId?: string;
  batchGroupName?: string | null;
  batchName?: string | null;
  jobNumber: string;
  agentDeviceId: string;
  expiresUtc: string;
  completed: boolean;
};

async function requestCustomerLocalUploadTicket(
  sessionToken: string,
  file: File,
  batchId: string | undefined,
  batchGroupName: string | undefined,
  batchName: string | undefined,
  idempotencyKey: string,
  grantToken: string,
  settings: CustomerPrintRequestSettings
): Promise<CustomerLocalUploadTicket> {
  const digest = await crypto.subtle.digest("SHA-256", await file.arrayBuffer());
  const sha256 = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
  return apiRequest<CustomerLocalUploadTicket>("/api/v1/public/sessions/current/local-upload-tickets", {
    method: "POST",
    headers: {
      "X-Print-Session": sessionToken,
      "Idempotency-Key": idempotencyKey
    },
    body: JSON.stringify({
      originalFileName: file.name,
      sizeBytes: file.size,
      sha256,
      grantToken,
      batchId: batchId ?? null,
      batchGroupName: batchGroupName ?? null,
      batchName: batchName?.trim() || null,
      colourMode: settings.colourMode,
      paperSize: settings.paperSize,
      isDuplex: settings.isDuplex,
      copies: settings.copies,
      orientation: settings.orientation
    })
  });
}

async function uploadCustomerLocalTicketViaCloud(
  sessionToken: string,
  file: File,
  ticket: CustomerLocalUploadTicket,
  grantToken: string
): Promise<UploadedPrintJob> {
  const form = ticket.completed ? undefined : new FormData();
  if (form) form.append("file", file, file.name);
  return apiRequest<UploadedPrintJob>(
    `/api/v1/public/sessions/current/local-upload-tickets/${encodeURIComponent(ticket.id)}/cloud-recovery`,
    {
      method: "POST",
      headers: {
        "X-Print-Session": sessionToken,
        "X-Print-Upload-Grant": grantToken
      },
      body: form
    }
  );
}

export async function uploadCustomerDocument(
  sessionToken: string,
  file: File,
  batchId?: string,
  batchGroupName?: string,
  batchName?: string,
  settings: CustomerPrintRequestSettings = { copies: 1, colourMode: "Mono", paperSize: "A4", isDuplex: false, orientation: "AUTO" }
) {
  const localKeys = customerLocalUploadAttemptStorageKeys(sessionToken, file);
  const pendingLocalIdempotency = sessionStorage.getItem(localKeys.idempotency);
  const pendingLocalGrant = sessionStorage.getItem(localKeys.grant);
  if (pendingLocalIdempotency && pendingLocalGrant) {
    // The local request may have reached the Agent. Recover that exact ticket
    // using HTTPS, retaining its PrintJob ID and one-use grant.
    const ticket = await requestCustomerLocalUploadTicket(
      sessionToken, file, batchId, batchGroupName, batchName, pendingLocalIdempotency, pendingLocalGrant, settings
    );
    const recovered = await uploadCustomerLocalTicketViaCloud(
      sessionToken, file, ticket, pendingLocalGrant
    );
    // Release the retry-only grant after a confirmed recovery. The customer
    // draft stores the canonical job, and a later intentional reprint needs a
    // fresh ticket. Ambiguous failures leave the grant untouched.
    clearCustomerLocalUploadAttempt(sessionToken, file);
    return recovered;
  }

  const storageKey = customerUploadAttemptStorageKey(sessionToken, file);
  const idempotencyKey = sessionStorage.getItem(storageKey) || crypto.randomUUID();
  sessionStorage.setItem(storageKey, idempotencyKey);

  const form = new FormData();
  form.append("file", file, file.name);
  if (batchGroupName) form.append("batchGroupName", batchGroupName);
  if (batchName?.trim()) form.append("batchName", batchName.trim());
  form.append("colourMode", settings.colourMode);
  form.append("paperSize", settings.paperSize);
  form.append("isDuplex", String(settings.isDuplex));
  form.append("copies", String(settings.copies));
  form.append("orientation", settings.orientation);
  const uploaded = await apiRequest<UploadedPrintJob>("/api/v1/public/sessions/current/jobs", {
    method: "POST",
    headers: {
      "X-Print-Session": sessionToken,
      "Idempotency-Key": idempotencyKey,
      ...(batchId ? { "X-Print-Batch-Id": batchId } : {})
    },
    body: form
  });

  sessionStorage.removeItem(storageKey);
  return uploaded;
}

type CustomerLocalAgentApi = { agentDeviceId: string; endpoints: string[] };

function customerLocalUploadAttemptStorageKeys(sessionToken: string, file: File) {
  const key = customerUploadAttemptStorageKey(sessionToken, file) + ".local";
  return { idempotency: key + ".key", grant: key + ".grant" };
}

function clearCustomerLocalUploadAttempt(sessionToken: string, file: File): void {
  const keys = customerLocalUploadAttemptStorageKeys(sessionToken, file);
  sessionStorage.removeItem(keys.idempotency);
  sessionStorage.removeItem(keys.grant);
}

export function hasPendingLocalCustomerUploadAttempt(sessionToken: string, file: File): boolean {
  const keys = customerLocalUploadAttemptStorageKeys(sessionToken, file);
  return Boolean(sessionStorage.getItem(keys.idempotency) && sessionStorage.getItem(keys.grant));
}

function createLocalUploadGrantToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function isPrivateLocalAgentEndpoint(value: string): URL | null {
  try {
    const endpoint = new URL(value);
    const octets = endpoint.hostname.split(".").map(Number);
    const privateIpv4 = octets.length === 4 &&
      octets.every(part => Number.isInteger(part) && part >= 0 && part <= 255) &&
      (octets[0] === 10 ||
        (octets[0] === 172 && octets[1] >= 16 && octets[1] <= 31) ||
        (octets[0] === 192 && octets[1] === 168));
    if (endpoint.protocol !== "http:" || endpoint.port !== "17842" ||
        endpoint.username || endpoint.password || endpoint.pathname !== "/" ||
        endpoint.search || endpoint.hash || !privateIpv4) return null;
    return endpoint;
  } catch {
    return null;
  }
}

export async function uploadCustomerDocumentLocally(
  sessionToken: string,
  file: File,
  localAgent: CustomerLocalAgentApi,
  batchId?: string,
  batchGroupName?: string,
  batchName?: string,
  cloudAssistedPrintingEnabled = false,
  settings: CustomerPrintRequestSettings = { copies: 1, colourMode: "Mono", paperSize: "A4", isDuplex: false, orientation: "AUTO" }
): Promise<UploadedPrintJob> {
  const keys = customerLocalUploadAttemptStorageKeys(sessionToken, file);
  const idempotencyKey = sessionStorage.getItem(keys.idempotency) || crypto.randomUUID();
  const grantToken = sessionStorage.getItem(keys.grant) || createLocalUploadGrantToken();
  sessionStorage.setItem(keys.idempotency, idempotencyKey);
  sessionStorage.setItem(keys.grant, grantToken);

  let ticket: CustomerLocalUploadTicket;
  try {
    ticket = await requestCustomerLocalUploadTicket(
      sessionToken, file, batchId, batchGroupName, batchName, idempotencyKey, grantToken, settings
    );
  } catch (exception) {
    if (exception instanceof ApiError && exception.status === 409 &&
        ["local_print_agent_unavailable", "local_print_printer_unavailable", "local_upload_printer_unavailable", "local_upload_printer_assignment_missing", "batch_local_agent_mismatch"].includes(exception.code ?? "")) {
      // These are explicit preflight rejections; the API did not issue a ticket.
      sessionStorage.removeItem(keys.idempotency);
      sessionStorage.removeItem(keys.grant);
      throw new LocalUploadPreflightError(exception.message, exception.status, exception.code);
    }
    if (!(exception instanceof ApiError) || exception.status === 0) {
      // Fetch may fail with TypeError/AbortError rather than an ApiError. The
      // POST may nevertheless have committed; retain the key and grant so the
      // Cloud-assisted route can retrieve and resume that exact ticket.
      throw new LocalUploadOutcomeUnknownError(
        "SmartPrint lost the response while preparing this upload. The original upload identity is saved; choose Cloud-assisted · HTTPS or retry this file and SmartPrint will resume the same job."
      );
    }
    throw exception;
  }

  if (isUncompletedLocalUploadTicketExpired(ticket.completed, ticket.expiresUtc)) {
    // Keep the original ticket/grant: its monthly quota and batch capacity are
    // still reserved during the 24-hour recovery window. First let the API
    // recover this exact file over HTTPS if the paired Agent is unavailable or
    // the shop has cloud-assisted printing. If the Agent is still online on a
    // plan without that entitlement, the ticket endpoint renews its LAN lease.
    try {
      const recovered = await uploadCustomerLocalTicketViaCloud(
        sessionToken, file, ticket, grantToken
      );
      clearCustomerLocalUploadAttempt(sessionToken, file);
      return recovered;
    } catch (exception) {
      if (exception instanceof ApiError &&
          exception.status === 409 &&
          exception.code === "local_upload_recovery_expired") {
        // The server's 24-hour recovery reservation has ended; this identity
        // can no longer materialize a job, so it is safe to start a new attempt.
        clearCustomerLocalUploadAttempt(sessionToken, file);
        throw new LocalUploadPreflightError(
          "The previous shop Wi-Fi upload attempt is too old to recover. Retry this file to start a fresh upload.",
          409,
          "local_upload_ticket_expired"
        );
      }
      if (exception instanceof ApiError &&
          exception.status === 409 &&
          exception.code === "batch_upload_locked_after_payment_start") {
        // Checkout intentionally excludes this unfinished file. Do not keep
        // offering it as part of a batch whose payment total is already locked.
        clearCustomerLocalUploadAttempt(sessionToken, file);
        throw new LocalUploadPreflightError(
          "Checkout has already started for this batch, so this unfinished file was not included. Start a new batch for this file.",
          409,
          "batch_upload_locked_after_payment_start"
        );
      }
      // A network interruption or reconciliation conflict is not proof that
      // the original ticket is dead. Preserve its idempotency key and grant.
      throw new LocalUploadOutcomeUnknownError(
        "SmartPrint is checking the original upload. Its ticket and secure retry identity are preserved; retry this same file to continue without creating a second print job."
      );
    }
  }

  if (ticket.completed) {
    // The document/job already exists. Reconcile its canonical status over the
    // API without retransmitting bytes or requiring Cloud-assisted new uploads.
    try {
      const completed = await uploadCustomerLocalTicketViaCloud(
        sessionToken, file, ticket, grantToken
      );
      clearCustomerLocalUploadAttempt(sessionToken, file);
      return {
        ...completed,
        job: {
          ...completed.job,
          batchId: ticket.batchId ?? completed.job.batchId ?? null,
          batchGroupName: ticket.batchGroupName ?? completed.job.batchGroupName ?? null,
          batchName: ticket.batchName ?? completed.job.batchName ?? null
        }
      };
    } catch {
      throw new LocalUploadOutcomeUnknownError(
        "SmartPrint has your original upload recorded. The shop connection could not refresh its status just now; retry this same file to restore the existing print request. No new upload or print job has been created."
      );
    }
  }

  const form = new FormData();
  form.append("file", file, file.name);
  if (ticket.agentDeviceId === localAgent.agentDeviceId) {
    for (const value of localAgent.endpoints.slice(0, 8)) {
    const endpoint = isPrivateLocalAgentEndpoint(value);
    if (!endpoint) continue;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 90_000);
    try {
      const response = await fetch(`${endpoint.origin}/api/v1/local/uploads/${encodeURIComponent(ticket.id)}`, {
        method: "POST",
        mode: "cors",
        cache: "no-store",
        credentials: "omit",
        headers: { "X-Print-Upload-Grant": grantToken },
        body: form,
        signal: controller.signal,
        targetAddressSpace: "local"
      } as RequestInit & { targetAddressSpace: "local" });

      const raw = await response.text();
      let payload: unknown = null;
      if (raw) {
        try { payload = JSON.parse(raw); } catch { payload = raw; }
      }
      if (!response.ok) {
        // Continue to the next advertised shop address before using HTTPS recovery.
        continue;
      }

      // A confirmed response is now represented by the canonical job in the
      // customer batch draft. The retry-only grant can be released safely.
      const uploaded = payload as UploadedPrintJob;
      if (!uploaded?.job?.id || !uploaded?.document?.id) continue;
      clearCustomerLocalUploadAttempt(sessionToken, file);
      return {
        ...uploaded,
        job: {
          ...uploaded.job,
          batchId: ticket.batchId ?? uploaded.job.batchId ?? null,
          batchGroupName: ticket.batchGroupName ?? uploaded.job.batchGroupName ?? null,
          batchName: ticket.batchName ?? uploaded.job.batchName ?? null
        }
      };
    } catch {
      // Try the next advertised shop address, then continue over HTTPS if enabled.
    } finally {
      window.clearTimeout(timeout);
    }
    }
  }

  // A LAN response can be lost after the Agent has safely staged the bytes.
  // HTTPS recovery reconciles this exact ticket and can never create another PrintJob.
  try {
      const recovered = await uploadCustomerLocalTicketViaCloud(
        sessionToken, file, ticket, grantToken
      );
      clearCustomerLocalUploadAttempt(sessionToken, file);
      return {
        ...recovered,
        job: {
          ...recovered.job,
          batchId: ticket.batchId ?? recovered.job.batchId ?? null,
          batchGroupName: ticket.batchGroupName ?? recovered.job.batchGroupName ?? null,
          batchName: ticket.batchName ?? recovered.job.batchName ?? null
        }
      };
    } catch (recoveryError) {
      const detail = recoveryError instanceof ApiError
        ? " HTTPS continuation could not be confirmed ("+recoveryError.message+")."
        : " HTTPS continuation could not be confirmed.";
      throw new LocalUploadOutcomeUnknownError(
        "The shop Wi-Fi connection was interrupted."+detail+
        " SmartPrint retained the original upload ticket and is ready to resume the same file. "+
        (cloudAssistedPrintingEnabled ? "HTTPS recovery was attempted. " : "Secure online recovery was attempted. ")+
        "Retry this file in the same batch; no second print job will be created."
      );
  }

  throw new LocalUploadOutcomeUnknownError(
    "The shop Wi-Fi transfer could not be confirmed. SmartPrint retained the original upload ticket. Reconnect to the shop computer and retry the same file in this batch."
  );
}

export function calculateCustomerQuote(sessionToken: string, jobId: string, values: {
  colourMode: "Mono" | "Colour";
  paperSize: "A4" | "A3" | "A5" | "LETTER" | "LEGAL";
  isDuplex: boolean;
  copies: number;
  orientation?: "AUTO" | "PORTRAIT" | "LANDSCAPE";
  printKind?: "DOCUMENT" | "PHOTO";
  photoSizeCode?: string;
  photoCount?: number;
  photosPerPage?: number;
}) {
  return apiRequest<PrintQuoteResult>(`/api/v1/public/jobs/${jobId}/quote`, {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify(values)
  });
}

export interface CustomerJobStatus {
  id: string;
  jobNumber: string;
  status: string;
  pageCount: number;
  copies: number;
  colourMode: string;
  paperSize: string;
  isDuplex: boolean;
  orientation?: string;
  pricingVersion?: number;
  // Photo quotes retain the configured per-photo rate used at quote time.
  printKind?: string;
  photoSizeCode?: string | null;
  photoCount?: number;
  photosPerPage?: number | null;
  photoUnitPriceMinorSnapshot?: number;
  currency: string;
  quotedAmountMinor: number;
  quoteExpiresUtc?: string | null;
  rejectedPageNumbersJson?: string;
  pdfBrandingEnabled?: boolean;
  updatedUtc: string;
  timeline?: Array<{ newStatus: string; actorType: string; createdUtc: string }>;
  payment?: CustomerPayment | null;
}

export function acceptCustomerQuote(sessionToken: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; currency: string; quotedAmountMinor: number; updatedUtc: string }>(
    `/api/v1/public/jobs/${jobId}/accept-quote`,
    { method: "POST", headers: { "X-Print-Session": sessionToken } }
  );
}

export function getCustomerJobStatus(sessionToken: string, jobId: string) {
  return apiRequest<{
    job: Omit<CustomerJobStatus, "timeline">;
    timeline: NonNullable<CustomerJobStatus["timeline"]>;
    payment?: CustomerPayment | null;
  }>(
    `/api/v1/public/jobs/${jobId}/status`,
    { headers: { "X-Print-Session": sessionToken } }
  ).then(result => ({ ...result.job, timeline: result.timeline, payment: result.payment ?? null }));
}

export interface ShopJobEvent {
  id: string;
  previousStatus?: string | null;
  newStatus: string;
  actorType: string;
  reasonCode?: string | null;
  createdUtc: string;
}


export function assignShopPrintJobPrinter(token: string, jobId: string, printerDeviceId: string) {
  return apiRequest<{
    id: string;
    status: string;
    batchId?: string | null;
    printerDeviceId: string;
    printerName: string;
    queueName?: string | null;
    agentDeviceId: string;
    assignedJobCount: number;
    updatedUtc: string;
  }>(`/api/v1/shop/print-jobs/${jobId}/printer`, {
    method: "PATCH",
    body: JSON.stringify({ printerDeviceId })
  }, token);
}


export function updateShopPrintJobSettings(token: string, jobId: string, values: {
  colourMode: "Mono" | "Colour";
  paperSize: "A4" | "A3" | "A5" | "LETTER" | "LEGAL";
  isDuplex: boolean;
  copies: number;
  orientation: "AUTO" | "PORTRAIT" | "LANDSCAPE";
}) {
  return apiRequest<{
    id: string; jobNumber: string; status: string; pageCount: number; copies: number;
    colourMode: string; paperSize: string; isDuplex: boolean; orientation: string;
    currency: string; quotedAmountMinor: number; amountMajor: number; pricingVersion: number;
    quoteExpiresUtc: string; customerApprovalRequired: boolean;
  }>(`/api/v1/shop/print-jobs/${jobId}/settings`, {
    method: "PATCH", body: JSON.stringify(values)
  }, token);
}

export function dispatchShopPrintJob(token: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; printerName: string; queueName: string; agentId: string; spoolerJobName: string }>(
    `/api/v1/shop/print-jobs/${jobId}/dispatch`, { method: "POST" }, token);
}

export function retryShopPdfExport(token: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; outputMode: string; printerName: string }>(
    `/api/v1/shop/print-jobs/${jobId}/retry-pdf-export`,
    { method: "POST" },
    token
  );
}

export function confirmShopPrintOutput(token: string, jobId: string) {
  return apiRequest<{ id: string; status: string; outputConfirmedUtc: string }>(
    `/api/v1/shop/print-jobs/${jobId}/confirm-output`, { method: "POST" }, token);
}

export function getShopPrinters(token: string) {
  return apiRequest<PrinterDevice[]>("/api/v1/shop/printers", {}, token);
}

export function getShopAgentDevices(token: string) {
  return apiRequest<AgentDevice[]>("/api/v1/shop/agent-devices", {}, token);
}

export function getShopJobEvents(token: string, jobId: string) {
  return apiRequest<ShopJobEvent[]>(`/api/v1/shop/print-jobs/${jobId}/events`, {}, token);
}

export function getShopPrintJobsPage(token: string, offset: number, take = 100, search = "", batchId?: string) {
  const safeOffset = Math.max(0, Math.floor(offset));
  const safeTake = Math.max(1, Math.min(100, Math.floor(take)));
  const query = new URLSearchParams({ offset: String(safeOffset), take: String(safeTake) });
  if (search.trim()) query.set("search", search.trim().slice(0, 120));
  if (batchId) query.set("batchId", batchId);
  return apiRequest<ShopPrintJob[]>(
    `/api/v1/shop/print-jobs?${query.toString()}`,
    {},
    token
  );
}

export function getShopPrintBatchJobs(token: string, batchId: string) {
  return getShopPrintJobsPage(token, 0, 100, "", batchId);
}

export function approveShopPrintJob(token: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; updatedUtc: string; nextStep?: string }>(
    `/api/v1/shop/print-jobs/${jobId}/approve`,
    { method: "POST" },
    token
  );
}

export function rejectShopPrintJob(token: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; updatedUtc: string }>(
    `/api/v1/shop/print-jobs/${jobId}/reject`,
    { method: "POST" },
    token
  );
}

export function applyShopPrintBatchReview(
  token: string,
  batchId: string,
  reviewedJobIds: string[],
  acceptedJobIds: string[],
  pdfBrandingEnabled = false
) {
  return apiRequest<{
    batchId: string;
    reviewedCount: number;
    acceptedCount: number;
    rejectedCount: number;
    quoteRefreshRequiredCount: number;
    updatedUtc: string;
    nextStep: string;
  }>(`/api/v1/shop/print-batches/${batchId}/review`, {
    method: "POST",
    body: JSON.stringify({ reviewedJobIds, acceptedJobIds, pdfBrandingEnabled })
  }, token);
}


export async function downloadShopDocument(
  token: string,
  jobId: string,
  variant: "original" | "output" = "original"
): Promise<Blob> {
  const query = variant === "output" ? "?variant=output" : "";
  const source = await apiRequest<
    | { mode: "CLOUD" }
    | { mode: "LOCAL_AGENT"; grantToken: string; endpoints: string[]; expiresUtc: string }
  >(`/api/v1/shop/print-jobs/${jobId}/preview-source${query}`, {}, token);

  if (source.mode === "CLOUD") {
    if (variant === "output")
      throw new ApiError("The saved PDF is available only on the paired shop computer. Reconnect that computer and try again.", 409);
    const documentPath = `/api/v1/shop/print-jobs/${jobId}/document`;
    const request = (accessToken: string) => fetchApiResponse(documentPath, {
      headers: {
        Accept: "application/pdf,image/jpeg,image/png",
        Authorization: `Bearer ${accessToken}`
      },
      cache: "no-store"
    });

    // preview-source may have refreshed an expired shop session internally.
    // Reuse that newer session for this second, binary request too; otherwise
    // the cloud document request can send the stale token and fail with 401
    // only on long-lived desktop sessions.
    let response = await request(token);
    if (response.status === 401) {
      const refreshed = await refreshStoredSession(token);
      if (refreshed) response = await request(refreshed.accessToken);
    }

    if (!response.ok) {
      const message = response.status === 401
        ? "Your shop session has expired. Sign in again to preview this document."
        : response.status === 404
          ? "This job document is no longer available."
          : `Document preview failed (${response.status}).`;
      throw new ApiError(message, response.status);
    }
    return response.blob();
  }

  if (Date.parse(source.expiresUtc) <= Date.now()) {
    throw new ApiError("The local preview grant expired. Close the preview and try again.", 409);
  }

  let lastError: Error | null = null;
  // The Windows app is running on the paired shop computer itself. Prefer
  // loopback so preview does not depend on Wi-Fi routing or firewall hairpinning.
  // Browser clients continue to use only server-advertised private LAN endpoints.
  const endpointValues = getLocalPreviewEndpointCandidates(isDesktopApp(), source.endpoints);
  for (const value of endpointValues.slice(0, 8)) {
    const endpoint = isPrivateLocalPreviewEndpoint(value, isDesktopApp());
    if (!endpoint) continue;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 15_000);
    try {
      const response = await fetch(
        `${endpoint.origin}/api/v1/local/documents/${encodeURIComponent(jobId)}/preview${variant === "output" ? "?variant=output" : ""}`,
        {
          method: "GET",
          mode: "cors",
          cache: "no-store",
          credentials: "omit",
          headers: { "X-Print-Preview-Grant": source.grantToken },
          signal: controller.signal,
          targetAddressSpace: "local"
        } as RequestInit & { targetAddressSpace: "local" }
      );
      if (response.ok) return await response.blob();

      const raw = await response.text();
      let detail = "";
      if (raw) {
        try {
          const payload = JSON.parse(raw) as { title?: string; detail?: string; message?: string };
          detail = payload.detail || payload.message || payload.title || "";
        } catch {
          detail = raw;
        }
      }
      lastError = new ApiError(detail || `Local document preview failed (${response.status}).`, response.status);
      if ([401, 403, 404, 409, 415].includes(response.status)) break;
    } catch (exception) {
      if (exception instanceof Error && exception.name === "AbortError") {
        lastError = new ApiError(
          "The paired shop computer did not respond. Check that the Windows Print Agent is running and connected, then try again.",
          0
        );
      } else if (exception instanceof TypeError) {
        lastError = new ApiError(
          "Could not connect to the paired shop computer. Check that the Windows Print Agent is running and allowed through Windows Firewall.",
          0
        );
      } else {
        lastError = exception instanceof Error ? exception : new Error("Could not reach the paired shop computer.");
      }
    } finally {
      window.clearTimeout(timeout);
    }
  }

  throw lastError ?? new ApiError(
    "The original file is stored on the shop computer. Open the shop console from the shop network and try preview again.",
    0
  );
}

export async function replaceShopPhotoDocumentLocally(
  token: string,
  jobId: string,
  file: File,
  photoSizeCode: string,
  photoCount: number,
  photosPerPage: number
) {
  const grant = await apiRequest<{
    grantToken: string;
    endpoints: string[];
    expiresUtc: string;
  }>(`/api/v1/shop/print-jobs/${jobId}/photo-revision-grant`, {
    method: "POST"
  }, token);

  if (Date.parse(grant.expiresUtc) <= Date.now())
    throw new ApiError("The local photo-edit permission expired. Reopen the photo editor and try again.", 409);

  const form = new FormData();
  form.append("file", file, file.name);
  form.append("photoSizeCode", photoSizeCode);
  form.append("photoCount", String(photoCount));
  form.append("photosPerPage", String(photosPerPage));

  let lastError: Error | null = null;
  for (const value of grant.endpoints.slice(0, 8)) {
    const endpoint = isPrivateLocalPreviewEndpoint(value);
    if (!endpoint) continue;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 45_000);
    try {
      const response = await fetch(
        `${endpoint.origin}/api/v1/local/documents/${encodeURIComponent(jobId)}/photo-revision`,
        {
          method: "POST",
          mode: "cors",
          cache: "no-store",
          credentials: "omit",
          headers: { "X-Print-Photo-Revision-Grant": grant.grantToken },
          body: form,
          signal: controller.signal,
          targetAddressSpace: "local"
        } as RequestInit & { targetAddressSpace: "local" }
      );
      if (response.ok) return await response.json() as {
        jobId: string;
        originalFileName: string;
        mediaType: string;
        sizeBytes: number;
        sha256: string;
        photoSizeCode: string;
        photoCount: number;
        pageCount: number;
        photosPerPage: number;
        photoUnitPriceMinor: number;
        quotedAmountMinor: number;
        pricingVersion: number;
      };

      const raw = await response.text();
      let detail = "";
      if (raw) {
        try {
          const payload = JSON.parse(raw) as { title?: string; detail?: string; message?: string };
          detail = payload.detail || payload.message || payload.title || "";
        } catch {
          detail = raw;
        }
      }
      lastError = new ApiError(detail || `Local photo revision failed (${response.status}).`, response.status);
      if ([401, 403, 404, 409, 413, 415, 422].includes(response.status)) break;
    } catch (exception) {
      lastError = exception instanceof Error ? exception : new Error("Could not reach the paired shop computer.");
    } finally {
      window.clearTimeout(timeout);
    }
  }

  throw lastError ?? new ApiError(
    "The paired shop computer has no reachable local photo-revision endpoint. Reconnect the Print Agent and try again.",
    0
  );
}

export function replaceShopPhotoDocument(
  token: string,
  jobId: string,
  file: File,
  photoSizeCode: string,
  photoCount: number,
  photosPerPage: number
) {
  const form = new FormData();
  form.append("file", file, file.name);
  form.append("photoSizeCode", photoSizeCode);
  form.append("photoCount", String(photoCount));
  form.append("photosPerPage", String(photosPerPage));
  return apiRequest<{
    id: string;
    status: string;
    pageCount: number;
    printKind: "PHOTO";
    photoSizeCode: string;
    photoCount: number;
    photosPerPage: number;
    photoUnitPriceMinorSnapshot: number;
    quotedAmountMinor: number;
    pricingVersion: number;
    quoteExpiresUtc: string;
    originalFileName: string;
    mediaType: string;
    sizeBytes: number;
  }>(`/api/v1/shop/print-jobs/${jobId}/photo-revision`, {
    method: "POST",
    body: form
  }, token);
}

export function createAgentEnrollmentCode(token: string) {
  return apiRequest<{ code: string; expiresUtc: string }>("/api/v1/shop/agent-enrollment-codes", {
    method: "POST"
  }, token);
}

export function revokeAgentDevice(token: string, agentId: string) {
  return apiRequest<{ id: string; status: string; revokedUtc: string }>(
    `/api/v1/shop/agent-devices/${agentId}/revoke`,
    { method: "PATCH" },
    token
  );
}

export function updateShopPaymentSettings(token: string, values: {
  upiEnabled: boolean;
  upiId: string | null;
  payeeName: string | null;
  cashEnabled: boolean;
  instantPaymentEnabled: boolean;
}) {
  return apiRequest<ShopPaymentSettings>("/api/v1/shop/payment-settings", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function verifyShopPayment(token: string, paymentId: string) {
  return apiRequest<{ id: string; status: string; jobNumber: string; jobStatus: string }>(
    `/api/v1/shop/payments/${paymentId}/verify`,
    { method: "POST" },
    token
  );
}

export function rejectShopPayment(token: string, paymentId: string, reason: string) {
  return apiRequest<{ id: string; status: string; rejectionReason: string }>(
    `/api/v1/shop/payments/${paymentId}/reject`,
    { method: "POST", body: JSON.stringify({ reason }) },
    token
  );
}

export interface CustomerBatchPayment {
  id: string;
  method: "UPI" | "CASH";
  status: string;
  amountMinor: number;
  currency: string;
  upiIdSnapshot?: string | null;
  payeeNameSnapshot?: string | null;
  paymentReference?: string | null;
  rejectionReason?: string | null;
  upiUri?: string | null;
  batchId: string;
  fileCount: number;
  createdUtc: string;
  updatedUtc?: string;
  verifiedUtc?: string | null;
}

export interface CustomerPayment {
  id: string;
  batchPaymentId?: string | null;
  method: "UPI" | "CASH";
  status: string;
  amountMinor: number;
  refundedAmountMinor?: number;
  currency: string;
  upiIdSnapshot?: string | null;
  payeeNameSnapshot?: string | null;
  paymentReference?: string | null;
  upiUri?: string | null;
  batchPayment?: CustomerBatchPayment | null;
  jobNumber?: string;
  jobStatus?: string;
  createdUtc?: string;
  updatedUtc?: string;
  verifiedUtc?: string | null;
}

export function startCustomerBatchPayment(sessionToken: string, batchId: string, method: "UPI" | "CASH") {
  return apiRequest<CustomerBatchPayment>(`/api/v1/public/batches/${batchId}/payments`, {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ method })
  });
}

export function reportCustomerBatchPayment(sessionToken: string, paymentId: string, referenceNumber: string) {
  return apiRequest<{
    id: string;
    status: string;
    amountMinor: number;
    currency: string;
    paymentReference: string;
    fileCount: number;
    message: string;
  }>(`/api/v1/public/batch-payments/${paymentId}/report`, {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ referenceNumber })
  });
}

export function startCustomerPayment(sessionToken: string, jobId: string, method: "UPI" | "CASH") {
  return apiRequest<CustomerPayment>(`/api/v1/public/jobs/${jobId}/payments`, {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ method })
  });
}

export function reportCustomerUpiPayment(sessionToken: string, paymentId: string, referenceNumber: string) {
  return apiRequest<{ id: string; status: string; paymentReference: string; jobStatus: string; message: string }>(
    `/api/v1/public/payments/${paymentId}/report`,
    {
      method: "POST",
      headers: { "X-Print-Session": sessionToken },
      body: JSON.stringify({ referenceNumber })
    }
  );
}


export interface PlatformPlanVersion {
  id: string;
  version: number;
  status: "Draft" | "Published" | "Retired" | string;
  currency: string;
  monthlyPriceMinor: number | null;
  annualPriceMinor: number | null;
  maxPrinters: number | null;
  maxAgentDevices: number | null;
  maxQrStations: number | null;
  maxMonthlyPrintJobs: number | null;
  maxDocumentBytes: number | null;
  cloudAssistedPrintingEnabled: boolean;
  whiteLabelEnabled: boolean;
  advancedReportsEnabled: boolean;
  createdUtc: string;
  updatedUtc: string;
  publishedUtc: string | null;
}

export interface PlatformPlan {
  id: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  isSystemPlan: boolean;
  createdUtc: string;
  updatedUtc: string;
  versions: PlatformPlanVersion[];
}

export interface PlatformPlanVersionInput {
  currency: string;
  monthlyPriceMinor: number | null;
  annualPriceMinor: number | null;
  maxPrinters: number | null;
  maxAgentDevices: number | null;
  maxQrStations: number | null;
  maxMonthlyPrintJobs: number | null;
  maxDocumentBytes: number | null;
  cloudAssistedPrintingEnabled: boolean;
  whiteLabelEnabled: boolean;
  advancedReportsEnabled: boolean;
}

export interface PlatformSubscriptionItem {
  id: string;
  slug: string;
  businessName: string;
  status: string;
  createdUtc: string;
  subscription: {
    id: string;
    planCode: string;
    status: string;
    billingInterval: string;
    periodStartUtc: string;
    periodEndUtc: string;
    createdUtc: string;
    planVersionId: string | null;
  } | null;
  plan: {
    id: string;
    code: string;
    name: string;
    version: number;
    status: string;
    currency: string;
    monthlyPriceMinor: number | null;
    annualPriceMinor: number | null;
    isActive: boolean;
  } | null;
}

export interface PlatformSubscriptionsPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: PlatformSubscriptionItem[];
}

export interface PlatformSubscriptionAudit {
  id: string;
  previousSubscriptionId: string | null;
  previousStatus: string | null;
  assignedSubscriptionId: string;
  planVersionId: string;
  actorUserId: string | null;
  eventType: string;
  planCode: string;
  billingInterval: string;
  status: string;
  amountMinor: number;
  currency: string;
  periodStartUtc: string;
  periodEndUtc: string;
  reason: string;
  paymentReference: string | null;
  idempotencyKey: string;
  createdUtc: string;
}

export function getPlatformSubscriptions(token: string, values: {
  search?: string;
  page?: number;
  pageSize?: number;
}) {
  const query = new URLSearchParams();
  if (values.search) query.set("search", values.search);
  if (values.page) query.set("page", String(values.page));
  if (values.pageSize) query.set("pageSize", String(values.pageSize));
  const suffix = query.toString();
  return apiRequest<PlatformSubscriptionsPage>(
    `/api/v1/platform/subscriptions${suffix ? `?${suffix}` : ""}`,
    {},
    token
  );
}

export function assignPlatformSubscription(token: string, tenantId: string, values: {
  planVersionId: string;
  billingInterval: string;
  reason: string;
  idempotencyKey: string;
  paymentConfirmed?: boolean;
  paymentReference?: string;
}) {
  return apiRequest<{
    subscriptionId: string;
    previousSubscriptionId: string | null;
    planCode: string;
    status: string;
    billingInterval: string;
    periodStartUtc: string;
    periodEndUtc: string;
    eventType: string;
    duplicateRequest: boolean;
  }>(`/api/v1/platform/tenants/${tenantId}/subscriptions`, {
    method: "POST",
    body: JSON.stringify(values)
  }, token);
}

export function getPlatformSubscriptionHistory(token: string, tenantId: string) {
  return apiRequest<PlatformSubscriptionAudit[]>(
    `/api/v1/platform/tenants/${tenantId}/subscription-history`,
    {},
    token
  );
}

export function getPlatformPlans(token: string) {
  return apiRequest<PlatformPlan[]>("/api/v1/platform/plans", {}, token);
}

export function createPlatformPlan(token: string, values: {
  code: string;
  name: string;
  description?: string;
  version: PlatformPlanVersionInput;
}) {
  return apiRequest<{ id: string; code: string; name: string; description: string | null; isActive: boolean; isSystemPlan: boolean; version: PlatformPlanVersion }>("/api/v1/platform/plans", {
    method: "POST",
    body: JSON.stringify(values)
  }, token);
}

export function updatePlatformPlan(token: string, planId: string, values: {
  name: string;
  description?: string;
  isActive: boolean;
}) {
  return apiRequest<{ id: string; code: string; name: string; description: string | null; isActive: boolean; updatedUtc: string }>(`/api/v1/platform/plans/${planId}`, {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function savePlatformPlanDraft(token: string, planId: string, values: PlatformPlanVersionInput) {
  return apiRequest<PlatformPlanVersion>(`/api/v1/platform/plans/${planId}/draft`, {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function publishPlatformPlanVersion(token: string, planId: string, version: number) {
  return apiRequest<PlatformPlanVersion>(
    `/api/v1/platform/plans/${planId}/versions/${version}/publish`,
    { method: "POST" },
    token
  );
}


export interface PlatformOperationalSettings {
  trialMaxMonthlyPrintJobs: number;
  cloudCompletedDocumentRetentionDays: number;
  agentCompletedDocumentRetentionDays: number;
  abandonedDocumentRetentionDays: number;
  pendingLocalUploadRetentionDays: number;
  expiredUploadTicketRetentionDays: number;
  emptyCustomerSessionRetentionDays: number;
  staleRefreshTokenRetentionDays: number;
  apiRetentionSweepHours: number;
  agentRetentionSweepHours: number;
  allowPaidJobClaimsWhenSuspended: boolean;
  updatedByUserId: string | null;
  updatedUtc: string;
}

export interface PlatformOperationalSettingsInput {
  trialMaxMonthlyPrintJobs: number;
  cloudCompletedDocumentRetentionDays: number;
  agentCompletedDocumentRetentionDays: number;
  abandonedDocumentRetentionDays: number;
  pendingLocalUploadRetentionDays: number;
  expiredUploadTicketRetentionDays: number;
  emptyCustomerSessionRetentionDays: number;
  staleRefreshTokenRetentionDays: number;
  apiRetentionSweepHours: number;
  agentRetentionSweepHours: number;
  allowPaidJobClaimsWhenSuspended: boolean;
  reason: string;
}

export interface PlatformOperationalSettingsAudit {
  id: string;
  reason: string;
  actorUserId: string | null;
  createdUtc: string;
  changesJson: string;
}

export function getPlatformOperationalSettings(token: string) {
  return apiRequest<PlatformOperationalSettings>("/api/v1/platform/settings/operations", {}, token);
}

export function getPlatformOperationalSettingsHistory(token: string) {
  return apiRequest<PlatformOperationalSettingsAudit[]>("/api/v1/platform/settings/operations/history", {}, token);
}

export function updatePlatformOperationalSettings(token: string, values: PlatformOperationalSettingsInput) {
  return apiRequest<PlatformOperationalSettings>("/api/v1/platform/settings/operations", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}
