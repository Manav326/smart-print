import assert from "node:assert/strict";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn, spawnSync } from "node:child_process";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
const capabilityPath = process.env.SMARTPRINT_EPSON_L360_CAPABILITIES_PATH;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR must point to the installed Playwright package.");
if (!capabilityPath) throw new Error("SMARTPRINT_EPSON_L360_CAPABILITIES_PATH must point to the live Epson driver capability artifact.");

const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const capabilities = JSON.parse(readFileSync(capabilityPath, "utf8"));
assert.match(capabilities.driverName ?? "", /EPSON.*L360|L360.*EPSON/i,
  "Shop-owner UI fixture must use the driver name reported by the installed Epson L360 package.");
const artifactDirectory = process.env.SMARTPRINT_EPSON_L360_UI_ARTIFACT_DIR ?? "artifacts/epson-l360-shop-owner";
const printManifestPath = process.env.SMARTPRINT_EPSON_L360_PRINT_MANIFEST_PATH ?? "artifacts/epson-l360/shop-owner-print-workflow.json";
const baseUrl = process.env.SMARTPRINT_EPSON_L360_UI_BASE_URL ?? "http://127.0.0.1:4175";
mkdirSync(artifactDirectory, { recursive: true });
mkdirSync(join(printManifestPath, ".."), { recursive: true });

const shopPayload = Buffer.from(JSON.stringify({
  sub: "epson-l360-shop-owner-qa",
  tenant_id: "epson-l360-shop-owner-qa-tenant"
})).toString("base64url");
const shopToken = `e30.${shopPayload}.epson-l360-browser-qa`;

const printer = {
  id: "30000000-0000-4000-8000-000000000360",
  displayName: "Epson L360 Series",
  manufacturer: "Epson",
  model: "L360",
  connectionType: "WindowsQueue",
  outputMode: "PHYSICAL",
  connectionAddress: null,
  queueName: capabilities.queueName ?? "SmartPrint CI Epson L360",
  capabilitiesJson: JSON.stringify(capabilities),
  status: "Ready",
  isActive: true,
  isDefault: false,
  agentDeviceId: "50000000-0000-4000-8000-000000000360",
  lastSeenUtc: new Date().toISOString()
};

const profile = {
  id: "epson-l360-shop-owner-qa",
  slug: "epson-l360-shop-owner-qa",
  businessName: "Epson L360 QA Print Shop",
  status: "Active",
  ownerName: "Shop Owner",
  ownerEmail: "epson-l360-qa@example.invalid",
  displayName: "Epson L360 QA Print Shop",
  logoUrl: null,
  accentColor: "#7662E8",
  currency: "INR",
  timeZone: "Asia/Kolkata",
  whiteLabelEnabled: false
};

