import assert from "node:assert/strict";
import { existsSync, mkdirSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn, spawnSync } from "node:child_process";
import { tmpdir } from "node:os";

const appMode = (process.env.SMARTPRINT_MIXED_BATCH_REFUND_APP || "web").toLowerCase();
if (!["web", "desktop", "desktop-native"].includes(appMode)) {
  throw new Error("App mode must be web, desktop, or desktop-native.");
}
if (appMode === "desktop-native" && process.platform !== "win32") {
  throw new Error("The native WebView2 acceptance case runs on Windows only.");
}
const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR is required.");
const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const artifactDirectory = process.env.SMARTPRINT_MIXED_BATCH_REFUND_ARTIFACT_DIR ||
  join("artifacts", "mixed-batch-refund", appMode);
mkdirSync(artifactDirectory, { recursive: true });

const port = appMode === "web" ? "4174" : "1420";
const baseUrl = process.env.SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL || "http://127.0.0.1:" + port;
const webViewDebugPort = Number(process.env.SMARTPRINT_WEBVIEW2_DEBUG_PORT || "9228");
if (!Number.isInteger(webViewDebugPort) || webViewDebugPort < 1024 || webViewDebugPort > 65535) {
  throw new Error("SMARTPRINT_WEBVIEW2_DEBUG_PORT must be a valid TCP port.");
}
const webViewDebugUrl = "http://127.0.0.1:" + webViewDebugPort;
const nativeStartupTimeoutSeconds = Number(process.env.SMARTPRINT_WEBVIEW2_STARTUP_TIMEOUT_SECONDS || "420");
if (!Number.isInteger(nativeStartupTimeoutSeconds) ||
    nativeStartupTimeoutSeconds < 120 ||
    nativeStartupTimeoutSeconds > 900) {
  throw new Error("SMARTPRINT_WEBVIEW2_STARTUP_TIMEOUT_SECONDS must be an integer from 120 to 900.");
}
const serverArgs = appMode === "desktop"
  ? ["--filter", "@smart-print/desktop", "dev", "--host", "127.0.0.1", "--port", port, "--strictPort"]
  : ["--filter", "@smart-print/portal", "dev", "--host", "127.0.0.1", "--port", port, "--strictPort"];

function nativeWebViewProcessDiagnostics() {
  const query = spawnSync("powershell.exe", [
    "-NoProfile", "-NonInteractive", "-Command",
    "Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('smartprint-desktop.exe','msedgewebview2.exe') } | ForEach-Object { '{0} PID={1} Parent={2} Args={3}' -f $_.Name,$_.ProcessId,$_.ParentProcessId,$_.CommandLine }"
  ], { encoding: "utf8", windowsHide: true, timeout: 15000 });
  return [
    "Native process diagnostics:",
    query.stdout || query.stderr || "(no process details)",
    "Tauri test additionalBrowserArgs:",
    nativeBrowserArguments || "(not configured)"
  ].join("\n");
}

