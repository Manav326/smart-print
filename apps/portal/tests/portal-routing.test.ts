import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { adminPathForSection, adminSectionFromPath, authStorageKey, resolvePortalRoute, shopPathForSection, shopSectionFromPath } from "../src/portal-routing.ts";
import { applyThemePreference, readDarkModePreference, readSidebarCollapsedPreference, saveDarkModePreference, saveSidebarCollapsedPreference } from "../src/ui-preferences.ts";

test("public root stays public even when a user has a stored session", () => {
  assert.deepEqual(resolvePortalRoute("/"), { kind: "public" });
  assert.deepEqual(resolvePortalRoute("/features/"), { kind: "public" });
});

test("admin scope owns its base path and nested routes", () => {
  assert.deepEqual(resolvePortalRoute("/admin"), { kind: "admin" });
  assert.deepEqual(resolvePortalRoute("/admin/"), { kind: "admin" });
  assert.deepEqual(resolvePortalRoute("/admin/tenants/42"), { kind: "admin" });
  assert.deepEqual(resolvePortalRoute("/administrator"), { kind: "public" });
});

test("shop scope owns its base path and nested routes", () => {
  assert.deepEqual(resolvePortalRoute("/shop"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/shop/"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/shop/settings"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/login"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/signup"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/shopping"), { kind: "public" });
});

test("station route remains separate from admin and shop scopes", () => {
  assert.deepEqual(resolvePortalRoute("/s/ABC_123"), {
    kind: "station",
    publicCode: "ABC_123"
  });
});

test("Android customer station keeps the shared print page and offers native local-network permission and phone files", () => {
  const appSource = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const androidSource = readFileSync(
    new URL("../../android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt", import.meta.url),
    "utf8"
  );
  const apiSource = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");

  assert.match(appSource, /function CustomerPrintFlow\(/);
  assert.match(appSource, /requestSmartPrintNativeLocalNetworkAccess/);
  assert.match(appSource, /bridge\.addEventListener\("message",handleReply\)/);
  assert.match(apiSource, /value\.startsWith\("localNetworkAccess:"\)/);
  assert.match(appSource, /nativeLocalNetworkPermissionDenied=access==="denied"/);
  assert.match(appSource, /allow SmartPrint under Android Settings/);
  assert.match(appSource, /Tap Check Wi-Fi to connect to this shop/);
  assert.match(androidSource, /override fun onShowFileChooser\([\s\S]*?showPicker\(\)/);
  assert.match(androidSource, /text\("SmartPrint My Files"/);
  assert.match(androidSource, /button\("Browse phone"/);
  assert.match(androidSource, /deliverPhoneDocuments\(uris: List<Uri>\)/);
  assert.match(androidSource, /callback\.onReceiveValue\(selected\.toTypedArray\(\)\)/);
  assert.match(androidSource, /requestLocalNetworkAccess\(replyProxy: JavaScriptReplyProxy\)/);
});

test("admin and shop browser credentials use independent session keys", () => {
  assert.equal(authStorageKey("admin"), "qrtoprint.auth.admin");
  assert.equal(authStorageKey("shop"), "qrtoprint.auth.shop");
  assert.notEqual(authStorageKey("admin"), authStorageKey("shop"));
});


test("shop workspace sections have stable deep links and restore correctly", () => {
  const sections = [
    "Order Journey",
    "Overview",
    "Print Queue",
    "Printers",
    "QR Stations",
    "Print Agent",
    "Pricing",
    "Subscription",
    "Payments",
    "Customers",
    "Reports",
    "Settings"
  ] as const;

  for (const section of sections) {
    const path = shopPathForSection(section);
    assert.ok(path === "/shop" || path.startsWith("/shop/"));
    assert.equal(shopSectionFromPath(path), section);
  }

  assert.equal(shopSectionFromPath("/shop"), "Order Journey");
  assert.equal(shopSectionFromPath("/shop/overview"), "Overview");
  assert.equal(shopSectionFromPath("/shop/settings/"), "Settings");
  assert.equal(shopSectionFromPath("/shop/queue/jobs/42"), "Print Queue");
  assert.equal(shopSectionFromPath("/shop/unknown"), "Overview");
});


test("admin workspace sections have stable deep links and restore nested views", () => {
  const sections = ["Overview", "Tenants", "Plans", "Subscriptions", "Operations", "Advertising"] as const;
  for (const section of sections) {
    const path = adminPathForSection(section);
    assert.ok(path === "/admin" || path.startsWith("/admin/"));
    assert.equal(adminSectionFromPath(path), section);
  }
  assert.equal(adminSectionFromPath("/admin/tenants/42"), "Tenants");
  assert.equal(adminSectionFromPath("/admin/plans/version/3"), "Plans");
  assert.equal(adminSectionFromPath("/admin/advertising"), "Advertising");
  assert.equal(adminSectionFromPath("/admin/unknown"), "Overview");
});


test("theme defaults to light, survives logout, and applies to the document root", async () => {
  const values = new Map<string, string>();
  const originalWindow = (globalThis as { window?: unknown }).window;
  const originalDocument = (globalThis as { document?: unknown }).document;
  const documentElement = { dataset: {} as Record<string, string>, style: {} as Record<string, string> };
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname: "/shop" },
      localStorage: {
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => values.set(key, value)
      },
      sessionStorage: {
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => values.set(key, value),
        removeItem: (key: string) => values.delete(key)
      }
    }
  });
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    value: {
      documentElement,
      querySelector: () => null
    }
  });
  try {
    assert.equal(readDarkModePreference(), false);
    assert.equal(documentElement.dataset.theme, undefined);
    applyThemePreference(false);
    assert.equal(documentElement.dataset.theme, "light");
    assert.equal(documentElement.style.colorScheme, "light");

    saveDarkModePreference(true);
    applyThemePreference(true);
    assert.equal(readDarkModePreference(), true);
    assert.equal(documentElement.dataset.theme, "dark");
    assert.equal(documentElement.style.colorScheme, "dark");

    saveSidebarCollapsedPreference(true);
    assert.equal(readSidebarCollapsedPreference(), true);

    const apiSource = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");
    assert.match(apiSource, /export function storeTokens\([\s\S]*?sessionStorage\.setItem\(scopedKey/);
    assert.match(apiSource, /else sessionStorage\.removeItem\(scopedKey\)/);
    assert.doesNotMatch(apiSource.slice(apiSource.indexOf("export function storeTokens"), apiSource.indexOf("export const signIn")), /localStorage/);

    values.set("qrtoprint.auth.shop", JSON.stringify({ accessToken: "test-access" }));
    values.delete("qrtoprint.auth.shop");
    assert.equal(values.has("qrtoprint.auth.shop"), false);
    assert.equal(readDarkModePreference(), true);
    assert.equal(values.get("qrtoprint.theme"), "dark");
  } finally {
    if (originalWindow === undefined) delete (globalThis as { window?: unknown }).window;
    else Object.defineProperty(globalThis, "window", { configurable: true, value: originalWindow });
    if (originalDocument === undefined) delete (globalThis as { document?: unknown }).document;
    else Object.defineProperty(globalThis, "document", { configurable: true, value: originalDocument });
  }
});