const shopNow = new Date().toISOString();
function documentJob(id, jobNumber, pagesPerSheet) {
  return {
    id, jobNumber, status: "LOCAL_QUEUE_READY", pageCount: 8, copies: 1,
    colourMode: "Colour", paperSize: "A4", isDuplex: false, orientation: "PORTRAIT",
    pagesPerSheet, batchId: null, batchName: null, batchGroupName: "N-up driver tests",
    currency: "INR", quotedAmountMinor: 1200, createdUtc: shopNow,
    localQueueReceivedUtc: shopNow, spoolerJobId: null, spoolerJobName: null,
    printerDeviceId: null, printerName: null, queueName: null, outputMode: "PHYSICAL",
    storageMode: "CLOUD", origin: "CUSTOMER_QR", printKind: "DOCUMENT",
    privatePrinting: false, documentAgentDeviceId: null,
    document: { originalFileName: `Epson L360 ${pagesPerSheet}-up sample.pdf`, mediaType: "application/pdf", sizeBytes: 24000 }
  };
}
function photoJob(id, jobNumber, photoSizeCode, photoPaperFinish, photoOutputLayout, mediaType) {
  const photoCount = photoSizeCode === "4x6" ? 1 : 4;
  return {
    id, jobNumber, status: "LOCAL_QUEUE_READY", pageCount: 1, copies: 1,
    colourMode: "Colour", paperSize: "A4", isDuplex: false, orientation: "PORTRAIT",
    pagesPerSheet: 1, batchId: null, batchName: null, batchGroupName: "Photo paper driver tests",
    currency: "INR", quotedAmountMinor: 900, createdUtc: shopNow,
    localQueueReceivedUtc: shopNow, spoolerJobId: null, spoolerJobName: null,
    printerDeviceId: null, printerName: null, queueName: null, outputMode: "PHYSICAL",
    storageMode: "CLOUD", origin: "CUSTOMER_QR", printKind: "PHOTO",
    photoSizeCode, photoPaperFinish, photoOutputLayout, photoCount,
    photosPerPage: photoCount, photoSourceAvailable: false, privatePrinting: false,
    documentAgentDeviceId: null,
    document: { originalFileName: `Epson L360 ${photoSizeCode} ${photoPaperFinish} photo.pdf`, mediaType, sizeBytes: 12000 }
  };
}
const shopJobs = [
  documentJob("60000000-0000-4000-8000-000000000002", "SP-EPS-2UP", 2),
  documentJob("60000000-0000-4000-8000-000000000004", "SP-EPS-4UP", 4),
  photoJob("60000000-0000-4000-8000-000000000035", "SP-EPS-35X45", "35x45", "GLOSSY", "A4_SHEET", "application/pdf"),
  photoJob("60000000-0000-4000-8000-000000000025", "SP-EPS-25X35", "25x35", "MATTE", "A4_SHEET", "application/pdf"),
  photoJob("60000000-0000-4000-8000-000000000022", "SP-EPS-2X2", "2x2", "GLOSSY", "A4_SHEET", "application/pdf"),
  photoJob("60000000-0000-4000-8000-000000000046", "SP-EPS-4X6", "4x6", "GLOSSY", "PHOTO_PAPER", "image/jpeg")
];
const assignmentEvents = [];
const dispatchEvents = [];

function startServer() {
  const packageManager = process.env.SMARTPRINT_PNPM_COMMAND ?? "pnpm";
  const args = ["--filter", "@smart-print/portal", "dev", "--host", "127.0.0.1", "--port", "4175", "--strictPort"];
  const options = { stdio: "inherit", env: process.env, windowsHide: true, detached: process.platform !== "win32" };
  const child = process.platform === "win32"
    ? spawn(process.env.ComSpec ?? "cmd.exe", ["/d", "/c", [packageManager, ...args].join(" ")], options)
    : spawn(packageManager, args, options);
  child.spawnError = null;
  child.on("error", error => { child.spawnError = error; });
  return child;
}

function stopServer(child) {
  if (!child?.pid) return;
  if (process.platform === "win32") {
    spawnSync("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], {
      stdio: "ignore",
      windowsHide: true
    });
  } else {
    try { process.kill(-child.pid, "SIGTERM"); }
    catch { if (child.exitCode === null) child.kill("SIGTERM"); }
  }
}

