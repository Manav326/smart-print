import assert from "node:assert/strict";
import { mkdirSync, readFileSync } from "node:fs";
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
const baseUrl = process.env.SMARTPRINT_EPSON_L360_UI_BASE_URL ?? "http://127.0.0.1:4175";
mkdirSync(artifactDirectory, { recursive: true });

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
  connectionType: "USB",
  outputMode: "PHYSICAL",
  connectionAddress: null,
  queueName: capabilities.queueName ?? "SmartPrint CI Epson L360",
  capabilitiesJson: JSON.stringify(capabilities),
  status: "Ready",
  isActive: true,
  isDefault: true,
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
      createdUtc: new Date().toISOString(),
      lastSeenUtc: new Date().toISOString(),
      revokedUtc: null,
      printerCount: 1,
      localEndpoints: []
    }];
    else if (path.endsWith("/qr-stations") || path.endsWith("/print-jobs") ||
             path.endsWith("/payments") || path.endsWith("/refunds")) value = [];
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
  const facts = await details.locator(".printer-capability-facts").innerText();
  assert.match(facts, /EPSON L360|Epson L360/i);
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

  console.log("[EPSON-L360-SHOP-OWNER] PASS: Actual Epson driver capability snapshot is visible in shop-owner Printers details in desktop light/dark and mobile light layouts.");
} finally {
  if (browser) await browser.close();
  stopServer(server);
}
