import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const source = readFileSync(resolve(root, "src/i18n/catalog.ts"), "utf8");

function readCatalog(name) {
  const start = source.indexOf("const " + name);
  if (start < 0) throw new Error("Missing catalog declaration: " + name);
  const bodyStart = source.indexOf("{", start);
  const end = source.indexOf("\n}", bodyStart);
  if (bodyStart < 0 || end < 0) throw new Error("Unterminated catalog declaration: " + name);
  const entries = [];
  for (const line of source.slice(bodyStart, end).split("\n")) {
    const trimmed = line.trim();
    if (!trimmed.startsWith('"')) continue;
    const keyEnd = trimmed.indexOf('"', 1);
    if (keyEnd < 0 || trimmed.slice(keyEnd + 1).trimStart()[0] !== ":") continue;
    const valueStart = trimmed.indexOf('"', keyEnd + 1);
    const valueEnd = trimmed.lastIndexOf('"');
    if (valueStart < 0 || valueEnd <= valueStart) continue;
    entries.push([trimmed.slice(1, keyEnd), trimmed.slice(valueStart + 1, valueEnd)]);
  }
  const keys = entries.map(([key]) => key);
  const duplicates = keys.filter((key, index) => keys.indexOf(key) !== index);
  if (duplicates.length) throw new Error(name + " has duplicate keys: " + [...new Set(duplicates)].join(", "));
  return new Map(entries);
}

function placeholders(value) {
  const found = [];
  let cursor = 0;
  while (cursor < value.length) {
    const open = value.indexOf("{", cursor);
    if (open < 0) break;
    const close = value.indexOf("}", open + 1);
    if (close < 0) break;
    found.push(value.slice(open + 1, close));
    cursor = close + 1;
  }
  return found.sort().join("|");
}

const registryStart = source.indexOf("const uiCatalogs = {");
const registryEnd = source.indexOf("} as const;", registryStart);
if (registryStart < 0 || registryEnd < 0) throw new Error("Missing typed locale catalog registry");
const localeNames = source.slice(registryStart + "const uiCatalogs = {".length, registryEnd)
  .split(",").map(name => name.trim()).filter(Boolean);
if (!localeNames.includes("english")) throw new Error("English fallback catalog must be registered");
if (localeNames.join(",") !== "english,hindi,hinglish") {
  throw new Error("Only English, Hindi and Hinglish may be selectable in this release");
}
const catalogs = Object.fromEntries(localeNames.map(name => [name, readCatalog(name)]));
const baseKeys = [...catalogs.english.keys()];

for (const [locale, catalog] of Object.entries(catalogs)) {
  const missing = baseKeys.filter(key => !catalog.has(key));
  const extra = [...catalog.keys()].filter(key => !catalogs.english.has(key));
  if (missing.length || extra.length) {
    throw new Error(locale + " catalog drift. Missing: " + missing.join(", ") + "; extra: " + extra.join(", "));
  }
  for (const key of baseKeys) {
    const value = catalog.get(key);
    if (!value?.trim()) throw new Error(locale + " has an empty translation for " + key);
    if (placeholders(value) !== placeholders(catalogs.english.get(key))) {
      throw new Error(locale + " placeholder mismatch for " + key);
    }
  }
}

