import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn, spawnSync } from "node:child_process";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR must point to the installed Playwright package directory.");
const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const artifactDirectory = process.env.SMARTPRINT_DOCUMENT_PREVIEW_ARTIFACT_DIR ?? "artifacts/document-preview-browser";
const baseUrl = process.env.SMARTPRINT_DOCUMENT_PREVIEW_BASE_URL ?? "http://127.0.0.1:4175";
const headless = process.env.SMARTPRINT_DOCUMENT_PREVIEW_HEADED !== "1";
mkdirSync(artifactDirectory, { recursive: true });

const stationCode = "BATCH-QA";
const sessionToken = "batch-upload-browser-qa-session-token";

function startServer() {
  const packageManager = process.env.SMARTPRINT_PNPM_COMMAND ?? "pnpm";
  const args = ["--filter", "@smart-print/portal", "dev", "--host", "127.0.0.1", "--port", "4175", "--strictPort"];
  const options = { stdio: "inherit", env: process.env, windowsHide: true, detached: process.platform !== "win32" };
  if (process.platform === "win32") {
    const child = spawn(process.env.ComSpec ?? "cmd.exe", ["/d", "/c", [packageManager, ...args].join(" ")], options);
    child.spawnError = null;
    child.on("error", error => { child.spawnError = error; });
    return child;
  }
  const child = spawn(packageManager, args, options);
  child.spawnError = null;
  child.on("error", error => { child.spawnError = error; });
  return child;
}
function stopServer(child) {
  if (!child?.pid) return;
  if (process.platform === "win32") spawnSync("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore", windowsHide: true });
  else { try { process.kill(-child.pid, "SIGTERM"); } catch { if (child.exitCode === null) child.kill("SIGTERM"); } }
}
async function waitForServer(child) {
  for (let attempt = 0; attempt < 90; attempt += 1) {
    if (child?.spawnError) throw new Error("Vite server failed to start: " + child.spawnError.message);
    if (child?.exitCode !== null && child?.exitCode !== undefined) throw new Error("Vite server exited with code " + child.exitCode);
    try { const response = await fetch(baseUrl + "/"); if (response.ok) return; } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error("Vite server did not become ready within 90 seconds.");
}

function pdfFixture(label) {
  const safeLabel = label.replace(/[()\\]/g, "").slice(0, 70);
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
    null,
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
  ];
  const stream = "BT /F1 22 Tf 48 760 Td (" + safeLabel + ") Tj ET\n";
  objects[3] = "<< /Length " + Buffer.byteLength(stream, "ascii") + " >>\nstream\n" + stream + "endstream";
  let body = "%PDF-1.4\n";
  const offsets = [0];
  objects.forEach((object, index) => {
    offsets.push(Buffer.byteLength(body, "ascii"));
    body += (index + 1) + " 0 obj\n" + object + "\nendobj\n";
  });
  const xrefOffset = Buffer.byteLength(body, "ascii");
  body += "xref\n0 6\n0000000000 65535 f \n";
  offsets.slice(1).forEach(offset => { body += String(offset).padStart(10, "0") + " 00000 n \n"; });
  body += "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" + xrefOffset + "\n%%EOF\n";
  return Buffer.from(body, "ascii");
}
async function imageFixture(page, mimeType) {
  const dataUrl = await page.evaluate(async type => {
    const canvas = document.createElement("canvas");
    canvas.width = 120; canvas.height = 160;
    const context = canvas.getContext("2d");
    if (!context) throw new Error("Canvas is unavailable.");
    context.fillStyle = type === "image/jpeg" ? "#3868ac" : "#2f8a62";
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.fillStyle = "#fff";
    context.fillRect(20, 25, 80, 110);
    return canvas.toDataURL(type, 0.9);
  }, mimeType);
  return Buffer.from(dataUrl.slice(dataUrl.indexOf(",") + 1), "base64");
}
function json(route, value, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(value) });
}

