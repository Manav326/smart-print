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
const expectedMinimumBytes = 5 * 1024 * 1024;

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

  console.log("[EPSON-L360-DOWNLOAD] Opening Epson India product support page.");
  await page.goto(officialSupportUrl, { waitUntil: "domcontentloaded", timeout: 90000 });
  await page.locator("body").waitFor({ state: "visible" });

  const driverLink = await page.locator("a[href]").evaluateAll(anchors => {
    const candidates = anchors.map(anchor => ({
      text: (anchor.textContent ?? "").trim(),
      href: anchor.href
    })).filter(item => {
      let parsed;
      try { parsed = new URL(item.href); } catch { return false; }
      return /DriverDownloadInfo\.do/i.test(parsed.pathname) &&
        parsed.searchParams.get("PRN")?.toLowerCase() === "l360 series" &&
        parsed.searchParams.get("CTI") === "4" &&
        parsed.searchParams.get("OSC") === "W1064";
    });
    return candidates[0] ?? null;
  });

  if (!driverLink?.href) {
    const links = await page.locator("a[href]").evaluateAll(anchors =>
      anchors.map(anchor => ({
        text: (anchor.textContent ?? "").trim().slice(0, 80),
        href: anchor.href
      })).filter(item => /download/i.test(item.text + " " + item.href)).slice(-20)
    );
    throw new Error(
      "Epson India did not expose the L360 64-bit driver link. Download-related links: " +
      JSON.stringify(links)
    );
  }

  console.log("[EPSON-L360-DOWNLOAD] Following Epson's driver-download flow: " + driverLink.href);
  await page.goto(driverLink.href, { waitUntil: "domcontentloaded", timeout: 90000 });
  await page.locator("body").waitFor({ state: "visible" });
  await page.waitForLoadState("networkidle", { timeout: 20000 }).catch(() => undefined);

  const pageInfo = await page.evaluate(() => ({
    url: location.href,
    title: document.title,
    text: (document.body?.innerText ?? "").slice(0, 5000),
    links: Array.from(document.querySelectorAll("a[href]")).map(anchor => ({
      text: (anchor.textContent ?? "").trim().slice(0, 100),
      href: anchor.href
    })).filter(item =>
      /download|L360|\.exe|\/module\//i.test(item.text + " " + item.href)
    ).slice(0, 40),
    controls: Array.from(document.querySelectorAll("button,input[type=submit],input[type=checkbox],label"))
      .map(element => ({
        tag: element.tagName,
        text: (element.textContent ?? "").trim().slice(0, 100),
        type: element.getAttribute("type") ?? "",
        checked: element.checked ?? false,
        label: element.labels?.[0]?.textContent?.trim() ?? "",
        value: element.value ?? ""
      })).slice(0, 40)
  }));
  console.log("[EPSON-L360-DOWNLOAD] Download page: " + JSON.stringify(pageInfo));

  const downloadLink = await page.locator("a[href]").evaluateAll(anchors => {
    const candidates = anchors.map(anchor => ({
      text: (anchor.textContent ?? "").trim(),
      href: anchor.href
    })).filter(item =>
      /L360_x64_260JAHomeExportAsiaML\.exe/i.test(item.href) ||
      /\/f\/module\/[^/]+\/L360_x64_260JAHomeExportAsiaML\.exe/i.test(item.href)
    );
    return candidates[0] ?? null;
  });

  // The support site can land on the Download Center metadata page first.
  // Use its live browser session for the final file request.
  if (downloadLink?.href) {
    console.log("[EPSON-L360-DOWNLOAD] Found official binary link: " + downloadLink.href);
    const binaryPage = await context.newPage();
    const downloadPromise = binaryPage.waitForEvent("download", { timeout: 90000 });
    try {
      await binaryPage.goto(downloadLink.href, { waitUntil: "commit", timeout: 90000 });
    } catch (error) {
      if (!String(error).includes("ERR_ABORTED")) throw error;
    }
    const download = await downloadPromise;
    await download.saveAs(target);
  } else {
    const downloadControls = await page.locator("a,button").evaluateAll(elements =>
      elements.map(element => ({
        tag: element.tagName,
        text: (element.textContent ?? "").trim(),
        href: element.href ?? ""
      })).filter(item => /download|立即下载/i.test(item.text + " " + item.href))
    );
    if (downloadControls.length === 0) {
      throw new Error(
        "Epson Download Center exposed no download link or control. Page diagnostics: " +
        JSON.stringify(pageInfo)
      );
    }

    const trigger = page.locator("a,button").filter({ hasText: /download|立即下载/i }).last();
    await trigger.evaluate(element => {
      if (element instanceof HTMLAnchorElement) element.target = "_self";
    });
    const downloadPromise = page.waitForEvent("download", { timeout: 90000 });
    await trigger.click({ timeout: 15000 });
    const download = await downloadPromise;
    await download.saveAs(target);
  }

  const file = statSync(target);
  assert.ok(file.size >= expectedMinimumBytes,
    "Epson driver download is unexpectedly small: " + file.size + " bytes");
  console.log("[EPSON-L360-DOWNLOAD] Saved official Epson package: " + target + " (" + file.size + " bytes).");
} finally {
  await browser.close();
}
