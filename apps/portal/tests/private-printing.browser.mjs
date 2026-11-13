import assert from "node:assert/strict";
import { createHash, randomUUID, webcrypto } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn, spawnSync } from "node:child_process";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR must point to the installed Playwright package directory.");
const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const artifactDirectory = process.env.SMARTPRINT_PRIVATE_PRINTING_ARTIFACT_DIR ?? "artifacts/private-printing-browser";
const baseUrl = process.env.SMARTPRINT_PRIVATE_PRINTING_BASE_URL ?? "http://127.0.0.1:4174";
const headless = process.env.SMARTPRINT_PRIVATE_PRINTING_HEADED !== "1";
mkdirSync(artifactDirectory, { recursive: true });

const AGENT_ID = "11111111-1111-4111-8111-111111111111";
const STATION_CODE = "PRIVATE-QA";
const SESSION_TOKEN = "private-printing-browser-qa-session-token";

function startServer() {
  const packageManager = process.env.SMARTPRINT_PNPM_COMMAND ?? "pnpm";
  const args = ["--filter", "@smart-print/portal", "dev", "--host", "127.0.0.1", "--port", "4174", "--strictPort"];
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
  if (process.platform === "win32") {
    spawnSync("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore", windowsHide: true });
  } else {
    try { process.kill(-child.pid, "SIGTERM"); }
    catch { if (child.exitCode === null) child.kill("SIGTERM"); }
  }
}