async function installCloudBatchHarness(context) {
  const requestLog = [];
  const attemptsByName = new Map();
  const successfulByName = new Map();
  let active = 0;
  let maximumActive = 0;
  let unexpected = [];
  const now = new Date().toISOString();
  const session = {
    id: randomUUID(),
    status: "Active",
    expiresUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    sessionToken,
    shop: { displayName: "Standard Batch QA Shop", accentColor: "#2563eb", currency: "INR" },
    printer: {
      displayName: "SmartPrint QA Printer",
      manufacturer: "QA",
      model: "Virtual printer",
      status: "Ready",
      capabilitiesJson: "{}",
      outputMode: "PHYSICAL"
    },
    paymentOptions: { upiEnabled: false, cashEnabled: true, instantPaymentEnabled: false },
    localAgent: {
      agentDeviceId: "22222222-2222-4222-8222-222222222222",
      endpoints: [],
      printerOutputMode: "PHYSICAL",
      printerStatus: "Ready",
      printerLastSeenUtc: now
    },
    cloudAssistedPrintingEnabled: false,
    privatePrintingVirtualOutputEnabled: false
  };

  await context.route("**/api/v1/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method().toUpperCase();
    if (method === "POST" && path === "/api/v1/public/stations/" + stationCode + "/sessions")
      return json(route, session);
    if (method === "POST" && path === "/api/v1/public/sessions/current/resume")
      return json(route, session);

    if (method === "POST" && path === "/api/v1/public/sessions/current/jobs") {
      const headers = request.headers();
      const raw = request.postDataBuffer() ?? Buffer.alloc(0);
      const multipartName = raw.toString("latin1").match(/filename="([^"]+)"/i)?.[1];
      assert.ok(multipartName, "The real browser request must include one file in multipart/form-data.");
      assert.match(headers["content-type"] ?? "", /^multipart\/form-data; boundary=/i);
      const name = multipartName;
      const attempt = (attemptsByName.get(name) ?? 0) + 1;
      attemptsByName.set(name, attempt);
      const batchId = headers["x-print-batch-id"];
      const idempotencyKey = headers["idempotency-key"];
      assert.ok(batchId, "Every cloud upload must retain the shared batch identifier.");
      assert.ok(idempotencyKey, "Every file upload must carry an idempotency key.");
      requestLog.push({ name, attempt, batchId, idempotencyKey, bytes: raw.length });

      active += 1;
      maximumActive = Math.max(maximumActive, active);
      await new Promise(resolve => setTimeout(resolve, 35));
      try {
        // One deterministic failure proves that the customer can retry the
        // unfinished file without uploading the other 19 files a second time.
        if (name === "batch-07.pdf" && attempt === 1) {
          return json(route, { message: "Synthetic one-file transport failure." }, 503);
        }
        const id = randomUUID();
        successfulByName.set(name, (successfulByName.get(name) ?? 0) + 1);
        const mediaType = name.endsWith(".jpg") ? "image/jpeg" : name.endsWith(".png") ? "image/png" : "application/pdf";
        return json(route, {
          job: {
            id,
            batchId,
            batchGroupName: "Documents",
            batchName: "20-file retry QA",
            jobNumber: "SP-BATCH-QA-" + String(successfulByName.size).padStart(3, "0"),
            status: "AWAITING_CONFIGURATION",
            pageCount: 1,
            copies: 1,
            colourMode: "Mono",
            paperSize: "A4",
            isDuplex: false,
            orientation: "AUTO",
            privatePrinting: false,
            printKind: "DOCUMENT",
            photoCount: 0,
            currency: "INR",
            quotedAmountMinor: 0,
            createdUtc: now,
            outputMode: "PHYSICAL"
          },
          document: {
            id: randomUUID(),
            originalFileName: name,
            mediaType,
            sizeBytes: raw.length,
            pageSizesJson: "[]",
            storageMode: "CLOUD"
          },
          duplicateRequest: false
        });
      } finally {
        active = Math.max(0, active - 1);
      }
    }

    unexpected.push({ method, path });
    return json(route, { message: "Unexpected API request: " + method + " " + path }, 404);
  });

  return { requestLog, attemptsByName, successfulByName, unexpected, get maximumActive() { return maximumActive; } };
}

