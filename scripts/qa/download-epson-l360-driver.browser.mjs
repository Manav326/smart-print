import assert from "node:assert/strict";
import { mkdirSync, statSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { pathToFileURL } from "node:url";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
const outputPath = process.env.SMARTPRINT_EPSON_L360_PACKAGE_OUTPUT;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR is required.");
if (!outputPath) throw new Error("SMARTPRINT_EPSON_L360_PACKAGE_OUTPUT is required.");

const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const target = resolve(outputPath);
mkdirSync(dirname(target), { recursive: true });

const officialSupportUrl =
  "https://www.epson.co.in/Support/Printers/All-In-One/L-Series/Epson-L360/s/SPT_C11CE55501?review-filter=Windows+10+64-bit";
// This module URL is the official Download Center page linked by Epson's L360
// product support page for its Windows x64 Printer Driver v2.60.00.
const officialDownloadCenterUrl =
  "https://download-center.epson.com/download/?device_id=L360+Series&module_id=39ac0a8a-10f0-4760-8949-cd1dd0ad646c%3A2.60.00&os=WIN1064&region=GB";
const expectedMinimumBytes = 5 * 1024 * 1024;
const expectedFilePattern = /L360_x64_260JAHomeExportAsiaML\.exe/i;

const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({
    acceptDownloads: true,
    viewport: { width: 1440, height: 1000 },
    userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36"
  });
  const page = await context.newPage();
  page.on("response", response => {
    const url = response.url();
    if (/epson|download/i.test(url)) {
      console.log("[EPSON-L360-DOWNLOAD] HTTP " + response.status() + " " + url);
    }
  });
  page.on("requestfailed", request => {
    if (/epson|download/i.test(request.url())) {
      console.log("[EPSON-L360-DOWNLOAD] REQUEST FAILED " + request.url() + " " + (request.failure()?.errorText ?? ""));
    }
  });

  console.log("[EPSON-L360-DOWNLOAD] Verifying the L360 Windows x64 driver on Epson India support.");
  const supportResponse = await page.goto(officialSupportUrl, { waitUntil: "domcontentloaded", timeout: 90000 });
  await page.locator("body").waitFor({ state: "visible" });
  const supportText = await page.locator("body").innerText();
  assert.match(supportText, /Epson L360/i, "Epson India support page did not identify the L360 product.");
  assert.match(supportText, /Printer Driver for Win 64-bit/i,
    "Epson India support page did not list the L360 64-bit printer driver.");
  console.log("[EPSON-L360-DOWNLOAD] Epson India support page HTTP " + (supportResponse?.status() ?? "unknown"));

  console.log("[EPSON-L360-DOWNLOAD] Opening the Epson-published Download Center module page for v2.60.00.");
  const downloadCenterResponse = await page.goto(officialDownloadCenterUrl, {
    waitUntil: "domcontentloaded",
    timeout: 90000
  });
  await page.locator("body").waitFor({ state: "visible" });
  await page.waitForLoadState("networkidle", { timeout: 25000 }).catch(() => undefined);
  const pageInfo = await Promise.all(page.frames().map(async frame => {
    try {
      return await frame.evaluate(() => ({
        url: location.href,
        title: document.title,
        text: (document.body?.innerText ?? "").slice(0, 3500),
        links: Array.from(document.querySelectorAll("a[href]")).map(anchor => ({
          text: (anchor.textContent ?? "").trim().slice(0, 120),
          href: anchor.href,
          target: anchor.target
        })).filter(item => /download|L360|\.exe|\/f\/module\//i.test(item.text + " " + item.href)).slice(0, 40),
        controls: Array.from(document.querySelectorAll("button,input[type=submit],input[type=checkbox],label"))
          .map(element => ({
            tag: element.tagName,
            text: (element.textContent ?? "").trim().slice(0, 120),
            type: element.getAttribute("type") ?? "",
            checked: element.checked ?? false,
            label: element.labels?.[0]?.textContent?.trim() ?? "",
            value: element.value ?? ""
          })).slice(0, 40)
      }));
    } catch (error) {
      return { url: frame.url(), error: String(error) };
    }
  }));
  console.log("[EPSON-L360-DOWNLOAD] Download Center HTTP " + (downloadCenterResponse?.status() ?? "unknown"));
  console.log("[EPSON-L360-DOWNLOAD] Download Center frame diagnostics: " + JSON.stringify(pageInfo));

  const frames = page.frames();
  let directDownload = null;
  let downloadButtonFrame = null;

  for (const frame of frames) {
    try {
      const candidate = await frame.locator("a[href]").evaluateAll(anchors => anchors.map(anchor => ({
        href: anchor.href,
        text: (anchor.textContent ?? "").trim()
      })).find(item =>
        /L360_x64_260JAHomeExportAsiaML\.exe/i.test(item.href) ||
        /\/f\/module\/[^/]+\/L360_x64_260JAHomeExportAsiaML\.exe/i.test(item.href)
      ) ?? null);
      if (candidate?.href) {
        directDownload = { frame, href: candidate.href };
        break;
      }

      const downloadControls = await frame.locator("a,button,input[type=submit]").evaluateAll(elements =>
        elements.map(element => ({
          tag: element.tagName,
          text: (element.textContent ?? "").trim(),
          href: element.href ?? "",
          value: element.value ?? ""
        })).filter(item => /^(download|proceed to download|download now)$/i.test(item.text || item.value))
      );
      if (downloadControls.length > 0) {
        downloadButtonFrame = frame;
      }
    } catch {
      // Cross-origin and transient frames are inspected independently; a failed
      // frame must not prevent checking the actual Epson Download Center frame.
    }
  }

  if (directDownload) {
    console.log("[EPSON-L360-DOWNLOAD] Found the official Epson binary link: " + directDownload.href);
    const exactLink = directDownload.frame.locator("a[href]").evaluateAll(anchors =>
      anchors.find(anchor => /L360_x64_260JAHomeExportAsiaML\.exe/i.test(anchor.href))?.href ?? null
    );
    const href = await exactLink;
    if (!href) throw new Error("The Epson binary link disappeared before it could be activated.");
    const downloadPage = await context.newPage();
    const downloadPromise = downloadPage.waitForEvent("download", { timeout: 90000 });
    try {
      await downloadPage.goto(href, { waitUntil: "commit", timeout: 90000 });
    } catch (error) {
      if (!String(error).includes("ERR_ABORTED")) throw error;
    }
    const download = await downloadPromise;
    await download.saveAs(target);
  } else if (downloadButtonFrame) {
    const control = downloadButtonFrame.locator("a,button,input[type=submit]").filter({
      hasText: /^(download|proceed to download|download now)$/i
    }).last();
    await control.evaluate(element => {
      if (element instanceof HTMLAnchorElement) element.target = "_self";
    });
    const downloadPromise = page.waitForEvent("download", { timeout: 90000 });
    await control.click({ timeout: 20000 });
    const download = await downloadPromise;
    await download.saveAs(target);
  } else {
    throw new Error(
      "The official Epson L360 Download Center module page exposed neither the v2.60.00 binary link nor its Download control. HTTP " +
      (downloadCenterResponse?.status() ?? "unknown") + ". Diagnostics: " + JSON.stringify(pageInfo)
    );
  }

  const file = statSync(target);
  assert.ok(file.size >= expectedMinimumBytes,
    "Epson driver download is unexpectedly small: " + file.size + " bytes");
  console.log("[EPSON-L360-DOWNLOAD] Saved official Epson package: " + target + " (" + file.size + " bytes).");
} finally {
  await browser.close();
}
