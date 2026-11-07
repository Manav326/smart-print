import assert from "node:assert/strict";
import { mkdirSync, writeFileSync, appendFileSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR is required.");
const qaConfig = JSON.parse(readFileSync(new URL("./smartprint-qa.config.json", import.meta.url), "utf8"));
const rawUrl = (process.env.SMARTPRINT_LIVE_QA_BASE_URL || "").trim();
if (!rawUrl) throw new Error("SMARTPRINT_LIVE_QA_BASE_URL is required.");
const parsedBase = new URL(rawUrl);
if (parsedBase.protocol !== "https:" || !parsedBase.hostname || parsedBase.username || parsedBase.password ||
    !["", "/"].includes(parsedBase.pathname) || parsedBase.search || parsedBase.hash) {
  throw new Error("SMARTPRINT_LIVE_QA_BASE_URL must be a plain HTTPS origin.");
}
const baseUrl = parsedBase.origin;
const outputDir = process.env.LIVE_QA_ARTIFACT_DIR || "artifacts/live-environment-qa";
const screenshots = join(outputDir, "screenshots");
const traces = join(outputDir, "traces");
mkdirSync(screenshots, { recursive: true });
mkdirSync(traces, { recursive: true });

const isEnabled = key => ["true", "1", "yes"].includes((process.env[key] || "").toLowerCase());
const runShop = isEnabled("LIVE_QA_CHECK_SHOP_WORKSPACE");
const runAdmin = isEnabled("LIVE_QA_CHECK_PLATFORM_ADMIN");
const runStation = isEnabled("LIVE_QA_CHECK_CUSTOMER_STATION");
const shopEmail = process.env.SMARTPRINT_LIVE_QA_SHOP_EMAIL || "";
const shopPassword = process.env.SMARTPRINT_LIVE_QA_SHOP_PASSWORD || "";
const adminEmail = process.env.SMARTPRINT_LIVE_QA_ADMIN_EMAIL || "";
const adminPassword = process.env.SMARTPRINT_LIVE_QA_ADMIN_PASSWORD || "";
const stationPath = (process.env.SMARTPRINT_LIVE_QA_STATION_PATH || "").trim();

if (runShop && (!shopEmail || !shopPassword)) throw new Error("Dedicated shop QA credentials are missing.");
if (runAdmin && (!adminEmail || !adminPassword)) throw new Error("Dedicated platform-admin QA credentials are missing.");
if (runStation && !/^\/s\/[A-Za-z0-9_-]+\/?$/.test(stationPath)) throw new Error("Set SMARTPRINT_LIVE_QA_STATION_PATH to a dedicated station path such as /s/QA_STATION.");

const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const report = {
  targetOrigin: baseUrl,
  startedUtc: new Date().toISOString(),
  runner: "GitHub-hosted ubuntu-24.04",
  safety: { ordersCreated: false, paymentsAttempted: false, refundsRecorded: false, printJobsDispatched: false, shopSettingsChanged: false, customerSessionCreated: runStation },
  checks: [], warnings: [], failures: []
};

function redact(value) {
  let text = String(value ?? "");
  for (const secret of [shopPassword, adminPassword, shopEmail, adminEmail]) {
    if (secret) text = text.split(secret).join("[REDACTED]");
  }
  return text.slice(0, 1000);
}
async function check(name, action) {
  const started = Date.now();
  try {
    const details = await action();
    report.checks.push({ name, status: "passed", durationMs: Date.now() - started, details: details ?? null });
    console.log("PASS " + name);
  } catch (error) {
    const message = redact(error instanceof Error ? error.message : error);
    report.checks.push({ name, status: "failed", durationMs: Date.now() - started, error: message });
    report.failures.push({ name, error: message });
    console.error("FAIL " + name + ": " + message);
  }
}
function skipped(name, reason) {
  report.checks.push({ name, status: "skipped", reason });
  console.log("SKIP " + name + ": " + reason);
}
async function targetFetch(path) {
  const url = new URL(path, baseUrl);
  assert.equal(url.origin, baseUrl, "Request escaped the configured QA origin.");
  return fetch(url, { redirect: "manual", signal: AbortSignal.timeout(20000) });
}
function diagnosticsFor(page) {
  const value = { pageErrors: [], serverErrors: [] };
  page.on("pageerror", error => value.pageErrors.push(redact(error.message)));
  page.on("response", response => {
    try {
      const url = new URL(response.url());
      if (url.origin === baseUrl && response.status() >= 500 && url.pathname !== "/downloads/status") {
        value.serverErrors.push({ status: response.status(), path: url.pathname });
      }
    } catch {}
  });
  return value;
}
function assertDiagnostics(value, label) {
  assert.equal(value.pageErrors.length, 0, label + " browser errors: " + value.pageErrors.join(" | "));
  assert.equal(value.serverErrors.length, 0, label + " server errors: " + JSON.stringify(value.serverErrors));
}
async function visit(browser, label, path, selector) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, ignoreHTTPSErrors: false });
  const page = await context.newPage();
  const diagnostics = diagnosticsFor(page);
  try {
    const response = await page.goto(new URL(path, baseUrl).toString(), { waitUntil: "domcontentloaded", timeout: 30000 });
    assert.ok(response, label + " did not return an HTTP response.");
    assert.equal(response.status(), 200, label + " returned HTTP " + response.status());
    assert.equal(new URL(page.url()).origin, baseUrl, label + " redirected away from the configured origin.");
    await page.locator(selector).first().waitFor({ state: "visible", timeout: 20000 });
    const insecure = await page.evaluate(() => performance.getEntriesByType("resource").map(x => x.name).filter(x => x.startsWith("http://")));
    assert.equal(insecure.length, 0, label + " loaded insecure resources: " + insecure.join(", "));
    await page.screenshot({ path: join(screenshots, label + ".png"), fullPage: true, animations: "disabled" });
    assertDiagnostics(diagnostics, label);
    return { context, page, diagnostics };
  } catch (error) {
    await page.screenshot({ path: join(screenshots, label + "-failure.png"), fullPage: true, animations: "disabled" }).catch(() => {});
    await context.close().catch(() => {});
    throw error;
  }
}
async function workspace(browser, label, email, password, sections, authSelector) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, ignoreHTTPSErrors: false });
  const page = await context.newPage();
  const diagnostics = diagnosticsFor(page);
  await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
  try {
    const path = label === "shop-workspace" ? "/shop" : "/admin";
    const response = await page.goto(new URL(path, baseUrl).toString(), { waitUntil: "domcontentloaded", timeout: 30000 });
    assert.ok(response && response.status() === 200, label + " login route failed.");
    await page.locator(authSelector).waitFor({ state: "visible", timeout: 15000 });
    await page.getByLabel("Email address").fill(email);
    await page.locator('input[type="password"]').first().fill(password);
    const buttonName = label === "shop-workspace" ? /Sign in to workspace/i : /Sign in to administration/i;
    await page.getByRole("button", { name: buttonName }).click();
    try {
      await page.locator(".app-shell").waitFor({ state: "visible", timeout: 30000 });
    } catch {
      const message = (await page.locator(".auth-error").allTextContents().catch(() => [])).join(" ");
      throw new Error(label + " login did not reach the workspace. " + (message || "Check the dedicated QA account and live API."));
    }
    await page.locator(".sidebar .nav-item").first().waitFor({ state: "visible", timeout: 15000 });
    for (const name of sections) {
      const item = page.locator(".sidebar .nav-item").filter({ hasText: name }).first();
      await item.waitFor({ state: "visible", timeout: 10000 });
      await item.click();
      await page.waitForFunction(
        label => [...document.querySelectorAll(".sidebar .nav-item")].some(x => x.textContent.trim().includes(label) && x.classList.contains("active")),
        name, { timeout: 10000 }
      );
    }
    const insecure = await page.evaluate(() => performance.getEntriesByType("resource").map(x => x.name).filter(x => x.startsWith("http://")));
    assert.equal(insecure.length, 0, label + " loaded insecure resources.");
    assertDiagnostics(diagnostics, label);
    await page.screenshot({ path: join(screenshots, label + ".png"), fullPage: true, animations: "disabled" });
  } finally {
    await context.tracing.stop({ path: join(traces, label + ".zip") }).catch(() => {});
    await context.close().catch(() => {});
  }
}

