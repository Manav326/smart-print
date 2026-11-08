import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn, spawnSync } from "node:child_process";
import { deflateSync } from "node:zlib";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR must point to the installed Playwright package directory.");
const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const artifactDirectory = process.env.SMARTPRINT_PHOTO_STUDIO_ARTIFACT_DIR ?? "artifacts/photo-studio-browser";
const baseUrl = process.env.SMARTPRINT_PHOTO_STUDIO_BASE_URL ?? "http://127.0.0.1:4173";
const headless = process.env.SMARTPRINT_PHOTO_STUDIO_HEADED !== "1";
mkdirSync(artifactDirectory, { recursive: true });

function startServer() {
  const packageManager = process.env.SMARTPRINT_PNPM_COMMAND ?? "pnpm";
  const args = ["--filter", "@smart-print/portal", "dev", "--host", "0.0.0.0", "--port", "4173", "--strictPort"];
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
    // pnpm starts Vite as a descendant; terminate the entire process tree, not only pnpm.
    spawnSync("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore", windowsHide: true });
  } else {
    // detached:true gives the server its own process group. Kill the group to avoid
    // leaving Vite alive and occupying the port for the repository's other browser suites.
    try {
      process.kill(-child.pid, "SIGTERM");
    } catch {
      if (child.exitCode === null) child.kill("SIGTERM");
    }
  }
}

async function waitForServer(child) {
  for (let attempt = 0; attempt < 90; attempt += 1) {
    if (child?.spawnError) throw new Error("Photo Studio Vite server failed to start: " + child.spawnError.message);
    if (child?.exitCode !== null && child?.exitCode !== undefined) throw new Error("Photo Studio Vite server exited with code " + child.exitCode);
    try {
      const response = await fetch(baseUrl + "/tests/photo-studio.browser.html");
      if (response.ok) return;
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error("Photo Studio test server did not become ready within 90 seconds.");
}

async function assertNoPageOverflow(page, label) {
  const dimensions = await page.evaluate(() => ({
    viewport: window.innerWidth,
    document: document.documentElement.scrollWidth,
    body: document.body.scrollWidth
  }));
  assert.ok(dimensions.document <= dimensions.viewport + 1, label + " document overflows: " + JSON.stringify(dimensions));
  assert.ok(dimensions.body <= dimensions.viewport + 1, label + " body overflows: " + JSON.stringify(dimensions));
}

async function openComponentStudio(page) {
  const previousDialog = page.getByRole("dialog", { name: "Photo Studio" });
  if (await previousDialog.count()) await previousDialog.waitFor({ state: "detached" });
  await page.locator("#open-photo-studio").waitFor({ state: "visible" });
  await page.waitForFunction(() => {
    const button = document.querySelector("#open-photo-studio");
    return button instanceof HTMLButtonElement && !button.disabled;
  });
  await page.locator("#open-photo-studio").click();
  const dialog = page.getByRole("dialog", { name: "Photo Studio" });
  await dialog.waitFor({ state: "visible" });
  return dialog;
}

async function activatePhotoTool(page, label, controlLabel) {
  const dialog = page.getByRole("dialog", { name: "Photo Studio" });
  const toolButtons = dialog.locator('nav[aria-label="Photo editing tools"] button');
  await toolButtons.filter({ hasText: label }).first().click();
  await page.waitForFunction(({ tool, field }) => {
    const root = document.querySelector('[role="dialog"]');
    const active = Array.from(root?.querySelectorAll('nav[aria-label="Photo editing tools"] button') ?? [])
      .some(button => button.textContent?.trim() === tool && button.getAttribute("aria-pressed") === "true");
    const controlLabel = Array.from(root?.querySelectorAll("label") ?? [])
      .find(item => item.textContent?.trim().startsWith(field));
    return active && Boolean(controlLabel?.querySelector("input, select, textarea"));
  }, { tool: label, field: controlLabel });
}

function getEditorControl(page, label) {
  return page.locator('[role="dialog"] label')
    .filter({ hasText: label })
    .locator("input, select, textarea")
    .first();
}

async function setControlValue(page, label, value) {
  await getEditorControl(page, label).evaluate((element, nextValue) => {
    const prototype = element instanceof HTMLSelectElement ? HTMLSelectElement.prototype :
      element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set;
    if (!setter) throw new Error("Could not update control: " + element.tagName);
    setter.call(element, nextValue);
    element.dispatchEvent(new Event("input", { bubbles: true }));
    element.dispatchEvent(new Event("change", { bubbles: true }));
    element.blur();
  }, value);
}

async function samplePhotoBackground(page) {
  await page.getByRole("button", { name: "Background" }).click();
  await page.getByRole("button", { name: "Set background #dff3e7" }).click();
  await setControlValue(page, "Background tolerance", "48");
  assert.equal(await getEditorControl(page, "Background tolerance").inputValue(), "48");
  await page.getByRole("button", { name: "Reset background" }).click();
  assert.equal(await getEditorControl(page, "Background tolerance").inputValue(), "30");
  assert.equal((await page.getByLabel("Replacement background").inputValue()).toLowerCase(), "#ffffff");
  await page.getByRole("button", { name: /Pick background from photo/ }).click();
  await page.locator('[data-testid="photo-studio-preview-canvas"]').evaluate(canvas => {
    const bounds = canvas.getBoundingClientRect();
    const sampleXmm = Number(canvas.getAttribute("data-background-sample-x-mm"));
    const sampleYmm = Number(canvas.getAttribute("data-background-sample-y-mm"));
    if (!Number.isFinite(sampleXmm) || sampleXmm <= 0) {
      throw new Error("Photo Studio must expose the first photo tile's background sample X coordinate.");
    }
    if (!Number.isFinite(sampleYmm) || sampleYmm <= 0) {
      throw new Error("Photo Studio must expose the first photo tile's background sample Y coordinate.");
    }
    const clientX = bounds.left + bounds.width * (sampleXmm / 210);
    const clientY = bounds.top + bounds.height * (sampleYmm / 297);
    canvas.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX, clientY }));
  });
  const sampled = await page.locator('[data-testid="photo-studio-background-state"] strong').textContent();
  assert.ok(sampled && sampled.trim() !== "Not selected", "The selected photo background colour was not sampled.");
  await setControlValue(page, "Replacement background", "#dcecff");
  await setControlValue(page, "Background tolerance", "42");
  return sampled.trim();
}