async function waitForServer(child) {
  for (let attempt = 0; attempt < 90; attempt += 1) {
    if (child.spawnError) throw new Error("Epson shop-owner Vite server failed: " + child.spawnError.message);
    if (child.exitCode !== null) throw new Error("Epson shop-owner Vite server exited with code " + child.exitCode);
    try {
      const response = await fetch(baseUrl + "/");
      if (response.ok) return;
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error("Epson shop-owner Vite server did not become ready.");
}

async function createShopOwnerPage(browser, viewport, label) {
  const context = await browser.newContext({ viewport });
  await context.addInitScript(({ token }) => {
    sessionStorage.setItem("qrtoprint.auth.shop", JSON.stringify({
      tokenType: "Bearer",
      accessToken: token,
      refreshToken: "epson-l360-shop-owner-refresh",
      accessTokenExpiresUtc: "2099-01-01T00:00:00.000Z"
    }));
  }, { token: shopToken });

  const page = await context.newPage();
  const apiRequests = [];
  await page.route("**/api/v1/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    apiRequests.push({ method: request.method(), path });

    let value;
    if (path === "/api/v1/shop/profile") value = profile;
    else if (path === "/api/v1/shop/printers") value = [printer];
    else if (path === "/api/v1/shop/agent-devices") value = [{
      id: printer.agentDeviceId,
      deviceName: "Epson Driver QA Runner",
      status: "Online",
      createdUtc: shopNow,
      lastSeenUtc: new Date().toISOString(),
      revokedUtc: null,
      printerCount: 1,
      localEndpoints: []
    }];
    else if (path === "/api/v1/shop/print-jobs" && request.method() === "GET") value = shopJobs;
    else if (/^\/api\/v1\/shop\/print-jobs\/[^/]+\/printer$/.test(path) && request.method() === "PATCH") {
      const jobId = path.split("/")[5];
      const job = shopJobs.find(item => item.id === jobId);
      const body = request.postDataJSON();
      if (!job || body.printerDeviceId !== printer.id) {
        await route.fulfill({ status: 409, contentType: "application/json", body: JSON.stringify({ message: "Epson L360 assignment was not selected." }) });
        return;
      }
      const assignedJobs = job.batchId ? shopJobs.filter(item => item.batchId === job.batchId && item.status !== "REJECTED" && item.status !== "REFUNDED") : [job];
      for (const assigned of assignedJobs) {
        assigned.printerDeviceId = printer.id;
        assigned.printerName = printer.displayName;
        assigned.queueName = printer.queueName;
        assigned.outputMode = printer.outputMode;
        assigned.agentDeviceId = printer.agentDeviceId;
      }
      assignmentEvents.push({ jobId, printerDeviceId: printer.id, printerName: printer.displayName, queueName: printer.queueName });
      value = {
        id: job.id, status: job.status, batchId: job.batchId ?? null,
        printerDeviceId: printer.id, printerName: printer.displayName,
        queueName: printer.queueName, agentDeviceId: printer.agentDeviceId,
        assignedJobCount: assignedJobs.length, updatedUtc: new Date().toISOString()
      };
    }
    else if (/^\/api\/v1\/shop\/print-jobs\/[^/]+\/dispatch$/.test(path) && request.method() === "POST") {
      const jobId = path.split("/")[5];
      const job = shopJobs.find(item => item.id === jobId);
      if (!job || job.printerDeviceId !== printer.id) {
        await route.fulfill({ status: 409, contentType: "application/json", body: JSON.stringify({ message: "Dispatch blocked: job is not assigned to Epson L360." }) });
        return;
      }
      const spoolerJobName = `SmartPrint-${job.jobNumber}-EpsonL360-QA`;
      dispatchEvents.push({ jobId, jobNumber: job.jobNumber, printerDeviceId: job.printerDeviceId, queueName: job.queueName, spoolerJobName });
      job.status = "PRINT_AUTHORISED";
      job.spoolerJobName = spoolerJobName;
      value = {
        id: job.id, jobNumber: job.jobNumber, status: job.status,
        printerName: job.printerName, queueName: job.queueName,
        agentId: printer.agentDeviceId, spoolerJobName
      };
    }
    else if (path.endsWith("/qr-stations") || path.endsWith("/payments") || path.endsWith("/refunds")) value = [];
    else if (path.endsWith("/pricing")) value = null;
    else if (path.endsWith("/payment-settings")) value = {
      upiEnabled: false, cashEnabled: true, instantPaymentEnabled: false,
      updatedUtc: new Date().toISOString()
    };
    else if (path.endsWith("/subscription")) value = null;
    else {
      await route.fulfill({
        status: 404,
        contentType: "application/json",
        body: JSON.stringify({ message: "Unexpected API request in isolated Epson L360 shop-owner QA: " + path })
      });
      return;
    }

    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(value)
    });
  });

  await page.goto(baseUrl + "/shop/printers", { waitUntil: "domcontentloaded" });
  await page.locator(".printer-capability-panel").waitFor({ state: "visible", timeout: 20000 });
  assert.ok(apiRequests.some(request => request.path === "/api/v1/shop/printers"),
    "Shop owner printer details must be loaded from the printers API fixture.");
  return { context, page, label };
}

