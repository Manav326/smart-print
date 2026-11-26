import assert from "node:assert/strict";
import test from "node:test";
import {
  DEFAULT_UI_LOCALE,
  UI_LOCALE_OPTIONS,
  formatUiCurrency,
  formatUiDate,
  formatUiNumber,
  getUiLocaleDirection,
  getUiLocaleLanguage,
  normalizeUiLocale,
  resolveUiLocale,
  translateUi,
  translateUiText,
} from "../src/i18n/catalog.ts";

test("only English, Hindi and Hinglish are selectable and English remains the safe default", () => {
  assert.deepEqual(UI_LOCALE_OPTIONS.map(option => option.id), ["english", "hindi", "hinglish"]);
  assert.equal(DEFAULT_UI_LOCALE, "english");
  assert.equal(normalizeUiLocale("unknown-locale"), "english");
});

test("locale aliases resolve without enabling additional selectable languages", () => {
  assert.equal(resolveUiLocale("en-IN"), "english");
  assert.equal(resolveUiLocale("hi-IN"), "hindi");
  assert.equal(resolveUiLocale("hi-Latn-IN"), "hinglish");
  assert.equal(resolveUiLocale("fr-FR"), null);
});

test("document language and direction follow the selected locale", () => {
  assert.equal(getUiLocaleLanguage("english"), "en");
  assert.equal(getUiLocaleLanguage("hindi"), "hi");
  assert.equal(getUiLocaleLanguage("hinglish"), "hi-Latn");
  assert.equal(getUiLocaleDirection("hindi"), "ltr");
});

test("typed catalog messages are translated in Hindi and Hinglish", () => {
  assert.equal(translateUi("hindi", "nav.overview"), "अवलोकन");
  assert.equal(translateUi("hinglish", "nav.overview"), "Overview");
  assert.equal(translateUi("english", "nav.overview"), "Overview");
});

test("screen copy uses pretranslated dictionaries and preserves English source fallback", () => {
  assert.equal(translateUiText("hindi", "Choose how files are handled"), "चुनें कि फ़ाइलों को कैसे संभाला जाएगा");
  assert.equal(translateUiText("hinglish", "Choose how files are handled"), "Choose karein files kaise handle hongi");
  assert.equal(translateUiText("hindi", "A newly added phrase"), "A newly added phrase");
});

test("screen copy interpolation preserves dynamic order and queue details", () => {
  const source = "The Agent could not identify this Windows job. Manually check the {queue} queue for {job}, then inspect every printed page before reconciling.";
  const translated = translateUiText("hindi", source, { queue: "Canon LBP", job: "SP-1042" });
  assert.match(translated, /Canon LBP/);
  assert.match(translated, /SP-1042/);
  assert.doesNotMatch(translated, /\{queue\}|\{job\}/);
});

test("number, currency and date formatting use the selected locale", () => {
  assert.equal(typeof formatUiNumber(1234.5, "hindi"), "string");
  assert.match(formatUiCurrency(1234.5, "english", "INR"), /1,234\.50/);
  assert.equal(typeof formatUiDate("2026-11-26T12:00:00Z", "hindi", { timeZone: "Asia/Kolkata" }), "string");
});