function verifyPdfEnvelope(buffer, expectedPages) {
  const bytes = Buffer.from(buffer);
  const value = bytes.toString("latin1");
  assert.ok(value.startsWith("%PDF-1.4"), "Prepared file must be a PDF.");
  assert.ok(value.includes("/MediaBox [0 0 595.275590551 841.88976378]"), "Every page must use exact A4 physical dimensions.");
  assert.match(value, new RegExp("/Count " + expectedPages + "\\b"));
  assert.equal((value.match(/\/Type \/Page \/Parent 2 0 R/g) ?? []).length, expectedPages);
  assert.equal((value.match(/\/MediaBox \[0 0 595\.275590551 841\.88976378\]/g) ?? []).length, expectedPages,
    "Every page must declare the exact A4 MediaBox.");
  const xrefOffset = Number(value.match(/startxref\n(\d+)\n%%EOF/)?.[1]);
  assert.ok(Number.isInteger(xrefOffset), "PDF must declare its cross-reference byte offset.");
  assert.ok(value.slice(xrefOffset).startsWith("xref\n"), "The declared byte offset must point to xref.");
  let objectNumber = 1;
  for (const match of value.slice(xrefOffset).matchAll(/(\d{10}) 00000 n/g)) {
    const offset = Number(match[1]);
    assert.ok(value.slice(offset).startsWith(`${objectNumber} 0 obj`), "PDF object offsets must be correct.");
    objectNumber += 1;
  }
  assert.ok(objectNumber > 2, "PDF must contain catalog, page tree and image/content objects.");
  assert.ok(value.endsWith("%%EOF") || value.endsWith("%%EOF\n"), "PDF must have a final EOF marker.");
  return { bytes: bytes.length, pageCount: expectedPages, a4MediaBox: true, validXrefObjects: objectNumber - 1 };
}

function verifyPreparedPdfResult(result, expected) {
  assert.equal(result.mediaType, "application/pdf");
  assert.equal(result.sizeCode, expected.sizeCode);
  assert.equal(result.photoCount, expected.photoCount);
  assert.equal(result.photosPerPage, expected.photosPerPage);
  assert.equal(result.pageCount, expected.pageCount);
  assert.ok(result.bytes > 1000, "The prepared PDF must contain real output bytes.");
  assert.equal(result.mediaBoxCount, expected.pageCount,
    "Every exported page must use the exact A4 MediaBox.");
  assert.equal(result.fullPageImageTransforms, expected.pageCount,
    "Each A4 PDF page must place one full-page raster.");
  assert.equal(result.previewPageCount, expected.pageCount);
  assert.equal(result.pdfImageSizes.length, expected.pageCount);
  for (const [width, height] of result.pdfImageSizes) {
    assert.equal(width, 2480, "Every page image must be 300-DPI A4 width.");
    assert.equal(height, 3508, "Every page image must be 300-DPI A4 height.");
  }
  assert.deepEqual(result.embeddedPreviewPages, Array(expected.pageCount).fill(true),
    "The PDF must embed the exact JPEG page previews reviewed in Photo Studio.");
  assert.equal(result.pixelProbes.length, expected.photoCount,
    "Every requested photo tile must have a raster pixel probe.");
  const expectedPagePhotoCounts = Array.from({ length: expected.pageCount }, (_, index) =>
    Math.min(expected.photosPerPage, expected.photoCount - index * expected.photosPerPage));
  const observedPagePhotoCounts = expectedPagePhotoCounts.map((_, index) =>
    result.pixelProbes.filter(probe => probe.page === index + 1).length);
  assert.deepEqual(observedPagePhotoCounts, expectedPagePhotoCounts,
    "The raster must contain the expected number of photo tiles on each page.");
  for (const probe of result.pixelProbes) {
    assert.equal(probe.rgb.length, 3);
    assert.equal(probe.backgroundRgb.length, 3);
    assert.ok(probe.rgb.some(channel => channel < 245),
      "Each photo centre must contain image pixels, not blank paper.");
  }
  return result;
}