function startServer() {
  const baseEnvironment = { ...process.env, VITE_API_BASE_URL: "" };
  const options = { stdio: "inherit", env: baseEnvironment, windowsHide: true, detached: process.platform !== "win32" };
  let child;
  if (appMode === "desktop-native" && process.env.SMARTPRINT_MIXED_BATCH_REFUND_EXECUTABLE) {
    // Pull-and-test mode launches the isolated CI/local QA executable itself.
    // That executable was built with this exact localhost CDP port; never add
    // debugging flags to or mutate the production executable.
    const executable = process.env.SMARTPRINT_MIXED_BATCH_REFUND_EXECUTABLE;
    if (!existsSync(executable)) throw new Error("The supplied Windows QA executable does not exist: " + executable);
    const userDataFolder = process.env.SMARTPRINT_WEBVIEW2_USER_DATA_FOLDER ||
      join(tmpdir(), "smartprint-mixed-batch-webview2-" + process.pid + "-" + Date.now());
    mkdirSync(userDataFolder, { recursive: true });
    nativeBrowserArguments = "prebuilt QA executable; expected localhost CDP port " + webViewDebugPort;
    child = spawn(executable, [], {
      stdio: "inherit",
      windowsHide: false,
      env: { ...baseEnvironment, WEBVIEW2_USER_DATA_FOLDER: userDataFolder }
    });
  } else if (appMode === "desktop-native") {
    // The native test uses Tauri dev with Vite-local API URLs only. CDP is enabled
    // exclusively for this child process and is bound to localhost.
    const userDataFolder = process.env.SMARTPRINT_WEBVIEW2_USER_DATA_FOLDER ||
      join(tmpdir(), "smartprint-mixed-batch-webview2-" + process.pid + "-" + Date.now());
    mkdirSync(userDataFolder, { recursive: true });
    const tauriDirectory = join(process.cwd(), "apps", "desktop", "src-tauri");
    const tauriConfigPath = join(tauriDirectory, ".tauri.qa-webview2-" + process.pid + ".json");
    const tauriConfig = JSON.parse(readFileSync(join(tauriDirectory, "tauri.conf.json"), "utf8"));
    const mainWindow = tauriConfig.app?.windows?.find(window => window.label === "main");
    if (!mainWindow) throw new Error("The Tauri application configuration has no main window.");
    nativeBrowserArguments = [
      "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection",
      "--remote-debugging-port=" + webViewDebugPort
    ].join(" ");
    mainWindow.additionalBrowserArgs = nativeBrowserArguments;
    writeFileSync(tauriConfigPath, JSON.stringify(tauriConfig, null, 2), "utf8");
    nativeConfigPath = tauriConfigPath;
    const nativeEnvironment = {
      ...baseEnvironment,
      WEBVIEW2_USER_DATA_FOLDER: userDataFolder
    };
    // GitHub-hosted Windows paths contain no spaces. Do not add shell quotes
    // around this one argument: cmd.exe preserves nested quotes when it builds
    // the child command line, and Tauri then treats them as part of the filename.
    const command = [
      "pnpm", "--filter", "@smart-print/desktop", "exec", "tauri", "dev",
      "--config", tauriConfigPath
    ].join(" ");
    child = spawn(process.env.ComSpec || "cmd.exe", ["/d", "/c", command], {
      ...options, env: nativeEnvironment
    });
  } else if (process.platform === "win32") {
    child = spawn(process.env.ComSpec || "cmd.exe", ["/d", "/c", ["pnpm", ...serverArgs].join(" ")], options);
  } else {
    child = spawn("pnpm", serverArgs, options);
  }
  child.spawnError = null;
  child.on("error", error => { child.spawnError = error; });
  return child;
}
function stopServer(child) {
  if (!child?.pid) return;
  if (process.platform === "win32") {
    spawnSync("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore", windowsHide: true });
  } else {
    try { process.kill(-child.pid, "SIGTERM"); }
    catch { if (child.exitCode === null) child.kill("SIGTERM"); }
  }
}
async function waitForServer(child) {
  const timeoutSeconds = appMode === "desktop-native" ? nativeStartupTimeoutSeconds : 120;
  const healthUrl = appMode === "desktop-native"
    ? webViewDebugUrl + "/json/version"
    : baseUrl + "/shop";
  let nextProgressLog = 30;

  for (let attempt = 0; attempt < timeoutSeconds; attempt += 1) {
    if (child?.spawnError) throw new Error("QA server failed to start: " + child.spawnError.message);
    if (child?.exitCode !== null && child?.exitCode !== undefined) {
      throw new Error("QA server exited with code " + child.exitCode);
    }
    try {
      const response = await fetch(healthUrl);
      if (response.ok) {
        if (appMode === "desktop-native") {
          console.log("Native Windows WebView2 CDP endpoint is ready: " + healthUrl);
        }
        return;
      }
    } catch {}

    if (appMode === "desktop-native" && attempt + 1 >= nextProgressLog) {
      console.log(
        "Waiting for the native Tauri/WebView2 host to finish its first build and expose localhost CDP (" +
        (attempt + 1) + "/" + timeoutSeconds + " seconds)."
      );
      nextProgressLog += 30;
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }

  throw new Error(appMode === "desktop-native"
    ? "Native Windows WebView2 did not expose its localhost CDP endpoint within " + timeoutSeconds +
      " seconds.\n" + nativeWebViewProcessDiagnostics()
    : "QA server did not become ready within 120 seconds.");
}

async function waitForNativeApplicationPage(page, phase, { requireAuthenticatedShell = true } = {}) {
  const expectedPort = new URL(baseUrl).port || "80";
  const prebuiltNativeExecutable = Boolean(process.env.SMARTPRINT_MIXED_BATCH_REFUND_EXECUTABLE);
  try {
    // Tauri dev uses the explicit Vite port. A packaged/CI QA executable uses
    // the app's bundled tauri.localhost origin instead, so in prebuilt mode
    // wait for the real Tauri bridge rather than incorrectly requiring :1420.
    if (!prebuiltNativeExecutable) {
      await page.waitForURL(
        url => url.protocol === "http:" && url.port === expectedPort,
        { timeout: 60_000 }
      );
    } else {
      await page.waitForFunction(
        () => location.protocol !== "about:" && location.protocol !== "devtools:",
        null,
        { timeout: 60_000 }
      );
    }
    await page.waitForFunction(
      () => typeof window.__TAURI_INTERNALS__?.invoke === "function",
      null,
      { timeout: 60_000 }
    );
    await page.locator("#smartprint-desktop-root").waitFor({
      state: "attached",
      timeout: 60_000
    });
    if (requireAuthenticatedShell) {
      await page.waitForFunction(
        () => document.body?.dataset.appShell === "desktop" &&
          Boolean(document.querySelector(".app-shell")),
        null,
        { timeout: 60_000 }
      );
    } else {
      // The very first Tauri page is intentionally unauthenticated. Wait for its
      // React root to render the sign-in route before writing the isolated test
      // credential; requiring .app-shell here deadlocks before the session exists.
      await page.waitForFunction(
        () => document.readyState === "complete" &&
          Boolean(document.querySelector("#smartprint-desktop-root")?.firstElementChild),
        null,
        { timeout: 60_000 }
      );
    }
  } catch (error) {
    const diagnostics = await page.evaluate(() => ({
      href: location.href,
      readyState: document.readyState,
      title: document.title,
      hasTauriBridge: typeof window.__TAURI_INTERNALS__?.invoke === "function",
      appShell: document.body?.dataset.appShell ?? null,
      rootChildren: document.querySelector("#smartprint-desktop-root")?.childElementCount ?? 0,
      bodyText: document.body?.innerText?.slice(0, 400) ?? ""
    })).catch(reason => ({ diagnosticError: reason instanceof Error ? reason.message : String(reason) }));
    throw new Error(
      "Native SmartPrint WebView was not ready during " + phase + ": " +
      JSON.stringify(diagnostics) + "; " +
      (error instanceof Error ? error.message : String(error)) + "\n" +
      nativeWebViewProcessDiagnostics()
    );
  }
}

const SHOP_TOKEN = "e30." + Buffer.from(JSON.stringify({
  sub: "mixed-batch-refund-qa-owner", tenant_id: "mixed-batch-refund-qa-tenant"
})).toString("base64url") + ".mixed-batch-refund-qa-signature";
const BATCH_ID = "81000000-0000-4000-8000-000000000001";
const BATCH_PAYMENT_ID = "82000000-0000-4000-8000-000000000001";
const SHOP_USER_ID = "83000000-0000-4000-8000-000000000001";
const now = new Date().toISOString();
const quoteExpiry = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
const allocations = new Map([
  ["84000000-0000-4000-8000-000000000001", [1000, 1000, 1000]],
  ["84000000-0000-4000-8000-000000000002", [800]],
  ["84000000-0000-4000-8000-000000000003", [1200]]
]);
function makeJob(id, jobNumber, fileName, mediaType, pageCount, amountMinor, paymentId) {
  return {
    id, batchId: BATCH_ID, batchGroupName: "Mixed PDF and images",
    batchName: "QA mixed PDF JPG PNG refund batch", rejectedPageNumbersJson: "[]",
    jobNumber, status: "PAID_AUTHORISED", pageCount, copies: 1, colourMode: "Colour",
    paperSize: "A4", isDuplex: false, orientation: "AUTO", pdfBrandingEnabled: false,
    pricingVersion: 1, printKind: "DOCUMENT", currency: "INR", quotedAmountMinor: amountMinor,
    quoteExpiresUtc: quoteExpiry, createdUtc: now, storageMode: "CLOUD",
    outputMode: "PHYSICAL", outputType: "PHYSICAL", localQueueReceivedUtc: null,
    claimedByAgentDeviceId: null, failureCode: null,
    payment: { id: paymentId, status: "VERIFIED", amountMinor, refundedAmountMinor: 0, currency: "INR", method: "CASH" },
    document: { originalFileName: fileName, mediaType, sizeBytes: 4096 }
  };
}
const jobs = [
  makeJob("84000000-0000-4000-8000-000000000001", "SP-QA-MIXED-001", "mixed-exam-set.pdf", "application/pdf", 3, 3000, "85000000-0000-4000-8000-000000000001"),
  makeJob("84000000-0000-4000-8000-000000000002", "SP-QA-MIXED-002", "candidate-photo.jpg", "image/jpeg", 1, 800, "85000000-0000-4000-8000-000000000002"),
  makeJob("84000000-0000-4000-8000-000000000003", "SP-QA-MIXED-003", "supporting-scan.png", "image/png", 1, 1200, "85000000-0000-4000-8000-000000000003")
];
const payments = jobs.map(job => ({
  id: job.payment.id, printJobId: job.id, jobNumber: job.jobNumber,
  documentName: job.document.originalFileName, batchPaymentId: BATCH_PAYMENT_ID,
  batchFileCount: jobs.length, method: "CASH", status: "VERIFIED",
  amountMinor: job.quotedAmountMinor, refundedAmountMinor: 0, currency: "INR",
  paymentReference: null, createdUtc: now, updatedUtc: now, verifiedUtc: now
}));
const refunds = [];
const refundsByIdempotencyKey = new Map();
const refundRequests = [];
const apiRequests = [];
const unhandledApi = [];
let refundSequence = 0;

const shopProfile = {
  id: "mixed-batch-refund-qa-shop", slug: "mixed-batch-refund-qa",
  businessName: "Mixed Batch Refund QA Shop", displayName: "Mixed Batch Refund QA Shop",
  status: "Active", ownerName: "QA Shop Owner", ownerEmail: "mixed-refund@example.invalid",
  logoUrl: null, accentColor: "#7662E8", currency: "INR", timeZone: "Asia/Kolkata", whiteLabelEnabled: false
};
const pricing = {
  isConfigured: true, monoPerSideMinor: 100, colourPerSideMinor: 200,
  monoDuplexPerSheetMinor: 180, colourDuplexPerSheetMinor: 350,
  minimumChargeMinor: 100, photo35x45Minor: 2500, photo25x35Minor: 1800,
  photo2x2Minor: 3000, photo4x6Minor: 6000, version: 1, updatedUtc: now
};
const subscription = {
  generatedUtc: now,
  subscription: {
    id: "86000000-0000-4000-8000-000000000001", planCode: "STARTER", status: "ACTIVE",
    billingInterval: "MONTHLY", periodStartUtc: now, periodEndUtc: quoteExpiry, createdUtc: now
  },
  plan: null,
  entitlements: {
    maxPrinters: 2, maxAgentDevices: 1, maxQrStations: 2, maxMonthlyPrintJobs: 1000,
    maxDocumentBytes: 52428800, cloudAssistedPrintingEnabled: true, whiteLabelEnabled: false,
    advancedReportsEnabled: false, isLegacyUnconfigured: false, isAccessActive: true
  },
  usage: {
    printerCount: 0, agentDeviceCount: 1, qrStationCount: 1, monthlyPrintJobCount: 3,
    monthlyPeriodStartUtc: now, monthlyPeriodEndUtc: quoteExpiry
  }
};
const agentDevices = [{
  id: "87000000-0000-4000-8000-000000000001",
  deviceName: "QA Windows desktop host", status: "Online", createdUtc: now,
  lastSeenUtc: now, revokedUtc: null, printerCount: 0, localEndpoints: []
}];

function json(route, value, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(value) });
}
function previewRefund(job, rejectedPageNumbers) {
  const pageAllocations = allocations.get(job.id);
  const previous = JSON.parse(job.rejectedPageNumbersJson || "[]");
  const selected = [...new Set(rejectedPageNumbers)].sort((a, b) => a - b);
  if (selected.length !== rejectedPageNumbers.length ||
      selected.some(page => !Number.isInteger(page) || page < 1 || page > job.pageCount) ||
      previous.some(page => !selected.includes(page))) return null;
  const additional = selected.filter(page => !previous.includes(page))
    .reduce((sum, page) => sum + pageAllocations[page - 1], 0);
  const payment = payments.find(item => item.printJobId === job.id);
  if (!payment) return null;
  const cumulative = payment.refundedAmountMinor + additional;
  return {
    id: job.id, pageCount: job.pageCount, rejectedPageNumbers: selected,
    rejectedPageCount: selected.length, acceptedPageCount: job.pageCount - selected.length,
    originalAmountMinor: payment.amountMinor, previouslyRefundedAmountMinor: payment.refundedAmountMinor,
    additionalRefundAmountMinor: additional, cumulativeRefundAmountMinor: cumulative,
    remainingPaidAmountMinor: payment.amountMinor - cumulative, currency: payment.currency
  };
}
async function installApiRoutes(page) {
  await page.route("**/api/**", async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const method = request.method().toUpperCase();
    apiRequests.push({ method, path });

    if (method === "GET" && path === "/api/v1/shop/profile") return json(route, shopProfile);
    if (method === "GET" && path === "/api/v1/shop/pricing") return json(route, pricing);
    if (method === "GET" && ["/api/v1/shop/printers", "/api/v1/shop/qr-stations"].includes(path)) return json(route, []);
    if (method === "GET" && path === "/api/v1/shop/agent-devices") return json(route, agentDevices);
    if (method === "GET" && path === "/api/v1/shop/payment-settings") {
      return json(route, { upiEnabled: false, upiId: null, payeeName: null, cashEnabled: true, instantPaymentEnabled: false, updatedUtc: now });
    }
    if (method === "GET" && path === "/api/v1/shop/subscription") return json(route, subscription);
    if (method === "GET" && path === "/api/v1/shop/payments") return json(route, payments);
    if (method === "GET" && path === "/api/v1/shop/refunds") return json(route, refunds);
    if (method === "GET" && path === "/api/v1/shop/print-jobs") return json(route, jobs);

    const previewMatch = path.match(/^\/api\/v1\/shop\/print-jobs\/([^/]+)\/refund-preview$/);
    if (previewMatch && method === "POST") {
      const job = jobs.find(item => item.id === previewMatch[1]);
      if (!job) return json(route, { message: "QA job not found." }, 404);
      const preview = previewRefund(job, request.postDataJSON().rejectedPageNumbers || []);
      return preview ? json(route, preview) : json(route, { code: "invalid_page_selection" }, 400);
    }

    const refundMatch = path.match(/^\/api\/v1\/shop\/print-jobs\/([^/]+)\/refund$/);
    if (refundMatch && method === "POST") {
      const job = jobs.find(item => item.id === refundMatch[1]);
      if (!job) return json(route, { message: "QA job not found." }, 404);
      const body = request.postDataJSON();
      const duplicate = refundsByIdempotencyKey.get(body.idempotencyKey);
      if (duplicate) {
        if (duplicate.printJobId !== job.id) return json(route, { code: "refund_idempotency_key_reused" }, 409);
        return json(route, { ...duplicate, duplicateRequest: true });
      }
      if (body.confirmedSent !== true || body.refundMethod !== "CASH" ||
          typeof body.reason !== "string" || body.reason.trim().length < 3 ||
          typeof body.idempotencyKey !== "string" || body.idempotencyKey.length < 8) {
        return json(route, { code: "invalid_refund_request" }, 400);
      }
      const preview = previewRefund(job, body.rejectedPageNumbers || []);
      if (!preview || preview.additionalRefundAmountMinor <= 0) return json(route, { code: "invalid_refund_amount" }, 409);
      const payment = payments.find(item => item.printJobId === job.id);
      const fullyRejected = preview.rejectedPageCount === job.pageCount;
      const record = {
        id: "88000000-0000-4000-8000-" + String(++refundSequence).padStart(12, "0"),
        printJobId: job.id, printPaymentId: payment.id, jobNumber: job.jobNumber,
        documentName: job.document.originalFileName, amountMinor: preview.additionalRefundAmountMinor,
        currency: payment.currency, method: "CASH", status: "RECORDED_AS_SENT",
        reference: null, reason: body.reason.trim(), refundedUtc: new Date().toISOString(),
        refundedByUserId: SHOP_USER_ID, refundedByName: "QA Shop Owner"
      };
      job.rejectedPageNumbersJson = JSON.stringify(preview.rejectedPageNumbers);
      job.status = fullyRejected ? "REFUNDED" : "PAID_AUTHORISED";
      job.failureCode = fullyRejected ? "SHOP_REJECTED_FILE_FULLY_REFUNDED" : "SHOP_REJECTED_PAGES_PARTIALLY_REFUNDED";
      payment.refundedAmountMinor = preview.cumulativeRefundAmountMinor;
      payment.status = fullyRejected ? "REFUNDED" : "PARTIALLY_REFUNDED";
      payment.updatedUtc = record.refundedUtc;
      job.payment.status = payment.status;
      job.payment.refundedAmountMinor = payment.refundedAmountMinor;
      refundRequests.push({
        jobId: job.id, idempotencyKey: body.idempotencyKey,
        rejectedPageNumbers: preview.rejectedPageNumbers, confirmedSent: body.confirmedSent
      });
      refunds.push(record);
      refundsByIdempotencyKey.set(body.idempotencyKey, record);
      return json(route, {
        ...record, rejectedPageNumbers: preview.rejectedPageNumbers,
        cumulativeRefundAmountMinor: preview.cumulativeRefundAmountMinor,
        remainingPaidAmountMinor: preview.remainingPaidAmountMinor
      }, 201);
    }

    unhandledApi.push({ method, path });
    return json(route, { message: "Unhandled API request in isolated mixed-batch refund QA." }, 404);
  });
}
async function openRefundDialog(page, fileName) {
  const card = page.locator(".queue-batch-file-card").filter({ hasText: fileName });
  await card.waitFor({ state: "visible", timeout: 20000 });
  await card.getByRole("button", { name: "Review pages / refund", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Review pages and refund" });
  await dialog.waitFor({ state: "visible", timeout: 15000 });
  return dialog;
}
async function rejectAndRefundPages(page, fileName, pageNumbers, expectedRefund, expectedAcceptedPages) {
  const dialog = await openRefundDialog(page, fileName);
  const job = jobs.find(item => item.document.originalFileName === fileName);
  assert.ok(job, "QA job not found for " + fileName);
  const pageAllocations = allocations.get(job.id);
  assert.ok(pageAllocations, "Quote-locked page allocations not found for " + fileName);

  let expectedRunningRefundMinor = 0;
  for (let index = 0; index < pageNumbers.length; index += 1) {
    const number = pageNumbers[index];
    const row = dialog.locator(".page-refund-grid label").filter({ hasText: "Page " + number });
    const checkbox = row.locator("input");
    await checkbox.check();
    assert.equal(await checkbox.isChecked(), true, "Page " + number + " must be selected for " + fileName);

    expectedRunningRefundMinor += pageAllocations[number - 1];
    const expectedRemainingPages = job.pageCount - index - 1;
    const expectedRefundText = "Return ₹" + (expectedRunningRefundMinor / 100).toFixed(2);
    const expectedRemainingText = String(expectedRemainingPages) + " pages will remain";
    await page.waitForFunction(
      ({ refundText, remainingText }) => {
        const text = document.querySelector(".page-refund-estimate")?.textContent || "";
        return text.includes(refundText) && text.includes(remainingText);
      },
      { refundText: expectedRefundText, remainingText: expectedRemainingText },
      { timeout: 20000 }
    );
  }

  const estimate = dialog.locator(".page-refund-estimate");
  const estimateText = await estimate.innerText();
  assert.ok(estimateText.includes(String(expectedAcceptedPages) + " pages will remain"), estimateText);
  assert.ok(estimateText.includes("Return ₹" + expectedRefund.toFixed(2)), estimateText);
  assert.equal(await dialog.locator(".refund-confirm-line input").isChecked(), false);
  await dialog.locator(".refund-confirm-line input").check();
  await dialog.getByRole("button", { name: "Record refund and reject selected pages" }).click();
  await dialog.waitFor({ state: "detached", timeout: 20000 });
}

let server = null;
let browser = null;
let context = null;
let page = null;
let nativePreviousSecureSession = null;
let nativeSecureSessionCaptured = false;
let nativeSecureSessionOverwritten = false;
let nativeCredentialSessionRestored = false;
const pageErrors = [];
let desktopShellBooted = false;
let duplicateReplay = null;
let nativeConfigPath = null;
let nativeBrowserArguments = null;
let nativeRegistryRestore = null;

async function restoreNativeCredentialSession() {
  if (appMode !== "desktop-native" || !nativeSecureSessionCaptured || !nativeSecureSessionOverwritten || !page) return;
  await page.evaluate(async previousSession => {
    const bridge = window.__TAURI_INTERNALS__;
    if (!bridge || typeof bridge.invoke !== "function") {
      throw new Error("Native Tauri bridge disappeared before Credential Manager cleanup.");
    }
    await bridge.invoke("write_secure_session", { session: previousSession });
    const restoredSession = await bridge.invoke("read_secure_session");
    if (restoredSession !== previousSession) {
      throw new Error("Windows Credential Manager did not return the previously saved session after restoration.");
    }
    sessionStorage.removeItem("qrtoprint.auth.shop");
  }, nativePreviousSecureSession);
  nativeSecureSessionOverwritten = false;
  nativeCredentialSessionRestored = true;
}

try {
  if (appMode === "desktop-native" || !process.env.SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL) {
    server = startServer();
    await waitForServer(server);
  }

  if (appMode === "desktop-native") {
    // Attach Playwright to the actual WebView2 hosted by the Tauri Windows process.
    browser = await chromium.connectOverCDP(webViewDebugUrl);
    const contexts = browser.contexts();
    if (!contexts.length) throw new Error("Native WebView2 did not expose a browser context.");
    context = contexts[0];
    const expectedPort = new URL(baseUrl).port;
    for (let attempt = 0; attempt < 120; attempt += 1) {
      const pages = context.pages();
      const prebuiltNativeExecutable = Boolean(process.env.SMARTPRINT_MIXED_BATCH_REFUND_EXECUTABLE);
      page = pages.find(candidate => {
        try {
          if (prebuiltNativeExecutable) {
            const url = new URL(candidate.url());
            return url.protocol !== "about:" && url.protocol !== "devtools:";
          }
          return new URL(candidate.url()).port === expectedPort;
        } catch { return false; }
      }) || pages[0] || null;
      if (page) break;
      await new Promise(resolve => setTimeout(resolve, 500));
    }
    if (!page) throw new Error("Native WebView2 did not expose a SmartPrint window page over CDP.");
  } else {
    browser = await chromium.launch({ headless: process.env.SMARTPRINT_MIXED_BATCH_REFUND_HEADED !== "1" });
    context = await browser.newContext({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });
    page = await context.newPage();
  }

  page.on("pageerror", error => pageErrors.push(error.message));
  await installApiRoutes(page);

  const secureSession = JSON.stringify({
    tokenType: "Bearer", accessToken: SHOP_TOKEN,
    refreshToken: "mixed-batch-refund-qa-refresh",
    accessTokenExpiresUtc: "2099-01-01T00:00:00.000Z"
  });

  if (appMode === "desktop-native") {
    await waitForNativeApplicationPage(page, "initial Tauri navigation", { requireAuthenticatedShell: false });
    nativePreviousSecureSession = await page.evaluate(async () => {
      const bridge = window.__TAURI_INTERNALS__;
      if (!bridge || typeof bridge.invoke !== "function") {
        throw new Error("Native Tauri command bridge is not available inside WebView2.");
      }
      return await bridge.invoke("read_secure_session");
    });
    if (nativePreviousSecureSession !== null && typeof nativePreviousSecureSession !== "string") {
      throw new Error("Native secure-session command returned an unexpected value.");
    }
    nativeSecureSessionCaptured = true;
    await page.evaluate(async session => {
      const bridge = window.__TAURI_INTERNALS__;
      await bridge.invoke("write_secure_session", { session });
      const savedSession = await bridge.invoke("read_secure_session");
      if (savedSession !== session) {
        throw new Error("Windows Credential Manager did not return the synthetic QA session immediately after writing it.");
      }
      // The native command is verified above. Seed the same scoped browser key
      // used by readStoredTokens as a deterministic fallback for the already
      // running WebView; the real app shell and all shop UI still run in WebView2.
      sessionStorage.setItem("qrtoprint.auth.shop", session);
    }, secureSession);
    nativeSecureSessionOverwritten = true;
    await page.reload({ waitUntil: "domcontentloaded" });
    await waitForNativeApplicationPage(page, "reload after installing the QA secure session");
  } else {
    await page.addInitScript(({ sessionToken, desktopMode, serializedSession }) => {
      sessionStorage.setItem("qrtoprint.auth.shop", JSON.stringify({
        tokenType: "Bearer", accessToken: sessionToken, refreshToken: "mixed-batch-refund-qa-refresh",
        accessTokenExpiresUtc: "2099-01-01T00:00:00.000Z"
      }));
      if (desktopMode) {
        Object.defineProperty(window, "__TAURI_INTERNALS__", {
          configurable: true,
          value: {
            invoke: async command => {
              if (command === "read_secure_session") return serializedSession;
              if (command === "write_secure_session") return null;
              if (command === "get_local_agent_status") return {
                serviceReachable: true, localApiReachable: true, paired: true,
                agentDeviceId: "87000000-0000-4000-8000-000000000001",
                status: "PAIRED", apiBaseUrl: "http://127.0.0.1:17842", readiness: null
              };
              return null;
            }
          }
        });
      }
    }, {
      sessionToken: SHOP_TOKEN,
      desktopMode: appMode === "desktop",
      serializedSession: secureSession
    });
    await page.goto(baseUrl + "/shop", { waitUntil: "networkidle" });
  }

  await page.locator(".app-shell").waitFor({ state: "visible", timeout: 30000 });
  if (appMode === "desktop" || appMode === "desktop-native") {
    assert.equal(await page.locator("body").getAttribute("data-app-shell"), "desktop");
    desktopShellBooted = true;
  }

  const queueNav = page.locator(".sidebar .nav-item").filter({ hasText: "Print Queue" }).first();
  await queueNav.waitFor({ state: "visible", timeout: 20000 });
  await queueNav.click();
  await page.getByRole("heading", { name: "Print jobs", exact: true }).waitFor({ state: "visible", timeout: 20000 });
  const batchRow = page.locator(".queue-batch-summary-row").filter({ hasText: "QA mixed PDF JPG PNG refund batch" });
  await batchRow.waitFor({ state: "visible", timeout: 20000 });
  assert.match(await batchRow.innerText(), /3 files/i);
  await batchRow.getByRole("button", { name: "Files", exact: true }).click();
  await page.screenshot({ path: join(artifactDirectory, "mixed-batch-before-refunds-" + appMode + ".png"), fullPage: true });

  // A page decision can be undone before the refund is recorded.
  const pdfCard = page.locator(".queue-batch-file-card").filter({ hasText: "mixed-exam-set.pdf" });
  await pdfCard.getByRole("button", { name: "Review pages / refund", exact: true }).click();
  const pdfDialog = page.getByRole("dialog", { name: "Review pages and refund" });
  await pdfDialog.waitFor({ state: "visible" });
  const page2Check = pdfDialog.locator(".page-refund-grid label").filter({ hasText: "Page 2" }).locator("input");
  await page2Check.check();
  await page.waitForFunction(() => document.querySelector(".page-refund-estimate")?.textContent?.includes("10.00"));
  await page2Check.uncheck();
  await page.waitForFunction(() => {
    const estimate = document.querySelector(".page-refund-estimate")?.textContent || "";
    return estimate.includes("3 pages will remain") && estimate.includes("₹0.00");
  });
  assert.equal(refunds.length, 0, "Undo must not create a refund.");
  await pdfDialog.locator(".modal-actions").getByRole("button", { name: "Close", exact: true }).click();
  await pdfDialog.waitFor({ state: "detached" });

  // PDF pages 2+3 are rejected; page 1 stays accepted.
  await rejectAndRefundPages(page, "mixed-exam-set.pdf", [2, 3], 20, 1);
  // Both source image types are separate files and refund allocations.
  await rejectAndRefundPages(page, "candidate-photo.jpg", [1], 8, 0);
  await rejectAndRefundPages(page, "supporting-scan.png", [1], 12, 0);

  const pdfJob = jobs[0], jpgJob = jobs[1], pngJob = jobs[2];
  const pdfPayment = payments[0], jpgPayment = payments[1], pngPayment = payments[2];
  assert.deepEqual(JSON.parse(pdfJob.rejectedPageNumbersJson), [2, 3]);
  assert.equal(pdfJob.status, "PAID_AUTHORISED");
  assert.equal(pdfPayment.status, "PARTIALLY_REFUNDED");
  assert.equal(pdfPayment.refundedAmountMinor, 2000);
  assert.equal(pdfJob.pageCount - JSON.parse(pdfJob.rejectedPageNumbersJson).length, 1);
  assert.equal(jpgJob.status, "REFUNDED");
  assert.equal(jpgPayment.status, "REFUNDED");
  assert.equal(jpgPayment.refundedAmountMinor, 800);
  assert.deepEqual(JSON.parse(jpgJob.rejectedPageNumbersJson), [1]);
  assert.equal(pngJob.status, "REFUNDED");
  assert.equal(pngPayment.status, "REFUNDED");
  assert.equal(pngPayment.refundedAmountMinor, 1200);
  assert.deepEqual(JSON.parse(pngJob.rejectedPageNumbersJson), [1]);

  const totalRefundMinor = refunds.reduce((sum, item) => sum + item.amountMinor, 0);
  const originalPaidMinor = payments.reduce((sum, item) => sum + item.amountMinor, 0);
  const remainingPaidMinor = payments.reduce((sum, item) => sum + item.amountMinor - item.refundedAmountMinor, 0);
  assert.equal(refunds.length, 3);
  assert.deepEqual(refunds.map(item => item.amountMinor).sort((a, b) => a - b), [800, 1200, 2000]);
  assert.equal(totalRefundMinor, 4000);
  assert.equal(originalPaidMinor, 5000);
  assert.equal(remainingPaidMinor, 1000);
  assert.equal(totalRefundMinor + remainingPaidMinor, originalPaidMinor);

  const firstPdfRequest = refundRequests.find(item => item.jobId === pdfJob.id);
  assert.ok(firstPdfRequest?.idempotencyKey);
  duplicateReplay = await page.evaluate(async ({ jobId, token, requestBody }) => {
    const response = await fetch("/api/v1/shop/print-jobs/" + jobId + "/refund", {
      method: "POST",
      headers: { "Content-Type": "application/json", "Authorization": "Bearer " + token },
      body: JSON.stringify(requestBody)
    });
    return { status: response.status, body: await response.json() };
  }, {
    jobId: pdfJob.id,
    token: SHOP_TOKEN,
    requestBody: {
      refundMethod: "CASH",
      refundReference: null,
      reason: "Customer rejected selected pages after shop preview.",
      rejectedPageNumbers: [2, 3],
      confirmedSent: true,
      idempotencyKey: firstPdfRequest.idempotencyKey
    }
  });
  assert.equal(duplicateReplay.status, 200);
  assert.equal(duplicateReplay.body.duplicateRequest, true);
  assert.equal(refunds.length, 3, "Idempotent retry must not add a refund ledger row.");

  const paymentsNav = page.locator(".sidebar .nav-item").filter({ hasText: "Payments" }).first();
  await paymentsNav.click();
  await page.getByRole("heading", { name: "Refund audit log" }).waitFor({ state: "visible", timeout: 20000 });
  const audit = page.locator(".refund-audit-panel");
  assert.equal(await audit.locator(".refund-record").count(), 3);
  const auditText = await audit.innerText();
  for (const fileName of ["mixed-exam-set.pdf", "candidate-photo.jpg", "supporting-scan.png"]) {
    assert.ok(auditText.includes(fileName), "Refund audit missing " + fileName);
  }
  await page.screenshot({ path: join(artifactDirectory, "mixed-batch-refund-audit-" + appMode + ".png"), fullPage: true });

  const forbiddenActions = apiRequests.filter(item =>
    item.method === "POST" && /\/(approve|dispatch|confirm-output|verify|print)$/.test(item.path)
  );
  assert.deepEqual(forbiddenActions, []);
  assert.deepEqual(unhandledApi, [], "Unexpected API calls: " + JSON.stringify(unhandledApi));
  assert.deepEqual(pageErrors, [], "Uncaught UI errors: " + pageErrors.join("; "));

  if (appMode === "desktop-native") {
    await restoreNativeCredentialSession();
    assert.equal(nativeCredentialSessionRestored, true, "The prior Windows Credential Manager session must be restored.");
  }

  const report = {
    caseId: "BATCH-REFUND-MIXED-001",
    scenario: "Shop owner reviews one paid multi-file, multi-type batch; rejects selected pages from multiple files; records complete refunds for every rejected page; leaves accepted pages paid and eligible.",
    testedClient: appMode === "web"
      ? "Web shop portal"
      : appMode === "desktop-native"
        ? "Actual Windows Tauri/WebView2 application automated over localhost CDP with the real Credential Manager bridge"
        : "Windows desktop entry/shared shop UI with a Tauri secure-session and Agent IPC contract test double",
    appMode,
    desktopShellBooted,
    nativeWebView2Automated: appMode === "desktop-native",
    nativeCredentialSessionRestored: appMode === "desktop-native" ? nativeCredentialSessionRestored : null,
    batchId: BATCH_ID,
    files: [
      { name: "mixed-exam-set.pdf", mediaType: "application/pdf", totalPages: 3, rejectedPages: [2, 3], acceptedPages: 1, paidMinor: 3000, refundMinor: 2000 },
      { name: "candidate-photo.jpg", mediaType: "image/jpeg", totalPages: 1, rejectedPages: [1], acceptedPages: 0, paidMinor: 800, refundMinor: 800 },
      { name: "supporting-scan.png", mediaType: "image/png", totalPages: 1, rejectedPages: [1], acceptedPages: 0, paidMinor: 1200, refundMinor: 1200 }
    ],
    financials: {
      currency: "INR", originalPaidMinor, rejectedPagesRefundedMinor: totalRefundMinor,
      acceptedPagesRetainedMinor: remainingPaidMinor, refundLedgerEntries: refunds.length,
      reconcilesExactly: totalRefundMinor + remainingPaidMinor === originalPaidMinor
    },
    pageUndoBeforeRecord: true,
    idempotentRetryReturnsExistingRefund: duplicateReplay.body.duplicateRequest === true,
    acceptedPdfStatus: pdfJob.status,
    acceptedPdfRejectedPages: JSON.parse(pdfJob.rejectedPageNumbersJson),
    acceptedPdfPagesRemaining: pdfJob.pageCount - JSON.parse(pdfJob.rejectedPageNumbersJson).length,
    fullyRejectedImageStatuses: [jpgJob.status, pngJob.status],
    physicalPrinterUsed: false,
    livePaymentUsed: false,
    externalRefundTransferPerformed: false,
    refundLedgerIsInMemory: true,
    apiRequestCount: apiRequests.length
  };
  writeFileSync(join(artifactDirectory, "mixed-batch-refund-" + appMode + ".json"), JSON.stringify(report, null, 2), "utf8");
  console.log(JSON.stringify(report, null, 2));
  if (appMode !== "desktop-native") await context.close();
} catch (error) {
  if (page) {
    try {
      await page.screenshot({ path: join(artifactDirectory, "mixed-batch-refund-failure-" + appMode + ".png"), fullPage: true });
    } catch {}
  }
  writeFileSync(join(artifactDirectory, "mixed-batch-refund-failure-" + appMode + ".json"), JSON.stringify({
    appMode, message: error instanceof Error ? error.message : String(error), pageErrors, apiRequests, unhandledApi
  }, null, 2), "utf8");
  throw error;
} finally {
  let restoreError = null;
  if (appMode === "desktop-native" && nativeSecureSessionOverwritten && page) {
    try {
      await restoreNativeCredentialSession();
    } catch (error) {
      restoreError = error instanceof Error ? error : new Error(String(error));
      writeFileSync(
        join(artifactDirectory, "mixed-batch-refund-native-session-restore-failure.txt"),
        restoreError.message,
        "utf8"
      );
    }
  }
  if (browser) await browser.close();
  stopServer(server);
  if (nativeConfigPath) {
    try {
      unlinkSync(nativeConfigPath);
      nativeConfigPath = null;
    } catch (error) {
      restoreError = restoreError || (error instanceof Error ? error : new Error(String(error)));
      writeFileSync(
        join(artifactDirectory, "mixed-batch-refund-native-config-cleanup-failure.txt"),
        restoreError.message,
        "utf8"
      );
    }
  }
  if (restoreError) throw new Error("Could not clean up the temporary native QA config: " + restoreError.message);
}
