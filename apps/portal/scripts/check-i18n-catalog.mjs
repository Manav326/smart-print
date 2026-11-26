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
console.log("i18n catalog check passed: " + baseKeys.length + " keyed messages × " + localeNames.length + " locales and " + screenHindi.size + " screen-copy pairs; no catalog drift or blank translations.");