async function verifyComponentResult(page, expectedMode, expectedPages = 2) {
  const raw = await page.locator("#photo-studio-result").textContent();
  assert.ok(raw, "The Photo Studio did not return a prepared PDF result.");
  const result = JSON.parse(raw);
  assert.equal(result.mode, expectedMode);
  return verifyPreparedPdfResult(result, {
    sizeCode: "35x45",
    photoCount: 17,
    photosPerPage: 9,
    pageCount: expectedPages
  });
}

async function exerciseComponentFullFlow(browser, scenario) {
  const context = await browser.newContext({ viewport: scenario.viewport, deviceScaleFactor: 1 });
  const page = await context.newPage();
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(error.message));
  const url = baseUrl + "/tests/photo-studio.browser.html?mode=" + scenario.mode + "&theme=" + scenario.theme +
    "&jobId=SP-PHOTO-STUDIO-" + scenario.mode.toUpperCase() + "-001";
  await page.goto(url, { waitUntil: "networkidle" });
  const originalFingerprint = await page.locator("#photo-studio-source-fingerprint").textContent();
  assert.ok(originalFingerprint, "The synthetic source photo was not created.");
  await openComponentStudio(page);
  await page.keyboard.press("Escape");
  await page.getByRole("dialog").waitFor({ state: "detached" });
  assert.equal(await page.evaluate(() => document.activeElement?.id), "open-photo-studio", "Escape must restore focus to the opener.");
  await openComponentStudio(page);

  await page.getByRole("button", { name: "Crop" }).click();
  await setControlValue(page, "Zoom", "1.35");
  await setControlValue(page, "Horizontal position", "55");
  await setControlValue(page, "Vertical position", "45");
  await page.getByRole("button", { name: "Reset crop" }).click();
  assert.equal(await getEditorControl(page, "Zoom").inputValue(), "1");
  assert.equal(await getEditorControl(page, "Horizontal position").inputValue(), "50");
  assert.equal(await getEditorControl(page, "Vertical position").inputValue(), "50");
  await setControlValue(page, "Zoom", "1.35");
  await setControlValue(page, "Horizontal position", "55");
  await setControlValue(page, "Vertical position", "45");
  await activatePhotoTool(page, "Looks", "Brightness");
  await page.getByRole("button", { name: /Black & white/ }).click();
  await setControlValue(page, "Brightness", "108");
  await setControlValue(page, "Contrast", "120");
  await setControlValue(page, "Colour", "82");
  await page.getByRole("button", { name: "Warm" }).click();
  await activatePhotoTool(page, "Sheet", "Photos per A4 page");

  const size = page.getByLabel("Photo size");
  for (const sizeCode of ["25x35", "2x2", "4x6", "35x45"]) {
    await size.selectOption(sizeCode);
    await assertNoPageOverflow(page, scenario.mode + " " + sizeCode);
  }

  const quantityInput = page.getByLabel("Number of photos");
  await quantityInput.fill("100");
  await quantityInput.press("Tab");
  await page.waitForFunction(() => {
    const input = document.querySelector('[role="dialog"] input[aria-label="Number of photos"]');
    const rows = Array.from(document.querySelectorAll('[data-testid="photo-studio-sheet-summary"] > div'));
    const pages = rows.find(row => row.textContent?.includes("A4 pages in this job"))?.querySelector("strong")?.textContent;
    return input instanceof HTMLInputElement && input.value === "100" && pages === "4";
  }, null, { timeout: 10000 });
  await page.getByLabel("Photos per A4 page").selectOption("25");
  let quantitySummary = await page.locator('[data-testid="photo-studio-sheet-summary"]').innerText();
  assert.match(quantitySummary, /A4 pages in this job\s+4/, "100 photos at 25 per page must produce four A4 pages.");
  await page.getByLabel("Number of photos").fill("1");
  await page.getByLabel("Photos per A4 page").selectOption("1");
  quantitySummary = await page.locator('[data-testid="photo-studio-sheet-summary"]').innerText();
  assert.match(quantitySummary, /A4 pages in this job\s+1/, "One requested photo must produce one page.");
  await page.getByLabel("Number of photos").fill("17");
  await page.getByLabel("Number of photos").press("Tab");
  await page.getByLabel("Photos per A4 page").selectOption("9");
  await assertNoPageOverflow(page, scenario.mode + " quantity-17");
  await samplePhotoBackground(page);

  await activatePhotoTool(page, "Sheet", "Photos per A4 page");
  const captionToggle = page.getByRole("checkbox", { name: /Add name and DOB to each photo/ });
  assert.equal(await page.getByRole("button", { name: "Text" }).isDisabled(), true,
    "Caption styling must stay disabled until the owner opts in.");
  await captionToggle.check();
  assert.equal(await page.getByRole("button", { name: "Text" }).isDisabled(), false,
    "Caption styling must become available after explicit opt-in.");
  await page.getByLabel("Name for form (optional)").fill("Photo Studio QA");
  await page.getByLabel("Date of birth (optional)").fill("2000-01-02");
  await page.getByRole("button", { name: "Text" }).click();
  await page.getByLabel("Caption font size").selectOption("7");
  await setControlValue(page, "Caption colour", "#c02020");
  await activatePhotoTool(page, "Sheet", "Photos per A4 page");

  await size.selectOption("35x45");
  await page.getByLabel("Number of photos").fill("17");
  await page.getByLabel("Number of photos").press("Tab");
  await page.getByLabel("Photos per A4 page").selectOption("9");

  const summary = await page.locator('[data-testid="photo-studio-sheet-summary"]').innerText();
  assert.match(summary, /A4 pages in this job\s+2/);
  assert.match(summary, /Final A4 page:\s+8 photos/);
  await page.screenshot({ path: join(artifactDirectory, scenario.mode + "-" + scenario.theme + "-editor.png"), fullPage: true });
  await page.getByRole("button", { name: "Next A4 page" }).click();
  const finalPageLabel = await page.locator('[data-testid="photo-studio-preview-canvas"]').getAttribute("aria-label");
  assert.match(finalPageLabel ?? "", /page 2 of 2 with 8 photos/i);
  await page.getByRole("button", { name: "Previous A4 page" }).click();
  await page.getByRole("button", { name: scenario.mode === "shop" ? "Save sheet to this job" : "Prepare A4 sheet" }).click();
  await page.waitForFunction(() => {
    const result = document.querySelector("#photo-studio-result");
    if (!result?.textContent) return false;
    try { return Boolean(JSON.parse(result.textContent).pageCount); } catch { return false; }
  }, { timeout: 90000 });
  const result = await verifyComponentResult(page, scenario.mode);
  assert.equal(result.sourceFingerprintBefore, originalFingerprint);
  assert.equal(result.sourceFingerprintAfter, originalFingerprint, "The original source file must remain byte-identical after editing and PDF preparation.");
  const targetBackground = [220, 236, 255];
  for (const probe of result.pixelProbes) {
    assert.ok(probe.backgroundRgb.every((value, index) => Math.abs(value - targetBackground[index]) <= 40),
      "Every photo tile must contain the selected replacement background colour.");
  }
  const captionPixelCount = result.pixelProbes.reduce((total, probe) => total + probe.captionRedPixels, 0);
  assert.ok(captionPixelCount > 0, "Opt-in red name/DOB captions must appear in the exported raster.");
  assert.equal(pageErrors.length, 0, "Uncaught Photo Studio component errors: " + pageErrors.join("; "));
  await page.screenshot({ path: join(artifactDirectory, scenario.mode + "-" + scenario.theme + "-prepared.png"), fullPage: true });
  await context.close();
  return { label: "component-" + scenario.mode + "-" + scenario.theme, result: { pageCount: result.pageCount, photoCount: result.photoCount } };
}

