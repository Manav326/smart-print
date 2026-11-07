import assert from "node:assert/strict";
import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { executeIsolatedQaRecord, shouldFailQaRun } from "./local-e2e-record-runner.mjs";
import { getUnexpectedHttpResponses } from "./local-e2e-http-audit.mjs";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR is required.");
const qaConfig = JSON.parse(readFileSync(new URL("./smartprint-qa.config.json", import.meta.url), "utf8"));
const configuredLocalUrl = new URL(qaConfig.origins.local);
const allowedHosts = new Set(["127.0.0.1", "localhost", "[::1]"]);
if (configuredLocalUrl.protocol !== "http:" || !allowedHosts.has(configuredLocalUrl.hostname) ||
    configuredLocalUrl.pathname !== "/" || configuredLocalUrl.search || configuredLocalUrl.hash ||
    configuredLocalUrl.username || configuredLocalUrl.password) {
  throw new Error("The central SmartPrint local origin must be a plain loopback HTTP origin.");
}
const baseUrl = new URL(process.env.SMARTPRINT_QA_BASE_URL || configuredLocalUrl.origin);
if (baseUrl.origin !== configuredLocalUrl.origin || baseUrl.pathname !== "/" ||
    baseUrl.search || baseUrl.hash || baseUrl.username || baseUrl.password) {
  throw new Error("The full E2E suite is restricted to the configured local SmartPrint portal at " +
    configuredLocalUrl.origin + ". It will not create QA data on production.");
}

const requiredEnvironment = [
  "SMARTPRINT_QA_SHOP1_EMAIL", "SMARTPRINT_QA_SHOP1_PASSWORD", "SMARTPRINT_QA_SHOP1_NAME",
  "SMARTPRINT_QA_SHOP2_EMAIL", "SMARTPRINT_QA_SHOP2_PASSWORD", "SMARTPRINT_QA_SHOP2_NAME",
  "SMARTPRINT_QA_ADMIN_EMAIL", "SMARTPRINT_QA_ADMIN_PASSWORD",
  "SMARTPRINT_QA_CUSTOMER_EMAIL", "SMARTPRINT_QA_CUSTOMER_PASSWORD", "SMARTPRINT_QA_CUSTOMER_NAME"
];
for (const name of requiredEnvironment) {
  if (!process.env[name]) throw new Error("Missing locally provisioned QA account variable: " + name);
}

const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const runId = process.env.SMARTPRINT_QA_RUN_ID || new Date().toISOString().replace(/[:.]/g, "-");
const outputDirectory = process.env.SMARTPRINT_QA_ARTIFACT_DIR || join("artifacts", "smartprint-qa", runId, "local-e2e");
const screenshotsDirectory = join(outputDirectory, "screenshots");
mkdirSync(screenshotsDirectory, { recursive: true });

const report = {
  product: "SmartPrint",
  environment: "LOCAL_DOCKER_POSTGRES_REAL_API",
  target: baseUrl.origin,
  runId,
  startedUtc: new Date().toISOString(),
  browser: null,
  browserVersion: null,
  browserExecutablePath: null,
  safety: {
    usedMockApiRoutes: false,
    usedSyntheticJwtTokens: false,
    registeredLocalQaShops: false,
    registeredLocalQaCustomer: false,
    bootstrappedLocalQaPlatformAdmin: true,
    qaPlanPublished: false,
    qaPlanAssignedToShop1: false,
    qaPlanAssignedToShop2: false,
    assignedQaOnlyFreePlan: false,
    syntheticDocumentsUploaded: false,
    realLocalDatabaseJobsCreated: false,
    paymentIntentsCreated: false,
    syntheticUtrSubmitted: false,
    syntheticUtrMarkedVerified: false,
    refundsRecorded: false,
    printDispatches: false,
    physicalPrinterOutputClaimed: false,
    productionDatabaseTouched: false
  },
  accounts: {
    shopOwner1: process.env.SMARTPRINT_QA_SHOP1_EMAIL,
    shopOwner2: process.env.SMARTPRINT_QA_SHOP2_EMAIL,
    platformAdmin: process.env.SMARTPRINT_QA_ADMIN_EMAIL,
    customer: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL
  },
  checks: [],
  warnings: [],
  scopeNotes: [],
  errors: [],
  caseMappingErrors: []
};

let browser;
let context;
let page;
let pageErrors = [];
let consoleErrors = [];
let serverErrors = [];
let resourceErrors = [];
let requestFailures = [];
let httpErrorResponses = [];
let expectedHttpErrors = [];
let networkStatusConsoleErrors = [];
let shop1Page;
let shop2Page;
let adminPage;
let customerPage;
let guestCustomerPage;
let guestCustomerSession;

let shop1Tokens;
let shop2Tokens;
let adminTokens;
let customerTokens;
let customerSession;
let shop1TenantId;
let shop2TenantId;
let shop1Station;
let shop2Station;
let shop1Printer;
let shop2Printer;
let uploadedJob;
let uploadedBatchId;
let batchCustomerPage;
let batchCustomerSession;
let reviewedBatchId;
let reviewedBatchShopJobs = [];
let reviewedBatchJobIdsByFileName = null;
let paymentRejectedJob;
let paymentRejectedBatchId;
let paymentRejectedBatchPaymentId;
let qaPlanVersionId;
let qaPlan;
let qaAgentEnrollment;
let qaAgentIdentity;
let qaAgentLastId;

const CASE_IDS_BY_RECORD_NAME = new Map([
  ["Local portal and API health", ["ENV-001", "ENV-002", "ENV-003", "ENV-004"]],
  ["Public pages and registration/login/admin route rendering", ["PUB-001", "PUB-002", "PUB-003", "PUB-004", "PUB-005"]],
  ["Public routes real-browser responsive matrix", [
    "RESP-001", "RESP-002", "RESP-003", "RESP-004", "RESP-005",
    "RESP-006", "RESP-007", "RESP-008", "RESP-009", "RESP-010",
    "RESP-011", "RESP-012", "RESP-013", "RESP-014", "RESP-015",
    "RESP-016", "RESP-017", "RESP-018", "RESP-019", "RESP-020"
  ]],
  ["Create or sign in to the first real QA shop through the shop UI", ["SHOPAUTH-001", "SHOPAUTH-002", "SHOPAUTH-003"]],
  ["Create or sign in to the second real QA shop through the shop UI", ["SHOPAUTH-004", "SHOPAUTH-005", "SHOPAUTH-006"]],
  ["Create or sign in to the real global customer account", ["CUSTAUTH-001", "CUSTAUTH-002", "CUSTAUTH-003", "CUSTAUTH-004"]],
  ["Bootstrap-created platform administrator signs in through the real admin UI", ["ADMINAUTH-001", "ADMINAUTH-002"]],
  ["Create and publish a free QA-only plan through the platform UI", ["PLAN-001", "PLAN-002"]],
  ["Assign the QA-only plan to Shop One through the platform UI", ["PLAN-003", "PLAN-004"]],
  ["Assign the QA-only plan to Shop Two through the platform UI", ["PLAN-005", "PLAN-006"]],
  ["Platform admin navigates every real control-centre section", ["PLATFORM-005", "PLATFORM-006", "PLATFORM-007", "PLATFORM-008", "PLATFORM-009"]],
  ["Shop one provisions real QA printer and QR station", ["SHOP-009", "SHOP-010"]],
  ["Shop one configures print pricing through the real UI", ["SHOP-011", "SHOP-012"]],
  ["Shop one configures payment methods through the real UI", ["SHOP-013", "SHOP-014"]],
  ["Shop two configures isolated real QA printer/station", ["SHOP-015", "SHOP-016", "SHOP-017", "SHOP-018"]],
  ["Shop one navigates every real sidebar section and the account-menu-only tools", [
    "SHOPUI-001", "SHOPUI-002", "SHOPUI-003", "SHOPUI-004", "SHOPUI-005",
    "SHOPUI-006", "SHOPUI-007", "SHOPUI-008", "SHOPUI-009", "SHOPUI-010",
    "SHOPUI-011", "SHOPUI-012", "SHOPUI-013", "SHOPUI-014", "SHOPUI-015"
  ]],
  ["Shop two navigates every real sidebar section and account-menu-only tools", [
    "SHOPUI-016", "SHOPUI-017", "SHOPUI-018", "SHOPUI-019", "SHOPUI-020",
    "SHOPUI-021", "SHOPUI-022", "SHOPUI-023", "SHOPUI-024", "SHOPUI-025",
    "SHOPUI-026", "SHOPUI-027"
  ]],
  ["Shop, platform-admin and cross-tenant authorization boundaries", ["RBAC-001", "RBAC-002", "RBAC-003", "RBAC-004", "RBAC-005"]],
  ["Customer opens the shop-one station link in a guest browser session", ["QR-008", "QR-009"]],
  ["Customer opens the shop-one station with authenticated customer identity", ["QR-010"]],
  ["Customer QR session is linked to the global customer account", ["CUST-001", "CUST-002"]],
  ["Real document validator rejects a malformed PDF without creating a print job", ["DOC-001", "DOC-002"]],
  ["Customer uploads a synthetic two-page PDF through the real portal UI", ["DOC-003", "DOC-004", "DOC-005"]],
  ["Customer accepts the calculated quote and sends the job to the real shop queue", ["JOB-004", "JOB-005", "JOB-006", "JOB-007"]],
  ["Shop rejects the unpaid QA job and customer sees the same terminal result", ["JOB-008", "JOB-009"]],
  ["Customer cannot start payment for a shop-rejected job", ["PAY-002"]],
  ["Customer opens a fresh QR session and uploads two real documents as one batch", ["BATCH-001", "BATCH-002", "BATCH-003", "BATCH-004"]],
  ["Two-file customer batch is sent to the shop and appears as one tenant-scoped batch", ["BATCH-005", "BATCH-006", "BATCH-007"]],
  ["Shopkeeper rejects one file, undoes that rejection, then applies the individual file decision", ["BATCH-008", "BATCH-009", "BATCH-010", "BATCH-011", "BATCH-012"]],
  ["Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received", ["PAY-003", "PAY-004", "PAY-005", "PAY-006", "PAY-007", "PAY-008", "PAY-009"]],
  ["Shop payment and refund screens load without recording false financial events", ["PAY-010", "REFUND-003", "REFUND-004"]],
  ["Agent and printer prerequisites are visible; dispatch without a paired QA Agent is safely blocked", ["AGENT-001", "AGENT-002", "AGENT-003"]],
  ["Dedicated QA Agent enrollment code can be generated without pairing or replacing a machine identity", ["AGENT-004", "AGENT-005"]],
  ["Agent API requires an authenticated paired identity for all protected endpoints", [
    "AGENT-006", "AGENT-007", "AGENT-008", "AGENT-009", "AGENT-010", "AGENT-011",
    "AGENT-012", "AGENT-013", "AGENT-014", "AGENT-015", "AGENT-016", "AGENT-017",
    "AGENT-018", "AGENT-019", "AGENT-020", "AGENT-021", "AGENT-022"
  ]],
  ["Disposable QA Agent enrolls, heartbeats, validates printer sync, rejects code replay and revokes safely", [
    "AGENT-023", "AGENT-024", "AGENT-025", "AGENT-026", "AGENT-027", "AGENT-028", "AGENT-029"
  ]],
  ["Platform and shop routes remain isolated after creating real QA data", ["RBAC-006", "TENANT-004"]],
  ["Live local browser has no same-origin server errors or critical resource failures", ["OBS-001", "OBS-002", "OBS-003", "OBS-004"]]
]);

const PLANNED_CASE_IDS = [
  ...new Set([
    ...[...CASE_IDS_BY_RECORD_NAME.values()].flat(),
    "AUTH-001", "AUTH-002", "AUTH-003", "AUTH-004", "AUTH-005", "AUTH-006", "AUTH-007", "AUTH-008",
    "SHOP-001", "SHOP-002", "SHOP-003", "SHOP-004", "SHOP-005", "SHOP-006", "SHOP-007", "SHOP-008",
    "QR-001", "QR-002", "QR-003", "QR-004", "UPLOAD-001", "UPLOAD-002",
    "JOB-001", "JOB-002", "JOB-003", "PAY-001", "REFUND-001", "REFUND-002", "SEC-001",
    "DEVICE-001", "DEVICE-002", "QR-005", "QR-006", "QR-007",
    "PLATFORM-001", "PLATFORM-002", "PLATFORM-003", "PLATFORM-004",
    "TENANT-001", "TENANT-002", "TENANT-003",
    "PHOTO-001", "PHOTO-002", "PHOTO-003", "PHOTO-004", "PHOTO-005", "PHOTO-006",
    "REPORT-001", "REPORT-002", "REPORT-003", "REPORT-004",
    "RBAC-007", "RBAC-008", "RBAC-009", "RBAC-010", "TENANT-005", "TENANT-006", "TENANT-007", "TENANT-008", "TENANT-009", "TENANT-010", "QR-011", "QR-012", "QR-013", "UPLOAD-003", "UPLOAD-004", "DOC-006", "JOB-010", "JOB-011", "PLATFORM-010", "PLATFORM-011", "PLATFORM-012", "PLATFORM-013", "PLATFORM-014", "SHOP-019", "SHOP-020", "SHOP-021", "SHOP-022", "SHOP-023", "SHOP-024", "PAY-011", "PAY-012", "PAY-013", "AGENT-030", "AGENT-031", "AGENT-032", "AGENT-033", "GUEST-001", "GUEST-002", "GUEST-003", "GUEST-004", "GUEST-005", "GUEST-006", "GUEST-007"
  ])
];

const recordStatuses = new Map();
const recordCaseIdOwners = new Map();
const plannedCaseIdSet = new Set(PLANNED_CASE_IDS);

async function record(name, action, caseIds = CASE_IDS_BY_RECORD_NAME.get(name) ?? []) {
  const checkId = "SP-LOCAL-" + String(report.checks.length + 1).padStart(3, "0");
  const seenInThisRecord = new Set();
  const duplicateCaseIds = [];
  const unplannedCaseIds = [];
  for (const caseId of caseIds) {
    if (seenInThisRecord.has(caseId) || recordCaseIdOwners.has(caseId)) {
      duplicateCaseIds.push(caseId);
    } else {
      recordCaseIdOwners.set(caseId, { checkId, name });
    }
    seenInThisRecord.add(caseId);
    if (!plannedCaseIdSet.has(caseId)) unplannedCaseIds.push(caseId);
  }
  if (duplicateCaseIds.length > 0 || unplannedCaseIds.length > 0) {
    const mappingError = {
      checkId,
      name,
      duplicateCaseIds: [...new Set(duplicateCaseIds)],
      unplannedCaseIds: [...new Set(unplannedCaseIds)]
    };
    report.caseMappingErrors.push(mappingError);
    console.error("CASE MAPPING ERROR " + name + ": " + JSON.stringify(mappingError));
  }

  const caseLabel = caseIds.length > 0 ? " | cases=" + caseIds.join(",") : " | cases=UNMAPPED";
  console.log("[QA-CASE-START] " + checkId + caseLabel + " | " + name);
  console.log("[QA-START] " + name);
  const outcome = await executeIsolatedQaRecord({
    name,
    action,
    caseIds,
    checkId,
    recordStatuses
  });
  report.checks.push(outcome.check);

  if (outcome.status === "WARN") {
    report.warnings.push({ name, message: outcome.warning || "The check completed with a warning." });
    console.warn("WARN " + name + ": " + (outcome.warning || "The check completed with a warning."));
  } else if (outcome.status === "FAIL") {
    report.errors.push({ name, message: outcome.errorMessage || outcome.check.error || "The check failed." });
    console.error("FAIL " + name + ": " + (outcome.errorMessage || outcome.check.error || "The check failed."));
  } else if (outcome.status === "SKIPPED") {
    console.warn("SKIPPED " + name + " (blocked by: " + (outcome.check.details?.blockedBy || []).join("; ") + ")");
  } else {
    console.log("PASS " + name + " (" + outcome.check.durationMs + " ms)");
  }
  console.log("[QA-CASE-END] " + checkId + caseLabel + " | " + outcome.status + " | " + outcome.check.durationMs + " ms | " + name);

  return outcome.check.details ?? null;
}

function markExpectedHttpError(result) {
  if (!result || Number(result.status) < 400 || !result.method || !result.url) return;
  const url = new URL(result.url, baseUrl);
  expectedHttpErrors.push({
    method: String(result.method).toUpperCase(),
    path: url.pathname + url.search,
    status: Number(result.status)
  });
}

function assertExpectedHttpStatus(result, expected, label) {
  const allowed = Array.isArray(expected) ? expected : [expected];
  assert.ok(allowed.includes(result.status),
    label + " returned HTTP " + result.status + "; expected " + allowed.join("/") + ". Body: " + String(result.text ?? "").slice(0, 900));
  markExpectedHttpError(result);
  return result;
}

function assertHttp(result, expected, label) {
  const allowed = Array.isArray(expected) ? expected : [expected];
  assert.ok(allowed.includes(result.status),
    label + " returned HTTP " + result.status + "; expected " + allowed.join("/") + ". Body: " + result.text.slice(0, 900));
  markExpectedHttpError(result);
  return result.json;
}

async function request(page, method, path, { token, sessionToken, body, headers = {}, expectedOrigin = baseUrl.origin } = {}) {
  assert.ok(page && typeof page.url === "function", "QA request requires an initialised browser page.");
  await page.bringToFront();
  const currentPageUrl = page.url();
  assert.ok(currentPageUrl && currentPageUrl !== "about:blank",
    "QA request blocked: browser page is still about:blank. A required station/session navigation did not complete.");
  let currentPageOrigin;
  try {
    currentPageOrigin = new URL(currentPageUrl).origin;
  } catch {
    throw new Error("QA request blocked: browser page has an invalid URL: " + currentPageUrl);
  }
  assert.equal(currentPageOrigin, expectedOrigin,
    "QA request blocked: browser page is not on the expected local SmartPrint origin.");
  const url = new URL(path, baseUrl).toString();
  assert.equal(new URL(url).origin, expectedOrigin, "QA request attempted to leave the local SmartPrint origin.");
  const result = await page.evaluate(async ({ url, method, token, sessionToken, body, headers }) => {
    const requestHeaders = new Headers(headers || {});
    requestHeaders.set("Accept", "application/json");
    if (token) requestHeaders.set("Authorization", "Bearer " + token);
    if (sessionToken) requestHeaders.set("X-Print-Session", sessionToken);
    const options = { method, headers: requestHeaders, cache: "no-store", credentials: "same-origin", signal: AbortSignal.timeout(15000) };
    if (body !== undefined) {
      if (body instanceof FormData || body instanceof Blob) options.body = body;
      else {
        requestHeaders.set("Content-Type", "application/json");
        options.body = JSON.stringify(body);
      }
    }
    const response = await fetch(url, options);
    const text = await response.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch {}
    return { status: response.status, url: response.url, method, text, json };
  }, { url, method, token, sessionToken, body, headers });
  assert.equal(new URL(result.url).origin, expectedOrigin, "Local API request redirected outside the local origin.");
  return result;
}

async function sendBatchAndWaitForShopApproval(page, sessionToken, jobIds, label) {
  assert.ok(Array.isArray(jobIds) && jobIds.length > 0,
    label + ": at least one uploaded job ID is required before quote acceptance.");
  assert.ok(typeof sessionToken === "string" && sessionToken.length > 0,
    label + ": the station session capability is required.");
  const ids = jobIds.map(String);
  assert.equal(new Set(ids).size, ids.length,
    label + ": every uploaded file must have a distinct PrintJob ID.");

  // The UI handler intentionally runs asynchronously. A visible status panel
  // is not proof that the POST completed; wait for the exact per-job command
  // and then read back the persisted server state.
  const responseOutcome = Promise.all(ids.map(async jobId => {
    const expectedPath = "/api/v1/public/jobs/" + jobId + "/accept-quote";
    const response = await page.waitForResponse(candidate => {
      if (candidate.request().method() !== "POST") return false;
      try {
        const candidateUrl = new URL(candidate.url());
        return candidateUrl.origin === baseUrl.origin && candidateUrl.pathname === expectedPath;
      } catch {
        return false;
      }
    }, { timeout: 20000 });
    return { jobId, response };
  })).then(responses => ({ responses }), error => ({ error }));

  const sendButton = page.getByRole("button", { name: "Send batch to shop" });
  try {
    await sendButton.waitFor({ state: "visible", timeout: 30000 });
    await sendButton.click();
  } catch (error) {
    await responseOutcome;
    throw error;
  }

  const outcome = await responseOutcome;
  if (outcome.error) throw outcome.error;

  const accepted = [];
  for (const { jobId, response } of outcome.responses) {
    const responseText = await response.text();
    let payload = null;
    try { payload = responseText ? JSON.parse(responseText) : null; } catch {}
    assert.equal(response.status(), 200,
      label + ": quote acceptance for " + jobId + " returned HTTP " + response.status() +
      ". Body: " + responseText.slice(0, 900));
    assert.equal(payload?.status, "AWAITING_SHOP_APPROVAL",
      label + ": the accept-quote response did not move " + jobId + " to shop approval.");

    const persisted = assertHttp(await request(page, "GET",
      "/api/v1/public/jobs/" + jobId + "/status",
      { sessionToken }), 200, label + ": persisted state after quote acceptance");
    assert.equal(persisted.job?.status, "AWAITING_SHOP_APPROVAL",
      label + ": persisted state for " + jobId + " did not reach shop approval.");
    accepted.push({ jobId, status: persisted.job.status });
  }
  return accepted;
}

function watchPage(targetPage) {
  targetPage.on("pageerror", error => pageErrors.push(String(error.message).slice(0, 500)));
  targetPage.on("requestfailed", request => {
    try {
      const url = new URL(request.url());
      if (url.origin !== baseUrl.origin) return;
      const error = request.failure()?.errorText || "Unknown network request failure";
      if (error === "net::ERR_ABORTED") return;
      requestFailures.push({
        method: request.method(),
        path: url.pathname,
        resourceType: request.resourceType(),
        error: error.slice(0, 300)
      });
    } catch {}
  });
  targetPage.on("console", message => {
    if (message.type() !== "error") return;
    const messageText = String(message.text()).slice(0, 500);
    if (/^Failed to load resource: the server responded with a status of \d{3}\b/i.test(messageText)) {
      networkStatusConsoleErrors.push({ pageUrl: targetPage.url(), message: messageText });
      return;
    }
    const sourceUrl = message.location()?.url || "";
    if (!sourceUrl || sourceUrl.startsWith(baseUrl.origin)) {
      consoleErrors.push(messageText);
    }
  });
  targetPage.on("response", response => {
    try {
      const url = new URL(response.url());
      if (url.origin !== baseUrl.origin) return;
      if (response.status() >= 500) serverErrors.push({ status: response.status(), path: url.pathname });
      if (response.status() >= 400) {
        httpErrorResponses.push({
          method: response.request().method().toUpperCase(),
          path: url.pathname + url.search,
          status: response.status()
        });
      }
      if (["script", "stylesheet", "font"].includes(response.request().resourceType()) && response.status() >= 400) {
        resourceErrors.push({ status: response.status(), path: url.pathname, type: response.request().resourceType() });
      }
    } catch {}
  });
}

async function screenshot(page, name) {
  await page.bringToFront();
  await page.screenshot({
    path: join(screenshotsDirectory, name.replace(/[^a-z0-9-]/gi, "-").toLowerCase() + ".png"),
    fullPage: true,
    animations: "disabled",
    timeout: 15000
  }).catch(error => {
    console.warn("[QA-SCREENSHOT-WARN] " + name + ": " + String(error?.message || error).slice(0, 240));
  });
}

async function openRoute(page, path, selector, label) {
  await page.bringToFront();
  console.log("[QA-VISIBLE] " + label + " | " + path);
  const response = await page.goto(new URL(path, baseUrl).toString(), {
    waitUntil: "domcontentloaded",
    timeout: 30000
  });
  assert.ok(response, label + " returned no document response.");
  assert.equal(response.status(), 200, label + " returned HTTP " + response.status() + ".");
  assert.equal(new URL(response.url()).origin, baseUrl.origin, label + " left the local origin.");
  await page.locator(selector).first().waitFor({ state: "visible", timeout: 20000 });
  const offOrigin = await page.evaluate(() => {
    const expected = window.location.origin;
    return performance.getEntriesByType("resource")
      .map(entry => entry.name)
      .filter(value => {
        try {
          const parsed = new URL(value, expected);
          return (parsed.protocol === "http:" || parsed.protocol === "https:") && parsed.origin !== expected;
        } catch { return true; }
      });
  });
  assert.equal(offOrigin.length, 0, label + " loaded resources outside the local SmartPrint origin: " + offOrigin.join(", "));
  await screenshot(page, label);
  return { status: response.status(), url: page.url(), title: await page.title() };
}

async function tokensFromSession(page, scope) {
  const raw = await page.evaluate(scope => sessionStorage.getItem("qrtoprint.auth." + scope), scope);
  assert.ok(raw, "The real " + scope + " UI session was not stored in sessionStorage.");
  const tokens = JSON.parse(raw);
  assert.ok(tokens.accessToken, "The real " + scope + " login did not return an access token.");
  assert.ok(tokens.refreshToken, "The real " + scope + " login did not return a refresh token.");
  return tokens;
}

