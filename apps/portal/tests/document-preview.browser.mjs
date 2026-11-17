import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn, spawnSync } from "node:child_process";
import { createServer } from "node:http";

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
  const options = {
    stdio: "inherit",
    env: { ...process.env, VITE_API_BASE_URL: "http://127.0.0.1:4176" },
    windowsHide: true,
    detached: process.platform !== "win32"
  };
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
  const jobsById = new Map();
  const statusRequests = [];
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
        const job = {
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
        };
        jobsById.set(id, job);
        return json(route, {
          job,
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

    const statusMatch = path.match(/^\/api\/v1\/public\/jobs\/([0-9a-f-]+)\/status$/i);
    if (method === "GET" && statusMatch) {
      const job = jobsById.get(statusMatch[1]);
      if (!job) return json(route, { message: "Unknown QA print job." }, 404);
      statusRequests.push(statusMatch[1]);
      return json(route, {
        job: {
          ...job,
          updatedUtc: new Date().toISOString(),
          quoteExpiresUtc: null,
          privateDocumentPurgedUtc: null,
          outputCompletedUtc: null
        },
        timeline: [],
        payment: null
      });
    }

    unexpected.push({ method, path });
    return json(route, { message: "Unexpected API request: " + method + " " + path }, 404);
  });

  return { requestLog, attemptsByName, successfulByName, jobsById, statusRequests, unexpected, get maximumActive() { return maximumActive; } };
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
  assert.equal(harness.jobsById.size, 20, "Each accepted upload must have one canonical status record.");
  assert.ok(harness.statusRequests.length > 0, "The customer page must poll the status of its accepted uploads.");
  assert.ok(harness.statusRequests.every(id => harness.jobsById.has(id)),
    "Every status poll must resolve to the original accepted PrintJob.");
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

function startBatchApiServer() {
  const metrics = {
    requests: [],
    attempts: new Map(),
    jobsByIdempotencyKey: new Map(),
    active: 0,
    maximumActive: 0
  };
  const now = new Date().toISOString();
  const session = {
    id: randomUUID(),
    status: "Active",
    expiresUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    sessionToken,
    shop: { displayName: "500 MiB Cloud Batch QA Shop", accentColor: "#2563eb", currency: "INR" },
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
      agentDeviceId: "33333333-3333-4333-8333-333333333333",
      endpoints: [],
      printerOutputMode: "PHYSICAL",
      printerStatus: "Ready",
      printerLastSeenUtc: now
    },
    cloudAssistedPrintingEnabled: false,
    privatePrintingVirtualOutputEnabled: false
  };

  const server = createServer(async (request, response) => {
    const origin = request.headers.origin ?? "*";
    response.setHeader("Access-Control-Allow-Origin", origin);
    response.setHeader("Vary", "Origin");
    response.setHeader("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
    response.setHeader("Access-Control-Allow-Headers", "Content-Type, X-Print-Session, Idempotency-Key, X-Print-Batch-Id");
    response.setHeader("Access-Control-Max-Age", "600");
    if (request.method === "OPTIONS") {
      response.writeHead(204);
      response.end();
      return;
    }
    const path = new URL(request.url ?? "/", "http://127.0.0.1:4176").pathname;
    const sendJson = (status, value) => {
      response.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store" });
      response.end(JSON.stringify(value));
    };

    if (request.method === "POST" && path === "/api/v1/public/stations/" + stationCode + "/sessions") {
      sendJson(200, session);
      return;
    }
    if (request.method === "POST" && path === "/api/v1/public/sessions/current/resume") {
      sendJson(200, session);
      return;
    }
    if (request.method !== "POST" || path !== "/api/v1/public/sessions/current/jobs") {
      sendJson(404, { message: "Unexpected API request: " + request.method + " " + path });
      return;
    }

    const contentType = request.headers["content-type"] ?? "";
    const boundary = contentType.match(/boundary=(?:"([^"]+)"|([^;]+))/i)?.slice(1).find(Boolean);
    if (!contentType.toLowerCase().startsWith("multipart/form-data") || !boundary) {
      sendJson(415, { message: "Expected one multipart file upload." });
      request.resume();
      return;
    }
    const batchId = request.headers["x-print-batch-id"];
    const idempotencyKey = request.headers["idempotency-key"];
    const chunks = [];
    let prefix = Buffer.alloc(0);
    let tail = Buffer.alloc(0);
    let bodyBytes = 0;
    metrics.active += 1;
    metrics.maximumActive = Math.max(metrics.maximumActive, metrics.active);
    request.on("data", chunk => {
      bodyBytes += chunk.length;
      if (prefix.length < 32 * 1024) prefix = Buffer.concat([prefix, chunk.subarray(0, Math.min(chunk.length, 32 * 1024 - prefix.length))]);
      tail = Buffer.concat([tail, chunk]).subarray(-4096);
    });
    request.on("error", error => {
      metrics.active = Math.max(0, metrics.active - 1);
      if (!response.headersSent) sendJson(400, { message: error.message });
    });
    request.on("end", () => {
      try {
        const prefixText = prefix.toString("latin1");
        const fileHeader = prefixText.match(/filename="([^"]+)"/i);
        assert.ok(fileHeader, "The standard cloud route must stream a file part.");
        const name = fileHeader[1];
        const filenameAt = prefixText.indexOf(fileHeader[0]);
        const headerEnd = prefixText.indexOf("\r\n\r\n", filenameAt);
        assert.ok(headerEnd >= 0, "The multipart file header must terminate before its content.");
        const footer = Buffer.from("\r\n--" + boundary + "--\r\n", "latin1");
        assert.ok(tail.subarray(-footer.length).equals(footer), "The final multipart boundary must be complete.");
        const nextPartBoundary = Buffer.from("\\r\\n--" + boundary + "\\r\\n", "latin1");
        const fileEndInTail = tail.indexOf(nextPartBoundary);
        assert.ok(fileEndInTail >= 0, "The file part must end at a valid multipart boundary.");
        const fileEndOffset = bodyBytes - tail.length + fileEndInTail;
        const fileBytes = fileEndOffset - (headerEnd + 4);
        assert.equal(prefix.subarray(headerEnd + 4, headerEnd + 9).toString("ascii"), "%PDF-",
          "Each streamed file must retain its original PDF bytes.");
        assert.equal(fileBytes, 25 * 1024 * 1024, "Every file must transfer its full 25 MiB payload.");
        assert.ok(batchId && idempotencyKey, "Batch and idempotency headers are required.");
        const attempt = (metrics.attempts.get(name) ?? 0) + 1;
        metrics.attempts.set(name, attempt);
        metrics.requests.push({ name, attempt, batchId, idempotencyKey, fileBytes, bodyBytes });
        metrics.active = Math.max(0, metrics.active - 1);

        if (name === "capacity-07.pdf" && attempt === 1) {
          sendJson(503, { message: "Synthetic one-file cloud transfer failure." });
          return;
        }
        let result = metrics.jobsByIdempotencyKey.get(idempotencyKey);
        if (!result) {
          result = {
            job: {
              id: randomUUID(),
              batchId,
              batchGroupName: "Documents",
              batchName: "500 MiB cloud batch",
              jobNumber: "SP-CAPACITY-" + String(metrics.jobsByIdempotencyKey.size + 1).padStart(3, "0"),
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
              mediaType: "application/pdf",
              sizeBytes: fileBytes,
              pageSizesJson: "[]",
              storageMode: "CLOUD"
            },
            duplicateRequest: false
          };
          metrics.jobsByIdempotencyKey.set(idempotencyKey, result);
        } else {
          result = { ...result, duplicateRequest: true };
        }
        sendJson(200, result);
      } catch (error) {
        metrics.active = Math.max(0, metrics.active - 1);
        sendJson(400, { message: error instanceof Error ? error.message : String(error) });
      }
    });
  });
  server.listen(4176, "127.0.0.1");
  return { server, metrics };
}