async function exercisePhotoSizeOutputs(browser) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });
  const page = await context.newPage();
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(error.message));
  await page.goto(baseUrl + "/tests/photo-studio.browser.html?mode=customer&theme=light", { waitUntil: "networkidle" });
  const generated = [];
  for (const sizeCode of ["25x35", "2x2", "4x6"]) {
    await openComponentStudio(page);
    await activatePhotoTool(page, "Sheet", "Photos per A4 page");
    await page.getByLabel("Photo size").selectOption(sizeCode);
    assert.equal(await page.getByLabel("Photo size").inputValue(), sizeCode, "The requested photo size must be selected before export.");
    await setControlValue(page, "Number of photos", "3");
    await page.getByLabel("Photos per A4 page").selectOption("auto");
    const summary = await page.locator('[data-testid="photo-studio-sheet-summary"]').innerText();
    const expectedPerPage = Number(summary.match(/Photos per A4 page\s+(\d+)/)?.[1] ?? 0);
    assert.ok(expectedPerPage >= 1, sizeCode + " must expose a valid automatic page density.");
    await page.getByRole("button", { name: "Prepare A4 sheet" }).click();
    await page.waitForFunction((expectedSize) => {
      const node = document.querySelector("#photo-studio-result");
      if (!node?.textContent) return false;
      try {
        const output = JSON.parse(node.textContent);
        return Boolean(output.pageCount) && output.sizeCode === expectedSize;
      } catch { return false; }
    }, sizeCode, { timeout: 90000 });
    const result = JSON.parse(await page.locator("#photo-studio-result").textContent() ?? "{}");
    const expectedPages = Math.ceil(3 / expectedPerPage);
    verifyPreparedPdfResult(result, {
      sizeCode,
      photoCount: 3,
      photosPerPage: expectedPerPage,
      pageCount: expectedPages
    });
    await page.screenshot({ path: join(artifactDirectory, "photo-size-" + sizeCode + ".png"), fullPage: true });
    generated.push({ sizeCode, requestedPhotos: 3, photosPerPage: expectedPerPage, pageCount: expectedPages });
  }
  assert.equal(pageErrors.length, 0, "Uncaught size-export browser errors: " + pageErrors.join("; "));
  await context.close();
  return { label: "all-supported-photo-sizes-exported", generated };
}

