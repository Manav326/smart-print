import assert from "node:assert/strict";
import { createHash, randomUUID } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn, spawnSync } from "node:child_process";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR must point to the installed Playwright package.");
const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const artifactDirectory = process.env.SMARTPRINT_BATCH_UPLOAD_ARTIFACT_DIR ?? "artifacts/batch-upload-browser";
const baseUrl = process.env.SMARTPRINT_BATCH_UPLOAD_BASE_URL ?? "http://127.0.0.1:4175";
const stationCode = "CLOUD-BATCH-QA";
const sessionToken = "cloud-batch-browser-qa-session-token";
const failedFileName = "batch-file-11.pdf";
mkdirSync(artifactDirectory, { recursive: true });

function startServer() {
  const command = process.env.SMARTPRINT_PNPM_COMMAND ?? "pnpm";
  const args = ["--filter", "@smart-print/portal", "dev", "--host", "127.0.0.1", "--port", "4175", "--strictPort"];
  const options = { stdio: "inherit", env: process.env, windowsHide: true, detached: process.platform !== "win32" };
  if (process.platform === "win32") {
    const child = spawn(process.env.ComSpec ?? "cmd.exe", ["/d", "/c", [command, ...args].join(" ")], options);
    child.spawnError = null;
    child.on("error", error => { child.spawnError = error; });
    return child;
  }
  const child = spawn(command, args, options);
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
    if (child?.spawnError) throw new Error("Portal failed to start: " + child.spawnError.message);
    if (child?.exitCode !== null && child?.exitCode !== undefined) throw new Error("Portal exited with code " + child.exitCode);
    try { if ((await fetch(baseUrl + "/")).ok) return; } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error("Portal did not become ready within 90 seconds.");
}
function pdfFixture(label) {
  const safeLabel = label.replace(/[^\x20-\x7e]/g, "").replace(/[()\\]/g, "").slice(0, 70);
  const stream = "BT /F1 18 Tf 72 720 Td (" + safeLabel + ") Tj ET\n";
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    "<< /Length " + Buffer.byteLength(stream, "ascii") + " >>\nstream\n" + stream + "endstream"
  ];
  let source = "%PDF-1.4\n";
  const offsets = [0];
  objects.forEach((body, index) => {
    offsets.push(Buffer.byteLength(source, "ascii"));
    source += (index + 1) + " 0 obj\n" + body + "\nendobj\n";
  });
  const xref = Buffer.byteLength(source, "ascii");
  source += "xref\n0 6\n0000000000 65535 f \n";
  for (let index = 1; index <= 5; index += 1)
    source += String(offsets[index]).padStart(10, "0") + " 00000 n \n";
  source += "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n";
  return Buffer.from(source, "ascii");
}
const files = Array.from({ length: 20 }, (_, index) => {
  const name = "batch-file-" + String(index + 1).padStart(2, "0") + ".pdf";
  const bytes = pdfFixture("SmartPrint batch acceptance " + (index + 1));
  return { name, mimeType: "application/pdf", buffer: bytes, sizeBytes: bytes.length,
    sha256: createHash("sha256").update(bytes).digest("hex") };
});

const stationSession = {
  id: randomUUID(), status: "Active",
  expiresUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
  sessionToken,
  shop: { displayName: "Cloud Batch QA Shop", accentColor: "#2563eb", currency: "INR" },
  printer: { displayName: "Cloud Queue QA Printer", manufacturer: "SmartPrint", model: "QA Queue",
    status: "Ready", capabilitiesJson: "{}", outputMode: "PHYSICAL" },
  photoPricing: { photo35x45Minor: 2500, photo25x35Minor: 1800, photo2x2Minor: 3000, photo4x6Minor: 6000 },
  paymentOptions: { upiEnabled: false, cashEnabled: true, instantPaymentEnabled: false },
  localAgent: null,
  privatePrintingVirtualOutputEnabled: false,
  cloudAssistedPrintingEnabled: false
};

