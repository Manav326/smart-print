import assert from "node:assert/strict";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn, spawnSync } from "node:child_process";

const appMode = (process.env.SMARTPRINT_MIXED_BATCH_REFUND_APP || "web").toLowerCase();
if (!["web", "desktop"].includes(appMode)) throw new Error("App mode must be web or desktop.");
const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR is required.");
const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const artifactDirectory = process.env.SMARTPRINT_MIXED_BATCH_REFUND_ARTIFACT_DIR ||
  join("artifacts", "mixed-batch-refund", appMode);
mkdirSync(artifactDirectory, { recursive: true });

const port = appMode === "desktop" ? "1420" : "4174";
const baseUrl = process.env.SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL || "http://127.0.0.1:" + port;
const serverArgs = appMode === "desktop"
  ? ["--filter", "@smart-print/desktop", "dev", "--host", "127.0.0.1", "--port", port, "--strictPort"]
  : ["--filter", "@smart-print/portal", "dev", "--host", "127.0.0.1", "--port", port, "--strictPort"];

function startServer() {
  const options = { stdio: "inherit", env: { ...process.env, VITE_API_BASE_URL: "" }, windowsHide: true, detached: process.platform !== "win32" };
  const child = process.platform === "win32"
    ? spawn(process.env.ComSpec || "cmd.exe", ["/d", "/c", ["pnpm", ...serverArgs].join(" ")], options)
    : spawn("pnpm", serverArgs, options);
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
  for (let attempt = 0; attempt < 90; attempt += 1) {
    if (child?.spawnError) throw new Error("QA server failed to start: " + child.spawnError.message);
    if (child?.exitCode !== null && child?.exitCode !== undefined) throw new Error("QA server exited with code " + child.exitCode);
    try { if ((await fetch(baseUrl + "/shop")).ok) return; } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error("QA server did not become ready within 90 seconds.");
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
  for (const number of pageNumbers) {
    const row = dialog.locator(".page-refund-grid label").filter({ hasText: "Page " + number });
    await row.locator("input").check();
  }
  const estimate = dialog.locator(".page-refund-estimate");
  await page.waitForFunction(
    amount => document.querySelector(".page-refund-estimate")?.textContent?.includes(amount),
    expectedRefund.toFixed(2),
    { timeout: 15000 }
  );
  const estimateText = await estimate.innerText();
  assert.ok(estimateText.includes(String(expectedAcceptedPages) + " pages will remain"), estimateText);
  assert.ok(estimateText.includes(expectedRefund.toFixed(2)), estimateText);
  assert.equal(await dialog.locator(".refund-confirm-line input").isChecked(), false);
  await dialog.locator(".refund-confirm-line input").check();
  await dialog.getByRole("button", { name: "Record refund and reject selected pages" }).click();
  await dialog.waitFor({ state: "detached", timeout: 20000 });
}

let server = null;
let browser = null;
let page = null;
const pageErrors = [];
let desktopShellBooted = false;
let duplicateReplay = null;
try {
  if (!process.env.SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL) {
    server = startServer();
    await waitForServer(server);
  }
  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });
  page = await context.newPage();
  page.on("pageerror", error => pageErrors.push(error.message));
  await installApiRoutes(page);

  await page.addInitScript(({ sessionToken, desktopMode, secureSession }) => {
    sessionStorage.setItem("qrtoprint.auth.shop", JSON.stringify({
      tokenType: "Bearer", accessToken: sessionToken, refreshToken: "mixed-batch-refund-qa-refresh",
      accessTokenExpiresUtc: "2099-01-01T00:00:00.000Z"
    }));
    if (desktopMode) {
      Object.defineProperty(window, "__TAURI_INTERNALS__", {
        configurable: true,
        value: {
          invoke: async command => {
            if (command === "read_secure_session") return secureSession;
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
    secureSession: JSON.stringify({
      tokenType: "Bearer", accessToken: SHOP_TOKEN,
      refreshToken: "mixed-batch-refund-qa-refresh",
      accessTokenExpiresUtc: "2099-01-01T00:00:00.000Z"
    })
  });

  await page.goto(baseUrl + "/shop", { waitUntil: "networkidle" });
  await page.locator(".app-shell").waitFor({ state: "visible", timeout: 30000 });
  if (appMode === "desktop") {
    assert.equal(await page.locator("body").getAttribute("data-app-shell"), "desktop");
    desktopShellBooted = true;
  }

  const queueNav = page.locator(".sidebar .nav-item").filter({ hasText: "Print Queue" }).first();
  await queueNav.waitFor({ state: "visible", timeout: 20000 });
  await queueNav.click();
  await page.getByRole("heading", { name: "Customer print jobs" }).waitFor({ state: "visible", timeout: 20000 });
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
  await page.waitForFunction(() => document.querySelector(".page-refund-estimate")?.textContent?.includes("Select at least one additional page"));
  assert.equal(refunds.length, 0, "Undo must not create a refund.");
  await pdfDialog.getByRole("button", { name: "Close" }).click();
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

  const report = {
    caseId: "BATCH-REFUND-MIXED-001",
    scenario: "Shop owner reviews one paid multi-file, multi-type batch; rejects selected pages from multiple files; records complete refunds for every rejected page; leaves accepted pages paid and eligible.",
    testedClient: appMode === "web"
      ? "Web shop portal"
      : "Windows desktop entry/shared shop UI with Tauri secure-session and Agent IPC contract test double",
    appMode,
    desktopShellBooted,
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
  await context.close();
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
  if (browser) await browser.close();
  stopServer(server);
}