async function exerciseComponentViewport(browser, scenario) {
  const context = await browser.newContext({ viewport: scenario.viewport, deviceScaleFactor: 1 });
  const page = await context.newPage();
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(error.message));
  await page.goto(baseUrl + "/tests/photo-studio.browser.html?mode=" + scenario.mode + "&theme=" + scenario.theme, { waitUntil: "networkidle" });
  await openComponentStudio(page);
  await assertNoPageOverflow(page, scenario.label);
  for (const tool of ["Sheet", "Crop", "Looks", "Background"]) {
    await page.getByRole("button", { name: tool, exact: true }).click();
    await page.locator('[data-testid="photo-studio-tool-panel"]').waitFor({ state: "visible" });
  }
  await page.getByRole("button", { name: scenario.mode === "shop" ? "Save sheet to this job" : "Prepare A4 sheet" }).waitFor({ state: "visible" });
  await page.screenshot({ path: join(artifactDirectory, scenario.label + ".png"), fullPage: true });
  assert.equal(pageErrors.length, 0, "Uncaught viewport errors: " + pageErrors.join("; "));
  await page.keyboard.press("Escape");
  await page.getByRole("dialog").waitFor({ state: "detached" });
  await context.close();
  return { label: scenario.label, viewport: scenario.viewport };
}

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit += 1) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function pngChunk(name, data) {
  const type = Buffer.from(name, "ascii");
  const length = Buffer.alloc(4); length.writeUInt32BE(data.length);
  const checksumInput = Buffer.concat([type, data]);
  const checksum = Buffer.alloc(4); checksum.writeUInt32BE(crc32(checksumInput));
  return Buffer.concat([length, type, data, checksum]);
}
function makeShopFixturePng(width = 360, height = 480) {
  const rows = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y += 1) {
    const row = y * (width * 4 + 1);
    rows[row] = 0;
    for (let x = 0; x < width; x += 1) {
      let red = 216 + Math.round(20 * y / height), green = 232 - Math.round(12 * x / width), blue = 244 - Math.round(18 * y / height);
      const face = ((x - width * .5) / (width * .19)) ** 2 + ((y - height * .35) / (height * .19)) ** 2 <= 1;
      const shirt = y > height * .64 && Math.abs(x - width * .5) < width * (.18 + .25 * (y / height - .64));
      const hair = face && y < height * .29;
      if (face) { red = 225; green = 183; blue = 155; }
      if (hair) { red = 55; green = 63; blue = 76; }
      if (shirt) { red = 48; green = 66; blue = 91; }
      const offset = row + 1 + x * 4;
      rows[offset] = red; rows[offset + 1] = green; rows[offset + 2] = blue; rows[offset + 3] = 255;
    }
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4);
  header[8] = 8; header[9] = 6; header[10] = 0; header[11] = 0; header[12] = 0;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk("IHDR", header),
    pngChunk("IDAT", deflateSync(rows)),
    pngChunk("IEND", Buffer.alloc(0))
  ]);
}