let browser;
try {
  await check("API liveness", async () => {
    const response = await targetFetch("/health/live");
    assert.equal(response.status, 200, "Liveness status was " + response.status);
    const body = await response.json();
    assert.equal(body.status, "live");
    assert.equal(body.service, "qrtoprint-api");
    return { status: response.status, version: body.version ?? null };
  });
  await check("API readiness and database connectivity", async () => {
    const response = await targetFetch("/health/ready");
    assert.equal(response.status, 200, "Readiness status was " + response.status);
    const body = await response.json();
    assert.equal(body.status, "ready");
    assert.equal(body.database, "connected");
    return { status: response.status, database: body.database };
  });
  await check("Unauthenticated platform API is denied", async () => {
    const response = await targetFetch("/api/v1/platform/overview");
    assert.ok([401, 403].includes(response.status), "Expected 401/403, received " + response.status);
    return { status: response.status };
  });
  await check("Unknown API route returns 404", async () => {
    const response = await targetFetch("/api/v1/live-qa-route-that-does-not-exist");
    assert.equal(response.status, 404, "Unknown API route returned " + response.status);
    return { status: response.status };
  });
  await check("Public release availability endpoint", async () => {
    const response = await targetFetch(qaConfig.downloads.statusPath);
    assert.ok([200, 503].includes(response.status), "Unexpected release status HTTP " + response.status);
    if (response.status === 503) {
      report.warnings.push("Release metadata endpoint returned 503; investigate GitHub release connectivity before relying on in-app downloads.");
    } else {
      assert.match((response.headers.get("cache-control") || "").toLowerCase(), /no-store/, "Release status should use Cache-Control: no-store.");
    }
    return { status: response.status, cacheControl: response.headers.get("cache-control") || "not reported" };
  });

  browser = await chromium.launch({ headless: true, args: ["--no-sandbox"] });

  await check("Public landing page and HTTPS assets", async () => {
    const result = await visit(browser, "public-landing", "/", ".sp-public");
    try {
      const heading = (await result.page.locator(".sp-hero h1").innerText()).replace(/\s+/g, " ").trim();
      assert.match(heading, /From their phone/i);
      return { heading };
    } finally { await result.context.close(); }
  });
  await check("Shop sign-in route", async () => {
    const result = await visit(browser, "shop-login", "/shop", ".auth-screen");
    try {
      await result.page.getByLabel("Email address").waitFor({ state: "visible" });
      await result.page.locator('input[type="password"]').first().waitFor({ state: "visible" });
      await result.page.getByRole("button", { name: /Sign in to workspace/i }).waitFor({ state: "visible" });
      return { form: "visible" };
    } finally { await result.context.close(); }
  });
  await check("Legacy login route continuity", async () => {
    const result = await visit(browser, "login-route", "/login", ".auth-screen");
    try {
      await result.page.getByLabel("Email address").waitFor({ state: "visible" });
      return { route: "/login", form: "visible" };
    } finally { await result.context.close(); }
  });
  await check("Shop registration route", async () => {
    const result = await visit(browser, "signup-route", "/signup", ".auth-screen");
    try {
      for (const label of ["Shop or business name", "Your name", "Email address"]) {
        await result.page.getByLabel(label).waitFor({ state: "visible" });
      }
      await result.page.locator('input[type="password"]').first().waitFor({ state: "visible" });
      return { route: "/signup", registrationSubmitted: false };
    } finally { await result.context.close(); }
  });
  await check("Platform-admin sign-in route", async () => {
    const result = await visit(browser, "platform-admin-login", "/admin", ".platform-admin-auth-screen");
    try {
      await result.page.getByLabel("Email address").waitFor({ state: "visible" });
      await result.page.locator('input[type="password"]').first().waitFor({ state: "visible" });
      await result.page.getByRole("button", { name: /Sign in to administration/i }).waitFor({ state: "visible" });
      return { route: "/admin", restrictedForm: "visible" };
    } finally { await result.context.close(); }
  });

  if (runShop) {
    await check("Authenticated shop workspace and all 11 sections", async () => {
      await workspace(browser, "shop-workspace", shopEmail, shopPassword,
        ["Overview", "Print Queue", "Printers", "QR Stations", "Print Agent", "Pricing", "Subscription", "Payments", "Customers", "Reports", "Settings"],
        ".auth-screen");
      return { sectionsVisited: 11, writesPerformed: 0 };
    });
  } else skipped("Authenticated shop workspace", "Enable check_shop_workspace and configure dedicated QA shop credentials.");

  if (runAdmin) {
    await check("Authenticated platform-admin workspace and all 5 sections", async () => {
      await workspace(browser, "platform-admin-workspace", adminEmail, adminPassword,
        ["Overview", "Shop tenants", "Plans & entitlements", "Shop subscriptions", "Operations & retention"],
        ".platform-admin-auth-screen");
      return { sectionsVisited: 5, writesPerformed: 0 };
    });
  } else skipped("Authenticated platform-admin workspace", "Enable check_platform_admin and configure dedicated QA admin credentials.");

  if (runStation) {
    await check("Dedicated customer QR station creates a temporary session", async () => {
      const result = await visit(browser, "customer-qr-station", stationPath, ".customer-entry");
      try {
        const state = result.page.locator(".customer-entry-card .entry-eyebrow");
        await state.waitFor({ state: "visible", timeout: 25000 });
        const label = (await state.innerText()).trim();
        assert.equal(label, "SECURE SESSION CREATED", "QR station did not create a session; displayed state: " + label);
        return { station: "/s/[configured-test-code]", sessionCreated: true };
      } finally { await result.context.close(); }
    });
  } else skipped("Dedicated customer QR station", "Enable check_customer_station and configure a disposable test station path.");

  await check("HTTPS origin consistency across application and health routes", async () => {
    const paths = ["/", "/shop", "/login", "/signup", "/admin", "/health/live", "/health/ready"];
    const checked = [];
    for (const path of paths) {
      const response = await targetFetch(path);
      assert.equal(response.status, 200, path + " returned HTTP " + response.status);
      assert.equal(new URL(response.url).origin, baseUrl, path + " left the target origin.");
      checked.push(path);
    }
    return { routesChecked: checked.length, routes: checked };
  });
} finally {
  if (browser) await browser.close().catch(() => {});
}