const screenRegistryStart = source.indexOf("const screenTextCatalogs:");
if (screenRegistryStart < 0) throw new Error("Missing screen-level translation dictionaries");
function readScreenCatalog(locale) {
  const localeStart = source.indexOf("  " + locale + ": {", screenRegistryStart);
  if (localeStart < 0) throw new Error("Missing screen-level dictionary for " + locale);
  const bodyStart = source.indexOf("\n", localeStart) + 1;
  const commaEnd = source.indexOf("\n  },", bodyStart);
  const finalEnd = source.indexOf("\n  }\n};", bodyStart);
  const end = commaEnd < 0 ? finalEnd : finalEnd < 0 ? commaEnd : Math.min(commaEnd, finalEnd);
  if (bodyStart <= 0 || end < 0) throw new Error("Unterminated screen-level dictionary for " + locale);
  const entries = [];
  const entryPattern = /^("(?:\\.|[^"\\])*")\s*:\s*("(?:\\.|[^"\\])*"),?$/;
  for (const line of source.slice(bodyStart, end).split("\n")) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    const match = entryPattern.exec(trimmed);
    if (!match) throw new Error("Invalid screen-level key/value entry in " + locale + ": " + trimmed);
    entries.push([JSON.parse(match[1]), JSON.parse(match[2])]);
  }
  const keys = entries.map(([key]) => key);
  const duplicates = keys.filter((key, index) => keys.indexOf(key) !== index);
  if (duplicates.length) throw new Error(locale + " screen dictionary has duplicate keys: " + [...new Set(duplicates)].join(", "));
  for (const [key, value] of entries) {
    if (!key.trim() || !value.trim()) throw new Error(locale + " screen dictionary contains a blank key/value");
  }
  return new Map(entries);
}
const screenHindi = readScreenCatalog("hindi");
const screenHinglish = readScreenCatalog("hinglish");
const screenMissingHindi = [...screenHinglish.keys()].filter(key => !screenHindi.has(key));
const screenMissingHinglish = [...screenHindi.keys()].filter(key => !screenHinglish.has(key));
if (screenMissingHindi.length || screenMissingHinglish.length) {
  throw new Error("Screen translation dictionary drift. Hindi missing: " + screenMissingHindi.join(", ") + "; Hinglish missing: " + screenMissingHinglish.join(", "));
}
for (const [key, hindiValue] of screenHindi) {
  const hinglishValue = screenHinglish.get(key);
  if (placeholders(hindiValue) !== placeholders(hinglishValue)) {
    throw new Error("Screen translation placeholder mismatch for " + key);
  }
}

const hindiTranslatedCount = [...screenHindi].filter(([key, value]) => key !== value).length;
const hinglishTranslatedCount = [...screenHinglish].filter(([key, value]) => key !== value).length;
if (hindiTranslatedCount / screenHindi.size < 0.9) {
  throw new Error("Hindi screen copy must remain meaningfully translated, not mostly English fallback");
}
if (hinglishTranslatedCount / screenHinglish.size < 0.65) {
  throw new Error("Hinglish screen copy must remain meaningfully translated, not mostly English fallback");
}

const requiredDynamicScreenCopy = [
  "Customer app · Home",
  "Customer app · My Files",
  "Shop owner · Overview",
  "640 × 200 px (2× asset for a 320 × 100 display)",
  "600 × 500 px (2× asset for a 300 × 250 card)",
  "1456 × 180 px (2× asset for a 728 × 90 display)",
];
for (const key of requiredDynamicScreenCopy) {
  if (!screenHindi.has(key) || !screenHinglish.has(key)) {
    throw new Error("Missing dynamic UI translation for " + key);
  }
}