const SHOP_TOKEN = "e30." + Buffer.from(JSON.stringify({ sub: "photo-studio-e2e-owner", tenant_id: "photo-studio-e2e-tenant" })).toString("base64url") + ".photo-studio-e2e-signature";
const PHOTO_JOB_ID = "70000000-0000-4000-8000-000000000007";
const SHOP_PROFILE = {
  id: "photo-studio-e2e-shop", slug: "photo-studio-e2e", businessName: "Photo Studio QA Shop",
  status: "Active", ownerName: "QA Shop Owner", ownerEmail: "owner@example.invalid",
  displayName: "Photo Studio QA Shop", logoUrl: null, accentColor: "#7662E8",
  currency: "INR", timeZone: "Asia/Kolkata", whiteLabelEnabled: false
};
const SHOP_PRICING = {
  isConfigured: true, monoPerSideMinor: 100, colourPerSideMinor: 200,
  monoDuplexPerSheetMinor: 180, colourDuplexPerSheetMinor: 350,
  minimumChargeMinor: 100, photo35x45Minor: 2500, photo25x35Minor: 1800,
  photo2x2Minor: 3000, photo4x6Minor: 6000, version: 1, updatedUtc: "2026-11-08T00:00:00Z"
};
function makePhotoJob(sourcePng, revisedPdf = null) {
  return {
    id: PHOTO_JOB_ID, jobNumber: "SP-PS-QA-007", status: "AWAITING_SHOP_APPROVAL",
    pageCount: revisedPdf ? 2 : 1, copies: 1, colourMode: "Colour", paperSize: "A4",
    isDuplex: false, currency: "INR", quotedAmountMinor: revisedPdf ? 42500 : 2500,
    createdUtc: "2026-11-08T00:00:00Z", storageMode: "CLOUD",
    printKind: revisedPdf ? "PHOTO" : "PHOTO", photoSizeCode: revisedPdf ? "35x45" : null,
    photoCount: revisedPdf ? 17 : 1, photoUnitPriceMinorSnapshot: 2500,
    document: {
      originalFileName: revisedPdf ? "SP-PS-QA-007_photo-sheet.pdf" : "owner-uploaded-source.png",
      mediaType: revisedPdf ? "application/pdf" : "image/png",
      sizeBytes: revisedPdf ? revisedPdf.length : sourcePng.length
    }
  };
}