async function registerOrSignInShop(page, email, password, businessName, ownerName) {
  await openRoute(page, "/signup", ".auth-screen", "shop-registration");
  await page.getByLabel("Shop or business name").fill(businessName);
  await page.getByLabel("Your name").fill(ownerName);
  await page.getByLabel("Email address").fill(email);
  await page.locator('input[type="password"]').first().fill(password);
  const registrationResponsePromise = page.waitForResponse(response => {
    const request = response.request();
    const url = new URL(response.url());
    return request.method() === "POST" &&
      url.origin === baseUrl.origin &&
      url.pathname === "/api/v1/auth/register-shop";
  }, { timeout: 30000 });
  await page.getByRole("button", { name: "Create my shop" }).click();
  const registrationResponse = await registrationResponsePromise;

  let registered = false;
  if (registrationResponse.status() === 201) {
    registered = true;
    await page.locator(".app-shell").waitFor({ state: "visible", timeout: 20000 });
  } else if (registrationResponse.status() === 409) {
    const errorMessage = page.locator(".auth-error");
    await errorMessage.waitFor({ state: "visible", timeout: 10000 });
    const error = (await errorMessage.innerText()).trim();
    assert.match(error, /already registered|already exists/i,
      "Shop registration returned HTTP 409 without the expected existing-account message: " + error);
    markExpectedHttpError({
      method: "POST",
      url: registrationResponse.url(),
      status: registrationResponse.status()
    });
    await openRoute(page, "/shop", ".auth-screen", "shop-sign-in");
    await page.getByLabel("Email address").fill(email);
    await page.locator('input[type="password"]').first().fill(password);
    await page.getByRole("button", { name: "Sign in to workspace" }).click();
    await page.locator(".app-shell").waitFor({ state: "visible", timeout: 20000 });
  } else {
    const responseBody = await registrationResponse.text().catch(() => "");
    throw new Error(
      "The real shop-registration flow returned HTTP " + registrationResponse.status() +
      " instead of 201 or the expected existing-account 409. Body: " + responseBody.slice(0, 700)
    );
  }

  if (registered) {
    const invalidLogin = await request(page, "POST", "/api/v1/auth/login", {
      body: { email, password: password + "-wrong" }
    });
    assertExpectedHttpStatus(invalidLogin, 401, "Shop login accepted an incorrect password.");

    const accountMenu = page.getByRole("button", { name: /Account options for/ });
    await accountMenu.click();
    await page.getByRole("button", { name: "Sign out" }).click();
    await openRoute(page, "/shop", ".auth-screen", "shop-sign-in-after-registration");
    await page.getByLabel("Email address").fill(email);
    await page.locator('input[type="password"]').first().fill(password);
    await page.getByRole("button", { name: "Sign in to workspace" }).click();
    await page.locator(".app-shell").waitFor({ state: "visible", timeout: 20000 });
  }

  const tokens = await tokensFromSession(page, "shop");
  await page.locator(".sidebar .nav-item").first().waitFor({ state: "visible", timeout: 15000 });
  return { tokens, registered, signInVerifiedInUi: true };
}

async function signInPlatformAdmin(page, email, password) {
  await openRoute(page, "/admin", ".platform-admin-auth-screen", "platform-admin-sign-in");
  const invalidLogin = await request(page, "POST", "/api/v1/auth/login", {
    body: { email, password: password + "-wrong" }
  });
  assertExpectedHttpStatus(invalidLogin, 401, "Platform admin login accepted an incorrect password.");
  await page.getByLabel("Email address").fill(email);
  await page.locator('input[type="password"]').first().fill(password);
  await page.getByRole("button", { name: "Sign in to administration" }).click();
  await page.locator(".app-shell").waitFor({ state: "visible", timeout: 20000 });
  await page.locator(".sidebar .nav-item").first().waitFor({ state: "visible", timeout: 15000 });
  const tokens = await tokensFromSession(page, "admin");
  return tokens;
}

async function navigateSidebarSection(page, label, progressContext = "") {
  await page.bringToFront();
  if (progressContext) console.log("[QA-PROGRESS] " + progressContext + " | opening " + label);
  const viewportWidth = await page.evaluate(() => window.innerWidth);
  const mobile = viewportWidth <= 850;
  const sidebar = page.locator(".sidebar").first();

  if (mobile) {
    const isOpen = await sidebar.evaluate(element => element.classList.contains("sidebar-open"));
    if (!isOpen) {
      const openNavigation = page.getByRole("button", { name: "Open navigation", exact: true });
      await openNavigation.waitFor({ state: "visible", timeout: 12000 });
      await openNavigation.click({ timeout: 12000 });
      await page.waitForFunction(
        () => document.querySelector(".sidebar")?.classList.contains("sidebar-open") === true,
        null,
        { timeout: 10000 }
      );
    }
  }

  const item = sidebar.locator(".nav-item").filter({ hasText: label }).first();
  await item.waitFor({ state: "visible", timeout: 12000 });
  await item.scrollIntoViewIfNeeded();
  await item.click({ timeout: 12000 });
  await page.waitForFunction(
    expected => [...document.querySelectorAll(".sidebar .nav-item")]
      .some(element => element.classList.contains("active") &&
        (element.getAttribute("aria-label") === expected ||
         element.getAttribute("title") === expected ||
         element.textContent.trim().startsWith(expected))),
    label,
    { timeout: 12000 }
  );

  if (mobile) {
    await page.waitForFunction(
      () => document.querySelector(".sidebar")?.classList.contains("sidebar-open") === false,
      null,
      { timeout: 10000 }
    );
  }
  if (progressContext) console.log("[QA-PROGRESS] " + progressContext + " | opened " + label);
}

async function navigateSections(page, labels, prefix) {
  const visited = [];
  for (let index = 0; index < labels.length; index++) {
    const label = labels[index];
    const progressContext = prefix + " sidebar " + (index + 1) + "/" + labels.length;
    await navigateSidebarSection(page, label, progressContext);
    await page.waitForTimeout(120);
    await screenshot(page, prefix + "-" + label);
    visited.push(label);
  }
  console.log("[QA-PROGRESS] " + prefix + " sidebar complete: " + visited.length + "/" + labels.length);
  return visited;
}