async function runShopDocumentPreview(browser) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
  await page.goto(baseUrl + "/tests/shop-document-preview.fixture.html", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => window.__smartPrintPreviewFixtureReady === true, null, { timeout: 30000 });

  for (const [id, expected] of [["pdf", "application/pdf"], ["jpeg", "image/jpeg"], ["png", "image/png"]]) {
    const section = page.locator("#" + id + "-case");
    assert.equal(await section.getAttribute("data-identified-type"), expected, id + " must be identified from its file bytes.");
    const surface = section.locator(".shop-document-preview");
    assert.equal(await surface.getAttribute("data-preview-type"), expected);
    assert.equal(await surface.getAttribute("data-preview-state"), "available");
  }
  await page.locator("#pdf-case iframe").waitFor({ state: "visible", timeout: 15000 });
  await page.waitForFunction(() => {
    const frame = document.querySelector("#pdf-case iframe");
    return frame instanceof HTMLIFrameElement && frame.src.startsWith("blob:");
  });

  for (const id of ["jpeg", "png"]) {
    const image = page.locator("#" + id + "-case img");
    await image.waitFor({ state: "visible" });
    await page.waitForFunction(id => {
      const image = document.querySelector("#" + id + "-case img");
      return image instanceof HTMLImageElement && image.complete && image.naturalWidth === 96 && image.naturalHeight === 128;
    }, id, { timeout: 15000 });
    await assert.equal(await page.locator("#" + id + "-case .shop-photo-preview-watermark").count(), 1);
  }

  const unsupported = page.locator("#unsupported-case .shop-document-preview");
  assert.equal(await unsupported.getAttribute("data-preview-state"), "unavailable");
  assert.equal(await page.locator("#unsupported-case .shop-document-preview-unavailable").count(), 1);
  const broken = page.locator("#broken-case .shop-document-preview");
  await page.waitForFunction(() => document.querySelector("#broken-case .shop-document-preview")?.getAttribute("data-preview-state") === "unavailable", null, { timeout: 15000 });
  assert.equal(await broken.getAttribute("data-preview-state"), "unavailable");

  await page.screenshot({ path: join(artifactDirectory, "shop-document-preview.png"), fullPage: true });
  await context.close();
  return { pdf: "inline iframe loaded", jpeg: "decoded", png: "decoded", unsupported: "safe fallback", brokenImage: "safe fallback" };
}