const localizedScreens = [
  "App.tsx",
  "AuthScreen.tsx",
  "CustomerUpiQr.tsx",
  "OrderJourneyPage.tsx",
  "PhotoStudio.tsx",
  "PublicLandingPage.tsx",
  "PlatformAdminConsole.tsx",
  "PlatformAdminAuthScreen.tsx",
  "PrivatePhotoStudioEntry.tsx",
  "PlatformDataNetworkSection.tsx",
  "PlatformOperationsSection.tsx",
  "PlatformPlansSection.tsx",
  "PlatformSubscriptionsSection.tsx",
  "ShopSubscriptionCheckout.tsx",
  "ShopGoogleSignInButton.tsx",
  "PlatformAdvertisingSection.tsx",
  "ThemeToggleButton.tsx",
];
for (const file of localizedScreens) {
  const screenSource = readFileSync(resolve(root, "src", file), "utf8");
  const usedKeys = [...screenSource.matchAll(/\bt\("(?:\\.|[^"\\])*"\)/g)]
    .map(match => JSON.parse(match[0].slice(2, -1)));
  const missing = [...new Set(usedKeys.filter(key => !screenHindi.has(key) || !screenHinglish.has(key)))];
  if (missing.length) {
    throw new Error(file + " references untranslated screen copy: " + missing.join(", "));
  }
}

const requiredScreenCoverage = {
  "App.tsx": [
    "Printer deselected. Existing files and Windows output were not deleted.",
    "Printer selected for new assignments.",
    "QR station enabled.",
    "QR station disabled. Existing customer sessions are unchanged.",
    "Virtual printer test authorised. The existing Windows spooler pipeline will run; no physical fulfilment will be recorded.",
    "Private test PDF authorised. The Agent will save a protected local test artifact; no physical print is recorded.",
    "Manually reconcile print output",
    "Confirm paper output",
    "Personal details saved. Your new email is now your sign-in address.",
    "Personal details updated.",
    "Private Printing keeps the original photo and editor settings on the customer's device. Shop-side revisions are disabled.",
    "The original photo is no longer available for editing. Refresh the queue before trying again.",
    "The paired Print Agent has been revoked. Re-enrol the shop computer before using this queue.",
    "Cash payment pending. Collect {amount} at the counter and mark it received only after the cash is physically in the shop.",
    "Shop payment",
    "UPI ID not available",
    "{count} photos · {size} · {pages} A4 page(s)",
    "Prices are stored in paise and calculated by the server. Updating this rate card affects new quotes; existing quotes retain their saved price and pricing version.",
    "Queue refreshes automatically while this page is open.",
    "Automatic refresh is off. Use Refresh queue whenever needed.",
    "Private source · preview disabled",
    "Preview original",
    "Your subscription expired. Renew using the plans below to restore access. Existing customer payment records and completed jobs remain available.",
    "Shop access is currently restricted. Review your account status or contact SmartPrint support for assistance."
  ],
  "PlatformPlansSection.tsx": [
    "Enter a three-letter currency code.",
    "Plan code must contain 2–40 letters, digits or underscores and start with a letter."
  ],
  "PlatformDataNetworkSection.tsx": [
    "Unique documents",
    "original file volume counted once",
    "Agent values are reported file lengths, not filesystem-allocated cluster usage; exact physical disk occupancy is not measured."
  ],
  "PlatformSubscriptionsSection.tsx": [
    "No plan assigned", "legacy", "No subscription record", "Granting…", "Grant access"
  ],
  "ShopGoogleSignInButton.tsx": [
    "Google sign-in did not initialize. Please reload and try again.",
    "Google sign-in could not be loaded. Check your connection and try again."
  ]
};
for (const [file, keys] of Object.entries(requiredScreenCoverage)) {
  const screenSource = readFileSync(resolve(root, "src", file), "utf8");
  for (const key of keys) {
    if (!screenHindi.has(key) || !screenHinglish.has(key)) {
      throw new Error(file + " is missing Hindi/Hinglish coverage for: " + key);
    }
    if (!screenSource.includes(key)) {
      throw new Error(file + " no longer references expected localized copy: " + key);
    }
  }
}

if (!source.includes("screenTextCatalogs[locale][sourceText] ?? sourceText")) {
  throw new Error("Screen copy must fall back to its English source text without runtime translation");
}

const metadataStart = source.indexOf("const localeMetadata:");
const metadataEnd = source.indexOf("};", metadataStart);
if (metadataStart < 0 || metadataEnd < 0) throw new Error("Missing locale metadata registry");
const metadata = source.slice(metadataStart, metadataEnd);
for (const locale of localeNames) {
  if (!metadata.includes(locale + ": {")) throw new Error(locale + " is missing locale metadata");
}
if (!source.includes("Object.entries(localeMetadata).map")) {
  throw new Error("Language selector options must be derived from the locale registry");
}
const defaults = [...metadata.matchAll(/^\s*(\w+): \{[^\n]*default: true/mg)].map(match => match[1]);
if (defaults.length !== 1 || defaults[0] !== "english") {
  throw new Error("English must remain the single first-use default locale");
}
console.log("i18n catalog check passed: " + baseKeys.length + " keyed messages × " + localeNames.length + " locales and " + screenHindi.size + " screen-copy pairs; no catalog drift or blank translations.");