async function runExactCapacitySelection(browser) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
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
  await uploadButton.click();
  await page.waitForFunction(() => document.querySelectorAll(".customer-uploaded-batch-item").length === 19, null, { timeout: 180000 });
  assert.equal(apiMetrics.metrics.requests.length, 20,
    "The browser must stream all 20 file bodies to the local cloud API before retry.");
  assert.equal(apiMetrics.metrics.maximumActive, 3,
    "The full-size 500 MiB batch must keep concurrency bounded at three.");
  const failedFile = page.getByRole("button", { name: "Upload 1 file(s)" });
  await failedFile.waitFor({ state: "visible", timeout: 30000 });
  await failedFile.click();
  await page.waitForFunction(() => document.querySelectorAll(".customer-uploaded-batch-item").length === 20, null, { timeout: 60000 });
  assert.equal(apiMetrics.metrics.requests.length, 21);
  assert.equal(apiMetrics.metrics.attempts.get("capacity-07.pdf"), 2);
  assert.equal(apiMetrics.metrics.jobsByIdempotencyKey.size, 20,
    "The retry must materialise one canonical job for the failed 25 MiB file.");
  assert.equal(new Set(apiMetrics.metrics.requests.map(item => item.batchId)).size, 1);
  assert.equal(new Set(apiMetrics.metrics.requests.map(item => item.idempotencyKey)).size, 20);
  assert.ok(apiMetrics.metrics.requests.every(item => item.fileBytes === 25 * 1024 * 1024));
  await context.close();
  return {
    files: 20,
    totalBytes: 500 * 1024 * 1024,
    selectionAccepted: true,
    cloudTransferBytes: apiMetrics.metrics.requests.reduce((sum, item) => sum + item.fileBytes, 0),
    peakConcurrency: apiMetrics.metrics.maximumActive,
    retryTransfers: 1,
    uniqueJobs: apiMetrics.metrics.jobsByIdempotencyKey.size
  };
}

let server = null;
let batchApiServer = null;
let browser = null;
let apiMetrics = null;
try {
  batchApiServer = startBatchApiServer();
  apiMetrics = batchApiServer;
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
  if (batchApiServer?.server) await new Promise(resolve => batchApiServer.server.close(resolve));
}