async function runCustomerBatchFlow(browser) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  const harness = await installCloudBatchHarness(context);
  await page.goto(baseUrl + "/s/" + stationCode, { waitUntil: "domcontentloaded" });
  await page.getByRole("heading", { name: /Standard Batch QA Shop/ }).waitFor({ state: "visible", timeout: 30000 });

  const files = [];
  for (let index = 1; index <= 18; index += 1) {
    if (index === 7) continue;
    const label = "batch " + String(index).padStart(2, "0");
    files.push({ name: "batch-" + String(index).padStart(2, "0") + ".pdf", mimeType: "application/pdf", buffer: pdfFixture(label) });
  }
  files.splice(1, 0, { name: "batch-photo.jpg", mimeType: "image/jpeg", buffer: await imageFixture(page, "image/jpeg") });
  files.splice(2, 0, { name: "batch-scan.png", mimeType: "image/png", buffer: await imageFixture(page, "image/png") });
  files.push({ name: "batch-07.pdf", mimeType: "application/pdf", buffer: pdfFixture("retry this file") });
  const expectedNames = files.map(file => file.name);
  assert.equal(files.length, 20);
  assert.ok(expectedNames.includes("batch-photo.jpg") && expectedNames.includes("batch-scan.png") && expectedNames.includes("batch-07.pdf"));

  const picker = page.locator('input[type="file"][multiple]').first();
  await picker.setInputFiles(files);
  await page.waitForFunction(() => document.querySelectorAll(".customer-folder-file-name").length === 20, null, { timeout: 30000 });

  // Exercise the actual customer preview surfaces for PDF, JPEG and PNG
  // before the same selected files are uploaded.
  await page.locator(".customer-file-preview iframe").waitFor({ state: "visible", timeout: 15000 });
  await assert.ok((await page.locator(".customer-file-preview iframe").getAttribute("src") ?? "").startsWith("blob:"));
  await page.locator(".customer-folder-file-name").filter({ hasText: "batch-photo.jpg" }).first().click();
  await page.waitForFunction(() => {
    const image = document.querySelector(".customer-file-preview img.customer-original-image-preview");
    return image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0;
  }, null, { timeout: 15000 });
  await page.locator(".customer-folder-file-name").filter({ hasText: "batch-scan.png" }).first().click();
  await page.waitForFunction(() => {
    const image = document.querySelector(".customer-file-preview img.customer-original-image-preview");
    return image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0;
  }, null, { timeout: 15000 });
  await page.locator(".customer-folder-file-name").filter({ hasText: "batch-01.pdf" }).first().click();
  await page.locator(".customer-file-preview iframe").waitFor({ state: "visible", timeout: 15000 });

  await page.screenshot({ path: join(artifactDirectory, "customer-20-file-batch-selected.png"), fullPage: true });
  const uploadButton = page.getByRole("button", { name: /Upload 20 file\(s\)/i });
  await uploadButton.waitFor({ state: "visible", timeout: 15000 });
  assert.equal(await uploadButton.isDisabled(), false, "A valid 20-file batch should be uploadable.");
  await uploadButton.click();

  await page.waitForFunction(() => document.querySelectorAll(".customer-uploaded-batch-item").length === 19, null, { timeout: 90000 });
  assert.equal(harness.requestLog.length, 20, "All 20 files should receive one upload attempt before retry.");
  assert.equal(harness.maximumActive, 3, "The UI must never exceed three concurrent cloud uploads.");
  assert.equal(await page.locator(".customer-flow-error[role='alert']").count(), 1);
  const firstPassItems = await page.locator(".customer-uploaded-batch-item").allTextContents();
  assert.ok(expectedNames.filter(name => name !== "batch-07.pdf").every(name => firstPassItems.some(item => item.includes(name))),
    "Every successful upload must remain visible after one file fails.");
  assert.equal(firstPassItems.some(item => item.includes("batch-07.pdf")), false,
    "The failed file must remain pending and must not appear as a successful upload.");

  await page.screenshot({ path: join(artifactDirectory, "customer-batch-partial-failure.png"), fullPage: true });
  const retryButton = page.getByRole("button", { name: /Upload 1 file\(s\)/i });
  await retryButton.waitFor({ state: "visible", timeout: 15000 });
  await retryButton.click();
  await page.waitForFunction(() => document.querySelectorAll(".customer-uploaded-batch-item").length === 20, null, { timeout: 30000 });

  assert.equal(harness.requestLog.length, 21, "Retry must transfer only the one unfinished file.");
  assert.equal(harness.attemptsByName.get("batch-07.pdf"), 2);
  assert.equal(harness.successfulByName.size, 20);
  assert.equal(harness.maximumActive, 3);
  assert.equal(new Set(harness.requestLog.map(request => request.batchId)).size, 1,
    "Every file and its retry must remain in the same batch.");
  assert.equal(new Set(harness.requestLog.map(request => request.idempotencyKey)).size, 20,
    "The retry must reuse its original idempotency key; successful files keep their own keys.");
  for (const name of expectedNames) {
    const attempts = harness.requestLog.filter(request => request.name === name);
    assert.equal(attempts.length, name === "batch-07.pdf" ? 2 : 1, "Unexpected duplicate upload attempts for " + name);
    if (attempts.length === 2) assert.equal(attempts[0].idempotencyKey, attempts[1].idempotencyKey);
  }
  assert.equal(harness.unexpected.length, 0, "Unexpected API requests: " + JSON.stringify(harness.unexpected));
  await page.screenshot({ path: join(artifactDirectory, "customer-batch-recovered.png"), fullPage: true });
  await context.close();
  return {
    files: 20,
    concurrentTransfers: harness.maximumActive,
    firstPassUploaded: 19,
    retryTransfers: 1,
    uniquePrintJobs: harness.successfulByName.size,
    duplicateJobs: 0,
    previewFormats: ["PDF", "JPEG", "PNG"]
  };
}