async function waitForServer(child) {
  for (let attempt = 0; attempt < 90; attempt += 1) {
    if (child?.spawnError) throw new Error("Private Printing Vite server failed to start: " + child.spawnError.message);
    if (child?.exitCode !== null && child?.exitCode !== undefined) throw new Error("Private Printing Vite server exited with code " + child.exitCode);
    try {
      const response = await fetch(baseUrl + "/");
      if (response.ok) return;
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error("Private Printing Vite server did not become ready within 90 seconds.");
}

function pdfFixture(label, pageCount) {
  assert.ok(Number.isInteger(pageCount) && pageCount >= 1 && pageCount <= 500,
    "PDF fixture page count must satisfy the Agent's document limit.");
  const safeLabel = label.replace(/[^\x20-\x7e]/g, "").replace(/[()\\]/g, "").slice(0, 80);
  const objects = [];
  objects[1] = "<< /Type /Catalog /Pages 2 0 R >>";
  const pageRefs = [];
  for (let index = 0; index < pageCount; index += 1) {
    const pageObjectId = 3 + index * 2;
    const contentObjectId = pageObjectId + 1;
    pageRefs.push(pageObjectId + " 0 R");
    objects[pageObjectId] =
      "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.275590551 841.88976378] " +
      "/Resources << >> /Contents " + contentObjectId + " 0 R >>";
    const stream = "q 0.96 g 0 0 595.275590551 841.88976378 re f Q\n";
    objects[contentObjectId] = "<< /Length " + Buffer.byteLength(stream, "ascii") +
      " >>\nstream\n" + stream + "endstream";
  }
  objects[2] = "<< /Type /Pages /Count " + pageCount + " /Kids [" + pageRefs.join(" ") + "] >>";
  const infoObjectId = 3 + pageCount * 2;
  objects[infoObjectId] = "<< /Producer (SmartPrint private QA) /Title (" + safeLabel + ") >>";

  let pdf = "%PDF-1.4\n% SmartPrint structurally valid private QA fixture\n";
  const offsets = new Array(infoObjectId + 1).fill(0);
  for (let objectId = 1; objectId <= infoObjectId; objectId += 1) {
    const body = objects[objectId];
    assert.ok(body, "PDF fixture is missing object " + objectId + ".");
    offsets[objectId] = Buffer.byteLength(pdf, "ascii");
    pdf += objectId + " 0 obj\n" + body + "\nendobj\n";
  }
  const xrefOffset = Buffer.byteLength(pdf, "ascii");
  pdf += "xref\n0 " + (infoObjectId + 1) + "\n";
  pdf += "0000000000 65535 f \n";
  for (let objectId = 1; objectId <= infoObjectId; objectId += 1) {
    pdf += String(offsets[objectId]).padStart(10, "0") + " 00000 n \n";
  }
  pdf += "trailer\n<< /Size " + (infoObjectId + 1) + " /Root 1 0 R /Info " + infoObjectId + " 0 R >>\n";
  pdf += "startxref\n" + xrefOffset + "\n%%EOF\n";

  const bytes = Buffer.from(pdf, "ascii");
  const source = bytes.toString("latin1");
  const startXref = Number(source.match(/startxref\s+(\d+)\s+%%EOF/)?.[1] ?? -1);
  assert.ok(startXref === xrefOffset && source.slice(startXref).startsWith("xref\n"),
    "PDF fixture must include a valid cross-reference offset.");
  for (let objectId = 1; objectId <= infoObjectId; objectId += 1) {
    const expectedXref = String(offsets[objectId]).padStart(10, "0") + " 00000 n ";
    assert.ok(source.split("\n").includes(expectedXref),
      "PDF fixture cross-reference must index object " + objectId + ".");
    assert.ok(source.slice(offsets[objectId]).startsWith(objectId + " 0 obj"),
      "PDF fixture object offset must point to the correct object.");
  }
  return bytes;
}
function parsePdfPageCount(bytes) {
  const text = Buffer.from(bytes).toString("latin1");
  const match = text.match(/\/Count\s+(\d+)/);
  return match ? Number(match[1]) : 1;
}

async function createJpeg(page, background) {
  const dataUrl = await page.evaluate(async colour => {
    const canvas = document.createElement("canvas");
    canvas.width = 1600;
    canvas.height = 2100;
    const context = canvas.getContext("2d");
    if (!context) throw new Error("Could not create the synthetic Photo Studio source image.");
    const gradient = context.createLinearGradient(0, 0, canvas.width, canvas.height);
    gradient.addColorStop(0, colour);
    gradient.addColorStop(1, "#eef2f8");
    context.fillStyle = gradient;
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.fillStyle = "#26344b";
    context.fillRect(420, 350, 760, 1280);
    context.fillStyle = "#f1c7a6";
    context.beginPath();
    context.arc(800, 820, 260, 0, Math.PI * 2);
    context.fill();
    return canvas.toDataURL("image/jpeg", 0.92);
  }, background);
  return Buffer.from(dataUrl.slice(dataUrl.indexOf(",") + 1), "base64");
}

function extractEncryptedFile(request) {
  const headers = request.headers();
  const contentType = headers["content-type"] ?? "";
  const boundaryMatch = contentType.match(/boundary=(?:"([^"]+)"|([^;]+))/i);
  assert.ok(boundaryMatch, "Private relay upload must use a multipart boundary.");
  const boundary = boundaryMatch[1] ?? boundaryMatch[2].trim();
  const body = request.postDataBuffer();
  assert.ok(body, "Private relay upload must include a multipart body.");
  const marker = Buffer.from('name="file"; filename="private-document.bin"');
  const markerOffset = body.indexOf(marker);
  assert.ok(markerOffset >= 0, "Private relay must use the opaque private-document.bin file name.");
  const headerEnd = body.indexOf(Buffer.from("\r\n\r\n"), markerOffset);
  assert.ok(headerEnd >= 0, "Private relay multipart file headers are incomplete.");
  const payloadStart = headerEnd + 4;
  const payloadEnd = body.indexOf(Buffer.from("\r\n--" + boundary), payloadStart);
  assert.ok(payloadEnd > payloadStart, "Private relay ciphertext part is missing.");
  return {
    ciphertext: body.subarray(payloadStart, payloadEnd),
    encryptedKey: headers["x-print-encrypted-key"],
    encryptedIv: headers["x-print-encrypted-iv"]
  };
}

async function decryptPrivateRelay(ticket, envelope, privateKey) {
  assert.ok(envelope.encryptedKey, "Private upload must wrap a fresh AES key for the paired Agent.");
  assert.ok(envelope.encryptedIv, "Private upload must supply its AES-GCM nonce.");
  const wrappedKey = Buffer.from(envelope.encryptedKey, "base64");
  const iv = Buffer.from(envelope.encryptedIv, "base64");
  assert.equal(iv.length, 12, "AES-GCM nonce must be 96 bits.");
  const rawAesKey = Buffer.from(await webcrypto.subtle.decrypt({ name: "RSA-OAEP" }, privateKey, wrappedKey));
  try {
    const aesKey = await webcrypto.subtle.importKey("raw", rawAesKey, { name: "AES-GCM" }, false, ["decrypt"]);
    const aad = Buffer.from(
      "SmartPrint.PrivatePrinting.v1|" + ticket.id + "|" + ticket.printJobId + "|" +
      ticket.sizeBytes + "|" + ticket.sha256,
      "utf8"
    );
    return Buffer.from(await webcrypto.subtle.decrypt(
      { name: "AES-GCM", iv, additionalData: aad, tagLength: 128 },
      aesKey,
      envelope.ciphertext
    ));
  } finally {
    rawAesKey.fill(0);
  }
}

function makeUploadedJob(ticket, ticketRequest, plaintext) {
  const printKind = ticketRequest.printKind ?? "DOCUMENT";
  const pageCount = printKind === "PHOTO"
    ? Math.ceil(Number(ticketRequest.photoCount) / Number(ticketRequest.photosPerPage))
    : printKind === "ID_DOCUMENT" ? 1 : parsePdfPageCount(plaintext);
  const mediaType = ticketRequest.detectedFileExtension === ".pdf"
    ? "application/pdf"
    : ticketRequest.detectedFileExtension === ".png" ? "image/png" : "image/jpeg";
  const pageSizes = Array.from({ length: pageCount }, () => ({
    width: 595.275590551,
    height: 841.88976378,
    orientation: "PORTRAIT"
  }));
  return {
    job: {
      id: ticket.printJobId,
      batchId: ticket.batchId,
      batchGroupName: ticket.batchGroupName,
      batchName: ticket.batchName ?? null,
      jobNumber: ticket.jobNumber,
      status: "AWAITING_CONFIGURATION",
      pageCount,
      copies: ticketRequest.copies ?? 1,
      colourMode: ticketRequest.colourMode ?? "Mono",
      paperSize: ticketRequest.paperSize ?? "A4",
      isDuplex: ticketRequest.isDuplex ?? false,
      orientation: ticketRequest.orientation ?? "AUTO",
      privatePrinting: true,
      printKind,
      photoSizeCode: ticketRequest.photoSizeCode ?? null,
      photoCount: ticketRequest.photoCount ?? 0,
      photosPerPage: ticketRequest.photosPerPage ?? null,
      currency: "INR",
      quotedAmountMinor: 0,
      createdUtc: new Date().toISOString()
    },
    document: {
      id: randomUUID(),
      originalFileName: "Private document",
      mediaType,
      sizeBytes: plaintext.length,
      sha256: ticketRequest.sha256,
      pageSizesJson: JSON.stringify(pageSizes),
      storageMode: "LOCAL_AGENT"
    },
    duplicateRequest: false
  };
}

async function installPrivatePrintingApiHarness(context, page) {
  const keyPair = await webcrypto.subtle.generateKey({
    name: "RSA-OAEP",
    modulusLength: 2048,
    publicExponent: Uint8Array.from([1, 0, 1]),
    hash: "SHA-256"
  }, true, ["encrypt", "decrypt"]);
  const publicKeyBytes = Buffer.from(await webcrypto.subtle.exportKey("spki", keyPair.publicKey));
  const tickets = new Map();
  const ticketRequests = [];
  const relayUploads = [];
  const allApiRequests = [];
  const unhandledApi = [];
  let nextJobNumber = 1;

  const sessionResponse = {
    id: randomUUID(),
    status: "Active",
    expiresUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    sessionToken: SESSION_TOKEN,
    shop: { displayName: "Private Printing QA Shop", accentColor: "#2563eb", currency: "INR" },
    printer: {
      displayName: "Microsoft Print to PDF · Development",
      manufacturer: "Microsoft",
      model: "Print to PDF",
      status: "Ready",
      capabilitiesJson: "{}",
      outputMode: "PDF_EXPORT"
    },
    photoPricing: {
      photo35x45Minor: 2500,
      photo25x35Minor: 1800,
      photo2x2Minor: 3000,
      photo4x6Minor: 6000
    },
    paymentOptions: { upiEnabled: false, cashEnabled: true, instantPaymentEnabled: false },
    localAgent: {
      agentDeviceId: AGENT_ID,
      endpoints: [],
      documentEncryptionPublicKey: publicKeyBytes.toString("base64"),
      printerOutputMode: "PDF_EXPORT",
      printerStatus: "Ready",
      printerLastSeenUtc: new Date().toISOString()
    },
    privatePrintingVirtualOutputEnabled: true,
    cloudAssistedPrintingEnabled: false
  };

  await context.route("**/api/v1/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method().toUpperCase();
    allApiRequests.push({ method, path });

    const json = value => route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(value)
    });
    const badRequest = (message, status = 404) => route.fulfill({
      status,
      contentType: "application/json",
      body: JSON.stringify({ message })
    });

    if (method === "POST" && path === "/api/v1/public/stations/" + STATION_CODE + "/sessions")
      return json(sessionResponse);

    if (method === "POST" && path === "/api/v1/public/sessions/current/local-upload-tickets") {
      const body = request.postDataJSON();
      assert.equal(body.privatePrinting, true, "Every file in this scenario must request Private Printing.");
      assert.equal(body.detectedFileExtension, ".pdf", "Prepared docs must be identified as PDF from their bytes.");
      const id = randomUUID();
      const ticket = {
        id,
        printJobId: randomUUID(),
        batchId: body.batchId ?? null,
        batchGroupName: body.batchGroupName ?? null,
        batchName: body.batchName ?? null,
        jobNumber: "SP-PRIVATE-QA-" + String(nextJobNumber).padStart(3, "0"),
        agentDeviceId: AGENT_ID,
        expiresUtc: new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString(),
        completed: false,
        privatePrinting: true,
        sha256: body.sha256,
        sizeBytes: body.sizeBytes,
        ticketRequest: body,
        relayAccepted: false,
        uploadResult: null
      };
      nextJobNumber += 1;
      tickets.set(id, ticket);
      ticketRequests.push({ id, ...body });
      return json({
        id,
        printJobId: ticket.printJobId,
        batchId: ticket.batchId,
        batchGroupName: ticket.batchGroupName,
        batchName: ticket.batchName,
        jobNumber: ticket.jobNumber,
        agentDeviceId: AGENT_ID,
        expiresUtc: ticket.expiresUtc,
        completed: false,
        privatePrinting: true,
        sha256: ticket.sha256
      });
    }

    const relayMatch = path.match(/^\/api\/v1\/public\/sessions\/current\/local-upload-tickets\/([0-9a-f-]+)\/private-relay(?:-status)?$/i);
    if (relayMatch) {
      const ticket = tickets.get(relayMatch[1]);
      if (!ticket) return badRequest("Unknown private upload ticket.", 404);
      if (path.endsWith("/private-relay-status") && method === "GET") {
        if (!ticket.relayAccepted) return json({ state: "PENDING" });
        return json({ state: "READY", uploadResult: ticket.uploadResult });
      }
      if (path.endsWith("/private-relay") && method === "POST") {
        assert.equal(request.headers()["x-print-session"], SESSION_TOKEN);
        const grant = request.headers()["x-print-upload-grant"];
        assert.ok(grant && grant.length >= 40, "Private relay must use its opaque upload grant.");
        const envelope = extractEncryptedFile(request);
        const plaintext = await decryptPrivateRelay(ticket, envelope, keyPair.privateKey);
        try {
          assert.equal(plaintext.length, ticket.sizeBytes, "Decrypted size must match the upload ticket.");
          assert.equal(createHash("sha256").update(plaintext).digest("hex"), ticket.sha256,
            "Decrypted bytes must match the original upload hash.");
          assert.ok(plaintext.subarray(0, 5).equals(Buffer.from("%PDF-", "ascii")),
            "Every prepared document must be PDF content.");
          assert.equal(envelope.ciphertext.length, plaintext.length + 16,
            "AES-GCM ciphertext must include its authentication tag.");
          ticket.uploadResult = makeUploadedJob(ticket, ticket.ticketRequest, plaintext);
          ticket.relayAccepted = true;
          relayUploads.push({
            ticketId: ticket.id,
            printJobId: ticket.printJobId,
            sha256: ticket.sha256,
            plaintextBytes: plaintext.length,
            encryptedBytes: envelope.ciphertext.length,
            printKind: ticket.ticketRequest.printKind ?? "DOCUMENT",
            photoSizeCode: ticket.ticketRequest.photoSizeCode ?? null,
            photoCount: ticket.ticketRequest.photoCount ?? null,
            photosPerPage: ticket.ticketRequest.photosPerPage ?? null,
            pageCount: ticket.uploadResult.job.pageCount
          });
        } finally {
          plaintext.fill(0);
        }
        return json({ accepted: true, duplicateRequest: false });
      }
    }

    const statusMatch = path.match(/^\/api\/v1\/public\/jobs\/([0-9a-f-]+)\/status$/i);
    if (statusMatch && method === "GET") {
      const ticket = [...tickets.values()].find(item => item.printJobId === statusMatch[1]);
      if (!ticket?.uploadResult) return badRequest("Unknown print job.", 404);
      return json({
        job: {
          ...ticket.uploadResult.job,
          updatedUtc: new Date().toISOString(),
          quoteExpiresUtc: null,
          privateDocumentPurgedUtc: null,
          outputType: "TEST_VIRTUAL",
          outputCompletedUtc: null
        },
        timeline: [],
        payment: null
      });
    }

    unhandledApi.push({ method, path });
    return badRequest("Unexpected API request in the isolated QA harness: " + method + " " + path, 501);
  });

  page.on("pageerror", error => unhandledApi.push({ method: "PAGEERROR", path: error.message }));
  return { tickets, ticketRequests, relayUploads, allApiRequests, unhandledApi };
}