async function assertEpsonDetails(page, label) {
  const card = page.locator(".printer-card").filter({ hasText: "Epson L360 Series" });
  await card.waitFor({ state: "visible" });
  assert.equal(await card.locator(".printer-capability-panel").count(), 1);

  const tiles = card.locator(".printer-capability-tile");
  const nupTile = tiles.filter({ hasText: "N-up" });
  const photoTile = tiles.filter({ hasText: "Photo media" });
  assert.match(await nupTile.innerText(), /2-up/i,
    "The actual Epson driver N-up capability should appear in the shop-owner printer details.");
  assert.match(await nupTile.innerText(), /4-up/i,
    "The actual Epson driver 4-up capability should appear in the shop-owner printer details.");
  assert.match(await photoTile.innerText(), /Glossy/i,
    "The Epson Glossy media should be visible in printer details.");
  assert.match(await photoTile.innerText(), /Matte/i,
    "The Epson Matte media should be visible in printer details.");

  const details = card.locator(".printer-diagnostics");
  const summary = details.locator("summary");
  if (!(await details.getAttribute("open"))) await summary.click();
  const driverIdentity = await card.locator(".printer-driver-identity").innerText();
  assert.match(driverIdentity, /EPSON.*L360|L360.*EPSON/i);
  const facts = await details.locator(".printer-capability-facts").innerText();
  assert.match(facts, /Photo media types/i);
  assert.match(facts, /Print Schema media/i);
  assert.match(facts, /gloss/i);
  assert.match(facts, /matt/i);

  const bodySize = await page.evaluate(() => ({
    viewport: window.innerWidth,
    document: document.documentElement.scrollWidth,
    body: document.body.scrollWidth
  }));
  assert.ok(bodySize.document <= bodySize.viewport + 1 && bodySize.body <= bodySize.viewport + 1,
    `Printer details overflow at ${label}: ${JSON.stringify(bodySize)}`);
}

async function exerciseShopOwnerPrintQueue(page) {
  await page.getByRole("button", { name: "Print Queue", exact: true }).click();
  await page.locator(".queue-jobs-table").waitFor({ state: "visible", timeout: 15000 });
  await page.locator(".queue-auto-toggle input").uncheck().catch(() => {});

  for (const job of shopJobs) {
    let summaryRow = page.locator(".queue-batch-summary-row").filter({ hasText: job.jobNumber });
    await summaryRow.waitFor({ state: "visible", timeout: 15000 });
    const assignSelect = summaryRow.locator('select[aria-label="Assign one printer for all files in batch"]');
    await assignSelect.selectOption(printer.id);
    await page.waitForFunction(({ jobNumber, printerName }) => {
      const row = Array.from(document.querySelectorAll(".queue-batch-summary-row"))
        .find(element => element.textContent?.includes(jobNumber));
      return row?.querySelector(".queue-printer-cell strong")?.textContent?.includes(printerName) ?? false;
    }, { jobNumber: job.jobNumber, printerName: printer.displayName });

    summaryRow = page.locator(".queue-batch-summary-row").filter({ hasText: job.jobNumber });
    if (await summaryRow.getByRole("button", { name: "Files", exact: true }).count()) {
      await summaryRow.getByRole("button", { name: "Files", exact: true }).click();
    }
    const fileCard = page.locator(".queue-batch-file-card").filter({ hasText: job.document.originalFileName });
    await fileCard.waitFor({ state: "visible", timeout: 15000 });
    const details = await fileCard.innerText();
    if (job.printKind === "DOCUMENT") {
      assert.match(details, new RegExp(job.pagesPerSheet + " per sheet", "i"),
        `Shop-owner queue did not show the requested ${job.pagesPerSheet}-up layout for ${job.jobNumber}.`);
    } else {
      assert.match(details, new RegExp(job.photoSizeCode.replace("x", "x"), "i"));
      assert.match(details, new RegExp(job.photoPaperFinish === "GLOSSY" ? "Glossy" : "Matte", "i"));
      assert.match(details, new RegExp(job.photoOutputLayout === "PHOTO_PAPER" ? "individual photo paper" : "A4 photo paper", "i"));
    }

    const startButton = fileCard.getByRole("button", { name: "Start printing", exact: true });
    await startButton.waitFor({ state: "visible", timeout: 10000 });
    await startButton.click();
    const dialog = page.getByRole("dialog").last();
    await dialog.waitFor({ state: "visible", timeout: 5000 });
    const dispatchResponse = page.waitForResponse(response =>
      response.request().method() === "POST" &&
      new URL(response.url()).pathname.endsWith(`/${job.id}/dispatch`));
    await dialog.getByRole("button", { name: "Start printing", exact: true }).click();
    const response = await dispatchResponse;
    assert.equal(response.status(), 200, `Shop owner dispatch failed for ${job.jobNumber}.`);
  }

  assert.equal(assignmentEvents.length, shopJobs.length,
    "Shop owner must explicitly assign every test job to Epson L360.");
  assert.equal(dispatchEvents.length, shopJobs.length,
    "Shop owner must dispatch every N-up/photo test job.");
  assert.ok(dispatchEvents.every(event => event.printerDeviceId === printer.id && event.queueName === printer.queueName),
    "Every test job must dispatch to the same Epson L360 queue selected in printer details.");

  const documentJobs = shopJobs.filter(job => job.printKind === "DOCUMENT");
  const photoJobs = shopJobs.filter(job => job.printKind === "PHOTO");
  assert.deepEqual(documentJobs.map(job => job.pagesPerSheet).sort(), [2, 4]);
  assert.deepEqual(photoJobs.map(job => job.photoSizeCode).sort(), ["2x2", "25x35", "35x45", "4x6"]);

  const manifest = {
    schemaVersion: 1,
    source: "SHOP_OWNER_BROWSER_WORKFLOW",
    createdUtc: new Date().toISOString(),
    shopId: profile.id,
    shopDisplayName: profile.displayName,
    printerDeviceId: printer.id,
    printerName: printer.displayName,
    driverName: capabilities.driverName,
    queueName: printer.queueName,
    physicalPrinterConnected: false,
    assignmentEvents,
    dispatchEvents,
    documentJobs: documentJobs.map(job => ({
      id: job.id, jobNumber: job.jobNumber, fileName: job.document.originalFileName,
      mediaType: job.document.mediaType, pageCount: job.pageCount,
      copies: job.copies, colourMode: job.colourMode, paperSize: job.paperSize,
      orientation: job.orientation, isDuplex: job.isDuplex, pagesPerSheet: job.pagesPerSheet
    })),
    photoJobs: photoJobs.map(job => ({
      id: job.id, jobNumber: job.jobNumber, fileName: job.document.originalFileName,
      mediaType: job.document.mediaType, pageCount: job.pageCount, copies: job.copies,
      photoSizeCode: job.photoSizeCode, photoPaperFinish: job.photoPaperFinish,
      photoOutputLayout: job.photoOutputLayout, photoCount: job.photoCount,
      photosPerPage: job.photosPerPage, paperSize: job.paperSize
    }))
  };
  writeFileSync(printManifestPath, JSON.stringify(manifest, null, 2), "utf8");
  await page.screenshot({
    path: join(artifactDirectory, "epson-l360-shop-owner-print-queue-after-dispatch.png"),
    fullPage: true,
    animations: "disabled"
  });
  console.log("[EPSON-L360-SHOP-OWNER] All N-up and photo jobs assigned to Epson L360 and dispatched; manifest saved at " + printManifestPath);
}