async function runExactCapacitySelection(browser) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
  await installCloudBatchHarness(context);
  await page.goto(baseUrl + "/s/" + stationCode, { waitUntil: "domcontentloaded" });
  await page.getByRole("heading", { name: /Standard Batch QA Shop/ }).waitFor({ state: "visible", timeout: 30000 });

  // Create 20 browser File objects directly; each contains a PDF signature and
  // exactly 25 MiB. This verifies the customer picker accepts the full 500 MiB
  // boundary without copying file bodies into the Node test process.
  await page.evaluate(() => {
    const transfer = new DataTransfer();
    for (let index = 0; index < 20; index += 1) {
      const size = 25 * 1024 * 1024;
      const header = new TextEncoder().encode("%PDF-1.4\n");
      const content = new Uint8Array(size - header.length);
      const bytes = new File([header, content], "capacity-" + String(index + 1).padStart(2, "0") + ".pdf", { type: "application/pdf" });
      transfer.items.add(bytes);
    }
    const input = document.querySelector('input[type="file"][multiple]');
    if (!(input instanceof HTMLInputElement)) throw new Error("The multi-file picker was not rendered.");
    input.files = transfer.files;
    input.dispatchEvent(new Event("change", { bubbles: true }));
  });
  await page.waitForFunction(() => document.querySelectorAll(".customer-folder-file-name").length === 20, null, { timeout: 30000 });
  assert.equal(await page.locator(".customer-upload-notice.warning").count(), 0,
    "A valid 20-file / 500 MiB selection must not show a capacity warning.");
  const uploadButton = page.getByRole("button", { name: /Upload 20 file\(s\)/i });
  await uploadButton.waitFor({ state: "visible", timeout: 15000 });
  assert.equal(await uploadButton.isDisabled(), false);
  await context.close();
  return { files: 20, totalBytes: 500 * 1024 * 1024, selectionAccepted: true, uploadStarted: false };
}

let server = null;
let browser = null;
try {
  if (!process.env.SMARTPRINT_DOCUMENT_PREVIEW_BASE_URL) {
    server = startServer();
    await waitForServer(server);
  }
  browser = await chromium.launch({ headless });
  const previews = await runShopDocumentPreview(browser);
  const batch = await runCustomerBatchFlow(browser);
  const capacity = await runExactCapacitySelection(browser);
  const summary = { completedAt: new Date().toISOString(), browser: "Chromium", status: "PASS", previews, batch, capacity };
  writeFileSync(join(artifactDirectory, "summary.json"), JSON.stringify(summary, null, 2));
  process.stdout.write("Document preview and batch upload browser acceptance passed: 20-file upload with one-file retry and the exact 500 MiB picker boundary.\n");
} catch (error) {
  writeFileSync(join(artifactDirectory, "failure.json"), JSON.stringify({
    message: error instanceof Error ? error.message : String(error),
    stack: error instanceof Error ? error.stack : null
  }, null, 2));
  throw error;
} finally {
  if (browser) await browser.close();
  stopServer(server);
}