async function selectFile(page, name) {
  const row = page.locator(".customer-folder-file-name").filter({ hasText: name }).first();
  await row.waitFor({ state: "visible", timeout: 15000 });
  await row.click();
}

async function prepareUploadPagePhoto(page, name) {
  await selectFile(page, name);
  const openPhotoStudio = page.locator(".customer-file-studio-button");
  await openPhotoStudio.waitFor({ state: "visible", timeout: 15000 });
  await openPhotoStudio.click();
  const dialog = page.getByRole("dialog", { name: "Photo Studio" });
  await dialog.waitFor({ state: "visible", timeout: 15000 });
  await dialog.getByLabel("Photo size").selectOption("4x6");
  await dialog.getByLabel("Number of photos").fill("2");
  await dialog.getByLabel("Number of photos").press("Tab");
  await page.waitForFunction(() => {
    const button = document.querySelector("#photo-studio-apply");
    return button instanceof HTMLButtonElement && !button.disabled;
  });
  await dialog.locator("#photo-studio-apply").click();
  await dialog.waitFor({ state: "detached", timeout: 30000 });
}

async function runScenario(browser) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  const harness = await installPrivatePrintingApiHarness(context, page);

  await page.goto(baseUrl + "/s/" + STATION_CODE, { waitUntil: "domcontentloaded" });
  await page.getByRole("heading", { name: /Private Printing QA Shop/ }).waitFor({ state: "visible", timeout: 30000 });
  const privateChoice = page.locator(".customer-privacy-option.private");
  await privateChoice.waitFor({ state: "visible" });
  assert.equal(await privateChoice.getAttribute("aria-pressed"), "false");
  assert.match(await privateChoice.innerText(), /development test PDF only/i,
    "The UI must disclose the development virtual-printer route.");
  await privateChoice.click();
  assert.equal(await privateChoice.getAttribute("aria-pressed"), "true");

  const idOne = pdfFixture("ID Studio prepared card one", 1);
  const idTwo = pdfFixture("ID Studio prepared card two", 1);
  const savedPhotoOne = pdfFixture("Saved Photo Studio sheet one", 2);
  const savedPhotoTwo = pdfFixture("Saved Photo Studio sheet two", 2);
  const savedPhotoThree = pdfFixture("Saved Photo Studio sheet three", 2);
  const multipageDocument = pdfFixture("Customer multipage document", 4);
  const newPhotoOne = await createJpeg(page, "#7c92bd");
  const newPhotoTwo = await createJpeg(page, "#bf9278");

  await page.evaluate(() => {
    window.__smartPrintNativePickerGroups = [
      "ID Studio", "ID Studio", "Photo Studio", "Photo Studio",
      "Photo Studio", "Documents", "Photo source", "Photo source"
    ];
    window.__smartPrintNativePrintKinds = [
      "ID_DOCUMENT", "ID_DOCUMENT", "PHOTO", "PHOTO",
      "PHOTO", "DOCUMENT", "DOCUMENT", "DOCUMENT"
    ];
    window.__smartPrintNativePhotoSettings = [
      null, null,
      { sizeCode: "35x45", photoCount: 18, photosPerPage: 9 },
      { sizeCode: "25x35", photoCount: 15, photosPerPage: 10 },
      { sizeCode: "2x2", photoCount: 12, photosPerPage: 6 },
      null, null, null
    ];
  });

  const picker = page.locator('input[type="file"][multiple]').first();
  await picker.setInputFiles([
    { name: "id-card-one.pdf", mimeType: "application/pdf", buffer: idOne },
    { name: "id-card-two.pdf", mimeType: "application/pdf", buffer: idTwo },
    { name: "saved-photo-sheet-one.pdf", mimeType: "application/pdf", buffer: savedPhotoOne },
    { name: "saved-photo-sheet-two.pdf", mimeType: "application/pdf", buffer: savedPhotoTwo },
    { name: "saved-photo-sheet-three.pdf", mimeType: "application/pdf", buffer: savedPhotoThree },
    { name: "college-document-multipage.pdf", mimeType: "application/pdf", buffer: multipageDocument },
    { name: "new-photo-one.jpg", mimeType: "image/jpeg", buffer: newPhotoOne },
    { name: "new-photo-two.jpg", mimeType: "image/jpeg", buffer: newPhotoTwo }
  ]);

  await page.waitForFunction(() => document.querySelectorAll(".customer-folder-file-name").length === 8, null, { timeout: 30000 });
  assert.equal(await privateChoice.getAttribute("aria-pressed"), "true",
    "Prepared ID Studio files must keep the complete batch in Private Printing.");
  await page.screenshot({ path: join(artifactDirectory, "01-private-eight-file-selection.png"), fullPage: true });

  await prepareUploadPagePhoto(page, "new-photo-one.jpg");
  await prepareUploadPagePhoto(page, "new-photo-two.jpg");
  await page.waitForFunction(() => document.querySelectorAll(".customer-folder-file-name").length === 8);
  await page.screenshot({ path: join(artifactDirectory, "02-private-eight-file-batch-prepared.png"), fullPage: true });

  const uploadButton = page.getByRole("button", { name: /Upload 8 file\(s\) securely/i });
  await uploadButton.waitFor({ state: "visible", timeout: 15000 });
  await uploadButton.click();
  await page.waitForFunction(() => document.querySelectorAll(".customer-uploaded-batch-item").length === 8, null, { timeout: 90000 });
  await page.screenshot({ path: join(artifactDirectory, "03-private-eight-file-uploaded.png"), fullPage: true });

  assert.equal(harness.ticketRequests.length, 8, "The eight-file selection must produce exactly eight upload tickets.");
  assert.equal(harness.relayUploads.length, 8, "Every file must complete through the encrypted private relay.");
  assert.equal(new Set(harness.ticketRequests.map(item => item.batchId)).size, 1,
    "All eight files must remain in one batch.");
  assert.ok(harness.ticketRequests.every(item => item.privatePrinting === true),
    "The API must receive Private Printing for every file.");
  assert.ok(harness.ticketRequests.every(item => item.originalFileName.startsWith("private-document")),
    "Private ticket metadata must not contain original customer file names.");
  assert.equal(harness.ticketRequests.filter(item => item.printKind === "ID_DOCUMENT").length, 2,
    "Exactly two prepared ID sheets must retain ID_DOCUMENT classification.");
  assert.equal(harness.ticketRequests.filter(item => item.printKind === "PHOTO").length, 5,
    "Three saved Photo Studio sheets and two upload-page outputs must be PHOTO jobs.");
  assert.equal(harness.ticketRequests.filter(item => item.printKind === "DOCUMENT").length, 1,
    "The batch must contain one ordinary multipage PDF.");
  const photoRequests = harness.ticketRequests.filter(item => item.printKind === "PHOTO");
  assert.equal(photoRequests.filter(item => item.photoSizeCode === "4x6").length, 2,
    "Both newly prepared upload-page sheets must carry their 4x6 metadata.");
  assert.ok(photoRequests.every(item => Number(item.photoCount) > 0 && Number(item.photosPerPage) > 0));
  assert.equal(harness.relayUploads.filter(item => item.pageCount === 2).length, 5,
    "All five Photo Studio sheets must preserve their two-page layout.");
  assert.equal(harness.relayUploads.find(item => item.printKind === "DOCUMENT")?.pageCount, 4,
    "The ordinary multipage PDF must remain four pages.");
  assert.ok(harness.relayUploads.every(item => item.encryptedBytes === item.plaintextBytes + 16),
    "AES-GCM ciphertext must include its authentication tag.");
  assert.equal(harness.allApiRequests.some(item => item.path.includes("/cloud-recovery")), false,
    "Private uploads must never use cloud recovery.");
  assert.equal(harness.unhandledApi.length, 0,
    "Unexpected portal requests: " + JSON.stringify(harness.unhandledApi));

  const selectedKinds = await page.locator(".customer-uploaded-batch-item .print-kind-tag").allTextContents();
  assert.equal(selectedKinds.filter(value => value.trim() === "Photo").length, 5);
  assert.equal(selectedKinds.filter(value => value.trim() === "Document").length, 3,
    "The customer batch list retains all three non-photo documents; the API ticket assertions verify which two are ID cards.");
  const uploadedLabels = await page.locator(".customer-uploaded-batch-item").allTextContents();
  assert.ok(uploadedLabels.every(value => value.includes("Private document")),
    "The upload receipt must use privacy-safe document names.");

  const result = {
    label: "private-eight-document-development-virtual-printer-batch",
    privatePrintingEnabled: await privateChoice.getAttribute("aria-pressed") === "true",
    configuredOutput: "Microsoft Print to PDF · non-production TEST_VIRTUAL",
    fileCount: harness.ticketRequests.length,
    idStudioSheets: 2,
    savedPhotoStudioSheets: 3,
    uploadPagePhotoStudioSheets: 2,
    ordinaryMultiPagePdfs: 1,
    printKinds: {
      ID_DOCUMENT: harness.ticketRequests.filter(item => item.printKind === "ID_DOCUMENT").length,
      PHOTO: harness.ticketRequests.filter(item => item.printKind === "PHOTO").length,
      DOCUMENT: harness.ticketRequests.filter(item => item.printKind === "DOCUMENT").length
    },
    batchIds: [...new Set(harness.ticketRequests.map(item => item.batchId))],
    privateRelayUploads: harness.relayUploads,
    cloudRecoveryUsed: false,
    physicalOutputClaimed: false
  };
  writeFileSync(join(artifactDirectory, "summary.json"), JSON.stringify({
    completedAt: new Date().toISOString(),
    browser: "Chromium",
    status: "PASS",
    scenario: result
  }, null, 2));
  await context.close();
  return result;
}

let server = null;
let browser = null;
try {
  if (!process.env.SMARTPRINT_PRIVATE_PRINTING_BASE_URL) {
    server = startServer();
    await waitForServer(server);
  }
  browser = await chromium.launch({ headless });
  const result = await runScenario(browser);
  process.stdout.write(
    "Private Printing browser acceptance passed: " + result.fileCount +
    " encrypted files (2 ID, 3 saved Photo Studio, 2 upload-page Photo Studio, 1 multipage PDF).\n"
  );
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