report.finishedUtc = new Date().toISOString();
report.summary = {
  passed: report.checks.filter(x => x.status === "passed").length,
  failed: report.checks.filter(x => x.status === "failed").length,
  skipped: report.checks.filter(x => x.status === "skipped").length,
  warnings: report.warnings.length
};
writeFileSync(join(outputDir, "report.json"), JSON.stringify(report, null, 2) + "\n");

const summary = [
  "## SmartPrint live environment QA",
  "",
  "- Target: " + baseUrl,
  "- Passed: **" + report.summary.passed + "**",
  "- Failed: **" + report.summary.failed + "**",
  "- Skipped: **" + report.summary.skipped + "**",
  "- Warnings: **" + report.summary.warnings + "**",
  "",
  "| Check | Result | Details |",
  "|---|---|---|",
  ...report.checks.map(item => {
    const detail = item.error || item.reason || JSON.stringify(item.details ?? {});
    return "| " + item.name.replaceAll("|", "\\|") + " | " + item.status + " | " + redact(detail).replaceAll("|", "\\|").replaceAll("\n", " ") + " |";
  }),
  "",
  ...report.warnings.map(item => "> Warning: " + redact(item)),
  "",
  "No orders, payment attempts, refunds, print dispatches or shop configuration changes are performed.",
  ""
].join("\n");
if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
console.log(summary);
if (report.summary.failed > 0) process.exitCode = 1;