function syntheticPdf(pageCount = 2, label = "SmartPrint local QA synthetic document") {
  const objects = [];
  const pageIds = [];
  const contentIds = [];
  const fontId = 3 + pageCount * 2;
  objects.push("<< /Type /Catalog /Pages 2 0 R >>");
  for (let index = 0; index < pageCount; index++) {
    pageIds.push(3 + index * 2);
    contentIds.push(4 + index * 2);
  }
  objects.push("<< /Type /Pages /Kids [" + pageIds.map(id => id + " 0 R").join(" ") + "] /Count " + pageCount + " >>");
  for (let index = 0; index < pageCount; index++) {
    const pageId = pageIds[index];
    const contentId = contentIds[index];
    const stream = "BT /F1 18 Tf 72 720 Td (" + label.replace(/[()\\]/g, "") + " - page " + (index + 1) + ") Tj ET";
    objects.push(
      "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 " + fontId + " 0 R >> >> /Contents " + contentId + " 0 R >>"
    );
    objects.push("<< /Length " + Buffer.byteLength(stream, "ascii") + " >>\nstream\n" + stream + "\nendstream");
  }
  objects.push("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
  let pdf = "%PDF-1.4\n";
  const offsets = [0];
  for (let index = 0; index < objects.length; index++) {
    offsets.push(Buffer.byteLength(pdf, "ascii"));
    pdf += (index + 1) + " 0 obj\n" + objects[index] + "\nendobj\n";
  }
  const xrefOffset = Buffer.byteLength(pdf, "ascii");
  pdf += "xref\n0 " + (objects.length + 1) + "\n";
  pdf += "0000000000 65535 f \n";
  for (let index = 1; index < offsets.length; index++) {
    pdf += String(offsets[index]).padStart(10, "0") + " 00000 n \n";
  }
  pdf += "trailer\n<< /Size " + (objects.length + 1) + " /Root 1 0 R >>\nstartxref\n" + xrefOffset + "\n%%EOF\n";
  return Buffer.from(pdf, "ascii");
}

async function uploadSyntheticPdf(page, sessionToken, batchId, idempotencyKey, fileName, bytes) {
  await page.bringToFront();
  const base64 = bytes.toString("base64");
  return await page.evaluate(async ({ sessionToken, batchId, idempotencyKey, fileName, base64 }) => {
    const content = Uint8Array.from(atob(base64), character => character.charCodeAt(0));
    const form = new FormData();
    form.append("file", new Blob([content], { type: "application/pdf" }), fileName);
    form.append("colourMode", "Mono");
    form.append("paperSize", "A4");
    form.append("isDuplex", "false");
    form.append("copies", "1");
    form.append("orientation", "AUTO");
    form.append("batchName", "SmartPrint automated local QA");
    const response = await fetch("/api/v1/public/sessions/current/jobs", {
      method: "POST",
      credentials: "same-origin",
      cache: "no-store",
      headers: {
        "X-Print-Session": sessionToken,
        "Idempotency-Key": idempotencyKey,
        "X-Print-Batch-Id": batchId
      },
      signal: AbortSignal.timeout(30000),
      body: form
    });
    const text = await response.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch {}
    return { status: response.status, url: response.url, method: "POST", text, json };
  }, { sessionToken, batchId, idempotencyKey, fileName, base64 });
}

async function clickAdminSection(page, label) {
  await navigateSidebarSection(page, label);
}

async function fillQaPlanVersion(page) {
  await page.getByLabel("Currency").fill("INR");
  await page.getByLabel("Monthly price · minor units").fill("0");
  await page.getByLabel("Annual price · minor units").fill("0");
  await page.getByLabel("Printers", { exact: true }).fill("10");
  await page.getByLabel("Agent computers").fill("3");
  await page.getByLabel("QR stations").fill("10");
  await page.getByLabel("Print jobs / month").fill("500");
  await page.getByLabel("Maximum document size · bytes").fill("52428800");

  async function setFeature(label, enabled) {
    const card = page.locator(".platform-plan-feature").filter({ hasText: label });
    const checkbox = card.locator('input[type="checkbox"]');
    const current = await checkbox.isChecked();
    if (current !== enabled) {
      // The checkbox is intentionally visually hidden and pointer-transparent.
      // Exercise the real label/card interaction instead of clicking the hidden
      // input's centre (which can land on the decorative SVG icon).
      await card.locator(".platform-plan-feature-copy").click();
    }
    assert.equal(await checkbox.isChecked(), enabled, label + " feature toggle did not update through its visible label.");
  }

  await setFeature("Cloud-assisted printing", true);
  await setFeature("White-label experience", false);
  await setFeature("Advanced reports", true);
}

async function ensureQaPlanThroughPlatformUi(adminPage, adminToken) {
  const code = "SMARTPRINT_QA_AUTOMATION";
  const firstRead = assertHttp(await request(adminPage, "GET", "/api/v1/platform/plans", {
    token: adminToken
  }), 200, "Platform plans before QA-plan setup");
  let plan = firstRead.find(item => item.code === code);

  await clickAdminSection(adminPage, "Plans & entitlements");

  if (!plan) {
    await adminPage.getByRole("button", { name: "New plan" }).click();
    await adminPage.getByLabel("Plan code").fill(code);
    await adminPage.getByLabel("Plan name").fill("SmartPrint Local QA Automation");
    await adminPage.getByLabel("Description").fill("Free, local-only entitlement used by the automated SmartPrint QA runner.");
    await fillQaPlanVersion(adminPage);
    await adminPage.getByRole("button", { name: "Create plan draft" }).click();
    await adminPage.locator(".platform-plan-list-item").filter({ hasText: code }).waitFor({ state: "visible", timeout: 20000 });
  } else {
    const row = adminPage.locator(".platform-plan-list-item").filter({ hasText: code }).first();
    await row.waitFor({ state: "visible", timeout: 12000 });
    await row.click();
    await adminPage.locator(".platform-plan-panel").first().waitFor({ state: "visible", timeout: 10000 });
    const activeToggle = adminPage.locator(".platform-plan-toggle input[type='checkbox']");
    if (await activeToggle.count()) {
      if (!(await activeToggle.isChecked())) {
        await activeToggle.check();
        await adminPage.getByRole("button", { name: "Save details" }).click();
        await adminPage.getByRole("status").filter({ hasText: "Plan details updated." }).waitFor({ state: "visible", timeout: 12000 });
      }
    }

    const existingVersions = plan.versions || [];
    const validPublished = existingVersions.some(version =>
      version.status === "Published" &&
      version.monthlyPriceMinor === 0 &&
      version.cloudAssistedPrintingEnabled === true
    );
    if (!validPublished) {
      const draftVersion = existingVersions.some(version => version.status === "Draft");
      if (!draftVersion) {
        // The selected plan panel automatically prepares the next version editor.
      }
      await fillQaPlanVersion(adminPage);
      const saveButton = adminPage.getByRole("button", { name: "Save draft" });
      await saveButton.click();
      await adminPage.getByRole("status").filter({ hasText: "Draft version saved." }).waitFor({ state: "visible", timeout: 12000 });
    }
  }

  let latest = assertHttp(await request(adminPage, "GET", "/api/v1/platform/plans", {
    token: adminToken
  }), 200, "Platform QA plan after draft save");
  plan = latest.find(item => item.code === code);
  assert.ok(plan?.id, "The QA plan does not exist after the platform UI save.");

  let selectedVersion = (plan.versions || [])
    .filter(version => version.status === "Published" &&
      version.monthlyPriceMinor === 0 &&
      version.cloudAssistedPrintingEnabled === true)
    .sort((left, right) => right.version - left.version)[0];

  if (!selectedVersion) {
    await clickAdminSection(adminPage, "Plans & entitlements");
    await adminPage.locator(".platform-plan-list-item").filter({ hasText: code }).first().click();
    await adminPage.getByRole("button", { name: "Publish version" }).waitFor({ state: "visible", timeout: 12000 });
    await adminPage.getByRole("button", { name: "Publish version" }).click();
    await adminPage.getByText(/Version \d+ published\./).waitFor({ state: "visible", timeout: 15000 });
    latest = assertHttp(await request(adminPage, "GET", "/api/v1/platform/plans", {
      token: adminToken
    }), 200, "Platform QA plan after publish");
    plan = latest.find(item => item.code === code);
    selectedVersion = (plan?.versions || [])
      .filter(version => version.status === "Published" &&
        version.monthlyPriceMinor === 0 &&
        version.cloudAssistedPrintingEnabled === true)
      .sort((left, right) => right.version - left.version)[0];
  }

  assert.ok(selectedVersion?.id, "A published, zero-price, cloud-assisted QA plan version is required.");
  return { planId: plan.id, planVersionId: selectedVersion.id, planCode: code, version: selectedVersion.version };
}

async function assignQaPlanThroughPlatformUi(adminPage, adminToken, shopName, planVersion) {
  await clickAdminSection(adminPage, "Shop subscriptions");
  const search = adminPage.getByPlaceholder("Search shop name or tenant slug");
  await search.fill(shopName);
  const tenantRow = adminPage.locator(".platform-subscription-table tbody tr").filter({ hasText: shopName }).first();
  await tenantRow.waitFor({ state: "visible", timeout: 15000 });

  let subscriptions = assertHttp(await request(adminPage, "GET",
    "/api/v1/platform/subscriptions?search=" + encodeURIComponent(shopName) + "&page=1&pageSize=20",
    { token: adminToken }), 200, "QA shop subscription before assignment");
  assert.ok(Array.isArray(subscriptions.items), "Subscription directory did not return its items array.");
  let tenant = subscriptions.items.find(item => item.businessName === shopName);
  assert.ok(tenant?.id, "The exact QA shop was not found in the subscription directory.");

  const alreadyAssigned = tenant.subscription?.planVersionId === planVersion.planVersionId &&
    tenant.subscription?.planCode === planVersion.planCode &&
    tenant.subscription?.status === "Active" &&
    tenant.subscription?.billingInterval === "Monthly" &&
    Date.parse(tenant.subscription?.periodEndUtc || "") > Date.now();

  if (!alreadyAssigned) {
    await tenantRow.getByRole("button", { name: "Grant plan access" }).click();
    const dialog = adminPage.getByRole("dialog", { name: "Grant plan access" });
    await dialog.waitFor({ state: "visible", timeout: 10000 });
    await dialog.getByLabel("Published plan").selectOption(planVersion.planVersionId + "|Monthly");
    await dialog.getByLabel("Reason for access grant").fill(
      "Local SmartPrint QA entitlement. Zero-priced plan; no payment or billing."
    );
    await dialog.getByRole("button", { name: "Grant access" }).click();
    await dialog.waitFor({ state: "detached", timeout: 20000 });
    await adminPage.getByRole("status").filter({ hasText: shopName }).waitFor({ state: "visible", timeout: 15000 });
    subscriptions = assertHttp(await request(adminPage, "GET",
      "/api/v1/platform/subscriptions?search=" + encodeURIComponent(shopName) + "&page=1&pageSize=20",
      { token: adminToken }), 200, "QA shop subscription after UI assignment");
    tenant = subscriptions.items.find(item => item.businessName === shopName);
  }

  assert.equal(tenant?.subscription?.planVersionId, planVersion.planVersionId);
  assert.equal(tenant?.subscription?.planCode, planVersion.planCode);
  assert.equal(tenant?.subscription?.status, "Active");
  assert.equal(tenant?.subscription?.billingInterval, "Monthly");

  // Open the real audit history UI and independently verify the read-only API record.
  const updatedRow = adminPage.locator(".platform-subscription-table tbody tr").filter({ hasText: shopName }).first();
  await updatedRow.getByRole("button", { name: "History" }).click();
  await adminPage.locator(".platform-subscription-history").waitFor({ state: "visible", timeout: 12000 });
  const history = assertHttp(await request(adminPage, "GET",
    "/api/v1/platform/tenants/" + tenant.id + "/subscription-history",
    { token: adminToken }), 200, "QA shop subscription audit history");
  assert.ok(Array.isArray(history) && history.some(item =>
    item.planCode === planVersion.planCode &&
    item.reason === "Local SmartPrint QA entitlement. Zero-priced plan; no payment or billing."
  ), "Subscription assignment audit history does not contain the QA reason and plan.");
  return {
    tenantId: tenant.id,
    assignedThroughUi: !alreadyAssigned,
    alreadyAssigned,
    planVersionId: planVersion.planVersionId,
    status: tenant.subscription.status,
    auditRows: history.length
  };
}

async function clickShopSection(page, label) {
  await navigateSidebarSection(page, label);
}

async function setupShopResources(page, tokens, shopLabel, onResourcesReady = () => {}) {
  const printerName = "SmartPrint QA no-dispatch test queue";
  const stationName = "SmartPrint QA customer station";

  let printers = assertHttp(await request(page, "GET", "/api/v1/shop/printers", {
    token: tokens.accessToken
  }), 200, shopLabel + " printer list");
  let printer = printers.find(item => item.displayName === printerName);

  if (!printer) {
    await clickShopSection(page, "Printers");
    await page.locator(".collection-panel").getByRole("button", { name: "Add printer" }).click();
    const dialog = page.getByRole("dialog", { name: "Add a printer" });
    await dialog.waitFor({ state: "visible", timeout: 10000 });
    await dialog.getByLabel("Display name").fill(printerName);
    await dialog.getByLabel("Manufacturer").selectOption("Other");
    await dialog.getByLabel("Model").fill("QA virtual queue — no physical device");
    await dialog.getByLabel("Connection type").selectOption("WindowsQueue");
    await dialog.getByLabel("Windows queue name").fill("SMARTPRINT_QA_NO_DISPATCH");
    await dialog.getByRole("button", { name: "Add printer" }).click();
    await dialog.waitFor({ state: "detached", timeout: 20000 });
    printers = assertHttp(await request(page, "GET", "/api/v1/shop/printers", {
      token: tokens.accessToken
    }), 200, shopLabel + " printer list after UI creation");
    printer = printers.find(item => item.displayName === printerName);
  }
  assert.ok(printer?.id, shopLabel + " QA printer was not persisted by the real UI flow.");

  let stations = assertHttp(await request(page, "GET", "/api/v1/shop/qr-stations", {
    token: tokens.accessToken
  }), 200, shopLabel + " QR station list");
  let station = stations.find(item => item.displayName === stationName);

  if (!station) {
    await clickShopSection(page, "QR Stations");
    await page.locator(".collection-panel").getByRole("button", { name: "Create station" }).click();
    const dialog = page.getByRole("dialog", { name: "Create a QR station" });
    await dialog.waitFor({ state: "visible", timeout: 10000 });
    await dialog.getByLabel("Station name").fill(stationName);
    await dialog.getByLabel("Default printer").selectOption(printer.id);
    await dialog.getByRole("button", { name: "Create station" }).click();
    await dialog.waitFor({ state: "detached", timeout: 20000 });
    stations = assertHttp(await request(page, "GET", "/api/v1/shop/qr-stations", {
      token: tokens.accessToken
    }), 200, shopLabel + " QR station list after UI creation");
    station = stations.find(item => item.displayName === stationName);
  }
  assert.ok(station?.id, shopLabel + " QA QR station was not persisted by the real UI flow.");

  if (!station.isEnabled) {
    await clickShopSection(page, "QR Stations");
    const stationCard = page.locator("#qr-station-" + station.id);
    await stationCard.getByRole("button", { name: "Enable station" }).click();
    await stationCard.getByRole("button", { name: "Disable station" }).waitFor({ state: "visible", timeout: 15000 });
    stations = assertHttp(await request(page, "GET", "/api/v1/shop/qr-stations", {
      token: tokens.accessToken
    }), 200, shopLabel + " enabled QR station list");
    station = stations.find(item => item.id === station.id);
  }
  assert.equal(station?.isEnabled, true, shopLabel + " QA QR station must be enabled.");
  assert.equal(station?.printerDeviceId, printer.id, shopLabel + " QR station must use its own QA printer.");
  assert.ok(station?.publicUrl, shopLabel + " QA QR station must expose its public URL.");

  // The resource records are persistent after this point. Publish the fixture
  // before optional pricing/payment configuration so a later failure does not
  // hide successfully created records from diagnostics or the next idempotent run.
  onResourcesReady({
    printer: { id: printer.id, displayName: printer.displayName, outputMode: printer.outputMode,
      agentDeviceId: printer.agentDeviceId ?? null, queueName: printer.queueName },
    station: { id: station.id, displayName: station.displayName, publicUrl: station.publicUrl,
      isEnabled: station.isEnabled, printerDeviceId: station.printerDeviceId }
  });


  return {
    printer: {
      id: printer.id,
      displayName: printer.displayName,
      outputMode: printer.outputMode,
      agentDeviceId: printer.agentDeviceId ?? null,
      queueName: printer.queueName
    },
    station: {
      id: station.id,
      displayName: station.displayName,
      publicUrl: station.publicUrl,
      isEnabled: station.isEnabled,
      printerDeviceId: station.printerDeviceId
    },
    physicalPrinterPaired: false,
    printDispatchEnabled: false
  };
}

async function configureShopPricing(page, tokens, shopLabel) {
  const pricingValues = {
    monoPerSideMinor: 200,
    colourPerSideMinor: 500,
    monoDuplexPerSheetMinor: 300,
    colourDuplexPerSheetMinor: 700,
    minimumChargeMinor: 100,
    photo35x45Minor: 500,
    photo25x35Minor: 500,
    photo2x2Minor: 700,
    photo4x6Minor: 1000
  };
  await clickShopSection(page, "Pricing");
  const pricingPanel = page.locator(".pricing-page");
  await pricingPanel.waitFor({ state: "visible", timeout: 15000 });
  assert.equal(await pricingPanel.locator('input[type="number"]').count(), 9,
    shopLabel + " pricing page did not render all nine rate inputs.");
  const pricingInputs = [
  {
    "id": "pricing-mono-per-side",
    "label": "Black & white",
    "value": "2.00"
  },
  {
    "id": "pricing-colour-per-side",
    "label": "Colour",
    "value": "5.00"
  },
  {
    "id": "pricing-mono-duplex",
    "label": "Black & white duplex",
    "value": "3.00"
  },
  {
    "id": "pricing-colour-duplex",
    "label": "Colour duplex",
    "value": "7.00"
  },
  {
    "id": "pricing-minimum-charge",
    "label": "Minimum job charge",
    "value": "1.00"
  },
  {
    "id": "pricing-photo-35x45",
    "label": "35 × 45 mm",
    "value": "5.00"
  },
  {
    "id": "pricing-photo-25x35",
    "label": "25 × 35 mm",
    "value": "5.00"
  },
  {
    "id": "pricing-photo-2x2",
    "label": "2 × 2 inch",
    "value": "7.00"
  },
  {
    "id": "pricing-photo-4x6",
    "label": "4 × 6 inch",
    "value": "10.00"
  }
];
  for (const pricingInput of pricingInputs) {
    const input = pricingPanel.locator("#" + pricingInput.id);
    await input.waitFor({ state: "visible", timeout: 10000 });
    assert.equal(await input.count(), 1,
      shopLabel + " expected exactly one #" + pricingInput.id + " pricing input.");
    assert.equal(await input.getAttribute("aria-label"), pricingInput.label,
      shopLabel + " #" + pricingInput.id + " has an unexpected explicit accessible name.");
    assert.equal(await pricingPanel.getByLabel(pricingInput.label, { exact: true }).count(), 1,
      shopLabel + " #" + pricingInput.id + " is missing its accessible label.");
    const nativeLabelDetails = await input.evaluate(element => ({
      id: element.id,
      ariaLabel: element.getAttribute("aria-label"),
      labels: Array.from(element.labels || []).map(label => ({
        htmlFor: label.htmlFor,
        text: (label.innerText || "").trim()
      })),
      enclosingLabel: element.closest("label")?.outerHTML ?? null,
      outerHTML: element.outerHTML
    }));
    assert.ok(nativeLabelDetails.labels.some(label => label.htmlFor === nativeLabelDetails.id),
      shopLabel + " #" + pricingInput.id + " is missing its native label[for] association. DOM: " +
      JSON.stringify(nativeLabelDetails));
    await input.fill(pricingInput.value);
  }
  const pricingSaveResponsePromise = page.waitForResponse(response => {
    const request = response.request();
    const url = new URL(response.url());
    return url.origin === baseUrl.origin &&
      url.pathname === "/api/v1/shop/pricing" &&
      request.method() === "PUT";
  }, { timeout: 20000 });
  await page.getByRole("button", { name: "Save print prices" }).click();
  const pricingSaveResponse = await pricingSaveResponsePromise;
  const pricingSaveBody = await pricingSaveResponse.json().catch(() => null);
  assert.equal(pricingSaveResponse.status(), 200,
    shopLabel + " pricing save must return HTTP 200. Body: " + JSON.stringify(pricingSaveBody));
  assert.equal(pricingSaveBody?.isConfigured, true,
    shopLabel + " pricing save response did not mark the rate card configured.");
  for (const [field, expected] of Object.entries(pricingValues)) {
    assert.equal(pricingSaveBody?.[field], expected,
      shopLabel + " pricing save response did not persist " + field + ".");
  }
  await page.locator(".inline-success").filter({ hasText: "Print rate card saved." })
    .waitFor({ state: "visible", timeout: 15000 });

  const pricing = assertHttp(await request(page, "GET", "/api/v1/shop/pricing", {
    token: tokens.accessToken
  }), 200, shopLabel + " persisted pricing");
  assert.equal(pricing.isConfigured, true, shopLabel + " saved pricing read-back is not configured.");
  assert.equal(pricing.monoPerSideMinor, pricingValues.monoPerSideMinor, shopLabel + " mono rate read-back differs.");
  assert.equal(pricing.colourPerSideMinor, pricingValues.colourPerSideMinor, shopLabel + " colour rate read-back differs.");
  assert.equal(pricing.photo4x6Minor, pricingValues.photo4x6Minor, shopLabel + " 4x6 photo rate read-back differs.");


  return { pricingVersion: pricing.version, pricingConfiguredThroughUi: true };
}

async function configureShopPaymentMethods(page, tokens, shopLabel) {
  await clickShopSection(page, "Payments");
  const methodCards = page.locator(".payment-method-card");
  const setVisiblePaymentSwitch = async (card, inputId, enabled, description) => {
    const input = card.locator("#" + inputId);
    assert.equal(await input.count(), 1, shopLabel + " " + description + " switch ID is missing or duplicated.");
    if (await input.isChecked() !== enabled) {
      const visibleSwitch = card.locator("label.switch");
      assert.equal(await visibleSwitch.count(), 1, shopLabel + " " + description + " visible switch is missing or duplicated.");
      await visibleSwitch.click();
    }
    assert.equal(await input.isChecked(), enabled,
      shopLabel + " " + description + " did not change through its visible switch.");
  };
  await setVisiblePaymentSwitch(methodCards.nth(0), "payment-upi-enabled", true, "UPI");
  await page.getByLabel("Shop UPI ID").fill("smartprintqa@upi");
  await page.getByLabel("Payee / business name").fill("SmartPrint Local QA");
  await setVisiblePaymentSwitch(methodCards.nth(1), "payment-cash-enabled", true, "cash");
  await setVisiblePaymentSwitch(methodCards.nth(2), "payment-instant-enabled", false, "instant payment");

  const paymentSaveResponsePromise = page.waitForResponse(response => {
    const request = response.request();
    const url = new URL(response.url());
    return url.origin === baseUrl.origin &&
      url.pathname === "/api/v1/shop/payment-settings" &&
      request.method() === "PUT";
  }, { timeout: 20000 });
  await page.getByRole("button", { name: "Save payment methods" }).click();
  const paymentSaveResponse = await paymentSaveResponsePromise;
  const paymentSaveBody = await paymentSaveResponse.json().catch(() => null);
  assert.equal(paymentSaveResponse.status(), 200,
    shopLabel + " payment settings save must return HTTP 200. Body: " + JSON.stringify(paymentSaveBody));
  assert.equal(paymentSaveBody?.upiEnabled, true, shopLabel + " save response did not enable UPI.");
  assert.equal(paymentSaveBody?.upiId, "smartprintqa@upi", shopLabel + " save response returned the wrong UPI ID.");
  assert.equal(paymentSaveBody?.payeeName, "SmartPrint Local QA", shopLabel + " save response returned the wrong payee.");
  assert.equal(paymentSaveBody?.cashEnabled, true, shopLabel + " save response did not enable cash.");
  assert.equal(paymentSaveBody?.instantPaymentEnabled, false, shopLabel + " instant payment must remain disabled.");
  await page.locator(".inline-success").filter({ hasText: "Shop payment settings saved." })
    .waitFor({ state: "visible", timeout: 15000 });

  const paymentSettings = assertHttp(await request(page, "GET", "/api/v1/shop/payment-settings", {
    token: tokens.accessToken
  }), 200, shopLabel + " persisted payment settings");
  assert.equal(paymentSettings.upiEnabled, true, shopLabel + " payment settings read-back did not enable UPI.");
  assert.equal(paymentSettings.upiId, "smartprintqa@upi", shopLabel + " payment settings read-back returned the wrong UPI ID.");
  assert.equal(paymentSettings.payeeName, "SmartPrint Local QA", shopLabel + " payment settings read-back returned the wrong payee.");
  assert.equal(paymentSettings.cashEnabled, true, shopLabel + " payment settings read-back did not enable cash.");
  assert.equal(paymentSettings.instantPaymentEnabled, false, shopLabel + " payment settings read-back unexpectedly enabled instant payment.");

  return { paymentMethodsConfiguredThroughUi: true, paymentSettingsVerified: true };
}

async function assertResponsive(page, label, viewport) {
  await page.setViewportSize(viewport);
  await page.waitForTimeout(120);
  const metrics = await page.evaluate(() => {
    const width = window.innerWidth;
    const controls = Array.from(document.querySelectorAll("button,a,input,select,textarea,[role='button']"));
    const clipped = [];
    for (const element of controls) {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      if (style.display === "none" || style.visibility === "hidden" || rect.width <= 0 || rect.height <= 0) continue;
      const sidebar = element.closest(".sidebar");
      if (sidebar && !sidebar.classList.contains("sidebar-open") && width <= 850) continue;
      let parent = element.parentElement;
      let insideHorizontalScroller = false;
      while (parent && parent !== document.body) {
        const parentStyle = getComputedStyle(parent);
        const parentRect = parent.getBoundingClientRect();
        if ((parentStyle.overflowX === "auto" || parentStyle.overflowX === "scroll") &&
            parent.scrollWidth > parent.clientWidth + 1 &&
            parentRect.left >= -1 && parentRect.right <= width + 1) {
          insideHorizontalScroller = true;
          break;
        }
        parent = parent.parentElement;
      }
      if (!insideHorizontalScroller && (rect.left < -1 || rect.right > width + 1)) {
        clipped.push({
          tag: element.tagName.toLowerCase(),
          label: element.getAttribute("aria-label") || element.textContent?.trim().slice(0, 60) || "",
          left: Math.round(rect.left),
          right: Math.round(rect.right)
        });
      }
    }
    return {
      width,
      documentWidth: document.documentElement.scrollWidth,
      bodyWidth: document.body.scrollWidth,
      clippedControls: clipped.slice(0, 12)
    };
  });
  assert.ok(metrics.documentWidth <= viewport.width + 1,
    label + " document overflow at " + viewport.width + "px: " + JSON.stringify(metrics));
  assert.ok(metrics.bodyWidth <= viewport.width + 1,
    label + " body overflow at " + viewport.width + "px: " + JSON.stringify(metrics));
  assert.equal(metrics.clippedControls.length, 0,
    label + " has interactive controls clipped at " + viewport.width + "px: " + JSON.stringify(metrics.clippedControls));
  return metrics;
}

async function setWorkspaceTheme(page, dark) {
  const toggle = page.locator(".theme-toggle-button").first();
  await toggle.waitFor({ state: "visible", timeout: 10000 });
  const desired = String(dark);
  if (await toggle.getAttribute("aria-pressed") !== desired) await toggle.click();
  assert.equal(await toggle.getAttribute("aria-pressed"), desired,
    "The real workspace theme toggle did not switch to " + (dark ? "dark" : "light") + ".");
}

async function runAuthenticatedResponsiveMatrix(page, sections, label) {
  const viewports = [
    { width: 1440, height: 1000 },
    { width: 1024, height: 900 },
    { width: 768, height: 1024 },
    { width: 520, height: 900 },
    { width: 390, height: 844 }
  ];
  let checks = 0;
  const totalChecks = sections.length * viewports.length * 2;
  for (const dark of [false, true]) {
    const themeLabel = dark ? "dark" : "light";
    console.log("[QA-PROGRESS] " + label + " responsive matrix | starting " + themeLabel + " theme");
    await setWorkspaceTheme(page, dark);
    for (const viewport of viewports) {
      console.log("[QA-PROGRESS] " + label + " responsive matrix | " + themeLabel + " theme at " + viewport.width + "px");
      await page.setViewportSize(viewport);
      for (const section of sections) {
        const progressContext = label + " responsive " + themeLabel + " " + viewport.width + "px (" + (checks + 1) + "/" + totalChecks + ")";
        await navigateSidebarSection(page, section, progressContext);
        await assertResponsive(page, label + " / " + section + " / " + themeLabel, viewport);
        checks += 1;
      }
      console.log("[QA-PROGRESS] " + label + " responsive matrix | completed " + themeLabel + " " + viewport.width + "px (" + checks + "/" + totalChecks + ")");
    }
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  await setWorkspaceTheme(page, false);
  await screenshot(page, label + "-responsive-final");
  return { workspace: label, sections: sections.length, themes: 2, viewportWidths: viewports.map(item => item.width), layoutChecks: checks, mockedApiRoutes: 0 };
}

try {
  browser = await chromium.launch({
    headless: false,
    args: ["--start-maximized"]
  });
  report.browser = "Playwright Chromium headed";
  report.browserVersion = browser.version();
  report.browserExecutablePath = chromium.executablePath();
  context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: false });
  context.on("page", targetPage => {
    watchPage(targetPage);
    targetPage.bringToFront().catch(error => {
      console.warn("[QA-VISIBLE-WARN] Could not foreground a new Chromium tab: " + String(error?.message || error).slice(0, 200));
    });
  });
  page = await context.newPage();
  await page.bringToFront();
  page.setDefaultTimeout(15000);

  await record("Local portal and API health", async () => {
    const portal = await page.goto(baseUrl.toString(), { waitUntil: "domcontentloaded" });
    assert.equal(portal?.status(), 200, "Local portal did not return HTTP 200.");
    const live = assertHttp(await request(page, "GET", "/health/live"), 200, "Local API liveness");
    const ready = assertHttp(await request(page, "GET", "/health/ready"), 200, "Local API readiness");
    assert.equal(live.status, "live");
    assert.equal(live.service, "qrtoprint-api");
    assert.equal(ready.status, "ready");
    assert.equal(ready.database, "connected");
    return { portal: 200, api: live.status, database: ready.database, version: live.version };
  });

  await record("Public pages and registration/login/admin route rendering", async () => {
    const routes = [
      ["/", ".sp-public", "landing"],
      ["/shop", ".auth-screen", "shop login"],
      ["/login", ".auth-screen", "legacy login"],
      ["/signup", ".auth-screen", "shop registration"],
      ["/admin", ".platform-admin-auth-screen", "platform admin login"]
    ];
    const results = [];
    for (const [path, selector, label] of routes) {
      results.push(await openRoute(page, path, selector, label));
    }
    return { routes: results.length, results };
  });

  await record("Public routes real-browser responsive matrix", async () => {
    const routes = [
      ["/", ".sp-public", "landing"],
      ["/shop", ".auth-screen", "shop-login"],
      ["/signup", ".auth-screen", "shop-signup"],
      ["/admin", ".platform-admin-auth-screen", "admin-login"]
    ];
    const viewports = [
      { width: 1440, height: 1000 },
      { width: 1024, height: 900 },
      { width: 768, height: 1024 },
      { width: 520, height: 900 },
      { width: 390, height: 844 }
    ];
    const checks = [];
    for (const [path, selector, label] of routes) {
      await openRoute(page, path, selector, label);
      for (const viewport of viewports) checks.push(await assertResponsive(page, label, viewport));
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
    return { realPages: routes.length, viewportChecks: checks.length, mockedRequests: 0 };
  });

  await record("Create or sign in to the first real QA shop through the shop UI", async () => {
    shop1Page = await context.newPage();
    const result = await registerOrSignInShop(
      shop1Page,
      process.env.SMARTPRINT_QA_SHOP1_EMAIL,
      process.env.SMARTPRINT_QA_SHOP1_PASSWORD,
      process.env.SMARTPRINT_QA_SHOP1_NAME,
      "SmartPrint Local QA Owner One"
    );
    shop1Tokens = result.tokens;
    report.safety.registeredLocalQaShops = result.registered;
    return { account: "QA shop one", registrationCreatedAccount: result.registered, workspaceOpened: true };
  });

  await record("Create or sign in to the second real QA shop through the shop UI", async () => {
    shop2Page = await context.newPage();
    const result = await registerOrSignInShop(
      shop2Page,
      process.env.SMARTPRINT_QA_SHOP2_EMAIL,
      process.env.SMARTPRINT_QA_SHOP2_PASSWORD,
      process.env.SMARTPRINT_QA_SHOP2_NAME,
      "SmartPrint Local QA Owner Two"
    );
    shop2Tokens = result.tokens;
    report.safety.registeredLocalQaShops = report.safety.registeredLocalQaShops || result.registered;
    return { account: "QA shop two", registrationCreatedAccount: result.registered, workspaceOpened: true };
  });

  await record("Create or sign in to the real global customer account", async () => {
    const registration = await request(page, "POST", "/api/v1/auth/register-customer", {
      body: {
        displayName: process.env.SMARTPRINT_QA_CUSTOMER_NAME,
        email: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL,
        password: process.env.SMARTPRINT_QA_CUSTOMER_PASSWORD
      }
    });
    let created = false;
    if (registration.status === 201) {
      created = true;
      report.safety.registeredLocalQaCustomer = true;
    } else {
      assertExpectedHttpStatus(registration, 409, "Customer registration returned an unexpected status: " + registration.status + " " + registration.text.slice(0, 500));
    }
    const invalidLogin = await request(page, "POST", "/api/v1/auth/customer-login", {
      body: {
        email: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL,
        password: process.env.SMARTPRINT_QA_CUSTOMER_PASSWORD + "-wrong"
      }
    });
    assertExpectedHttpStatus(invalidLogin, 401, "Customer login accepted an incorrect password.");
    const login = await request(page, "POST", "/api/v1/auth/customer-login", {
      body: {
        email: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL,
        password: process.env.SMARTPRINT_QA_CUSTOMER_PASSWORD
      }
    });
    customerTokens = assertHttp(login, 200, "QA customer login");
    const profile = assertHttp(await request(page, "GET", "/api/v1/customer/me", {
      token: customerTokens.accessToken
    }), 200, "QA customer profile");
    const jobsResponse = assertHttp(await request(page, "GET", "/api/v1/customer/jobs", {
      token: customerTokens.accessToken
    }), 200, "QA customer jobs");
    const jobs = jobsResponse.jobs;
    assert.ok(profile.id && profile.email === process.env.SMARTPRINT_QA_CUSTOMER_EMAIL);
    assert.ok(Array.isArray(jobs), "Customer job history response did not contain its jobs array.");
    return { account: "global customer", createdThisRun: created, signInVerified: true, tenantScoped: false, jobsInitially: jobs.length };
  });

  await record("Bootstrap-created platform administrator signs in through the real admin UI", async () => {
    adminPage = await context.newPage();
    adminTokens = await signInPlatformAdmin(
      adminPage,
      process.env.SMARTPRINT_QA_ADMIN_EMAIL,
      process.env.SMARTPRINT_QA_ADMIN_PASSWORD
    );
    const overview = assertHttp(await request(adminPage, "GET", "/api/v1/platform/overview", {
      token: adminTokens.accessToken
    }), 200, "Platform overview");
    assert.ok(Number.isFinite(overview.tenantCount));
    return { account: "local platform admin", tenantCount: overview.tenantCount };
  });

  await record("Create and publish a free QA-only plan through the platform UI", async () => {
    qaPlan = await ensureQaPlanThroughPlatformUi(adminPage, adminTokens.accessToken);
    qaPlanVersionId = qaPlan.planVersionId;
    report.safety.qaPlanPublished = true;
    return {
      planCode: qaPlan.planCode,
      planVersionId: qaPlan.planVersionId,
      freeMonthlyPrice: true,
      cloudAssistedPrinting: true,
      published: true,
      noPaymentRequiredOrRecorded: true
    };
  });

  await record("Assign the QA-only plan to Shop One through the platform UI", async () => {
    const assignmentContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const assignmentPage = await assignmentContext.newPage();
    watchPage(assignmentPage);
    try {
      const assignmentAdminTokens = await signInPlatformAdmin(
        assignmentPage,
        process.env.SMARTPRINT_QA_ADMIN_EMAIL,
        process.env.SMARTPRINT_QA_ADMIN_PASSWORD
      );
      const one = await assignQaPlanThroughPlatformUi(
        assignmentPage, assignmentAdminTokens.accessToken, process.env.SMARTPRINT_QA_SHOP1_NAME, qaPlan
      );
      shop1TenantId = one.tenantId;
      report.safety.qaPlanAssignedToShop1 = true;
      return {
        shop: "Shop One",
        tenantId: one.tenantId,
        planVersionId: qaPlan.planVersionId,
        assignmentPerformedThisRun: !one.alreadyAssigned,
        status: one.status,
        noPaymentRequiredOrRecorded: true
      };
    } finally {
      await assignmentContext.close().catch(() => {});
    }
  });

  await record("Assign the QA-only plan to Shop Two through the platform UI", async () => {
    const assignmentContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const assignmentPage = await assignmentContext.newPage();
    watchPage(assignmentPage);
    try {
      const assignmentAdminTokens = await signInPlatformAdmin(
        assignmentPage,
        process.env.SMARTPRINT_QA_ADMIN_EMAIL,
        process.env.SMARTPRINT_QA_ADMIN_PASSWORD
      );
      const two = await assignQaPlanThroughPlatformUi(
        assignmentPage, assignmentAdminTokens.accessToken, process.env.SMARTPRINT_QA_SHOP2_NAME, qaPlan
      );
      shop2TenantId = two.tenantId;
      report.safety.qaPlanAssignedToShop2 = true;
      report.safety.assignedQaOnlyFreePlan = Boolean(
        report.safety.qaPlanAssignedToShop1 && report.safety.qaPlanAssignedToShop2
      );
      return {
        shop: "Shop Two",
        tenantId: two.tenantId,
        planVersionId: qaPlan.planVersionId,
        assignmentPerformedThisRun: !two.alreadyAssigned,
        status: two.status,
        noPaymentRequiredOrRecorded: true
      };
    } finally {
      await assignmentContext.close().catch(() => {});
    }
  });

  await record("Platform admin navigates every real control-centre section", async () => {
    const sections = ["Overview", "Shop tenants", "Plans & entitlements", "Shop subscriptions", "Operations & retention"];
    const visited = await navigateSections(adminPage, sections, "platform-admin");
    assert.equal(visited.length, sections.length);
    const operations = assertHttp(await request(adminPage, "GET", "/api/v1/platform/settings/operations", {
      token: adminTokens.accessToken
    }), 200, "Platform operational settings");
    const operationsHistory = assertHttp(await request(adminPage, "GET", "/api/v1/platform/settings/operations/history", {
      token: adminTokens.accessToken
    }), 200, "Platform operational settings history");
    const tenants = assertHttp(await request(adminPage, "GET", "/api/v1/platform/tenants?page=1&pageSize=50", {
      token: adminTokens.accessToken
    }), 200, "Platform tenant directory");
    const plans = assertHttp(await request(adminPage, "GET", "/api/v1/platform/plans", {
      token: adminTokens.accessToken
    }), 200, "Platform plans");
    const subscriptions = assertHttp(await request(adminPage, "GET", "/api/v1/platform/subscriptions?page=1&pageSize=50", {
      token: adminTokens.accessToken
    }), 200, "Platform subscriptions");
    const subscriptionShopNames = new Set(subscriptions.items.map(item => item.businessName));
    assert.ok(subscriptionShopNames.has(process.env.SMARTPRINT_QA_SHOP1_NAME),
      "The platform control centre cannot find QA Shop One.");
    assert.ok(subscriptionShopNames.has(process.env.SMARTPRINT_QA_SHOP2_NAME),
      "The platform control centre cannot find QA Shop Two.");
    assert.ok(plans.some(item => item.code === "SMARTPRINT_QA_AUTOMATION"));
    const responsive = await runAuthenticatedResponsiveMatrix(
      adminPage,
      sections,
      "platform-admin"
    );
    return {
      sectionsVisited: visited,
      responsive,
      operationsSettingsLoaded: Boolean(operations),
      operationsHistoryRows: operationsHistory.length,
      tenantRows: tenants.items.length,
      planRows: plans.length,
      subscriptionRows: subscriptions.items.length
    };
  });

  await record("Shop one provisions real QA printer and QR station", async () => {
    const resources = await setupShopResources(shop1Page, shop1Tokens, "Shop one", ready => {
      shop1Printer = ready.printer;
      shop1Station = ready.station;
    });
    shop1Printer = resources.printer;
    shop1Station = resources.station;
    return {
      tenant: process.env.SMARTPRINT_QA_SHOP1_NAME,
      printerId: shop1Printer.id,
      stationId: shop1Station.id,
      stationEnabled: shop1Station.isEnabled,
      printerAndStationPersisted: true,
      printDispatchEnabled: false
    };
  });

  await record("Shop one configures print pricing through the real UI", async () => ({
    tenant: process.env.SMARTPRINT_QA_SHOP1_NAME,
    ...(await configureShopPricing(shop1Page, shop1Tokens, "Shop one"))
  }));

  await record("Shop one configures payment methods through the real UI", async () => ({
    tenant: process.env.SMARTPRINT_QA_SHOP1_NAME,
    ...(await configureShopPaymentMethods(shop1Page, shop1Tokens, "Shop one"))
  }));

  await record("Shop two configures isolated real QA printer/station", async () => {
    const resources = await setupShopResources(shop2Page, shop2Tokens, "Shop two", ready => {
      shop2Printer = ready.printer;
      shop2Station = ready.station;
    });
    shop2Printer = resources.printer;
    shop2Station = resources.station;
    return {
      tenant: process.env.SMARTPRINT_QA_SHOP2_NAME,
      printerId: shop2Printer.id,
      stationId: shop2Station.id,
      stationEnabled: shop2Station.isEnabled
    };
  });

  await record("SHOP: invalid device, printer, profile, account and pricing inputs fail without changing saved settings", async () => {
    const pricingBefore = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/pricing", {
      token: shop1Tokens.accessToken
    }), 200, "Shop one pricing before validation");
    const printerCountBefore = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/printers", {
      token: shop1Tokens.accessToken
    }), 200, "Shop one printers before validation").length;
    const stationCountBefore = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/qr-stations", {
      token: shop1Tokens.accessToken
    }), 200, "Shop one stations before validation").length;

    const commonPricing = {
      monoPerSideMinor: pricingBefore.monoPerSideMinor,
      colourPerSideMinor: pricingBefore.colourPerSideMinor,
      monoDuplexPerSheetMinor: pricingBefore.monoDuplexPerSheetMinor,
      colourDuplexPerSheetMinor: pricingBefore.colourDuplexPerSheetMinor,
      minimumChargeMinor: pricingBefore.minimumChargeMinor,
      photo35x45Minor: pricingBefore.photo35x45Minor,
      photo25x35Minor: pricingBefore.photo25x35Minor,
      photo2x2Minor: pricingBefore.photo2x2Minor,
      photo4x6Minor: pricingBefore.photo4x6Minor
    };
    const negativeRate = await request(shop1Page, "PUT", "/api/v1/shop/pricing", {
      token: shop1Tokens.accessToken,
      body: { ...commonPricing, monoPerSideMinor: -1 }
    });
    assertExpectedHttpStatus(negativeRate, 400, "Negative print rates must be rejected.");

    const zeroBaseRate = await request(shop1Page, "PUT", "/api/v1/shop/pricing", {
      token: shop1Tokens.accessToken,
      body: { ...commonPricing, monoPerSideMinor: 0 }
    });
    assertExpectedHttpStatus(zeroBaseRate, 400, "A zero mono base rate must be rejected.");

    const invalidCapabilities = await request(shop1Page, "POST", "/api/v1/shop/printers", {
      token: shop1Tokens.accessToken,
      body: {
        displayName: "QA invalid capabilities printer",
        manufacturer: "Test",
        model: "Validation only",
        connectionType: "WindowsQueue",
        queueName: "INVALID_QA_QUEUE",
        capabilitiesJson: "[]"
      }
    });
    assertExpectedHttpStatus(invalidCapabilities, 400, "Printer capabilities must be a JSON object, not an array.");

    const unsupportedConnection = await request(shop1Page, "POST", "/api/v1/shop/printers", {
      token: shop1Tokens.accessToken,
      body: {
        displayName: "QA unsupported connection printer",
        manufacturer: "Test",
        model: "Validation only",
        connectionType: "Bluetooth",
        queueName: null,
        capabilitiesJson: "{}"
      }
    });
    assertExpectedHttpStatus(unsupportedConnection, 400, "Unsupported printer connection types must be rejected.");

    const foreignPrinterStation = await request(shop1Page, "POST", "/api/v1/shop/qr-stations", {
      token: shop1Tokens.accessToken,
      body: { displayName: "QA must not bind foreign printer", printerDeviceId: shop2Printer.id }
    });
    assertExpectedHttpStatus(foreignPrinterStation, 400, "A station must not be attached to another shop's printer.");

    const currentProfile = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/profile", {
      token: shop1Tokens.accessToken
    }), 200, "Shop profile before validation");
    const insecureLogo = await request(shop1Page, "PUT", "/api/v1/shop/profile", {
      token: shop1Tokens.accessToken,
      body: {
        displayName: currentProfile.displayName,
        accentColor: "#123456",
        logoUrl: "http://example.invalid/logo.png"
      }
    });
    assertExpectedHttpStatus(insecureLogo, 400, "Shop branding must reject non-HTTPS logo URLs.");

    const invalidAccount = await request(shop1Page, "PUT", "/api/v1/shop/account", {
      token: shop1Tokens.accessToken,
      body: { ownerName: "SmartPrint QA Owner One", email: "invalid-email", currentPassword: null }
    });
    assertExpectedHttpStatus(invalidAccount, 400, "Owner account changes must reject invalid email addresses.");

    const pricingAfter = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/pricing", {
      token: shop1Tokens.accessToken
    }), 200, "Shop one pricing after validation");
    const printersAfter = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/printers", {
      token: shop1Tokens.accessToken
    }), 200, "Shop one printers after validation");
    const stationsAfter = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/qr-stations", {
      token: shop1Tokens.accessToken
    }), 200, "Shop one stations after validation");
    assert.equal(pricingAfter.version, pricingBefore.version, "Rejected pricing input must not increment the rate-card version.");
    assert.equal(printersAfter.length, printerCountBefore, "Rejected printer inputs must not create records.");
    assert.equal(stationsAfter.length, stationCountBefore, "Rejected station inputs must not create records.");

    return {
      rejectedInputs: [negativeRate.status, zeroBaseRate.status, invalidCapabilities.status, unsupportedConnection.status, foreignPrinterStation.status, insecureLogo.status, invalidAccount.status],
      rateCardVersionUnchanged: true,
      printerCountUnchanged: true,
      stationCountUnchanged: true
    };
  }, ["SHOP-001", "SHOP-002", "SHOP-003", "SHOP-004", "SHOP-005", "SHOP-006", "SHOP-007", "SHOP-008"]);

  await record("REPORTS: supported reporting windows return tenant-scoped summary data and invalid periods are rejected", async () => {
    const periods = [];
    for (const days of [7, 30, 90]) {
      const summary = assertHttp(await request(shop1Page, "GET",
        "/api/v1/shop/reports/summary?days=" + days,
        { token: shop1Tokens.accessToken }), 200, "Shop reports " + days + "-day window");
      assert.equal(summary.periodDays, days);
      assert.ok(Number.isInteger(summary.totalJobs) && summary.totalJobs >= 0);
      assert.ok(Number.isInteger(summary.completedJobs) && summary.completedJobs >= 0);
      assert.ok(Number.isInteger(summary.outputUnconfirmedJobs) && summary.outputUnconfirmedJobs >= 0);
      assert.ok(Number.isInteger(summary.pagesRequested) && summary.pagesRequested >= 0);
      assert.ok(Number.isInteger(summary.pendingPaymentCount) && summary.pendingPaymentCount >= 0);
      assert.ok(Array.isArray(summary.jobsByStatus));
      assert.ok(Array.isArray(summary.verifiedCollectionsByMethod));
      assert.ok(Array.isArray(summary.refundsRecordedByMethod));
      assert.ok(Date.parse(summary.fromUtc) < Date.parse(summary.toUtc));
      periods.push({ days, totalJobs: summary.totalJobs, pendingPayments: summary.pendingPaymentCount });
    }

    const unsupported = await request(shop1Page, "GET",
      "/api/v1/shop/reports/summary?days=14",
      { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(unsupported, 400, "Unsupported reporting periods must be rejected.");

    const secondTenant = assertHttp(await request(shop2Page, "GET",
      "/api/v1/shop/reports/summary?days=30",
      { token: shop2Tokens.accessToken }), 200, "Shop two's own reporting summary");
    assert.equal(secondTenant.periodDays, 30);
    assert.ok(Array.isArray(secondTenant.jobsByStatus));

    return {
      supportedPeriods: periods.map(item => item.days),
      invalidPeriod: unsupported.status,
      secondTenantReportScopedToItsOwnSession: true
    };
  }, ["REPORT-001", "REPORT-002", "REPORT-003", "REPORT-004"]);

  await record("Shop one navigates every real sidebar section and the account-menu-only tools", async () => {
    const sections = [
      "Order Journey", "Overview", "Print Queue", "Customers",
      "Printers", "QR Stations", "Print Agent", "Pricing", "Payments", "Reports"
    ];
    const visited = await navigateSections(shop1Page, sections, "shop-one");
    const accountMenu = shop1Page.getByRole("button", { name: /Account options for/ });
    console.log("[QA-PROGRESS] shop-one account tools | Personal details");
    await accountMenu.click();
    await shop1Page.getByRole("button", { name: "Personal details" }).click();
    await shop1Page.getByRole("dialog", { name: "Personal details" }).waitFor({ state: "visible", timeout: 10000 });
    await screenshot(shop1Page, "shop-one-personal-details");
    await shop1Page.getByRole("button", { name: "Close" }).last().click();
    console.log("[QA-PROGRESS] shop-one account tools | Shop profile & branding");
    await accountMenu.click();
    await shop1Page.getByRole("button", { name: "Shop profile & branding" }).click();
    await shop1Page.locator(".settings-panel").waitFor({ state: "visible", timeout: 10000 });
    await screenshot(shop1Page, "shop-one-profile-branding");
    console.log("[QA-PROGRESS] shop-one account tools | Trial & subscription");
    await accountMenu.click();
    await shop1Page.getByRole("button", { name: "Trial & subscription" }).click();
    await shop1Page.locator(".subscription-workspace").waitFor({ state: "visible", timeout: 10000 });
    await screenshot(shop1Page, "shop-one-subscription");
    console.log("[QA-PROGRESS] shop-one account tools | Help & support");
    await accountMenu.click();
    await shop1Page.getByRole("button", { name: "Help & support" }).click();
    await shop1Page.getByRole("dialog", { name: "Help & support" }).waitFor({ state: "visible", timeout: 10000 });
    await screenshot(shop1Page, "shop-one-help-support");
    await shop1Page.getByRole("button", { name: "Close" }).last().click();
    const checks = [
      ["/api/v1/shop/profile", "profile"],
      ["/api/v1/shop/subscription", "subscription"],
      ["/api/v1/shop/printers", "printers"],
      ["/api/v1/shop/qr-stations", "stations"],
      ["/api/v1/shop/pricing", "pricing"],
      ["/api/v1/shop/customers", "customers"],
      ["/api/v1/shop/print-jobs?offset=0&take=100", "jobs"],
      ["/api/v1/shop/reports/summary", "reports"],
      ["/api/v1/shop/agent-devices", "agents"],
      ["/api/v1/shop/payment-settings", "paymentSettings"],
      ["/api/v1/shop/payments", "payments"],
      ["/api/v1/shop/refunds", "refunds"]
    ];
    const payload = {};
    for (let index = 0; index < checks.length; index++) {
      const [path, key] = checks[index];
      const progress = "shop-one API reads " + (index + 1) + "/" + checks.length + " | " + key;
      console.log("[QA-PROGRESS] " + progress + " | started");
      payload[key] = assertHttp(await request(shop1Page, "GET", path, { token: shop1Tokens.accessToken }), 200, "Shop one " + key);
      console.log("[QA-PROGRESS] " + progress + " | completed");
    }
    const responsive = await runAuthenticatedResponsiveMatrix(
      shop1Page,
      sections,
      "shop-one"
    );
    return {
      sectionsVisited: visited,
      realApiReads: checks.length,
      printerRows: payload.printers.length,
      stationRows: payload.stations.length,
      pricingConfigured: Boolean(payload.pricing.isConfigured),
      responsive
    };
  });

  await record("Shop two navigates every real sidebar section and account-menu-only tools", async () => {
    const sections = [
      "Order Journey", "Overview", "Print Queue", "Customers",
      "Printers", "QR Stations", "Print Agent", "Pricing", "Payments", "Reports"
    ];
    const visited = await navigateSections(shop2Page, sections, "shop-two");
    const accountMenu = shop2Page.getByRole("button", { name: /Account options for/ });
    console.log("[QA-PROGRESS] shop-two account tools | Shop profile & branding");
    await accountMenu.click();
    await shop2Page.getByRole("button", { name: "Shop profile & branding" }).click();
    await shop2Page.locator(".settings-panel").waitFor({ state: "visible", timeout: 10000 });
    console.log("[QA-PROGRESS] shop-two account tools | Trial & subscription");
    await accountMenu.click();
    await shop2Page.getByRole("button", { name: "Trial & subscription" }).click();
    await shop2Page.locator(".subscription-workspace").waitFor({ state: "visible", timeout: 10000 });
    const paths = [
      "/api/v1/shop/profile", "/api/v1/shop/subscription", "/api/v1/shop/printers",
      "/api/v1/shop/qr-stations", "/api/v1/shop/pricing", "/api/v1/shop/customers",
      "/api/v1/shop/print-jobs?offset=0&take=100", "/api/v1/shop/reports/summary",
      "/api/v1/shop/agent-devices", "/api/v1/shop/payment-settings",
      "/api/v1/shop/payments", "/api/v1/shop/refunds"
    ];
    for (let index = 0; index < paths.length; index++) {
      const path = paths[index];
      console.log("[QA-PROGRESS] shop-two API reads " + (index + 1) + "/" + paths.length + " | " + path + " | started");
      assertHttp(await request(shop2Page, "GET", path, { token: shop2Tokens.accessToken }), 200, "Shop two " + path);
      console.log("[QA-PROGRESS] shop-two API reads " + (index + 1) + "/" + paths.length + " | completed");
    }
    const responsive = await runAuthenticatedResponsiveMatrix(
      shop2Page,
      sections,
      "shop-two"
    );
    return { sectionsVisited: visited, realApiReads: paths.length, responsive };
  });

  await record("Shop, platform-admin and cross-tenant authorization boundaries", async () => {
    const shopCannotSeePlatform = await request(shop1Page, "GET", "/api/v1/platform/overview", { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(shopCannotSeePlatform, [401, 403], "Shop owner unexpectedly accessed platform overview.");
    const adminCannotSeeShopProfile = await request(adminPage, "GET", "/api/v1/shop/profile", { token: adminTokens.accessToken });
    assertExpectedHttpStatus(adminCannotSeeShopProfile, [401, 403], "Platform admin unexpectedly accessed shop profile.");
    const shopOneCannotSeeShopTwoPrinter = await request(shop1Page, "PATCH",
      "/api/v1/shop/printers/" + shop2Printer.id + "/active?isActive=false",
      { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(shopOneCannotSeeShopTwoPrinter, 404, "Shop one could alter a printer belonging to shop two.");
    const customerCannotSeePlatform = await request(page, "GET", "/api/v1/platform/overview", {
      token: customerTokens.accessToken
    });
    assertExpectedHttpStatus(customerCannotSeePlatform, [401, 403], "A global customer must not access the platform control plane.");
    return { shopToPlatform: shopCannotSeePlatform.status, adminToShop: adminCannotSeeShopProfile.status, crossTenantPrinter: shopOneCannotSeeShopTwoPrinter.status, customerToPlatform: customerCannotSeePlatform.status };
  });

  await record("Customer opens the shop-one station link in a guest browser session", async () => {
    guestCustomerPage = await context.newPage();
    const stationPath = new URL(shop1Station.publicUrl).pathname;
    const publicCode = stationPath.split("/").filter(Boolean)[1];
    const customerQuery = "/api/v1/shop/customers?search=" +
      encodeURIComponent(process.env.SMARTPRINT_QA_CUSTOMER_EMAIL) + "&page=1&pageSize=50";
    const customersBefore = assertHttp(await request(shop1Page, "GET", customerQuery, {
      token: shop1Tokens.accessToken
    }), 200, "Shop customer directory before guest station entry");
    const customerBefore = customersBefore.items.find(item => item.displayName === process.env.SMARTPRINT_QA_CUSTOMER_NAME);
    const sessionCountBefore = customerBefore?.sessionCount ?? 0;

    const creationResponsePromise = guestCustomerPage.waitForResponse(response => {
      const request = response.request();
      const url = new URL(response.url());
      return request.method() === "POST" && url.origin === baseUrl.origin &&
        url.pathname === "/api/v1/public/stations/" + publicCode + "/sessions";
    }, { timeout: 20000 });

    const navigation = await guestCustomerPage.goto(new URL(stationPath, baseUrl).toString(), {
      waitUntil: "domcontentloaded", timeout: 30000
    });
    assert.equal(navigation?.status(), 200,
      "Opening the printed station link in an ordinary browser must render the customer entry page.");

    const creationResponse = await creationResponsePromise;
    assert.equal(creationResponse.status(), 201, "A public station URL must create a short-lived guest session.");
    const requestHeaders = await creationResponse.request().allHeaders();
    assert.equal(requestHeaders.authorization, undefined,
      "An ordinary browser link must not inherit a native SmartPrint account bearer token.");

    const created = await creationResponse.json();
    await guestCustomerPage.locator(".customer-entry-card .entry-eyebrow").waitFor({ state: "visible", timeout: 30000 });
    assert.equal((await guestCustomerPage.locator(".customer-entry-card .entry-eyebrow").innerText()).trim(), "SECURE SESSION CREATED");

    const sessionToken = await guestCustomerPage.evaluate(code =>
      sessionStorage.getItem("qrtoprint.customer-session:" + code), publicCode);
    assert.ok(sessionToken, "The browser must keep its QR session token in tab-scoped sessionStorage.");
    assert.equal(sessionToken, created.sessionToken, "The browser must persist the exact server-issued token.");
    assert.ok(!guestCustomerPage.url().includes(sessionToken),
      "The opaque session token must never appear in the QR URL or browser address bar.");

    const session = assertHttp(await request(guestCustomerPage, "GET", "/api/v1/public/sessions/current", {
      sessionToken
    }), 200, "Guest browser current customer session");
    assert.equal(session.id, created.id);
    assert.equal(session.shop.displayName, process.env.SMARTPRINT_QA_SHOP1_NAME);

    const customersAfter = assertHttp(await request(shop1Page, "GET", customerQuery, {
      token: shop1Tokens.accessToken
    }), 200, "Shop customer directory after guest station entry");
    const customerAfter = customersAfter.items.find(item => item.displayName === process.env.SMARTPRINT_QA_CUSTOMER_NAME);
    const sessionCountAfter = customerAfter?.sessionCount ?? 0;
    assert.equal(sessionCountAfter, sessionCountBefore,
      "A guest session without an Authorization header must not bind to or increment the global customer's shop session count.");

    guestCustomerSession = { sessionToken, publicCode, stationPath, id: session.id, shopName: session.shop.displayName };
    return {
      entryMode: "ordinary HTTPS browser link",
      authorizationHeaderSent: false,
      sessionId: session.id,
      sessionStoredInTabSessionStorage: true,
      tokenInUrl: false,
      globalCustomerSessionCountBefore: sessionCountBefore,
      globalCustomerSessionCountAfter: sessionCountAfter,
      shopName: session.shop.displayName
    };
  });

  await record("Customer opens the shop-one station with authenticated customer identity", async () => {
    customerPage = await context.newPage();
    const customerQuery = "/api/v1/shop/customers?search=" +
      encodeURIComponent(process.env.SMARTPRINT_QA_CUSTOMER_EMAIL) + "&page=1&pageSize=50";
    const customersBefore = assertHttp(await request(shop1Page, "GET", customerQuery, {
      token: shop1Tokens.accessToken
    }), 200, "Shop customer directory before authenticated station entry");
    const customerBefore = customersBefore.items.find(item =>
      item.displayName === process.env.SMARTPRINT_QA_CUSTOMER_NAME
    );
    const sessionCountBefore = customerBefore?.sessionCount ?? 0;
    let observedAuthorizationHeader = null;
    await customerPage.route("**/api/v1/public/stations/**/sessions", async route => {
      const headers = { ...route.request().headers(), authorization: "Bearer " + customerTokens.accessToken };
      observedAuthorizationHeader = headers.authorization;
      await route.continue({ headers });
    });
    const stationPath = new URL(shop1Station.publicUrl).pathname;
    const publicCode = stationPath.split("/").filter(Boolean)[1];
    const response = await customerPage.goto(new URL(stationPath, baseUrl).toString(), {
      waitUntil: "domcontentloaded", timeout: 30000
    });
    assert.equal(response?.status(), 200);
    await customerPage.locator(".customer-entry-card .entry-eyebrow").waitFor({ state: "visible", timeout: 30000 });
    assert.equal((await customerPage.locator(".customer-entry-card .entry-eyebrow").innerText()).trim(), "SECURE SESSION CREATED");
    assert.equal(observedAuthorizationHeader, "Bearer " + customerTokens.accessToken,
      "The authenticated native-account header contract must pass the global customer's bearer token only in Authorization.");

    const sessionToken = await customerPage.evaluate(code =>
      sessionStorage.getItem("qrtoprint.customer-session:" + code), publicCode);
    assert.ok(sessionToken, "The authenticated customer station token must be persisted in tab storage.");
    assert.ok(!customerPage.url().includes(sessionToken), "The opaque session token must never appear in the authenticated station URL.");
    const session = assertHttp(await request(customerPage, "GET", "/api/v1/public/sessions/current", {
      sessionToken
    }), 200, "Authenticated customer current station session");
    assert.equal(session.shop.displayName, process.env.SMARTPRINT_QA_SHOP1_NAME);
    const customerProfile = assertHttp(await request(customerPage, "GET", "/api/v1/customer/me", {
      token: customerTokens.accessToken
    }), 200, "Global customer profile for authenticated station linkage");
    assert.ok(customerProfile.id && customerProfile.email === process.env.SMARTPRINT_QA_CUSTOMER_EMAIL,
      "The authenticated QR session must belong to the expected global customer.");

    const customersAfter = assertHttp(await request(shop1Page, "GET", customerQuery, {
      token: shop1Tokens.accessToken
    }), 200, "Shop customer directory after authenticated station entry");
    const customerAfter = customersAfter.items.find(item =>
      item.displayName === process.env.SMARTPRINT_QA_CUSTOMER_NAME
    );
    assert.ok(customerAfter, "The authenticated station session must appear in the shop customer directory.");
    assert.equal(customerAfter.id, customerProfile.id,
      "The shop directory customer ID must match the signed-in global customer identity.");
    assert.equal(customerAfter.sessionCount, sessionCountBefore + 1,
      "The authenticated station session must bind exactly once to the global customer account.");

    customerSession = {
      sessionToken, publicCode, stationPath, id: session.id,
      customerId: customerProfile.id, sessionCount: customerAfter.sessionCount,
      shopName: session.shop.displayName
    };
    return {
      entryMode: "authenticated native-account header contract",
      accountBoundSession: true,
      sessionId: session.id,
      globalCustomerSessionCount: customerAfter.sessionCount,
      tokenInUrl: false,
      shopName: session.shop.displayName
    };
  });

  await record("QR/UPLOAD: session renewal, scoped resume and invalid local-upload metadata are safely rejected", async () => {
    const anonymousCurrent = await request(customerPage, "GET", "/api/v1/public/sessions/current");
    assertExpectedHttpStatus(anonymousCurrent, 401, "A customer session must not be readable without its opaque session token.");

    const renewed = assertHttp(await request(customerPage, "POST", "/api/v1/public/sessions/current/renew", {
      token: customerTokens.accessToken,
      sessionToken: customerSession.sessionToken
    }), 200, "Renew current QR session");
    assert.ok(renewed.expiresUtc, "Session renewal must return an expiry.");

    const resumed = assertHttp(await request(customerPage, "POST", "/api/v1/public/sessions/current/resume", {
      token: customerTokens.accessToken,
      sessionToken: customerSession.sessionToken,
      body: { publicCode: customerSession.publicCode }
    }), 200, "Resume current QR station session");
    assert.equal(resumed.id, customerSession.id, "Resume must return the original session, not create another session.");

    const shopTwoCode = new URL(shop2Station.publicUrl).pathname.split("/").filter(Boolean)[1];
    const crossStationResume = await request(customerPage, "POST", "/api/v1/public/sessions/current/resume", {
      token: customerTokens.accessToken,
      sessionToken: customerSession.sessionToken,
      body: { publicCode: shopTwoCode }
    });
    assertExpectedHttpStatus(crossStationResume, 401, "A customer session must not be rebound to another shop's station.");

    const invalidDigest = await request(customerPage, "POST", "/api/v1/public/sessions/current/local-upload-tickets", {
      sessionToken: customerSession.sessionToken,
      headers: { "Idempotency-Key": crypto.randomUUID() },
      body: {
        originalFileName: "smartprint-invalid-local-ticket.pdf",
        sizeBytes: 1024,
        sha256: "not-a-sha256-digest",
        grantToken: "g".repeat(48),
        colourMode: "Mono",
        paperSize: "A4",
        copies: 1,
        orientation: "AUTO"
      }
    });
    assertExpectedHttpStatus(invalidDigest, 422, "A local upload ticket with an invalid SHA-256 digest must be rejected.");

    const unsupportedExtension = await request(customerPage, "POST", "/api/v1/public/sessions/current/local-upload-tickets", {
      sessionToken: customerSession.sessionToken,
      headers: { "Idempotency-Key": crypto.randomUUID() },
      body: {
        originalFileName: "smartprint-invalid-local-ticket.exe",
        sizeBytes: 1024,
        sha256: "a".repeat(64),
        grantToken: "g".repeat(48),
        colourMode: "Mono",
        paperSize: "A4",
        copies: 1,
        orientation: "AUTO"
      }
    });
    assertExpectedHttpStatus(unsupportedExtension, 422, "Local upload must reject unsupported file extensions.");

    return {
      sessionRenewed: true,
      resumedSameSession: true,
      crossTenantResume: crossStationResume.status,
      invalidDigest: invalidDigest.status,
      unsupportedExtension: unsupportedExtension.status
    };
  }, ["QR-001", "QR-002", "QR-003", "QR-004", "UPLOAD-001", "UPLOAD-002"]);

  await record("Customer QR session is linked to the global customer account", async () => {
    assert.ok(customerSession?.id && customerSession?.customerId && customerSession?.sessionToken,
      "The authenticated customer station session must have a captured global identity.");

    const profile = assertHttp(await request(customerPage, "GET", "/api/v1/customer/me", {
      token: customerTokens.accessToken
    }), 200, "Global customer profile");
    assert.equal(profile.id, customerSession.customerId,
      "The customer token must resolve to the identity used for station entry.");

    const currentSession = assertHttp(await request(customerPage, "GET", "/api/v1/public/sessions/current", {
      sessionToken: customerSession.sessionToken
    }), 200, "Current authenticated QR session");
    assert.equal(currentSession.id, customerSession.id,
      "The current QR session must be the same server-issued session created with the customer bearer token.");

    const customerQuery = "/api/v1/shop/customers?search=" +
      encodeURIComponent(process.env.SMARTPRINT_QA_CUSTOMER_EMAIL) + "&page=1&pageSize=50";
    const directory = assertHttp(await request(shop1Page, "GET", customerQuery, {
      token: shop1Tokens.accessToken
    }), 200, "Shop directory for the authenticated global customer");
    const linkedCustomer = directory.items.find(item => item.id === profile.id);
    assert.ok(linkedCustomer,
      "The shop directory must link the authenticated station session to the same global customer ID.");
    assert.equal(linkedCustomer.sessionCount, customerSession.sessionCount,
      "The directory must retain the exact authenticated session count captured at creation.");

    const jobs = assertHttp(await request(customerPage, "GET", "/api/v1/customer/jobs", {
      token: customerTokens.accessToken
    }), 200, "Global customer job history");
    assert.ok(Array.isArray(jobs.jobs),
      "The global customer history endpoint must return a jobs collection.");
    return {
      sessionId: currentSession.id,
      globalCustomerId: profile.id,
      linkedShopCustomerId: linkedCustomer.id,
      authenticatedSessionCount: linkedCustomer.sessionCount,
      customerJobsBeforeUpload: jobs.jobs.length,
      globalIdentityApi: "PASS"
    };
  });

  await record("Guest customer completes a no-account upload from the shop station link", async () => {
    let guestJobId = null;
    let guestJobSubmissionAttempted = false;
    let guestJobRejected = false;
    let guestUploadRequestHeaders = null;
    try {
      assert.ok(guestCustomerPage && guestCustomerSession?.sessionToken,
        "A guest station link must be opened successfully before testing guest uploads.");

      const session = assertHttp(await request(guestCustomerPage, "GET", "/api/v1/public/sessions/current", {
        sessionToken: guestCustomerSession.sessionToken
      }), 200, "Guest browser current session");
      assert.equal(session.id, guestCustomerSession.id,
        "The guest browser must keep using the server-issued station session.");
      assert.equal(session.shop.displayName, process.env.SMARTPRINT_QA_SHOP1_NAME);
      assert.ok(!guestCustomerPage.url().includes(guestCustomerSession.sessionToken),
        "The opaque guest session token must never be present in the station URL.");

      const customerQuery = "/api/v1/shop/customers?search=" +
        encodeURIComponent(process.env.SMARTPRINT_QA_CUSTOMER_EMAIL) + "&page=1&pageSize=50";
      const customersBefore = assertHttp(await request(shop1Page, "GET", customerQuery, {
        token: shop1Tokens.accessToken
      }), 200, "Shop customer directory before guest upload");
      const customerBefore = customersBefore.items.find(item =>
        item.displayName === process.env.SMARTPRINT_QA_CUSTOMER_NAME
      );
      const customerSessionCountBefore = customerBefore?.sessionCount ?? 0;
      const customerJobsBefore = assertHttp(await request(customerPage, "GET", "/api/v1/customer/jobs", {
        token: customerTokens.accessToken
      }), 200, "Signed-in customer's history before guest upload");
      assert.ok(Array.isArray(customerJobsBefore.jobs));

      guestCustomerPage.on("request", observedRequest => {
        try {
          const observedUrl = new URL(observedRequest.url());
          if (observedRequest.method() === "POST" &&
              observedUrl.origin === baseUrl.origin &&
              observedUrl.pathname === "/api/v1/public/sessions/current/jobs") {
            guestUploadRequestHeaders = observedRequest.headers();
          }
        } catch {}
      });

      await guestCustomerPage.locator('input[type="file"][accept*="application/pdf"]').first().setInputFiles({
        name: "smartprint-guest-qa-" + runId + ".pdf",
        mimeType: "application/pdf",
        buffer: syntheticPdf(2, "SmartPrint guest-only QA upload")
      });
      const uploadButton = guestCustomerPage.getByRole("button", { name: /Upload 1 file/ });
      await uploadButton.waitFor({ state: "visible", timeout: 10000 });
      await uploadButton.click();
      await guestCustomerPage.locator(".customer-batch-quote-action").waitFor({
        state: "visible",
        timeout: 45000
      });
      await guestCustomerPage.getByRole("button", { name: "Calculate batch quote" }).click();
      await guestCustomerPage.locator(".customer-batch-quote-card").waitFor({
        state: "visible",
        timeout: 30000
      });

      const draft = await guestCustomerPage.evaluate(code => {
        const raw = localStorage.getItem("qrtoprint.customer-active-job:" + code);
        try { return raw ? JSON.parse(raw) : null; } catch { return null; }
      }, guestCustomerSession.publicCode);
      const job = draft?.uploaded?.job || draft?.uploadedBatch?.[0]?.job;
      assert.ok(job?.id, "The guest browser did not persist the uploaded server job metadata.");
      guestJobId = job.id;
      assert.ok(guestUploadRequestHeaders,
        "The guest browser did not send the expected real upload request.");
      assert.equal(guestUploadRequestHeaders.authorization, undefined,
        "Guest document upload must not send a customer Authorization bearer token.");
      assert.equal(guestUploadRequestHeaders["x-print-session"], guestCustomerSession.sessionToken,
        "Guest upload must carry only the opaque station-scoped print-session capability.");
      assert.equal(job.pageCount, 2);
      assert.equal(job.status, "AWAITING_CONFIGURATION");

      guestJobSubmissionAttempted = true;
      const acceptanceResponses = await sendBatchAndWaitForShopApproval(
        guestCustomerPage,
        guestCustomerSession.sessionToken,
        [job.id],
        "Guest customer"
      );
      const queued = assertHttp(await request(guestCustomerPage, "GET",
        "/api/v1/public/jobs/" + job.id + "/status",
        { sessionToken: guestCustomerSession.sessionToken }), 200, "Guest customer submitted job status");
      assert.equal(queued.job.status, "AWAITING_SHOP_APPROVAL");
      await screenshot(guestCustomerPage, "guest-customer-queued-job");

      const shopQueue = assertHttp(await request(shop1Page, "GET",
        "/api/v1/shop/print-jobs?offset=0&take=100&search=" + encodeURIComponent(job.jobNumber),
        { token: shop1Tokens.accessToken }), 200, "Shop queue for guest-uploaded job");
      assert.ok(shopQueue.some(item => item.id === job.id),
        "A guest-uploaded job must appear in the station owner's queue.");

      const customerJobsAfter = assertHttp(await request(customerPage, "GET", "/api/v1/customer/jobs", {
        token: customerTokens.accessToken
      }), 200, "Signed-in customer's history after guest upload");
      assert.ok(!customerJobsAfter.jobs.some(item => item.id === job.id),
        "A guest job must not leak into an unrelated signed-in customer's global history.");

      const customersAfter = assertHttp(await request(shop1Page, "GET", customerQuery, {
        token: shop1Tokens.accessToken
      }), 200, "Shop customer directory after guest upload");
      const customerAfter = customersAfter.items.find(item =>
        item.displayName === process.env.SMARTPRINT_QA_CUSTOMER_NAME
      );
      assert.equal(customerAfter?.sessionCount ?? 0, customerSessionCountBefore,
        "A guest upload must not bind the guest session to the signed-in customer account.");

      const rejected = assertHttp(await request(shop1Page, "POST",
        "/api/v1/shop/print-jobs/" + job.id + "/reject",
        { token: shop1Tokens.accessToken }), 200, "Shop rejects unpaid guest QA job");
      assert.equal(rejected.status, "REJECTED");
      guestJobRejected = true;
      const guestStatusAfterRejection = assertHttp(await request(guestCustomerPage, "GET",
        "/api/v1/public/jobs/" + job.id + "/status",
        { sessionToken: guestCustomerSession.sessionToken }), 200, "Guest sees rejected job status");
      assert.equal(guestStatusAfterRejection.job.status, "REJECTED");
      await screenshot(guestCustomerPage, "guest-customer-rejected-job");

      report.safety.syntheticDocumentsUploaded = true;
      report.safety.realLocalDatabaseJobsCreated = true;
      guestCustomerSession.jobId = job.id;
      return {
        entryMode: "ordinary browser station link; no customer Authorization header",
        guestSessionId: guestCustomerSession.id,
        pageCount: job.pageCount,
        jobNumber: job.jobNumber,
        shopQueueVisible: true,
        customerAccountHistoryLinked: false,
        sessionCountChanged: false,
        uploadAuthorizationHeaderSent: false,
        uploadUsedStationSessionCapability: true,
        quoteAcceptanceResponses: acceptanceResponses.length,
        finalStatus: guestStatusAfterRejection.job.status,
        paymentAttempted: false,
        dispatchAttempted: false
      };
    } finally {
      // A failure after submit must not leave an unattended unpaid job in the
      // QA shop queue. Resolve only this exact disposable guest job; never touch
      // another job, payment, customer account or printer.
      if (guestJobSubmissionAttempted && guestJobId && !guestJobRejected) {
        let current = null;
        for (let attempt = 0; attempt < 10; attempt++) {
          current = await request(guestCustomerPage, "GET",
            "/api/v1/public/jobs/" + guestJobId + "/status",
            { sessionToken: guestCustomerSession.sessionToken });
          if (current.status === 200 &&
              ["AWAITING_CONFIGURATION", "AWAITING_SHOP_APPROVAL", "REJECTED"].includes(current.json?.job?.status)) break;
          await guestCustomerPage.waitForTimeout(500);
        }
        assertExpectedHttpStatus(current, 200, "QA cleanup reads the disposable guest job state");
        if (current.json.job.status === "AWAITING_SHOP_APPROVAL") {
          const cleanup = await request(shop1Page, "POST",
            "/api/v1/shop/print-jobs/" + guestJobId + "/reject",
            { token: shop1Tokens.accessToken });
          assertExpectedHttpStatus(cleanup, 200, "QA cleanup rejects the unpaid guest job");
          assert.equal(cleanup.json?.status, "REJECTED",
            "QA cleanup must leave the guest job in a terminal rejected state.");
        } else if (current.json.job.status === "REJECTED") {
          // The normal assertion path may have completed the rejection already.
        } else {
          assert.equal(current.json.job.status, "AWAITING_CONFIGURATION",
            "QA cleanup found an unexpected guest job state and will not mutate it.");
          // The upload never entered the shop queue, so no shop-side work exists
          // to resolve. Leave its local guest draft untouched for diagnosis. 
        }
        guestJobRejected = true;
      }
    }
  }, ["GUEST-001", "GUEST-002", "GUEST-003", "GUEST-004", "GUEST-005", "GUEST-006", "GUEST-007"]);

  await record("PHOTO: customer Photo Studio edits and prepares a local multi-photo A4 PDF without creating a print job", async () => {
    const photoPage = await context.newPage();
    try {
      await photoPage.route("**/api/v1/public/stations/**/sessions", async route => {
        const headers = { ...route.request().headers(), authorization: "Bearer " + customerTokens.accessToken };
        await route.continue({ headers });
      });
      const stationResponse = await photoPage.goto(new URL(customerSession.stationPath, baseUrl).toString(), {
        waitUntil: "domcontentloaded",
        timeout: 30000
      });
      assert.equal(stationResponse?.status(), 200, "Photo QA QR station did not open.");
      await photoPage.locator(".customer-entry-card .entry-eyebrow").waitFor({ state: "visible", timeout: 30000 });

      const jobsBefore = assertHttp(await request(photoPage, "GET", "/api/v1/customer/jobs", {
        token: customerTokens.accessToken
      }), 200, "Customer jobs before Photo Studio");
      assert.ok(Array.isArray(jobsBefore.jobs),
        "Customer history before Photo Studio must contain a jobs array.");
      const jobIdsBeforePhotoStudio = jobsBefore.jobs.map(job => job.id).sort();
      const picker = photoPage.locator('input[type="file"][accept*="image/png"]').first();
      await picker.setInputFiles({
        name: "smartprint-photo-studio-qa.png",
        mimeType: "image/png",
        buffer: Buffer.from(
          "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC",
          "base64"
        )
      });

      const openStudio = photoPage.getByRole("button", { name: "Open Photo Studio" });
      await openStudio.waitFor({ state: "visible", timeout: 15000 });
      await openStudio.click();
      const studio = photoPage.getByRole("dialog", { name: "Photo Studio" });
      await studio.waitFor({ state: "visible", timeout: 15000 });
      await studio.locator("canvas").waitFor({ state: "visible", timeout: 15000 });
      await screenshot(photoPage, "customer-photo-studio-editor");

      const cropTool = studio.getByRole("button", { name: "Crop" });
      await cropTool.click();
      const zoomLabel = studio.locator("label").filter({ hasText: /^Zoom\b/ });
      const zoom = zoomLabel.locator('input[type="range"]');
      await zoom.waitFor({ state: "visible", timeout: 10000 });
      assert.equal(await zoomLabel.count(), 1, "Photo Studio must render exactly one Zoom control in the Crop tool.");
      assert.equal(await zoom.count(), 1, "Photo Studio Zoom must be a single native range input.");
      assert.equal(await zoom.getAttribute("type"), "range");
      await zoom.focus();
      await zoom.press("ArrowRight");
      assert.notEqual(await zoom.inputValue(), "1", "Crop zoom control did not respond to keyboard input.");

      await studio.getByRole("button", { name: "Looks" }).click();
      await studio.getByText("Photo look").waitFor({ state: "visible", timeout: 5000 });
      await studio.getByRole("button", { name: "Background" }).click();
      await studio.getByRole("button", { name: "Set background #f3f6fa" }).click();
      assert.equal(await studio.getByRole("button", { name: "Set background #f3f6fa" }).getAttribute("aria-pressed"), "true");

      await studio.getByRole("button", { name: "Sheet", exact: true }).click();
      await studio.getByRole("checkbox", { name: /Add name and DOB to each photo/ }).check();
      await studio.getByLabel("Name for form (optional)").fill("SmartPrint QA Photo");
      await studio.getByLabel("Date of birth (optional)").fill("1990-01-02");
      await studio.getByRole("button", { name: "Text" }).click();
      await studio.getByLabel("Caption font size").selectOption("7");
      await studio.getByRole("button", { name: "Sheet", exact: true }).click();

      await studio.getByLabel("Photo size").selectOption("25x35");
      const photoCount = studio.getByLabel("Number of photos");
      await photoCount.fill("3");
      await photoCount.blur();
      assert.equal(await photoCount.inputValue(), "3");
      await studio.getByLabel("Photos per A4 page").selectOption("2");
      // PhotoStudio.module.css is a CSS Module: its class names are hashed at build time.
      // Assert the visible summary rows through their user-facing labels instead of
      // querying the source-only ".sheetSummary" class name.
      const photoDimensionsRow = studio.getByText("Photo dimensions", { exact: true }).locator("xpath=..");
      const pageCountRow = studio.getByText("A4 pages in this job", { exact: true }).locator("xpath=..");
      const photosPerPageRow = studio.getByText("Photos per A4 page", { exact: true }).locator("xpath=..");
      assert.equal(await photoDimensionsRow.locator("strong").innerText(), "25 × 35 mm",
        "The selected photo dimensions must be reflected in the sheet summary.");
      assert.equal(await photosPerPageRow.locator("strong").innerText(), "2",
        "Manual density must be reflected in the sheet summary.");
      assert.equal(await pageCountRow.locator("strong").innerText(), "2",
        "Three selected photos at two per page must generate exactly two A4 pages.");

      await studio.getByRole("button", { name: "Prepare A4 sheet" }).click();
      await studio.waitFor({ state: "detached", timeout: 60000 });
      await photoPage.locator(".customer-file-preview").getByText(/Prepared photo sheet/).waitFor({
        state: "visible",
        timeout: 15000
      });
      const preparedPreview = photoPage.locator(".customer-edited-photo-preview");
      await preparedPreview.waitFor({ state: "visible", timeout: 15000 });
      await screenshot(photoPage, "customer-photo-studio-prepared-sheet");
      const preparedImages = await preparedPreview.locator("img").count();
      assert.ok(preparedImages >= 1, "Prepared A4 photo sheet preview was not rendered.");

      const jobsAfter = assertHttp(await request(photoPage, "GET", "/api/v1/customer/jobs", {
        token: customerTokens.accessToken
      }), 200, "Customer jobs after local Photo Studio preparation");
      assert.ok(Array.isArray(jobsAfter.jobs),
        "Customer history after Photo Studio must contain a jobs array.");
      const jobIdsAfterPhotoStudio = jobsAfter.jobs.map(job => job.id).sort();
      assert.deepEqual(jobIdsAfterPhotoStudio, jobIdsBeforePhotoStudio,
        "Preparing the photo sheet locally must not create, replace or remove a server print job.");

      return {
        sourcePhotoSelected: true,
        cropLookAndBackgroundToolsOpened: true,
        optionalNameAndDobApplied: true,
        photoSize: "25x35",
        photoCount: 3,
        generatedA4Pages: 2,
        photosPerPage: 2,
        previewImages: preparedImages,
        originalUploadNotSent: true,
        serverPrintJobCountUnchanged: true
      };
    } finally {
      await photoPage.close().catch(() => {});
    }
  }, ["PHOTO-001", "PHOTO-002", "PHOTO-003", "PHOTO-004", "PHOTO-005", "PHOTO-006"]);

  await record("Real document validator rejects a malformed PDF without creating a print job", async () => {
    const invalid = await uploadSyntheticPdf(
      customerPage, customerSession.sessionToken, crypto.randomUUID(), crypto.randomUUID(),
      "smartprint-invalid-qa.pdf", Buffer.from("This is not a PDF document.", "ascii")
    );
    assertExpectedHttpStatus(invalid, 422, "Malformed PDF was not rejected by the local document validator: " + invalid.text.slice(0, 500));
    return { status: invalid.status, printJobCreated: false };
  });

  await record("Customer uploads a synthetic two-page PDF through the real portal UI", async () => {
    await customerPage.locator('input[type="file"][accept*="application/pdf"]').first().setInputFiles({
      name: "smartprint-local-qa-" + runId + ".pdf",
      mimeType: "application/pdf",
      buffer: syntheticPdf(2, "SmartPrint local QA real upload")
    });
    const uploadButton = customerPage.getByRole("button", { name: /Upload 1 file/ });
    await uploadButton.waitFor({ state: "visible", timeout: 10000 });
    await uploadButton.click();
    await customerPage.locator(".customer-batch-quote-action").waitFor({ state: "visible", timeout: 45000 });
    await customerPage.getByRole("button", { name: "Calculate batch quote" }).click();
    await customerPage.locator(".customer-batch-quote-card").waitFor({ state: "visible", timeout: 30000 });
    const draft = await customerPage.evaluate(code => {
      const raw = localStorage.getItem("qrtoprint.customer-active-job:" + code);
      try { return raw ? JSON.parse(raw) : null; } catch { return null; }
    }, customerSession.publicCode);
    const job = draft?.uploaded?.job || draft?.uploadedBatch?.[0]?.job;
    assert.ok(job?.id, "The customer UI did not persist the real uploaded job metadata.");
    report.safety.syntheticDocumentsUploaded = true;
    report.safety.realLocalDatabaseJobsCreated = true;
    uploadedJob = job;
    uploadedBatchId = draft.batchId;
    assert.equal(job.pageCount, 2);
    assert.equal(job.status, "AWAITING_CONFIGURATION");
    assert.equal(typeof job.id, "string");
    return { jobNumber: job.jobNumber, pageCount: job.pageCount, initialStatus: job.status, batchId: uploadedBatchId };
  });

  await record("Customer accepts the calculated quote and sends the job to the real shop queue", async () => {
    const acceptanceResponses = await sendBatchAndWaitForShopApproval(
      customerPage,
      customerSession.sessionToken,
      [uploadedJob.id],
      "Signed-in customer"
    );
    const status = assertHttp(await request(customerPage, "GET",
      "/api/v1/public/jobs/" + uploadedJob.id + "/status",
      { sessionToken: customerSession.sessionToken }), 200, "Customer uploaded job status");
    assert.equal(status.job.status, "AWAITING_SHOP_APPROVAL");
    const shopQueue = assertHttp(await request(shop1Page, "GET",
      "/api/v1/shop/print-jobs?offset=0&take=100&search=" + encodeURIComponent(uploadedJob.jobNumber),
      { token: shop1Tokens.accessToken }), 200, "Shop print queue");
    assert.ok(shopQueue.some(item => item.id === uploadedJob.id), "The customer job did not appear in the correct shop's queue.");
    const customerJobsResponse = assertHttp(await request(customerPage, "GET", "/api/v1/customer/jobs", {
      token: customerTokens.accessToken
    }), 200, "Customer job history after upload");
    assert.ok(customerJobsResponse.jobs?.some(item => item.id === uploadedJob.id), "The signed-in customer did not see the job in their history.");
    return {
      jobNumber: uploadedJob.jobNumber,
      customerStatus: status.job.status,
      shopQueueVisible: true,
      customerHistoryVisible: true,
      quoteAcceptanceResponses: acceptanceResponses.length
    };
  });

  await record("Shop rejects the unpaid QA job and customer sees the same terminal result", async () => {
    const rejected = assertHttp(await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/reject",
      { token: shop1Tokens.accessToken }), 200, "Reject unpaid QA job");
    assert.equal(rejected.status, "REJECTED");
    const customerStatus = assertHttp(await request(customerPage, "GET",
      "/api/v1/public/jobs/" + uploadedJob.id + "/status",
      { sessionToken: customerSession.sessionToken }), 200, "Customer rejected job status");
    assert.equal(customerStatus.job.status, "REJECTED");
    return { jobNumber: uploadedJob.jobNumber, finalStatus: customerStatus.job.status, paymentAttempted: false, dispatchAttempted: false };
  });

  await record("Customer cannot start payment for a shop-rejected job", async () => {
    const start = await request(customerPage, "POST",
      "/api/v1/public/batches/" + uploadedBatchId + "/payments",
      { sessionToken: customerSession.sessionToken, body: { method: "UPI" } });
    assertExpectedHttpStatus(start, 409, "A rejected QA job's batch should not enter checkout.");
    return { status: start.status, rejectedBeforeCheckout: true };
  });



  await record("Customer opens a fresh QR session and uploads two real documents as one batch", async () => {
    batchCustomerPage = await context.newPage();
    const stationPath = shop1Station.publicUrl ? new URL(shop1Station.publicUrl).pathname : null;
    assert.ok(stationPath && /^\/s\/[A-Za-z0-9_-]+\/?$/.test(stationPath),
      "The QA station did not return a canonical public station URL.");

    await batchCustomerPage.route("**/api/v1/public/stations/**/sessions", async route => {
      const headers = { ...route.request().headers(), authorization: "Bearer " + customerTokens.accessToken };
      await route.continue({ headers });
    });
    const response = await batchCustomerPage.goto(new URL(stationPath, baseUrl).toString(), {
      waitUntil: "domcontentloaded",
      timeout: 30000
    });
    assert.equal(response?.status(), 200);
    await batchCustomerPage.locator(".customer-entry-card .entry-eyebrow").waitFor({ state: "visible", timeout: 30000 });
    assert.equal((await batchCustomerPage.locator(".customer-entry-card .entry-eyebrow").innerText()).trim(), "SECURE SESSION CREATED");

    const publicCode = stationPath.split("/").filter(Boolean)[1];
    const sessionToken = await batchCustomerPage.evaluate(code =>
      sessionStorage.getItem("qrtoprint.customer-session:" + code), publicCode);
    assert.ok(sessionToken, "The new QA customer session token was not stored.");
    const current = assertHttp(await request(batchCustomerPage, "GET", "/api/v1/public/sessions/current", {
      sessionToken
    }), 200, "Fresh QA customer session");
    assert.equal(current.shop.displayName, process.env.SMARTPRINT_QA_SHOP1_NAME);
    batchCustomerSession = { sessionToken, publicCode, stationPath, id: current.id };

    const input = batchCustomerPage.locator('input[type="file"][accept*="application/pdf"]').first();
    await input.setInputFiles([
      {
        name: "smartprint-qa-batch-a-" + runId + ".pdf",
        mimeType: "application/pdf",
        buffer: syntheticPdf(2, "SmartPrint QA batch document A")
      },
      {
        name: "smartprint-qa-batch-b-" + runId + ".pdf",
        mimeType: "application/pdf",
        buffer: syntheticPdf(1, "SmartPrint QA batch document B")
      }
    ]);

    const uploadButton = batchCustomerPage.getByRole("button", { name: /Upload 2 file\(s\)/ });
    await uploadButton.waitFor({ state: "visible", timeout: 10000 });
    await uploadButton.click();
    await batchCustomerPage.locator(".customer-uploaded-batch-item").nth(1).waitFor({ state: "visible", timeout: 60000 });
    await batchCustomerPage.locator(".customer-batch-quote-action").waitFor({ state: "visible", timeout: 15000 });
    await batchCustomerPage.getByRole("button", { name: "Calculate batch quote" }).click();
    await batchCustomerPage.locator(".customer-batch-quote-card").waitFor({ state: "visible", timeout: 45000 });

    const draft = await batchCustomerPage.evaluate(code => {
      const raw = localStorage.getItem("qrtoprint.customer-active-job:" + code);
      try { return raw ? JSON.parse(raw) : null; } catch { return null; }
    }, publicCode);
    assert.ok(Array.isArray(draft?.uploadedBatch), "Customer batch draft did not persist an uploadedBatch array.");
    assert.equal(draft.uploadedBatch.length, 2, "Exactly two real documents should be in the batch.");
    assert.ok(draft.batchId, "The real upload batch ID was not persisted.");
    const names = draft.uploadedBatch.map(item => item.document.originalFileName);
    assert.equal(new Set(names).size, 2, "Both source documents must remain distinct.");
    assert.ok(draft.uploadedBatch.every(item => item.job?.id), "Both file uploads must have real persisted PrintJob IDs.");
    const fileAName = "smartprint-qa-batch-a-" + runId + ".pdf";
    const fileBName = "smartprint-qa-batch-b-" + runId + ".pdf";
    const fileA = draft.uploadedBatch.find(item => item.document.originalFileName === fileAName);
    const fileB = draft.uploadedBatch.find(item => item.document.originalFileName === fileBName);
    assert.ok(fileA?.job?.id && fileB?.job?.id,
      "The uploaded batch must preserve a stable filename-to-PrintJob mapping for files A and B.");
    reviewedBatchJobIdsByFileName = { fileA: fileA.job.id, fileB: fileB.job.id };
    reviewedBatchId = draft.batchId;
    report.safety.syntheticDocumentsUploaded = true;
    report.safety.realLocalDatabaseJobsCreated = true;
    return {
      sessionId: current.id,
      fileCount: draft.uploadedBatch.length,
      sourceFiles: names,
      sourcePageCounts: draft.uploadedBatch.map(item => item.job.pageCount),
      uniquePrintJobs: new Set(draft.uploadedBatch.map(item => item.job.id)).size,
      sameBatchId: true,
      quoteCalculatedThroughUi: true
    };
  });

  await record("Two-file customer batch is sent to the shop and appears as one tenant-scoped batch", async () => {
    assert.ok(reviewedBatchJobIdsByFileName?.fileA && reviewedBatchJobIdsByFileName?.fileB,
      "The batch must retain both filename-to-job identities before customer acceptance.");
    const acceptanceResponses = await sendBatchAndWaitForShopApproval(
      batchCustomerPage,
      batchCustomerSession.sessionToken,
      [reviewedBatchJobIdsByFileName.fileA, reviewedBatchJobIdsByFileName.fileB],
      "Two-file customer batch"
    );
    reviewedBatchShopJobs = assertHttp(await request(shop1Page, "GET",
      "/api/v1/shop/print-jobs?offset=0&take=100&batchId=" + encodeURIComponent(reviewedBatchId),
      { token: shop1Tokens.accessToken }), 200, "Two-file QA batch in shop queue");
    assert.equal(reviewedBatchShopJobs.length, 2, "Shop queue must contain exactly the two uploaded files.");
    assert.ok(reviewedBatchShopJobs.every(job => job.batchId === reviewedBatchId));
    assert.deepEqual(
      reviewedBatchShopJobs.map(job => job.id).sort(),
      [reviewedBatchJobIdsByFileName.fileA, reviewedBatchJobIdsByFileName.fileB].sort(),
      "The shop queue must contain the exact two files uploaded in this batch."
    );
    assert.ok(reviewedBatchShopJobs.every(job => job.status === "AWAITING_SHOP_APPROVAL"));
    const customerStates = [];
    for (const job of reviewedBatchShopJobs) {
      const status = assertHttp(await request(batchCustomerPage, "GET",
        "/api/v1/public/jobs/" + job.id + "/status", {
          sessionToken: batchCustomerSession.sessionToken
        }), 200, "Customer batch job status");
      assert.equal(status.job.status, "AWAITING_SHOP_APPROVAL");
      customerStates.push(status.job.status);
    }
    return {
      batchId: reviewedBatchId,
      filesVisibleInShop: reviewedBatchShopJobs.length,
      allFilesAwaitShopReview: true,
      customerStatusCount: customerStates.length,
      quoteAcceptanceResponses: acceptanceResponses.length
    };
  });

  await record("Shopkeeper rejects one file, undoes that rejection, then applies the individual file decision", async () => {
    await clickShopSection(shop1Page, "Print Queue");
    assert.ok(reviewedBatchJobIdsByFileName?.fileA && reviewedBatchJobIdsByFileName?.fileB,
      "The reviewed batch must retain filename-to-job identity.");

    assert.ok(reviewedBatchShopJobs.length === 2,
      "The tenant-scoped API must contain both persisted files before opening the shop UI review.");
    const targetJob = reviewedBatchShopJobs[0];
    assert.ok(targetJob?.jobNumber && targetJob?.id,
      "The batch lookup must return a persisted job number and ID.");

    // Print Queue is paged and keeps its own loaded data. Search by the exact
    // real job number so this test does not assume a background refresh already
    // inserted a just-created batch into the current first page.
    const queueSearch = shop1Page.getByRole("searchbox", { name: "Search all print jobs" });
    await queueSearch.fill(targetJob.jobNumber);
    const searchResponsePromise = shop1Page.waitForResponse(response => {
      if (response.request().method() !== "GET") return false;
      try {
        const url = new URL(response.url());
        return url.origin === baseUrl.origin &&
          url.pathname === "/api/v1/shop/print-jobs" &&
          url.searchParams.get("offset") === "0" &&
          url.searchParams.get("take") === "100" &&
          url.searchParams.get("search") === targetJob.jobNumber;
      } catch {
        return false;
      }
    }, { timeout: 20000 });
    await shop1Page.locator(".queue-search-form button[type='submit']").click();
    const searchResponse = await searchResponsePromise;
    assert.equal(searchResponse.status(), 200, "The shop's real queue search must return HTTP 200.");
    const searchedJobs = await searchResponse.json();
    assert.ok(Array.isArray(searchedJobs), "The shop queue search must return a job array.");
    assert.deepEqual(
      searchedJobs.map(job => job.id),
      [targetJob.id],
      "Searching the exact persisted job number must return that one file in the authenticated shop."
    );

    const matchingQueueRows = shop1Page.locator(".queue-batch-summary-row")
      .filter({ hasText: targetJob.jobNumber });
    await matchingQueueRows.waitFor({ state: "visible", timeout: 15000 });
    assert.equal(await matchingQueueRows.count(), 1,
      "The exact searched job must appear in one shop batch summary row.");
    const reviewButton = matchingQueueRows.getByRole("button", { name: /Preview & review/ });
    await reviewButton.waitFor({ state: "visible", timeout: 15000 });
    await reviewButton.click();

    const modal = shop1Page.getByRole("dialog", { name: "Review batch" });
    await modal.waitFor({ state: "visible", timeout: 15000 });
    const rows = modal.locator(".batch-review-row");
    assert.equal(await rows.count(), 2, "The review dialog must contain exactly this batch's two files.");

    const fileAName = "smartprint-qa-batch-a-" + runId + ".pdf";
    const fileBName = "smartprint-qa-batch-b-" + runId + ".pdf";
    const fileARow = rows.filter({ hasText: fileAName });
    const fileBRow = rows.filter({ hasText: fileBName });
    assert.equal(await fileARow.count(), 1, "File A must be identifiable regardless of review-row ordering.");
    assert.equal(await fileBRow.count(), 1, "File B must be identifiable regardless of review-row ordering.");
    const fileADecision = fileARow.locator('input[type="checkbox"]');
    const fileBDecision = fileBRow.locator('input[type="checkbox"]');
    assert.equal(await fileADecision.count(), 1);
    assert.equal(await fileBDecision.count(), 1);
    assert.equal(await fileADecision.isChecked(), true, "File A must start accepted.");
    assert.equal(await fileBDecision.isChecked(), true, "File B must start accepted.");

    // Preview the exact File A document by its persisted job ID, independent of queue ordering.
    const previewPath = "/api/v1/shop/print-jobs/" + reviewedBatchJobIdsByFileName.fileA + "/document";
    const previewResponsePromise = shop1Page.waitForResponse(response => {
      try { return new URL(response.url()).pathname === previewPath; } catch { return false; }
    }, { timeout: 30000 });
    await fileARow.getByRole("button", { name: "Preview" }).click();
    const previewResponse = await previewResponsePromise;
    assert.equal(previewResponse.status(), 200, "The File A cloud preview request must succeed.");
    const previewDialog = shop1Page.getByRole("dialog", { name: fileAName });
    await previewDialog.waitFor({ state: "visible", timeout: 15000 });
    await previewDialog.locator(".shop-document-preview iframe").waitFor({ state: "visible", timeout: 10000 });
    await screenshot(shop1Page, "shop-batch-real-document-preview");
    await previewDialog.getByRole("button", { name: "Close preview" }).click();
    await previewDialog.waitFor({ state: "detached", timeout: 10000 });

    await fileBDecision.uncheck();
    assert.equal(await fileBDecision.isChecked(), false, "File B should be marked for rejection.");
    await fileBDecision.check();
    assert.equal(await fileBDecision.isChecked(), true, "The shopkeeper must be able to undo File B rejection before applying it.");
    await fileBDecision.uncheck();
    assert.equal(await fileADecision.isChecked(), true, "File A remains accepted.");
    assert.equal(await fileBDecision.isChecked(), false, "Only File B is rejected.");

    await modal.getByRole("button", { name: "Apply review" }).click();
    const confirmation = shop1Page.getByRole("dialog", { name: "Confirm batch review" });
    await confirmation.waitFor({ state: "visible", timeout: 10000 });
    await confirmation.getByRole("button", { name: "Apply review" }).click();
    await modal.waitFor({ state: "detached", timeout: 20000 });

    reviewedBatchShopJobs = assertHttp(await request(shop1Page, "GET",
      "/api/v1/shop/print-jobs?offset=0&take=100&batchId=" + encodeURIComponent(reviewedBatchId),
      { token: shop1Tokens.accessToken }), 200, "Batch status after shop review");
    assert.equal(reviewedBatchShopJobs.length, 2);
    const accepted = reviewedBatchShopJobs.filter(job => job.status === "AWAITING_PAYMENT");
    const rejected = reviewedBatchShopJobs.filter(job => job.status === "REJECTED");
    assert.equal(accepted.length, 1, "Exactly one file must remain accepted for checkout.");
    assert.equal(rejected.length, 1, "Exactly one file must be rejected and excluded.");
    assert.equal(accepted[0].id, reviewedBatchJobIdsByFileName.fileA,
      "The file left accepted must be File A.");
    assert.equal(rejected[0].id, reviewedBatchJobIdsByFileName.fileB,
      "The individually rejected file must be File B.");
    const customerRejected = assertHttp(await request(batchCustomerPage, "GET",
      "/api/v1/public/jobs/" + rejected[0].id + "/status", {
        sessionToken: batchCustomerSession.sessionToken
      }), 200, "Customer sees individually rejected file");
    const customerAccepted = assertHttp(await request(batchCustomerPage, "GET",
      "/api/v1/public/jobs/" + accepted[0].id + "/status", {
        sessionToken: batchCustomerSession.sessionToken
      }), 200, "Customer sees accepted file");
    assert.equal(customerRejected.job.status, "REJECTED");
    assert.equal(customerAccepted.job.status, "AWAITING_PAYMENT");
    paymentRejectedJob = accepted[0];
    report.safety.testJobRejectedBeforePayment = true;
    return {
      batchId: reviewedBatchId,
      acceptedFiles: accepted.length,
      rejectedFiles: rejected.length,
      undoBeforeApplyVerified: true,
      rejectedFileStatus: customerRejected.job.status,
      acceptedFileStatus: customerAccepted.job.status,
      rejectedFileIncludedInCheckout: false
    };
  });

  await record("Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received", async () => {
    const paymentBatchId = reviewedBatchId;
    const job = paymentRejectedJob;
    assert.ok(paymentBatchId && job?.id, "The reviewed two-file QA batch must contain one accepted job.");
    assert.equal(job.status, "AWAITING_PAYMENT", "Only the accepted file should be payable.");
    assert.ok(job.quotedAmountMinor > 0, "The accepted QA file must have a positive saved quote.");

    const payment = assertHttp(await request(batchCustomerPage, "POST",
      "/api/v1/public/batches/" + paymentBatchId + "/payments", {
        sessionToken: batchCustomerSession.sessionToken,
        body: { method: "UPI" }
      }), 200, "Start actual UPI batch payment intent");
    assert.ok(payment.id, "UPI intent did not return a payment identifier.");
    paymentRejectedBatchPaymentId = payment.id;
    assert.equal(payment.method, "UPI");
    assert.equal(payment.status, "AWAITING_CUSTOMER_PAYMENT");
    assert.ok(payment.amountMinor > 0);
    assert.equal(payment.currency, "INR");
    assert.equal(payment.fileCount, 1, "The rejected file must be excluded from the consolidated payment.");
    assert.equal(payment.amountMinor, job.quotedAmountMinor, "The payment total must equal only the accepted file quote.");
    report.safety.paymentIntentsCreated = true;

    const syntheticReference = "QA-NOT-REAL-" + runId.replace(/[^A-Za-z0-9_-]/g, "-");
    const submitted = assertHttp(await request(batchCustomerPage, "POST",
      "/api/v1/public/batch-payments/" + payment.id + "/report", {
        sessionToken: batchCustomerSession.sessionToken,
        body: { referenceNumber: syntheticReference }
      }), 200, "Submit clearly synthetic QA payment reference");
    assert.equal(submitted.status, "CUSTOMER_REPORTED");
    assert.equal(submitted.paymentReference, syntheticReference);

    const duplicateReport = assertHttp(await request(batchCustomerPage, "POST",
      "/api/v1/public/batch-payments/" + payment.id + "/report", {
        sessionToken: batchCustomerSession.sessionToken,
        body: { referenceNumber: syntheticReference }
      }), 200, "Idempotent duplicate payment-reference submission");
    assert.equal(duplicateReport.duplicateRequest, true);

    const reportedPayments = assertHttp(await request(shop1Page, "GET",
      "/api/v1/shop/payments", { token: shop1Tokens.accessToken }), 200, "Shop payment list after customer report");
    const reported = reportedPayments.find(item => item.id === payment.id);
    assert.ok(reported, "The QA payment did not appear in the real shop payment list.");
    assert.equal(reported.status, "CUSTOMER_REPORTED");

    const rejected = assertHttp(await request(shop1Page, "POST",
      "/api/v1/shop/payments/" + payment.id + "/reject", {
        token: shop1Tokens.accessToken,
        body: { reason: "Synthetic QA reference only; no real funds were transferred." }
      }), 200, "Shop rejects non-real QA payment reference");
    assert.equal(rejected.status, "REJECTED");

    const duplicateReject = assertHttp(await request(shop1Page, "POST",
      "/api/v1/shop/payments/" + payment.id + "/reject", {
        token: shop1Tokens.accessToken,
        body: { reason: "Synthetic QA reference only; no real funds were transferred." }
      }), 200, "Idempotent duplicate payment rejection");
    assert.equal(duplicateReject.duplicateRequest, true);

    const finalCustomerStatus = assertHttp(await request(batchCustomerPage, "GET",
      "/api/v1/public/jobs/" + job.id + "/status", {
        sessionToken: batchCustomerSession.sessionToken
      }), 200, "Customer sees rejected payment state");
    assert.equal(finalCustomerStatus.job.status, "PAYMENT_REJECTED");
    report.safety.syntheticUtrSubmitted = true;
    report.safety.syntheticUtrMarkedVerified = false;

    return {
      jobNumber: job.jobNumber,
      quoteAmountMinor: job.quotedAmountMinor,
      paymentMethod: payment.method,
      paymentId: payment.id,
      paymentReference: "[synthetic QA reference redacted]",
      referenceStatus: submitted.status,
      duplicateReferenceSubmission: duplicateReport.duplicateRequest,
      shopPaymentStatus: rejected.status,
      duplicateShopRejection: duplicateReject.duplicateRequest,
      customerJobStatus: finalCustomerStatus.job.status,
      fundsActuallyReceived: false,
      paymentVerified: false,
      printDispatchAttempted: false
    };
  });

  await record("Shop payment and refund screens load without recording false financial events", async () => {
    const payments = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/payments", {
      token: shop1Tokens.accessToken
    }), 200, "Shop payments");
    const refunds = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/refunds", {
      token: shop1Tokens.accessToken
    }), 200, "Shop refunds");
    const noVerifiedPaymentRefund = await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/refund-preview",
      { token: shop1Tokens.accessToken, body: { rejectedPageNumbers: [2] } });
    assertExpectedHttpStatus(noVerifiedPaymentRefund, 409, "Refund preview must be blocked without a verified payment.");
    return {
      paymentRows: payments.length,
      existingRefundRows: refunds.length,
      refundGuardWithoutVerifiedPayment: noVerifiedPaymentRefund.status,
      noRefundFalselyRecorded: true
    };
  });

  await record("JOB/PAYMENT/REFUND: rejected and unpaid files cannot be approved, dispatched, verified or refunded", async () => {
    const verifyRejectedPayment = await request(shop1Page, "POST",
      "/api/v1/shop/payments/" + paymentRejectedBatchPaymentId + "/verify",
      { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(verifyRejectedPayment, 409, "A rejected UPI report must never be marked as received.");

    const approveRejectedJob = await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/approve",
      { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(approveRejectedJob, 409, "A rejected file cannot be approved for printing.");

    const dispatchRejectedJob = await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/dispatch",
      { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(dispatchRejectedJob, 409, "A rejected file cannot be dispatched to a printer.");

    const refundPreview = await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + paymentRejectedJob.id + "/refund-preview",
      {
        token: shop1Tokens.accessToken,
        body: { rejectedPageNumbers: [1] }
      });
    assertExpectedHttpStatus(refundPreview, 409, "A payment-rejected file has no verified payment to refund.");

    const refundNotConfirmed = await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + paymentRejectedJob.id + "/refund",
      {
        token: shop1Tokens.accessToken,
        body: {
          reason: "QA must not claim a refund was sent",
          refundMethod: "UPI",
          refundReference: "QA-NOT-SENT",
          idempotencyKey: "qa-not-sent-" + crypto.randomUUID(),
          confirmedSent: false,
          rejectedPageNumbers: [1]
        }
      });
    assertExpectedHttpStatus(refundNotConfirmed, 400, "The system must require explicit confirmation that a refund was actually sent.");

    const events = assertHttp(await request(shop1Page, "GET",
      "/api/v1/shop/print-jobs/" + paymentRejectedJob.id + "/events",
      { token: shop1Tokens.accessToken }), 200, "Rejected payment job event history");
    assert.ok(Array.isArray(events) && events.some(item => item.newStatus === "PAYMENT_REJECTED"),
      "The job audit history must preserve the payment rejection.");

    const customerStatus = assertHttp(await request(batchCustomerPage, "GET",
      "/api/v1/public/jobs/" + paymentRejectedJob.id + "/status",
      { sessionToken: batchCustomerSession.sessionToken }), 200, "Customer payment-rejected job status");
    assert.equal(customerStatus.job.status, "PAYMENT_REJECTED");
    assert.equal(customerStatus.payment?.status, "REJECTED");

    const refunds = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/refunds", {
      token: shop1Tokens.accessToken
    }), 200, "Shop refunds after rejected/unpaid guard tests");
    assert.ok(Array.isArray(refunds));

    return {
      rejectedPaymentVerification: verifyRejectedPayment.status,
      rejectedApproval: approveRejectedJob.status,
      rejectedDispatch: dispatchRejectedJob.status,
      refundPreviewWithoutVerifiedPayment: refundPreview.status,
      refundWithoutSentConfirmation: refundNotConfirmed.status,
      auditEventsPreserved: true,
      customerSeesPaymentRejected: true,
      refundRecords: refunds.length,
      moneyMarkedReceived: false,
      refundRecordedAsSent: false,
      printerDispatches: false
    };
  }, ["JOB-001", "JOB-002", "JOB-003", "PAY-001", "REFUND-001", "REFUND-002", "SEC-001"]);

  await record("Agent and printer prerequisites are visible; dispatch without a paired QA Agent is safely blocked", async () => {
    const agents = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/agent-devices", {
      token: shop1Tokens.accessToken
    }), 200, "Shop Agent devices");
    const dispatch = await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/dispatch",
      { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(dispatch, 409, "Rejected/unpaid/no-agent job dispatch must be rejected.");
    return {
      pairedAgentsForQaTenant: agents.length,
      dispatchGuard: dispatch.status,
      physicalPrinterNotTouched: true
    };
  });

  await record("Dedicated QA Agent enrollment code can be generated without pairing or replacing a machine identity", async () => {
    qaAgentEnrollment = assertHttp(await request(shop1Page, "POST", "/api/v1/shop/agent-enrollment-codes", {
      token: shop1Tokens.accessToken
    }), 201, "Create Agent enrollment code");
    assert.ok(qaAgentEnrollment.code && Date.parse(qaAgentEnrollment.expiresUtc) > Date.now());
    return { codeCreated: true, expiresUtc: qaAgentEnrollment.expiresUtc, paired: false, existingAgentIdentityChanged: false };
  });

  await record("Agent API requires an authenticated paired identity for all protected endpoints", async () => {
    const jobId = crypto.randomUUID();
    const ticketId = crypto.randomUUID();
    const protectedRequests = [
      ["POST", "/api/v1/agent/jobs/claim"],
      ["GET", "/api/v1/agent/jobs/" + jobId + "/document"],
      ["POST", "/api/v1/agent/jobs/" + jobId + "/received", { sha256: "a".repeat(64), sizeBytes: 1024 }],
      ["POST", "/api/v1/agent/local-uploads/" + ticketId + "/claim"],
      ["POST", "/api/v1/agent/local-uploads/" + ticketId + "/complete", {
        pageCount: 1, pageSizesJson: "[]", fileExtension: ".pdf", mediaType: "application/pdf",
        sizeBytes: 1024, sha256: "a".repeat(64)
      }],
      ["GET", "/api/v1/agent/local-uploads/" + jobId + "/status"],
      ["POST", "/api/v1/agent/jobs/print-claim", { localJobs: [{ jobId, sha256: "a".repeat(64), sizeBytes: 1024 }] }],
      ["POST", "/api/v1/agent/jobs/" + jobId + "/pdf-exported", {
        fileName: "qa.pdf", outputPath: "C:\\SmartPrintQA\\qa.pdf", sizeBytes: 1024, sha256: "a".repeat(64)
      }],
      ["POST", "/api/v1/agent/jobs/" + jobId + "/spooler-accepted", {
        spoolerJobId: 1, spoolerJobName: "SmartPrint QA", spoolerStatus: "Printed"
      }],
      ["POST", "/api/v1/agent/jobs/" + jobId + "/print-blocked", { reasonCode: "QA_ONLY", message: "Unauthenticated QA probe" }],
      ["POST", "/api/v1/agent/jobs/" + jobId + "/output-unconfirmed", { reasonCode: "QA_ONLY", message: "Unauthenticated QA probe" }],
      ["POST", "/api/v1/agent/jobs/" + jobId + "/spooler-status", {
        spoolerJobId: 1, spoolerStatus: "Printed", pagesPrinted: 1, totalPages: 1
      }],
      ["POST", "/api/v1/agent/local-document-photo-revisions/" + jobId + "/authorize", {
        photoSizeCode: "35x45", photoCount: 1, photosPerPage: 1
      }],
      ["POST", "/api/v1/agent/local-document-photo-revisions/" + jobId + "/complete", {
        pageCount: 1, photoCount: 1, photosPerPage: 1, photoSizeCode: "35x45", sha256: "a".repeat(64)
      }],
      ["GET", "/api/v1/agent/local-document-previews/" + jobId + "/authorize"],
      ["POST", "/api/v1/agent/heartbeat", { version: "unauthenticated-qa-probe", operatingSystem: "Windows" }],
      ["POST", "/api/v1/agent/printers/sync", { printers: [] }]
    ];

    const outcomes = [];
    for (const [method, path, body] of protectedRequests) {
      const response = await request(page, method, path, { body });
      assertExpectedHttpStatus(response, 401, "Protected Agent endpoint must reject missing agent credentials: " + method + " " + path);
      outcomes.push({ method, path: path.replace(jobId, "[job]").replace(ticketId, "[ticket]"), status: response.status });
    }
    return { protectedAgentEndpoints: outcomes.length, allRejectedWithoutCredentials: true, outcomes };
  }, ["AGENT-006", "AGENT-007", "AGENT-008", "AGENT-009", "AGENT-010", "AGENT-011", "AGENT-012", "AGENT-013", "AGENT-014", "AGENT-015", "AGENT-016", "AGENT-017", "AGENT-018", "AGENT-019", "AGENT-020", "AGENT-021", "AGENT-022"]);

  await record("Disposable QA Agent enrolls, heartbeats, validates printer sync, rejects code replay and revokes safely", async () => {
    assert.ok(qaAgentEnrollment?.code, "The disposable QA enrollment code must exist.");
    const fingerprint = "SMARTPRINT-QA-EPHEMERAL-" + runId;
    let enrollmentReplay = null;
    let heartbeat = null;
    let invalidSync = null;
    let emptySync = null;
    let revoked = false;

    try {
      const enrolled = assertHttp(await request(page, "POST", "/api/v1/agent/enroll", {
        body: {
          code: qaAgentEnrollment.code,
          fingerprint,
          deviceName: "SmartPrint QA Disposable Agent"
        }
      }), 201, "Enroll disposable QA Agent through the real API");
      assert.ok(enrolled.agentDeviceId && enrolled.agentSecret);
      qaAgentIdentity = { id: enrolled.agentDeviceId, secret: enrolled.agentSecret };
      qaAgentLastId = qaAgentIdentity.id;

      const invalidAgentSecret = await request(page, "POST", "/api/v1/agent/heartbeat", {
        headers: {
          "X-Print-Agent-Id": qaAgentIdentity.id,
          "X-Print-Agent-Secret": "deliberately-invalid-qa-secret"
        },
        body: { version: "smartprint-local-qa", operatingSystem: "Windows", localEndpoints: [] }
      });
      assertExpectedHttpStatus(invalidAgentSecret, 401, "A known Agent ID with an incorrect secret must be rejected.");

      const malformedAgentId = await request(page, "POST", "/api/v1/agent/heartbeat", {
        headers: {
          "X-Print-Agent-Id": "not-a-guid",
          "X-Print-Agent-Secret": qaAgentIdentity.secret
        },
        body: { version: "smartprint-local-qa", operatingSystem: "Windows", localEndpoints: [] }
      });
      assertExpectedHttpStatus(malformedAgentId, 401, "A malformed Agent identity must be rejected.");

      const invalidEnrollmentCode = await request(page, "POST", "/api/v1/agent/enroll", {
        body: {
          code: "SMARTPRINT-QA-INVALID-ENROLLMENT-CODE",
          fingerprint: fingerprint + "-INVALID-CODE",
          deviceName: "SmartPrint QA Invalid Enrollment"
        }
      });
      assertExpectedHttpStatus(invalidEnrollmentCode, 401, "An unknown Agent enrollment code must be rejected.");

      const invalidEnrollmentMetadata = await request(page, "POST", "/api/v1/agent/enroll", {
        body: { code: qaAgentEnrollment.code, fingerprint: "", deviceName: "" }
      });
      assertExpectedHttpStatus(invalidEnrollmentMetadata, 400, "Enrollment must reject missing device identity metadata.");

      enrollmentReplay = await request(page, "POST", "/api/v1/agent/enroll", {
        body: {
          code: qaAgentEnrollment.code,
          fingerprint: fingerprint + "-REPLAY",
          deviceName: "SmartPrint QA Enrollment Replay"
        }
      });
      assertExpectedHttpStatus(enrollmentReplay, 401, "A one-time enrollment code must not be reusable.");

      const agentHeaders = {
        "X-Print-Agent-Id": qaAgentIdentity.id,
        "X-Print-Agent-Secret": qaAgentIdentity.secret
      };
      heartbeat = assertHttp(await request(page, "POST", "/api/v1/agent/heartbeat", {
        headers: agentHeaders,
        body: { version: "smartprint-local-qa", operatingSystem: "Windows", localEndpoints: [] }
      }), 200, "Authenticated QA Agent heartbeat");
      assert.equal(heartbeat.id, qaAgentIdentity.id);
      assert.equal(heartbeat.status, "Online");
      assert.ok(Array.isArray(heartbeat.localEndpoints));

      invalidSync = await request(page, "POST", "/api/v1/agent/printers/sync", {
        headers: agentHeaders,
        body: {
          printers: [{
            queueName: "SMARTPRINT_QA_INVALID_QUEUE",
            driverName: "QA Validation Only",
            portName: "QA-PORT",
            status: "Ready",
            isDefault: false,
            outputMode: "PHYSICAL",
            capabilitiesJson: "[]"
          }]
        }
      });
      assertExpectedHttpStatus(invalidSync, 400, "Invalid printer capability payload must be rejected.");

      emptySync = assertHttp(await request(page, "POST", "/api/v1/agent/printers/sync", {
        headers: agentHeaders,
        body: { printers: [] }
      }), 200, "Valid empty printer discovery sync");
      assert.equal(emptySync.discovered, 0, "No physical printer is asserted or discovered in this protocol-only run.");

      const shopDevices = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/agent-devices", {
        token: shop1Tokens.accessToken
      }), 200, "Shop sees the newly enrolled QA Agent");
      const device = shopDevices.find(item => item.id === qaAgentIdentity.id);
      assert.ok(device, "The enrolled Agent must be persisted under Shop One.");
      assert.equal(device.status, "Online");

      const revoke = assertHttp(await request(shop1Page, "PATCH",
        "/api/v1/shop/agent-devices/" + qaAgentIdentity.id + "/revoke",
        { token: shop1Tokens.accessToken }), 200, "Revoke disposable QA Agent");
      assert.equal(revoke.status, "Revoked");
      revoked = true;

      const heartbeatAfterRevoke = await request(page, "POST", "/api/v1/agent/heartbeat", {
        headers: agentHeaders,
        body: { version: "smartprint-local-qa", operatingSystem: "Windows", localEndpoints: [] }
      });
      assertExpectedHttpStatus(heartbeatAfterRevoke, 401, "A revoked Agent credential must no longer authenticate.");

      const finalDevices = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/agent-devices", {
        token: shop1Tokens.accessToken
      }), 200, "Shop sees the revoked QA Agent state");
      const finalDevice = finalDevices.find(item => item.id === qaAgentIdentity.id);
      assert.equal(finalDevice?.status, "Revoked");

      return {
        enrollmentCreatedPersistedAgent: true,
        oneTimeCodeReplay: enrollmentReplay.status,
        invalidAgentSecret: invalidAgentSecret.status,
        malformedAgentId: malformedAgentId.status,
        unknownEnrollmentCode: invalidEnrollmentCode.status,
        invalidEnrollmentMetadata: invalidEnrollmentMetadata.status,
        heartbeat: heartbeat.status,
        invalidPrinterSync: invalidSync.status,
        emptyPrinterDiscovery: emptySync.discovered,
        shopCanSeeAgent: true,
        revokeStatus: revoke.status,
        revokedAgentHeartbeat: heartbeatAfterRevoke.status,
        physicalPrintersCreatedOrTouched: false,
        realWindowsServicePaired: false,
        secretRecordedInEvidence: false
      };
    } finally {
      if (qaAgentIdentity && !revoked) {
        const cleanup = await request(shop1Page, "PATCH",
          "/api/v1/shop/agent-devices/" + qaAgentIdentity.id + "/revoke",
          { token: shop1Tokens.accessToken });
        if (cleanup.status !== 200) {
          throw new Error("QA cleanup could not revoke disposable Agent identity; HTTP " + cleanup.status);
        }
        revoked = true;
      }
      qaAgentIdentity = null;
    }
  }, [
    "AGENT-023", "AGENT-024", "AGENT-025", "AGENT-026", "AGENT-027", "AGENT-028", "AGENT-029",
    "AGENT-030", "AGENT-031", "AGENT-032", "AGENT-033"
  ]);

  await record("DEVICE/QR: printer and station controls restore safely; QR rotation invalidates the old public code", async () => {
    let printerNeedsRestore = false;
    let stationNeedsRestore = false;
    try {
      const disabledPrinterResponse = await request(shop1Page, "PATCH",
        "/api/v1/shop/printers/" + shop1Printer.id + "/active?isActive=false",
        { token: shop1Tokens.accessToken });
      if (disabledPrinterResponse.status === 200) printerNeedsRestore = true;
      const disabledPrinter = assertHttp(disabledPrinterResponse, 200, "Disable QA virtual printer");
      assert.equal(disabledPrinter.isActive, false);

      const enabledPrinterResponse = await request(shop1Page, "PATCH",
        "/api/v1/shop/printers/" + shop1Printer.id + "/active?isActive=true",
        { token: shop1Tokens.accessToken });
      if (enabledPrinterResponse.status === 200) printerNeedsRestore = false;
      const enabledPrinter = assertHttp(enabledPrinterResponse, 200, "Restore QA virtual printer");
      assert.equal(enabledPrinter.isActive, true);

      const oldStationCode = new URL(shop1Station.publicUrl).pathname.split("/").filter(Boolean)[1];
      const disabledStationResponse = await request(shop1Page, "PATCH",
        "/api/v1/shop/qr-stations/" + shop1Station.id + "/enabled?isEnabled=false",
        { token: shop1Tokens.accessToken });
      if (disabledStationResponse.status === 200) stationNeedsRestore = true;
      const disabledStation = assertHttp(disabledStationResponse, 200, "Disable QA QR station");
      assert.equal(disabledStation.isEnabled, false);

      const blockedSession = await request(page, "POST", "/api/v1/public/stations/" + oldStationCode + "/sessions");
      assertExpectedHttpStatus(blockedSession, 404, "A disabled QR station must not create new customer sessions.");

      const enabledStationResponse = await request(shop1Page, "PATCH",
        "/api/v1/shop/qr-stations/" + shop1Station.id + "/enabled?isEnabled=true",
        { token: shop1Tokens.accessToken });
      if (enabledStationResponse.status === 200) stationNeedsRestore = false;
      const enabledStation = assertHttp(enabledStationResponse, 200, "Restore QA QR station");
      assert.equal(enabledStation.isEnabled, true);

      const rotated = assertHttp(await request(shop1Page, "POST",
        "/api/v1/shop/qr-stations/" + shop1Station.id + "/rotate-code",
        { token: shop1Tokens.accessToken }), 200, "Rotate QA QR station public code");
      const newStationCode = new URL(rotated.publicUrl).pathname.split("/").filter(Boolean)[1];
      assert.ok(newStationCode && newStationCode !== oldStationCode, "QR rotation must issue a new public code.");

      const oldCodeAfterRotation = await request(page, "POST", "/api/v1/public/stations/" + oldStationCode + "/sessions");
      assertExpectedHttpStatus(oldCodeAfterRotation, 404, "A rotated public QR code must no longer create sessions.");

      const newCodeSession = assertHttp(await request(page, "POST",
        "/api/v1/public/stations/" + newStationCode + "/sessions",
        { token: customerTokens.accessToken }), 201, "Create session from rotated QR code");
      assert.ok(newCodeSession.id && newCodeSession.sessionToken);
      shop1Station.publicUrl = rotated.publicUrl;

      return {
        printerDisabledAndRestored: true,
        stationDisabledAndRestored: true,
        disabledCode: blockedSession.status,
        oldCodeAfterRotation: oldCodeAfterRotation.status,
        rotatedCodeCreatesSession: true
      };
    } finally {
      if (stationNeedsRestore) {
        const restored = await request(shop1Page, "PATCH",
          "/api/v1/shop/qr-stations/" + shop1Station.id + "/enabled?isEnabled=true",
          { token: shop1Tokens.accessToken });
        if (restored.status !== 200) throw new Error("QA cleanup could not re-enable its QR station; HTTP " + restored.status);
      }
      if (printerNeedsRestore) {
        const restored = await request(shop1Page, "PATCH",
          "/api/v1/shop/printers/" + shop1Printer.id + "/active?isActive=true",
          { token: shop1Tokens.accessToken });
        if (restored.status !== 200) throw new Error("QA cleanup could not re-enable its virtual printer; HTTP " + restored.status);
      }
    }
  }, ["DEVICE-001", "DEVICE-002", "QR-005", "QR-006", "QR-007"]);

  await record("Platform and shop routes remain isolated after creating real QA data", async () => {
    const crossTenant = await request(shop2Page, "GET",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/events",
      { token: shop2Tokens.accessToken });
    assertExpectedHttpStatus(crossTenant, 404, "Shop two could read shop one's job events.");
    const shopOneTenantList = assertHttp(await request(adminPage, "GET",
      "/api/v1/platform/tenants?search=" + encodeURIComponent(process.env.SMARTPRINT_QA_SHOP1_NAME) + "&page=1&pageSize=20",
      { token: adminTokens.accessToken }), 200, "Platform tenant search");
    assert.ok(shopOneTenantList.items.some(item => item.id === shop1TenantId));
    return { crossTenantJobEvents: crossTenant.status, adminCanFindQaTenant: true };
  });

  await record("PLATFORM: operational settings validate, audit no-op saves, and tenant suspension is reversible", async () => {
    const settings = assertHttp(await request(adminPage, "GET", "/api/v1/platform/settings/operations", {
      token: adminTokens.accessToken
    }), 200, "Platform settings before QA update");
    const invalidSettings = await request(adminPage, "PUT", "/api/v1/platform/settings/operations", {
      token: adminTokens.accessToken,
      body: {
        trialMaxMonthlyPrintJobs: 0,
        cloudCompletedDocumentRetentionDays: settings.cloudCompletedDocumentRetentionDays,
        agentCompletedDocumentRetentionDays: settings.agentCompletedDocumentRetentionDays,
        abandonedDocumentRetentionDays: settings.abandonedDocumentRetentionDays,
        pendingLocalUploadRetentionDays: settings.pendingLocalUploadRetentionDays,
        expiredUploadTicketRetentionDays: settings.expiredUploadTicketRetentionDays,
        emptyCustomerSessionRetentionDays: settings.emptyCustomerSessionRetentionDays,
        staleRefreshTokenRetentionDays: settings.staleRefreshTokenRetentionDays,
        apiRetentionSweepHours: settings.apiRetentionSweepHours,
        agentRetentionSweepHours: settings.agentRetentionSweepHours,
        allowPaidJobClaimsWhenSuspended: settings.allowPaidJobClaimsWhenSuspended,
        reason: "QA invalid boundary"
      }
    });
    assertExpectedHttpStatus(invalidSettings, 400, "Platform operational settings must reject out-of-range quotas.");

    const noOpUpdatePayload = {
      trialMaxMonthlyPrintJobs: settings.trialMaxMonthlyPrintJobs,
      cloudCompletedDocumentRetentionDays: settings.cloudCompletedDocumentRetentionDays,
      agentCompletedDocumentRetentionDays: settings.agentCompletedDocumentRetentionDays,
      abandonedDocumentRetentionDays: settings.abandonedDocumentRetentionDays,
      pendingLocalUploadRetentionDays: settings.pendingLocalUploadRetentionDays,
      expiredUploadTicketRetentionDays: settings.expiredUploadTicketRetentionDays,
      emptyCustomerSessionRetentionDays: settings.emptyCustomerSessionRetentionDays,
      staleRefreshTokenRetentionDays: settings.staleRefreshTokenRetentionDays,
      apiRetentionSweepHours: settings.apiRetentionSweepHours,
      agentRetentionSweepHours: settings.agentRetentionSweepHours,
      allowPaidJobClaimsWhenSuspended: settings.allowPaidJobClaimsWhenSuspended,
      reason: "QA audit verification with no effective policy change"
    };
    const savedSettings = assertHttp(await request(adminPage, "PUT", "/api/v1/platform/settings/operations", {
      token: adminTokens.accessToken,
      body: noOpUpdatePayload
    }), 200, "Safe no-op platform settings save");
    for (const key of Object.keys(noOpUpdatePayload)) {
      if (key === "reason") continue;
      assert.equal(savedSettings[key], noOpUpdatePayload[key], "QA no-op save changed platform policy field " + key);
    }
    const settingsHistory = assertHttp(await request(adminPage, "GET", "/api/v1/platform/settings/operations/history", {
      token: adminTokens.accessToken
    }), 200, "Platform settings audit history");
    assert.ok(settingsHistory.some(item => item.reason === noOpUpdatePayload.reason),
      "Platform settings save must append an audit event.");

    const invalidTenantTransition = await request(adminPage, "PATCH",
      "/api/v1/platform/tenants/" + shop2TenantId + "/status",
      {
        token: adminTokens.accessToken,
        body: { status: "Frozen", reason: "QA invalid status" }
      });
    assertExpectedHttpStatus(invalidTenantTransition, 400, "Unknown tenant states must be rejected.");

    let suspended = null;
    let blockedShopRead = null;
    let restored = null;
    let duplicateActive = null;
    let statusHistory = null;
    let tenantRestored = false;
    try {
      const suspendedResponse = await request(adminPage, "PATCH",
        "/api/v1/platform/tenants/" + shop2TenantId + "/status",
        {
          token: adminTokens.accessToken,
          body: { status: "Suspended", reason: "QA temporary suspension lifecycle test" }
        });
      suspended = assertHttp(suspendedResponse, 200, "Suspend QA shop two");
      assert.equal(suspended.status, "Suspended");

      blockedShopRead = await request(shop2Page, "GET", "/api/v1/shop/profile", {
        token: shop2Tokens.accessToken
      });
      assertExpectedHttpStatus(blockedShopRead, 403, "A suspended shop must be blocked from reading its ordinary workspace profile. Actual HTTP " +
        blockedShopRead.status + " from " + blockedShopRead.url + ". Body: " + blockedShopRead.text.slice(0, 600));

      const restoredResponse = await request(adminPage, "PATCH",
        "/api/v1/platform/tenants/" + shop2TenantId + "/status",
        {
          token: adminTokens.accessToken,
          body: { status: "Active", reason: "QA restored shop after reversible lifecycle test" }
        });
      restored = assertHttp(restoredResponse, 200, "Restore QA shop two");
      assert.equal(restored.status, "Active");
      tenantRestored = true;

      duplicateActive = assertHttp(await request(adminPage, "PATCH",
        "/api/v1/platform/tenants/" + shop2TenantId + "/status",
        {
          token: adminTokens.accessToken,
          body: { status: "Active", reason: "QA duplicate active-state idempotency check" }
        }), 200, "Duplicate active tenant status request");
      assert.equal(duplicateActive.duplicateRequest, true);

      statusHistory = assertHttp(await request(adminPage, "GET",
        "/api/v1/platform/tenants/" + shop2TenantId + "/status-history",
        { token: adminTokens.accessToken }), 200, "QA tenant status history");
      assert.ok(statusHistory.some(item => item.newStatus === "Suspended"));
      assert.ok(statusHistory.some(item => item.newStatus === "Active" && /QA restored shop/.test(item.reason)));
    } finally {
      if (!tenantRestored) {
        const cleanup = await request(adminPage, "PATCH",
          "/api/v1/platform/tenants/" + shop2TenantId + "/status",
          {
            token: adminTokens.accessToken,
            body: { status: "Active", reason: "QA safety cleanup restored tenant to Active" }
          });
        if (cleanup.status !== 200) throw new Error("QA cleanup could not restore Shop Two to Active; HTTP " + cleanup.status);
      }
    }

    return {
      invalidOperationalSettings: invalidSettings.status,
      noOpPolicyValuesPreserved: true,
      settingsAuditAdded: true,
      invalidTenantStatus: invalidTenantTransition.status,
      suspendedShopBlocked: blockedShopRead.status,
      tenantRestored: restored.status,
      duplicateActiveIdempotent: duplicateActive.duplicateRequest,
      tenantAuditEvents: statusHistory.length
    };
  }, ["PLATFORM-001", "PLATFORM-002", "PLATFORM-003", "PLATFORM-004", "TENANT-001", "TENANT-002", "TENANT-003"]);

  await record("AUTH: registration validation, role boundaries, refresh rotation and logout revocation", async () => {
    const invalidShopRegistration = await request(shop1Page, "POST", "/api/v1/auth/register-shop", {
      body: { businessName: "", ownerName: "", email: "not-an-email", password: "invalid" }
    });
    assertExpectedHttpStatus(invalidShopRegistration, 400, "Invalid shop registration data must be rejected.");

    const duplicateShopRegistration = await request(shop1Page, "POST", "/api/v1/auth/register-shop", {
      body: {
        businessName: process.env.SMARTPRINT_QA_SHOP1_NAME,
        ownerName: "SmartPrint QA Owner One",
        email: process.env.SMARTPRINT_QA_SHOP1_EMAIL,
        password: process.env.SMARTPRINT_QA_SHOP1_PASSWORD
      }
    });
    assertExpectedHttpStatus(duplicateShopRegistration, 409, "A duplicate shop email must not create a second tenant.");

    const duplicateCustomerRegistration = await request(page, "POST", "/api/v1/auth/register-customer", {
      body: {
        displayName: process.env.SMARTPRINT_QA_CUSTOMER_NAME,
        email: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL,
        password: process.env.SMARTPRINT_QA_CUSTOMER_PASSWORD
      }
    });
    assertExpectedHttpStatus(duplicateCustomerRegistration, 409, "A duplicate global customer email must be rejected.");

    const customerThroughShopLogin = await request(page, "POST", "/api/v1/auth/login", {
      body: { email: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL, password: process.env.SMARTPRINT_QA_CUSTOMER_PASSWORD }
    });
    assertExpectedHttpStatus(customerThroughShopLogin, 401, "A global customer must not authenticate as a shop owner.");

    const shopThroughCustomerLogin = await request(page, "POST", "/api/v1/auth/customer-login", {
      body: { email: process.env.SMARTPRINT_QA_SHOP2_EMAIL, password: process.env.SMARTPRINT_QA_SHOP2_PASSWORD }
    });
    assertExpectedHttpStatus(shopThroughCustomerLogin, 401, "A shop owner must not authenticate as a global customer.");

    const anonymousShopProfile = await request(page, "GET", "/api/v1/shop/profile");
    const anonymousPlatformOverview = await request(page, "GET", "/api/v1/platform/overview");
    assertExpectedHttpStatus(anonymousShopProfile, 401, "Shop profile must require an access token.");
    assertExpectedHttpStatus(anonymousPlatformOverview, 401, "Platform administration must require an access token.");

    const login = assertHttp(await request(shop2Page, "POST", "/api/v1/auth/login", {
      body: { email: process.env.SMARTPRINT_QA_SHOP2_EMAIL, password: process.env.SMARTPRINT_QA_SHOP2_PASSWORD }
    }), 200, "Independent shop token lifecycle login");
    const rotated = assertHttp(await request(shop2Page, "POST", "/api/v1/auth/refresh", {
      body: { refreshToken: login.refreshToken }
    }), 200, "Refresh token rotation");
    assert.ok(rotated.accessToken && rotated.refreshToken);
    assert.notEqual(rotated.refreshToken, login.refreshToken, "Refresh must rotate the refresh token.");

    const replay = await request(shop2Page, "POST", "/api/v1/auth/refresh", {
      body: { refreshToken: login.refreshToken }
    });
    assertExpectedHttpStatus(replay, 401, "A previously rotated refresh token must not be reusable.");

    const logout = await request(shop2Page, "POST", "/api/v1/auth/logout", {
      body: { refreshToken: rotated.refreshToken }
    });
    assertExpectedHttpStatus(logout, 204, "Logout should revoke the active refresh token.");

    const refreshAfterLogout = await request(shop2Page, "POST", "/api/v1/auth/refresh", {
      body: { refreshToken: rotated.refreshToken }
    });
    assertExpectedHttpStatus(refreshAfterLogout, 401, "A logged-out refresh token must not be reusable.");

    return {
      invalidRegistration: invalidShopRegistration.status,
      duplicateShop: duplicateShopRegistration.status,
      duplicateCustomer: duplicateCustomerRegistration.status,
      crossRoleLogin: [customerThroughShopLogin.status, shopThroughCustomerLogin.status],
      anonymousProtectedRoutes: [anonymousShopProfile.status, anonymousPlatformOverview.status],
      refreshRotated: true,
      oldRefreshReplay: replay.status,
      logoutStatus: logout.status,
      refreshAfterLogout: refreshAfterLogout.status
    };
  }, ["AUTH-001", "AUTH-002", "AUTH-003", "AUTH-004", "AUTH-005", "AUTH-006", "AUTH-007", "AUTH-008"]);


  await record("RBAC: a global customer token cannot enter shop-owner APIs", async () => {
    const response = await request(page, "GET", "/api/v1/shop/profile", { token: customerTokens.accessToken });
    assertExpectedHttpStatus(response, 403, "Customer identity must not be accepted as a shop owner.");
    return { customerToShopProfile: response.status, shopDataReturned: false };
  }, ["RBAC-007"]);

  await record("RBAC: a shop-owner token cannot enter customer identity APIs", async () => {
    const response = await request(page, "GET", "/api/v1/customer/me", { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(response, 403, "Shop-owner identity must not be accepted as a global customer.");
    return { shopToCustomerProfile: response.status, customerDataReturned: false };
  }, ["RBAC-008"]);

  await record("RBAC: a shop-owner token cannot access the platform control plane", async () => {
    const response = await request(page, "GET", "/api/v1/platform/overview", { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(response, 403, "Tenant shop owners must not access platform administration.");
    return { shopToPlatformOverview: response.status, platformDataReturned: false };
  }, ["RBAC-009"]);

  await record("RBAC: a customer token cannot access the platform control plane", async () => {
    const response = await request(page, "GET", "/api/v1/platform/overview", { token: customerTokens.accessToken });
    assertExpectedHttpStatus(response, 403, "Global customers must not access platform administration.");
    return { customerToPlatformOverview: response.status, platformDataReturned: false };
  }, ["RBAC-010"]);

  await record("TENANT: Shop One cannot disable Shop Two's printer", async () => {
    const attempt = await request(shop1Page, "PATCH", "/api/v1/shop/printers/" + shop2Printer.id + "/active?isActive=false", { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(attempt, 404, "Foreign tenant printer IDs must be indistinguishable from missing printer IDs.");
    const printers = assertHttp(await request(shop2Page, "GET", "/api/v1/shop/printers", { token: shop2Tokens.accessToken }), 200, "Shop Two printer read-back");
    assert.ok(printers.find(item => item.id === shop2Printer.id)?.isActive, "Cross-tenant access must not disable Shop Two's printer.");
    return { foreignPrinterMutation: attempt.status, foreignPrinterStillActive: true };
  }, ["TENANT-005"]);

  await record("TENANT: Shop One cannot disable Shop Two's QR station", async () => {
    const attempt = await request(shop1Page, "PATCH", "/api/v1/shop/qr-stations/" + shop2Station.id + "/enabled?isEnabled=false", { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(attempt, 404, "Foreign tenant QR station IDs must be rejected.");
    const stations = assertHttp(await request(shop2Page, "GET", "/api/v1/shop/qr-stations", { token: shop2Tokens.accessToken }), 200, "Shop Two station read-back");
    assert.ok(stations.find(item => item.id === shop2Station.id)?.isEnabled, "Cross-tenant access must not disable Shop Two's station.");
    return { foreignStationMutation: attempt.status, foreignStationStillEnabled: true };
  }, ["TENANT-006"]);

  await record("TENANT: Shop One cannot rotate Shop Two's QR station code", async () => {
    const attempt = await request(shop1Page, "POST", "/api/v1/shop/qr-stations/" + shop2Station.id + "/rotate-code", { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(attempt, 404, "Foreign station QR-code rotation must be rejected.");
    const stations = assertHttp(await request(shop2Page, "GET", "/api/v1/shop/qr-stations", { token: shop2Tokens.accessToken }), 200, "Shop Two station code read-back");
    assert.equal(stations.find(item => item.id === shop2Station.id)?.publicUrl, shop2Station.publicUrl,
      "A foreign tenant must not rotate or replace the station's public code.");
    return { foreignCodeRotation: attempt.status, publicUrlUnchanged: true };
  }, ["TENANT-007"]);

  await record("TENANT: printer listings expose only the authenticated shop's printers", async () => {
    const one = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/printers", { token: shop1Tokens.accessToken }), 200, "Shop One printer listing");
    const two = assertHttp(await request(shop2Page, "GET", "/api/v1/shop/printers", { token: shop2Tokens.accessToken }), 200, "Shop Two printer listing");
    assert.ok(one.some(item => item.id === shop1Printer.id));
    assert.ok(!one.some(item => item.id === shop2Printer.id), "Shop One must not receive Shop Two's printer row.");
    assert.ok(two.some(item => item.id === shop2Printer.id));
    assert.ok(!two.some(item => item.id === shop1Printer.id), "Shop Two must not receive Shop One's printer row.");
    return { shopOneRows: one.length, shopTwoRows: two.length, crossTenantRows: 0 };
  }, ["TENANT-008"]);

  await record("TENANT: QR station listings expose only the authenticated shop's stations", async () => {
    const one = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/qr-stations", { token: shop1Tokens.accessToken }), 200, "Shop One station listing");
    const two = assertHttp(await request(shop2Page, "GET", "/api/v1/shop/qr-stations", { token: shop2Tokens.accessToken }), 200, "Shop Two station listing");
    assert.ok(one.some(item => item.id === shop1Station.id));
    assert.ok(!one.some(item => item.id === shop2Station.id), "Shop One must not receive Shop Two's station row.");
    assert.ok(two.some(item => item.id === shop2Station.id));
    assert.ok(!two.some(item => item.id === shop1Station.id), "Shop Two must not receive Shop One's station row.");
    return { shopOneRows: one.length, shopTwoRows: two.length, crossTenantRows: 0 };
  }, ["TENANT-009"]);

  await record("TENANT: revoked Agent identity remains visible only to its source shop", async () => {
    assert.ok(qaAgentLastId, "The disposable Shop One Agent ID must have been captured.");
    const one = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/agent-devices", { token: shop1Tokens.accessToken }), 200, "Shop One Agent listing");
    const two = assertHttp(await request(shop2Page, "GET", "/api/v1/shop/agent-devices", { token: shop2Tokens.accessToken }), 200, "Shop Two Agent listing");
    assert.equal(one.find(item => item.id === qaAgentLastId)?.status, "Revoked", "Shop One must retain its revoked Agent audit row.");
    assert.ok(!two.some(item => item.id === qaAgentLastId), "A tenant must never see another shop's Agent identity.");
    return { sourceShopSeesRevokedIdentity: true, otherShopSeesIdentity: false };
  }, ["TENANT-010"]);

  await record("QR: unknown public station codes cannot create a customer session", async () => {
    const response = await request(page, "POST", "/api/v1/public/stations/SMARTPRINT-QA-NO-SUCH-STATION/sessions");
    assertExpectedHttpStatus(response, 404, "An unknown or disabled public station code must not create a session.");
    assert.equal(response.json?.code, "station_not_available");
    return { unknownStationStatus: response.status, sessionCreated: false };
  }, ["QR-011"]);

  await record("QR: an invalid opaque session token cannot read current session data", async () => {
    const response = await request(page, "GET", "/api/v1/public/sessions/current", { sessionToken: "SMARTPRINT-QA-INVALID-SESSION-TOKEN" });
    assertExpectedHttpStatus(response, 401, "An invalid opaque session token must be rejected.");
    return { invalidSessionRead: response.status, sessionDataReturned: false };
  }, ["QR-012"]);

  await record("QR: an invalid opaque session token cannot renew a session", async () => {
    const response = await request(page, "POST", "/api/v1/public/sessions/current/renew", { sessionToken: "SMARTPRINT-QA-INVALID-SESSION-TOKEN" });
    assertExpectedHttpStatus(response, 401, "An invalid opaque session token must not be renewed.");
    return { invalidSessionRenewal: response.status, sessionChanged: false };
  }, ["QR-013"]);

  await record("UPLOAD: a print upload requires an Idempotency-Key", async () => {
    const response = await request(page, "POST", "/api/v1/public/sessions/current/jobs", { sessionToken: customerSession.sessionToken });
    assertExpectedHttpStatus(response, 400, "A cloud document upload must require an idempotency key.");
    assert.equal(response.json?.code, "session_and_idempotency_required");
    return { missingIdempotencyKey: response.status, printJobCreated: false };
  }, ["UPLOAD-003"]);

  await record("UPLOAD: malformed batch identifiers are rejected before document ingestion", async () => {
    const response = await request(page, "POST", "/api/v1/public/sessions/current/jobs", {
      sessionToken: customerSession.sessionToken,
      headers: { "Idempotency-Key": "smartprint-qa-invalid-batch-" + crypto.randomUUID(), "X-Print-Batch-Id": "not-a-guid" }
    });
    assertExpectedHttpStatus(response, 400, "Malformed batch IDs must be rejected.");
    assert.equal(response.json?.code, "invalid_batch_id");
    return { invalidBatchId: response.status, printJobCreated: false };
  }, ["UPLOAD-004"]);

  await record("DOC: cloud document upload rejects non-multipart requests", async () => {
    const response = await request(page, "POST", "/api/v1/public/sessions/current/jobs", {
      sessionToken: customerSession.sessionToken,
      headers: { "Idempotency-Key": "smartprint-qa-non-multipart-" + crypto.randomUUID() },
      body: { unexpected: "json is not a document upload" }
    });
    assertExpectedHttpStatus(response, 400, "Document ingestion must require multipart form data.");
    assert.equal(response.json?.code, "multipart_form_required");
    return { nonMultipartUpload: response.status, printJobCreated: false };
  }, ["DOC-006"]);

  await record("JOB: a customer session cannot read a job outside its session", async () => {
    const response = await request(page, "GET", "/api/v1/public/jobs/" + crypto.randomUUID() + "/status", { sessionToken: customerSession.sessionToken });
    assertExpectedHttpStatus(response, 404, "A customer session must not reveal jobs outside its own session.");
    return { foreignOrUnknownJobStatus: response.status, jobDataReturned: false };
  }, ["JOB-010"]);

  await record("JOB: a signed-in customer cannot read another customer's job history entry", async () => {
    const response = await request(page, "GET", "/api/v1/customer/jobs/" + crypto.randomUUID() + "/status", { token: customerTokens.accessToken });
    assertExpectedHttpStatus(response, 404, "Customer account job status must hide jobs not owned by that customer.");
    return { foreignOrUnknownCustomerJob: response.status, jobDataReturned: false };
  }, ["JOB-011"]);

  await record("PLATFORM: tenant directory rejects overlong search input", async () => {
    const response = await request(adminPage, "GET", "/api/v1/platform/tenants?search=" + encodeURIComponent("x".repeat(101)), { token: adminTokens.accessToken });
    assertExpectedHttpStatus(response, 400, "Tenant search must reject more than 100 characters.");
    return { overlongSearch: response.status };
  }, ["PLATFORM-010"]);

  await record("PLATFORM: tenant directory rejects overlong status filters", async () => {
    const response = await request(adminPage, "GET", "/api/v1/platform/tenants?status=" + encodeURIComponent("S".repeat(25)), { token: adminTokens.accessToken });
    assertExpectedHttpStatus(response, 400, "Tenant status filters must reject more than 24 characters.");
    return { overlongStatus: response.status };
  }, ["PLATFORM-011"]);

  await record("PLATFORM: tenant directory clamps oversized pagination safely", async () => {
    const response = assertHttp(await request(adminPage, "GET", "/api/v1/platform/tenants?page=999999&pageSize=999", { token: adminTokens.accessToken }), 200, "Tenant directory upper pagination bounds");
    assert.equal(response.page, 10000);
    assert.equal(response.pageSize, 50);
    assert.ok(Array.isArray(response.items));
    return { page: response.page, pageSize: response.pageSize };
  }, ["PLATFORM-012"]);

  await record("PLATFORM: tenant directory clamps negative and zero pagination safely", async () => {
    const response = assertHttp(await request(adminPage, "GET", "/api/v1/platform/tenants?page=-5&pageSize=0", { token: adminTokens.accessToken }), 200, "Tenant directory lower pagination bounds");
    assert.equal(response.page, 1);
    assert.equal(response.pageSize, 1);
    assert.ok(Array.isArray(response.items));
    return { page: response.page, pageSize: response.pageSize };
  }, ["PLATFORM-013"]);

  await record("PLATFORM: unknown tenant status history does not disclose audit data", async () => {
    const response = await request(adminPage, "GET", "/api/v1/platform/tenants/" + crypto.randomUUID() + "/status-history", { token: adminTokens.accessToken });
    assertExpectedHttpStatus(response, 404, "Unknown tenant IDs must not return a status history.");
    return { unknownTenantHistory: response.status, auditRowsReturned: false };
  }, ["PLATFORM-014"]);

  await record("SHOP: overlong shop display names are rejected without changing saved branding", async () => {
    const before = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/profile", { token: shop1Tokens.accessToken }), 200, "Shop profile before display-name boundary test");
    const rejected = await request(shop1Page, "PUT", "/api/v1/shop/profile", {
      token: shop1Tokens.accessToken,
      body: { displayName: "Q".repeat(181), accentColor: before.accentColor, logoUrl: before.logoUrl }
    });
    assertExpectedHttpStatus(rejected, 400, "Shop display names must be limited to 180 characters.");
    const after = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/profile", { token: shop1Tokens.accessToken }), 200, "Shop profile after display-name boundary test");
    assert.equal(after.displayName, before.displayName, "Rejected profile changes must not persist.");
    return { rejectedStatus: rejected.status, savedDisplayNameUnchanged: true };
  }, ["SHOP-019"]);

  await record("SHOP: malformed accent colors are rejected without changing saved branding", async () => {
    const before = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/profile", { token: shop1Tokens.accessToken }), 200, "Shop profile before accent-color boundary test");
    const rejected = await request(shop1Page, "PUT", "/api/v1/shop/profile", {
      token: shop1Tokens.accessToken,
      body: { displayName: before.displayName, accentColor: "#12X45G", logoUrl: before.logoUrl }
    });
    assertExpectedHttpStatus(rejected, 400, "Accent colors must be six-digit hexadecimal colors.");
    const after = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/profile", { token: shop1Tokens.accessToken }), 200, "Shop profile after accent-color boundary test");
    assert.equal(after.accentColor, before.accentColor, "Rejected accent colors must not persist.");
    return { rejectedStatus: rejected.status, savedAccentColorUnchanged: true };
  }, ["SHOP-020"]);

  await record("SHOP: printer creation rejects missing manufacturer and model fields", async () => {
    const before = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/printers", { token: shop1Tokens.accessToken }), 200, "Printer list before required-field test");
    const rejected = await request(shop1Page, "POST", "/api/v1/shop/printers", {
      token: shop1Tokens.accessToken,
      body: { displayName: "SmartPrint QA Incomplete Printer", manufacturer: "", model: "", connectionType: "USB", capabilitiesJson: "{}" }
    });
    assertExpectedHttpStatus(rejected, 400, "Printer creation must require manufacturer and model.");
    const after = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/printers", { token: shop1Tokens.accessToken }), 200, "Printer list after required-field test");
    assert.equal(after.length, before.length, "Rejected printer creation must not create a record.");
    return { rejectedStatus: rejected.status, printerCountUnchanged: true };
  }, ["SHOP-021"]);

  await record("SHOP: QR station creation rejects an empty station name without creating a row", async () => {
    const before = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/qr-stations", { token: shop1Tokens.accessToken }), 200, "Station list before required-field test");
    const rejected = await request(shop1Page, "POST", "/api/v1/shop/qr-stations", {
      token: shop1Tokens.accessToken,
      body: { displayName: " ", printerDeviceId: shop1Printer.id }
    });
    assertExpectedHttpStatus(rejected, 400, "QR station creation must require a non-empty name.");
    const after = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/qr-stations", { token: shop1Tokens.accessToken }), 200, "Station list after required-field test");
    assert.equal(after.length, before.length, "Rejected station creation must not create a row.");
    return { rejectedStatus: rejected.status, stationCountUnchanged: true };
  }, ["SHOP-022"]);

  await record("SHOP: an unknown printer ID cannot be changed", async () => {
    const response = await request(shop1Page, "PATCH", "/api/v1/shop/printers/" + crypto.randomUUID() + "/active?isActive=false", { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(response, 404, "Unknown printer IDs must return not found.");
    return { unknownPrinterMutation: response.status, recordsChanged: false };
  }, ["SHOP-023"]);

  await record("SHOP: an unknown QR station ID cannot be changed", async () => {
    const response = await request(shop1Page, "PATCH", "/api/v1/shop/qr-stations/" + crypto.randomUUID() + "/enabled?isEnabled=false", { token: shop1Tokens.accessToken });
    assertExpectedHttpStatus(response, 404, "Unknown station IDs must return not found.");
    return { unknownStationMutation: response.status, recordsChanged: false };
  }, ["SHOP-024"]);

  await record("PAYMENT: UPI references shorter than four characters are rejected", async () => {
    const response = await request(page, "POST", "/api/v1/public/batch-payments/" + crypto.randomUUID() + "/report", { body: { referenceNumber: "A1" } });
    assertExpectedHttpStatus(response, 400, "UPI references shorter than four characters must be rejected.");
    return { shortReference: response.status, paymentUpdated: false };
  }, ["PAY-011"]);

  await record("PAYMENT: UPI references containing whitespace or punctuation are rejected", async () => {
    const response = await request(page, "POST", "/api/v1/public/batch-payments/" + crypto.randomUUID() + "/report", { body: { referenceNumber: "QA 1234!" } });
    assertExpectedHttpStatus(response, 400, "UPI reference characters must be letters, digits, hyphens or underscores.");
    return { invalidReferenceCharacters: response.status, paymentUpdated: false };
  }, ["PAY-012"]);

  await record("PAYMENT: UPI references longer than one hundred characters are rejected", async () => {
    const response = await request(page, "POST", "/api/v1/public/batch-payments/" + crypto.randomUUID() + "/report", { body: { referenceNumber: "Q".repeat(101) } });
    assertExpectedHttpStatus(response, 400, "UPI references must not exceed 100 characters.");
    return { overlongReference: response.status, paymentUpdated: false };
  }, ["PAY-013"]);


  await record("Live local browser has no same-origin server errors or critical resource failures", async () => {
    const unexpectedHttpErrors = getUnexpectedHttpResponses(httpErrorResponses, expectedHttpErrors);
    assert.equal(serverErrors.length, 0, JSON.stringify(serverErrors));
    assert.equal(resourceErrors.length, 0, JSON.stringify(resourceErrors));
    assert.equal(pageErrors.length, 0, pageErrors.join("\n"));
    assert.equal(consoleErrors.length, 0, consoleErrors.join("\n"));
    assert.equal(requestFailures.length, 0, JSON.stringify(requestFailures));
    assert.equal(unexpectedHttpErrors.length, 0, JSON.stringify(unexpectedHttpErrors));
    return {
      serverErrors: serverErrors.length,
      resourceErrors: resourceErrors.length,
      pageErrors: pageErrors.length,
      consoleErrors: consoleErrors.length,
      networkStatusConsoleMessages: networkStatusConsoleErrors.length,
      requestFailures: requestFailures.length,
      unexpectedHttpErrors: unexpectedHttpErrors.length,
      expectedHttpErrors: expectedHttpErrors.length,
      observedHttpErrors: httpErrorResponses.length,
      pagesObserved: context.pages().length
    };
  });

  report.scopeNotes.push({
    name: "Physical Windows print acceptance",
    message: "Not run. The QA printer record is deliberately non-dispatchable. Physical output requires a separate Windows Agent paired to a QA tenant and an explicitly designated test printer."
  });
  report.scopeNotes.push({
    name: "Android physical acceptance",
    message: "Not run by browser automation. Run the Android real-device/emulator stage separately; QR camera, Android document picker, local-network permission and LAN transfer cannot be proved by desktop Playwright."
  });
} finally {
  report.finishedUtc = new Date().toISOString();
  const caseResults = new Map();
  const caseOwners = new Map();
  const duplicateCaseIds = new Set();
  const unplannedCaseIds = new Set();
  for (const check of report.checks) {
    // Failed/uncertain checks cannot earn coverage. Duplicate assignments
    // are incomplete; unplanned IDs are reported and fail the coverage gate.
    const caseStatus = check.status === "FAIL" || check.status === "WARN"
      ? "INCOMPLETE"
      : check.status;
    for (const caseId of check.caseIds || []) {
      if (caseOwners.has(caseId)) duplicateCaseIds.add(caseId);
      else caseOwners.set(caseId, check.id);
      if (!plannedCaseIdSet.has(caseId)) unplannedCaseIds.add(caseId);
      const previousStatus = caseResults.get(caseId);
      if (previousStatus === "INCOMPLETE" || caseStatus === "INCOMPLETE") {
        caseResults.set(caseId, "INCOMPLETE");
      } else if (previousStatus === "SKIPPED" || caseStatus === "SKIPPED") {
        caseResults.set(caseId, "SKIPPED");
      } else {
        caseResults.set(caseId, caseStatus);
      }
    }
  }
  for (const caseId of duplicateCaseIds) {
    if (caseResults.has(caseId)) caseResults.set(caseId, "INCOMPLETE");
  }
  for (const caseId of unplannedCaseIds) caseResults.set(caseId, "INCOMPLETE");
  const passedCases = [...caseResults.values()].filter(status => status === "PASS").length;
  const incompleteCases = [...caseResults.values()].filter(status => status === "INCOMPLETE").length;
  const skippedCases = [...caseResults.values()].filter(status => status === "SKIPPED").length;
  const missingCases = PLANNED_CASE_IDS.filter(caseId => !caseResults.has(caseId));
  const unknownCaseStatuses = [...caseResults.values()].filter(status =>
    !["PASS", "INCOMPLETE", "SKIPPED"].includes(status)).length;
  const caseStatusTotalsMatch = unknownCaseStatuses === 0 &&
    passedCases + incompleteCases + skippedCases === caseResults.size;
  const uniqueCasesExercised = passedCases + incompleteCases;
  const caseExecutionCoveragePercent = Number(((passedCases / PLANNED_CASE_IDS.length) * 100).toFixed(1));
  const casePassPercent = caseExecutionCoveragePercent;
  report.caseCoverage = {
    plannedCases: PLANNED_CASE_IDS.length,
    caseIdsMappedToChecks: caseResults.size,
    uniqueCasesExercised,
    uniqueCasesFullyCompleted: passedCases,
    coveragePercent: caseExecutionCoveragePercent,
    caseExecutionCoveragePercent,
    casePassPercent,
    allPlannedCasesPassed: caseResults.size === PLANNED_CASE_IDS.length &&
      passedCases === PLANNED_CASE_IDS.length &&
      caseStatusTotalsMatch &&
      missingCases.length === 0 &&
      duplicateCaseIds.size === 0 &&
      unplannedCaseIds.size === 0,
    coverageGatePassed: caseResults.size === PLANNED_CASE_IDS.length &&
      passedCases === PLANNED_CASE_IDS.length &&
      incompleteCases === 0 &&
      skippedCases === 0 &&
      caseStatusTotalsMatch &&
      missingCases.length === 0 &&
      duplicateCaseIds.size === 0 &&
      unplannedCaseIds.size === 0 &&
      report.caseMappingErrors.length === 0,
    duplicateCaseIds: [...duplicateCaseIds].sort(),
    unplannedCaseIds: [...unplannedCaseIds].sort(),
    caseMappingErrors: report.caseMappingErrors.length,
    passedCases,
    incompleteCases,
    skippedCases,
    unknownCaseStatuses,
    caseStatusTotalsMatch,
    missingCases,
    caseStatuses: Object.fromEntries([...caseResults.entries()].sort(([left], [right]) => left.localeCompare(right)))
  };
  report.summary = {
    passed: report.checks.filter(check => check.status === "PASS").length,
    failed: report.checks.filter(check => check.status === "FAIL").length,
    skipped: report.checks.filter(check => check.status === "SKIPPED").length,
    warnings: report.warnings.length,
    outOfScopeStages: report.scopeNotes.length,
    plannedCases: report.caseCoverage.plannedCases,
    caseIdsMappedToChecks: report.caseCoverage.caseIdsMappedToChecks,
    uniqueCasesExercised: report.caseCoverage.uniqueCasesExercised,
    uniqueCasesFullyCompleted: report.caseCoverage.uniqueCasesFullyCompleted,
    coveragePercent: report.caseCoverage.coveragePercent,
    caseExecutionCoveragePercent: report.caseCoverage.caseExecutionCoveragePercent,
    casePassPercent: report.caseCoverage.casePassPercent,
    allPlannedCasesPassed: report.caseCoverage.allPlannedCasesPassed,
    coverageGatePassed: report.caseCoverage.coverageGatePassed,
    passedCases: report.caseCoverage.passedCases,
    incompleteCases: report.caseCoverage.incompleteCases,
    skippedCases: report.caseCoverage.skippedCases,
    unknownCaseStatuses: report.caseCoverage.unknownCaseStatuses,
    caseStatusTotalsMatch: report.caseCoverage.caseStatusTotalsMatch,
    warningDetails: report.warnings.map(item => ({ name: item.name, message: item.message })),
    duplicateCaseIds: report.caseCoverage.duplicateCaseIds,
    unplannedCaseIds: report.caseCoverage.unplannedCaseIds,
    caseMappingErrors: report.caseCoverage.caseMappingErrors,
    missingCases: report.caseCoverage.missingCases
  };
  writeFileSync(join(outputDirectory, "report.json"), JSON.stringify(report, null, 2) + "\n");
  writeFileSync(join(outputDirectory, "report.txt"),
    "SmartPrint local real-data QA\n" +
    "Target: " + report.target + "\n" +
    "Passed: " + report.summary.passed + "\n" +
    "Failed: " + report.summary.failed + "\n" +
    "Skipped: " + report.summary.skipped + "\n" +
    "Warnings: " + report.summary.warnings + "\n" +
    "Out-of-scope stages (not warnings): " + report.summary.outOfScopeStages + "\n" +
    "Out-of-scope acceptance notes: " + report.scopeNotes.map(item => item.name).join(", ") + "\n" +
    "Warning details (each is release-blocking):\n" +
    (report.warnings.length
      ? report.warnings.map(item => "- " + item.name + ": " + item.message).join("\n")
      : "None") + "\n" +
    "Case IDs with a check result: " + report.caseCoverage.caseIdsMappedToChecks + "/" + report.caseCoverage.plannedCases + "\n" +
    "Case IDs in checks that ran (PASS or incomplete): " + report.caseCoverage.uniqueCasesExercised + "/" + report.caseCoverage.plannedCases + "\n" +
    "Functional case IDs fully completed: " + report.caseCoverage.uniqueCasesFullyCompleted + "/" + report.caseCoverage.plannedCases + "\n" +
    "Functional case completion coverage: " + report.caseCoverage.caseExecutionCoveragePercent + "% (every planned case must pass)\n" +
    "Functional case pass rate: " + report.caseCoverage.casePassPercent + "%\n" +
    "Case IDs linked to incomplete checks: " + report.caseCoverage.incompleteCases + "\n" +
    "Skipped case IDs: " + report.caseCoverage.skippedCases + "\n" +
    "Unclassified case IDs: " + report.caseCoverage.unknownCaseStatuses + "\n" +
    "Case status totals reconcile: " + report.caseCoverage.caseStatusTotalsMatch + "\n" +
    "Coverage gate: " + (report.caseCoverage.coverageGatePassed ? "PASS" : "FAIL") + "\n" +
    "Missing test cases: " + report.caseCoverage.missingCases.join(", ") + "\n" +
    "Evidence: " + outputDirectory + "\n");
  console.log("\nSmartPrint local real-data QA summary:");
  console.log(JSON.stringify(report.summary, null, 2));
  console.log("Evidence: " + outputDirectory);
  await context?.close().catch(() => {});
  await browser?.close().catch(() => {});
}

if (shouldFailQaRun({
  failed: report.summary.failed,
  warnings: report.summary.warnings,
  skipped: report.summary.skipped,
  coverageGatePassed: report.caseCoverage.coverageGatePassed,
  caseMappingErrors: report.caseCoverage.caseMappingErrors
})) process.exitCode = 1;