async function exerciseRealShopOwnerFlow(browser, scenario) {
  const context = await browser.newContext({ viewport: scenario.viewport, deviceScaleFactor: 1 });
  const page = await context.newPage();
  const sourcePng = makeShopFixturePng();
  const sourceHash = createHash("sha256").update(sourcePng).digest("hex");
  const apiRequests = [];
  const unhandledApi = [];
  let revisedPdf = null;
  let revisionRequest = null;
  let revisionSaved = false;
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(error.message));

  await page.route("**/api/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method().toUpperCase();
    apiRequests.push({ method, path });
    const json = value => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(value) });

    if (path === "/api/v1/shop/profile") return json(SHOP_PROFILE);
    if (path === "/api/v1/shop/printers" || path === "/api/v1/shop/qr-stations" ||
        path === "/api/v1/shop/agent-devices" || path === "/api/v1/shop/payments" ||
        path === "/api/v1/shop/refunds") return json([]);
    if (path === "/api/v1/shop/pricing") return json(SHOP_PRICING);
    if (path === "/api/v1/shop/payment-settings") return json({
      upiEnabled: false, upiId: null, payeeName: null, cashEnabled: false,
      instantPaymentEnabled: false, updatedUtc: "2026-11-08T00:00:00Z"
    });
    if (path === "/api/v1/shop/subscription") return json({
      generatedUtc: "2026-11-08T00:00:00Z",
      subscription: { id: "e2e-sub", planCode: "STARTER", status: "ACTIVE", billingInterval: "MONTHLY",
        periodStartUtc: "2026-11-01T00:00:00Z", periodEndUtc: "2026-12-01T00:00:00Z", createdUtc: "2026-11-01T00:00:00Z" },
      plan: null, entitlements: {}
    });
    if (path === "/api/v1/shop/print-jobs" || path.startsWith("/api/v1/shop/print-jobs?")) {
      return json([makePhotoJob(sourcePng, revisedPdf)]);
    }
    if (path === "/api/v1/shop/print-jobs/" + PHOTO_JOB_ID + "/preview-source") {
      return json({ mode: "CLOUD" });
    }
    if (path === "/api/v1/shop/print-jobs/" + PHOTO_JOB_ID + "/document" && method === "GET") {
      return route.fulfill({
        status: 200,
        contentType: revisionSaved && revisedPdf ? "application/pdf" : "image/png",
        body: revisionSaved && revisedPdf ? revisedPdf : sourcePng
      });
    }
    if (path === "/api/v1/shop/print-jobs/" + PHOTO_JOB_ID + "/photo-revision" && method === "POST") {
      const body = request.postDataBuffer();
      assert.ok(body, "Shop owner photo revision must upload a multipart PDF to the original job.");
      const raw = body.toString("latin1");
      const field = name => {
        const marker = 'name="' + name + '"\r\n\r\n';
        const start = raw.indexOf(marker);
        assert.ok(start >= 0, "Missing photo revision field: " + name);
        const valueStart = start + marker.length;
        const end = raw.indexOf("\r\n", valueStart);
        return raw.slice(valueStart, end);
      };
      const fileName = raw.match(/name="file"; filename="([^"]+)"/)?.[1] ?? "";
      const pdfStart = body.indexOf(Buffer.from("%PDF-1.4"));
      const eofStart = body.lastIndexOf(Buffer.from("%%EOF"));
      assert.ok(pdfStart >= 0 && eofStart > pdfStart, "The shop save request must contain a complete PDF file.");
      revisedPdf = body.subarray(pdfStart, eofStart + 5);
      const requestedCount = Number(field("photoCount"));
      const perPage = Number(field("photosPerPage"));
      const sizeCode = field("photoSizeCode");
      const pdfText = revisedPdf.toString("latin1");
      const pageCount = Math.ceil(requestedCount / perPage);
      verifyPdfEnvelope(revisedPdf, pageCount);
      assert.equal(requestedCount, 17);
      assert.equal(perPage, 9);
      assert.equal(sizeCode, "35x45");
      assert.ok(fileName.toLowerCase().endsWith(".pdf"));
      revisionRequest = { jobId: PHOTO_JOB_ID, method, fileName, requestedCount, perPage, sizeCode, pdfBytes: revisedPdf.length, pageCount };
      revisionSaved = true;
      return json({
        id: PHOTO_JOB_ID, status: "AWAITING_SHOP_APPROVAL", pageCount, printKind: "PHOTO",
        photoSizeCode: sizeCode, photoCount: requestedCount, photoUnitPriceMinorSnapshot: 2500,
        quotedAmountMinor: requestedCount * 2500, pricingVersion: 1,
        quoteExpiresUtc: "2026-11-09T00:00:00Z", originalFileName: fileName,
        mediaType: "application/pdf", sizeBytes: revisedPdf.length
      });
    }

    unhandledApi.push({ method, path });
    return route.fulfill({ status: 404, contentType: "application/json", body: JSON.stringify({ message: "Unhandled isolated Photo Studio QA API route." }) });
  });

  await page.addInitScript(({ sessionToken }) => {
    sessionStorage.setItem("qrtoprint.auth.shop", JSON.stringify({
      tokenType: "Bearer", accessToken: sessionToken, refreshToken: "qa-refresh-token",
      accessTokenExpiresUtc: "2099-01-01T00:00:00.000Z"
    }));
  }, { sessionToken: SHOP_TOKEN });

  await page.goto(baseUrl + "/shop", { waitUntil: "networkidle" });
  await page.locator(".app-shell").waitFor({ state: "visible", timeout: 30000 });
  await page.waitForFunction(() =>
    document.querySelector('button[aria-label="Order Journey"]')?.classList.contains("active") === true,
    { timeout: 30000 }
  );
  await page.getByRole("button", { name: "Edit photo" }).waitFor({ state: "visible", timeout: 30000 });
  await page.getByRole("button", { name: "Edit photo" }).click();
  const dialog = page.getByRole("dialog", { name: "Photo Studio" });
  await dialog.waitFor({ state: "visible" });
  assert.match(await dialog.innerText(), /SHOP · EDIT EXISTING JOB/);
  await assertNoPageOverflow(page, "real-shop-owner-mobile-photo-studio");

  await page.getByRole("button", { name: "Crop" }).click();
  await setControlValue(page, "Zoom", "1.25");
  await setControlValue(page, "Horizontal position", "48");
  await setControlValue(page, "Vertical position", "52");
  await activatePhotoTool(page, "Looks", "Brightness");
  await page.getByRole("button", { name: "Crisp" }).click();
  await page.getByRole("button", { name: "Background" }).click();
  await page.getByRole("button", { name: /Pick background from photo/ }).click();
  await page.locator('[data-testid="photo-studio-preview-canvas"]').evaluate(canvas => {
    const bounds = canvas.getBoundingClientRect();
    canvas.dispatchEvent(new MouseEvent("click", {
      bubbles: true, clientX: bounds.left + bounds.width * .07,
      clientY: bounds.top + bounds.height * .37
    }));
  });
  await assertNoPageOverflow(page, "real-shop-owner-edited-preview");
  await activatePhotoTool(page, "Sheet", "Photos per A4 page");
  await page.getByLabel("Photo size").selectOption("35x45");
  await page.getByLabel("Number of photos").fill("17");
  await page.getByLabel("Number of photos").press("Tab");
  await page.getByLabel("Photos per A4 page").selectOption("9");
  await page.getByRole("checkbox", { name: /Add name and DOB to each photo/ }).check();
  await page.getByLabel("Name for form (optional)").fill("Shop Owner QA");
  await page.getByLabel("Date of birth (optional)").fill("1998-05-17");
  await page.getByRole("button", { name: "Text" }).click();
  await page.getByLabel("Caption font size").selectOption("7");
  await setControlValue(page, "Caption colour", "#202020");
  await activatePhotoTool(page, "Sheet", "Photos per A4 page");
  await page.getByRole("button", { name: "Next A4 page" }).click();
  const finalPage = await page.locator('[data-testid="photo-studio-preview-canvas"]').getAttribute("aria-label");
  assert.match(finalPage ?? "", /page 2 of 2 with 8 photos/i);
  await page.getByRole("button", { name: "Previous A4 page" }).click();
  await page.screenshot({ path: join(artifactDirectory, "real-shop-owner-existing-job.png"), fullPage: true });
  await page.getByRole("button", { name: "Save sheet to this job" }).click();
  await page.getByText(/The enhanced multi-page A4 photo PDF is saved to the same print job/).waitFor({ state: "visible", timeout: 90000 });
  assert.ok(revisionRequest, "The production shop-photo save callback must send a revision request.");
  assert.equal(revisionRequest.jobId, PHOTO_JOB_ID, "Saving an edit must revise the same existing job, not create a new job.");
  assert.equal(revisionSaved, true);
  assert.equal(createHash("sha256").update(sourcePng).digest("hex"), sourceHash, "The uploaded source bytes must remain unchanged.");
  assert.equal(apiRequests.some(item => item.method === "POST" && /\/payments|\/approve|\/dispatch|\/print$/.test(item.path)), false,
    "This test must not attempt a payment, approval, dispatch or physical print.");
  assert.deepEqual(unhandledApi, [], "Unexpected API calls: " + JSON.stringify(unhandledApi));

  const preview = page.getByRole("dialog");
  await preview.waitFor({ state: "visible" });
  assert.match(await preview.innerText(), /17 35 × 45 mm photos · 9 per A4 page · 2 A4 PDF page\(s\)/);
  assert.ok(revisionRequest.pdfBytes > 1000);
  await page.screenshot({ path: join(artifactDirectory, "real-shop-owner-saved-pdf.png"), fullPage: true });
  assert.equal(pageErrors.length, 0, "Uncaught shop workspace errors: " + pageErrors.join("; "));
  await context.close();
  return {
    label: "real-shop-owner-existing-job",
    originalJobId: PHOTO_JOB_ID,
    savedJobId: revisionRequest.jobId,
    photoCount: revisionRequest.requestedCount,
    photosPerPage: revisionRequest.perPage,
    pageCount: revisionRequest.pageCount,
    uploadedPdfBytes: revisionRequest.pdfBytes,
    originalSha256: sourceHash,
    paymentOrPhysicalPrintAttempted: false
  };
}