async function runScenario(browser) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  const fileByName = new Map(files.map(file => [file.name, file]));
  const requestCounts = new Map();
  const idempotencyKeysByFile = new Map();
  const jobsByIdempotencyKey = new Map();
  const createdJobs = new Map();
  const unhandledApi = [];
  const pageErrors = [];
  let activeUploads = 0;
  let maximumConcurrentUploads = 0;
  let failedFirstAttempt = false;

  await page.route("**/api/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method().toUpperCase();
    const json = (body, status = 200) => route.fulfill({
      status, contentType: "application/json", body: JSON.stringify(body)
    });

    if (method === "POST" && path === "/api/v1/public/stations/" + stationCode + "/sessions")
      return json(stationSession);
    if (method === "POST" && path === "/api/v1/public/sessions/current/resume")
      return json(stationSession);
    if (method === "POST" && path === "/api/v1/public/sessions/current/renew")
      return json({ expiresUtc: stationSession.expiresUtc, localAgent: null,
        privatePrintingVirtualOutputEnabled: false, paymentOptions: stationSession.paymentOptions });

    if (method === "POST" && path === "/api/v1/public/sessions/current/jobs") {
      const rawBody = request.postData() ?? "";
      const fileName = rawBody.match(/filename="([^"]+)"/)?.[1];
      const file = fileByName.get(fileName);
      assert.ok(file, "Cloud upload must send a selected file, received: " + String(fileName));
      const idempotencyKey = request.headers()["idempotency-key"];
      assert.ok(idempotencyKey, "Each cloud upload must carry a stable idempotency key.");
      const count = (requestCounts.get(fileName) ?? 0) + 1;
      requestCounts.set(fileName, count);
      idempotencyKeysByFile.set(fileName, [...(idempotencyKeysByFile.get(fileName) ?? []), idempotencyKey]);

      activeUploads += 1;
      maximumConcurrentUploads = Math.max(maximumConcurrentUploads, activeUploads);
      try {
        // Keep requests overlapping so this assertion measures actual browser
        // upload concurrency instead of only checking the worker helper in isolation.
        await new Promise(resolve => setTimeout(resolve, 90));
        let response = jobsByIdempotencyKey.get(idempotencyKey);
        if (!response) {
          const jobId = randomUUID();
          const batchId = request.headers()["x-print-batch-id"] ?? null;
          response = {
            job: {
              id: jobId, batchId, batchGroupName: "Batch documents", batchName: null,
              jobNumber: "SP-CLOUD-QA-" + String(createdJobs.size + 1).padStart(3, "0"),
              status: "AWAITING_CONFIGURATION", pageCount: 1, copies: 1,
              colourMode: "Mono", paperSize: "A4", isDuplex: false,
              orientation: "AUTO", pagesPerSheet: 1, currency: "INR",
              quotedAmountMinor: 0, createdUtc: new Date().toISOString(),
              printKind: "DOCUMENT", privatePrinting: false
            },
            document: {
              id: randomUUID(), originalFileName: file.name, mediaType: file.mimeType,
              sizeBytes: file.sizeBytes, sha256: file.sha256, pageSizesJson: "[]"
            },
            duplicateRequest: false
          };
          jobsByIdempotencyKey.set(idempotencyKey, response);
          createdJobs.set(job.name ?? file.name, response);
        } else {
          response = { ...response, duplicateRequest: true };
        }

        // Simulate an ambiguous response loss after the server has accepted
        // this one idempotency key. Retrying must recover the same job and must
        // not create a second print request for this customer file.
        if (fileName === failedFileName && count === 1) {
          failedFirstAttempt = true;
          return json({ message: "Simulated response interruption after ticket acceptance." }, 503);
        }
        return json(response);
      } finally {
        activeUploads = Math.max(0, activeUploads - 1);
      }
    }

    unhandledApi.push({ method, path });
    return json({ message: "Unhandled isolated upload QA request: " + method + " " + path }, 404);
  });

  page.on("pageerror", error => pageErrors.push(error.message));
  await page.goto(baseUrl + "/s/" + stationCode, { waitUntil: "domcontentloaded" });
  await page.getByRole("heading", { name: /Cloud Batch QA Shop/ }).waitFor({ state: "visible", timeout: 30000 });

  const picker = page.locator('input[type="file"][multiple]').first();
  await picker.setInputFiles(files);
  await page.waitForFunction(() => document.querySelectorAll(".customer-folder-file-name").length === 20, null, { timeout: 30000 });
  const initialUpload = page.getByRole("button", { name: /Upload 20 file\(s\)/i });
  await initialUpload.waitFor({ state: "visible", timeout: 15000 });
  await page.screenshot({ path: join(artifactDirectory, "01-twenty-file-cloud-batch-selected.png"), fullPage: true });
  await initialUpload.click();

  await page.waitForFunction(() =>
    document.querySelectorAll(".customer-uploaded-batch-item").length === 19 &&
    Boolean(document.querySelector(".customer-flow-error")), null, { timeout: 90000 });
  assert.equal(failedFirstAttempt, true, "The acceptance harness must exercise one ambiguous first response.");
  assert.equal(createdJobs.size, 20, "The simulated server must have exactly one canonical record per selected file.");
  assert.equal(maximumConcurrentUploads, 3, "The actual cloud UI must never exceed three concurrent transfers.");
  await page.screenshot({ path: join(artifactDirectory, "02-nineteen-complete-one-retry-needed.png"), fullPage: true });

  const retryButton = page.getByRole("button", { name: /Upload 1 file\(s\)/i });
  await retryButton.waitFor({ state: "visible", timeout: 15000 });
  await retryButton.click();
  await page.waitForFunction(() =>
    document.querySelectorAll(".customer-uploaded-batch-item").length === 20 &&
    !document.querySelector(".customer-flow-error"), null, { timeout: 45000 });

  assert.equal(requestCounts.size, 20, "Every selected file must reach the cloud endpoint.");
  assert.equal([...requestCounts.values()].reduce((sum, count) => sum + count, 0), 21,
    "Only the interrupted file should be transferred a second time.");
  for (const file of files) {
    const attempts = requestCounts.get(file.name);
    assert.equal(attempts, file.name === failedFileName ? 2 : 1,
      file.name + " must be sent once, except for the intentionally interrupted request.");
    const keys = idempotencyKeysByFile.get(file.name);
    assert.ok(keys?.length === attempts);
    assert.equal(new Set(keys).size, 1, file.name + " must reuse the same idempotency key on retry.");
  }
  assert.equal(createdJobs.size, 20, "The retry must not create a duplicate PrintJob.");
  assert.equal(new Set([...createdJobs.values()].map(item => item.job.id)).size, 20);
  assert.equal(maximumConcurrentUploads, 3);
  assert.deepEqual(unhandledApi, [], "Unexpected portal API requests: " + JSON.stringify(unhandledApi));
  assert.deepEqual(pageErrors, [], "Browser errors: " + JSON.stringify(pageErrors));

  const uploadedNames = await page.locator(".customer-uploaded-batch-item").allTextContents();
  assert.ok(files.every(file => uploadedNames.some(label => label.includes(file.name))),
    "The customer batch receipt must show all twenty original filenames.");
  await page.screenshot({ path: join(artifactDirectory, "03-twenty-file-cloud-batch-complete.png"), fullPage: true });

  const result = {
    status: "PASS",
    transport: "standard cloud API over the portal's HTTPS upload path",
    selectedFiles: files.length,
    filesAccepted: createdJobs.size,
    maximumConcurrentTransfers: maximumConcurrentUploads,
    ambiguousResponseRecovery: {
      file: failedFileName,
      attempts: requestCounts.get(failedFileName),
      sameIdempotencyKey: new Set(idempotencyKeysByFile.get(failedFileName)).size === 1,
      canonicalPrintJobs: 1
    },
    successfulFilesRetried: [...requestCounts].filter(([name, count]) => name !== failedFileName && count > 1).map(([name]) => name),
    allTwentyReceiptsVisible: uploadedNames.length === 20
  };
  writeFileSync(join(artifactDirectory, "summary.json"), JSON.stringify({
    completedAt: new Date().toISOString(), browser: "Chromium", scenario: result
  }, null, 2));
  await context.close();
  return result;
}

let server = null;
let browser = null;
try {
  if (!process.env.SMARTPRINT_BATCH_UPLOAD_BASE_URL) {
    server = startServer();
    await waitForServer(server);
  }
  browser = await chromium.launch({ headless: process.env.SMARTPRINT_BATCH_UPLOAD_HEADED !== "1" });
  const result = await runScenario(browser);
  process.stdout.write("Cloud batch upload browser acceptance passed: " +
    result.selectedFiles + " files; " + result.maximumConcurrentTransfers +
    " maximum concurrent transfers; one interrupted upload recovered idempotently.\n");
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
