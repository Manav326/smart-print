import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const source = readFileSync(resolve(root, "src/i18n/catalog.ts"), "utf8");

function readCatalog(name, declaration, terminator) {
  const start = source.indexOf(declaration);
  if (start < 0) throw new Error("Missing catalog declaration: " + name);
  const end = source.indexOf(terminator, start);
  if (end < 0) throw new Error("Unterminated catalog declaration: " + name);
  const body = source.slice(start, end);
  const entries = [...body.matchAll(/^\\s*"([^"]+)"\\s*:\\s*"((?:[^"\\\\]|\\\\.)*)"/gm)];
  const keys = entries.map((entry) => entry[1]);
  const duplicates = keys.filter((key, index) => keys.indexOf(key) !== index);
  if (duplicates.length) throw new Error(name + " has duplicate keys: " + [...new Set(duplicates)].join(", "));
  return new Map(entries.map((entry) => [entry[1], entry[2]]));
}

const catalogs = {
  english: readCatalog("English", "const english = {", "} as const;"),
  hindi: readCatalog("Hindi", "const hindi:", ";\\n\\nconst hinglish"),
  hinglish: readCatalog("Hinglish", "const hinglish:", ";\\n\\nexport const UI_LOCALE_OPTIONS"),
};
const baseKeys = [...catalogs.english.keys()];
const placeholders = (value) => [...value.matchAll(/\\{([a-zA-Z][a-zA-Z0-9_]*)\\}/g)].map((match) => match[1]).sort().join("|");
for (const [locale, catalog] of Object.entries(catalogs)) {
  const missing = baseKeys.filter((key) => !catalog.has(key));
  const extra = [...catalog.keys()].filter((key) => !catalogs.english.has(key));
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
for (const required of ['id: "english"', 'id: "hindi"', 'id: "hinglish"', 'normalized === "hi"', 'normalized === "hi-latn"']) {
  if (!source.includes(required)) throw new Error("Locale registry contract missing: " + required);
}
console.log("i18n catalog check passed: " + baseKeys.length + " keys × 3 locales; no missing keys, blank values, duplicate keys, or placeholder mismatches.");