let server = null;
let browser = null;
const results = [];
try {
  if (!process.env.SMARTPRINT_PHOTO_STUDIO_BASE_URL) {
    server = startServer();
    await waitForServer(server);
  }
  browser = await chromium.launch({ headless });
  results.push(await exerciseComponentFullFlow(browser, {
    mode: "customer", theme: "light", viewport: { width: 1440, height: 960 }
  }));
  results.push(await exerciseComponentFullFlow(browser, {
    mode: "shop", theme: "dark", viewport: { width: 390, height: 844 }
  }));
  results.push(await exerciseRealShopOwnerFlow(browser, {
    viewport: { width: 390, height: 844 }
  }));
  results.push(await exercisePhotoSizeOutputs(browser));
  results.push(await exerciseComponentViewport(browser, {
    mode: "customer", theme: "light", label: "customer-mobile-320x568",
    viewport: { width: 320, height: 568 }
  }));
  results.push(await exerciseComponentViewport(browser, {
    mode: "shop", theme: "light", label: "shop-tablet-768x1024",
    viewport: { width: 768, height: 1024 }
  }));
  writeFileSync(join(artifactDirectory, "summary.json"), JSON.stringify({
    completedAt: new Date().toISOString(),
    browser: "Chromium",
    executedScenarios: results,
    status: "PASS"
  }, null, 2));
  process.stdout.write("Photo Studio browser acceptance passed: " + results.length + " scenarios.\n");
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