const server = startServer();
let browser;
try {
  await waitForServer(server);
  browser = await chromium.launch({ headless: true });

  const desktop = await createShopOwnerPage(browser, { width: 1440, height: 960 }, "desktop");
  try {
    await assertEpsonDetails(desktop.page, "desktop");
    await desktop.page.screenshot({
      path: join(artifactDirectory, "epson-l360-shop-owner-printer-details-desktop-light.png"),
      fullPage: true,
      animations: "disabled"
    });
    await exerciseShopOwnerPrintQueue(desktop.page);
    await desktop.page.getByRole("button", { name: "Printers", exact: true }).click();
    await desktop.page.locator(".printer-capability-panel").waitFor({ state: "visible" });
    await desktop.page.getByRole("button", { name: "Switch to dark theme" }).click();
    await desktop.page.waitForFunction(() => document.documentElement.dataset.theme === "dark");
    await assertEpsonDetails(desktop.page, "desktop-dark");
    await desktop.page.screenshot({
      path: join(artifactDirectory, "epson-l360-shop-owner-printer-details-desktop-dark.png"),
      fullPage: true,
      animations: "disabled"
    });
  } finally {
    await desktop.context.close();
  }

  const mobile = await createShopOwnerPage(browser, { width: 390, height: 844 }, "mobile");
  try {
    await assertEpsonDetails(mobile.page, "mobile");
    await mobile.page.screenshot({
      path: join(artifactDirectory, "epson-l360-shop-owner-printer-details-mobile-light.png"),
      fullPage: true,
      animations: "disabled"
    });
  } finally {
    await mobile.context.close();
  }

  console.log("[EPSON-L360-SHOP-OWNER] PASS: The shop owner reviewed actual Epson L360 capabilities, saw N-up and photo stock requirements, assigned the same Epson queue and dispatched all 6 test jobs. Manifest: " + printManifestPath);
} finally {
  if (browser) await browser.close();
  stopServer(server);
}
