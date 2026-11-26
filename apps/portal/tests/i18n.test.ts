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
  assert.equal(translateUi("hinglish", "nav.overview"), "Main jaankari");
  assert.equal(translateUi("english", "nav.overview"), "Overview");
});

test("screen copy uses pretranslated dictionaries and preserves English source fallback", () => {
  assert.equal(translateUiText("hindi", "Choose how files are handled"), "चुनें कि फ़ाइलों को कैसे संभाला जाएगा");
  assert.equal(translateUiText("hinglish", "Choose how files are handled"), "Choose karein files kaise handle hongi");
  assert.equal(translateUiText("hindi", "A newly added phrase"), "A newly added phrase");
});

test("shop, printer and refund workflows use pretranslated screen copy", () => {
  assert.equal(translateUiText("hindi", "Driver capabilities"), "ड्राइवर की क्षमताएँ");
  assert.equal(translateUiText("hinglish", "Driver capabilities"), "Driver ki capabilities");
  assert.equal(translateUiText("hindi", "PDF saved"), "PDF सेव हो गई");
  assert.equal(translateUiText("hinglish", "PDF saved"), "PDF save ho gayi");
  assert.equal(
    translateUiText("hindi", "Return {amount}", { amount: "₹125.00" }),
    "₹125.00 लौटाएँ",
  );
  assert.equal(
    translateUiText("hinglish", "Select page(s) to see the refund amount."),
    "Refund amount dekhne ke liye page(s) select karein.",
  );
});

test("screen copy interpolation preserves dynamic order and queue details", () => {
  const source = "The Agent could not identify this Windows job. Manually check the {queue} queue for {job}, then inspect every printed page before reconciling.";
  const translated = translateUiText("hindi", source, { queue: "Canon LBP", job: "SP-1042" });
  assert.match(translated, /Canon LBP/);
  assert.match(translated, /SP-1042/);
  assert.doesNotMatch(translated, /\{queue\}|\{job\}/);
});

test("standalone private Photo Studio copy is translated without changing saved document data", () => {
  assert.equal(translateUiText("hindi", "Your private photo sheets, saved on this device."), "आपकी निजी फोटो शीट, इसी डिवाइस पर सेव रहती हैं।");
  assert.equal(translateUiText("hinglish", "Saved photo documents"), "Saved photo documents");
  assert.equal(
    translateUiText("hindi", "Delete “{name}” and its saved original photo from this device? This cannot be undone.", { name: "Passport photo" }),
    "“Passport photo” और उसकी सेव की गई ओरिजिनल फोटो इस डिवाइस से डिलीट करें? यह वापस नहीं किया जा सकता।",
  );
  assert.equal(
    translateUiText("hinglish", "Private preview · Page {page} of {pages}", { page: 2, pages: 4 }),
    "Private preview · 4 mein se page 2",
  );
});

test("customer QR session states are translated while shop names and session values stay dynamic", () => {
  assert.equal(translateUiText("hindi", "Connecting to your print shop"), "आपकी प्रिंट शॉप से कनेक्ट हो रहे हैं");
  assert.equal(translateUiText("hinglish", "Shop connected."), "Shop connect ho gayi.");
  assert.equal(
    translateUiText("hindi", "Secure session · Expires {expires}", { expires: "6:30 PM" }),
    "सुरक्षित सेशन · समाप्ति 6:30 PM",
  );
  assert.equal(
    translateUiText("hinglish", "Your order history is saved. You can start another print session when ready."),
    "Aapki order history saved hai. Jab ready hon, naya print session start kar sakte hain.",
  );
});

test("Photo Studio actions, order recovery and public download warnings are localized", () => {
  assert.equal(translateUiText("hindi", "Save private sheet"), "निजी शीट सेव करें");
  assert.equal(translateUiText("hinglish", "All files have a recorded final decision."), "Sabhi files ka final decision record hai.");
  assert.equal(
    translateUiText("hindi", "Retry {count} virtual test(s)", { count: 3 }),
    "3 वर्चुअल टेस्ट दोबारा चलाएँ",
  );
  assert.equal(
    translateUiText("hindi", "The interim Windows beta installer is unsigned. Windows may show an Unknown Publisher or SmartScreen prompt; some managed devices may block unsigned installers."),
    "अंतरिम Windows बीटा इंस्टॉलर पर डिजिटल साइन नहीं है। Windows में Unknown Publisher या SmartScreen चेतावनी दिख सकती है; कुछ प्रबंधित डिवाइस बिना साइन वाले इंस्टॉलर को रोक सकते हैं।",
  );
});

test("shop operations, platform policy and plan administration copy is localized", () => {
  assert.equal(
    translateUiText("hindi", "Shop-scoped virtual printer test access"),
    "शॉप के लिए सीमित वर्चुअल प्रिंटर टेस्ट एक्सेस",
  );
  assert.equal(
    translateUiText("hinglish", "Shop-scoped virtual printer test access"),
    "Ek shop ke liye limited virtual printer test access",
  );
  assert.equal(
    translateUiText("hindi", "What this subscription includes"),
    "इस सब्सक्रिप्शन में क्या शामिल है",
  );
  assert.equal(
    translateUiText("hinglish", "Choose a plan from the catalogue to inspect its versions and configure draft terms."),
    "Plan ke versions dekhne aur draft terms configure karne ke liye catalogue se plan choose karein.",
  );
  assert.equal(
    translateUiText("hindi", "Monthly price ({currency})", { currency: "INR" }),
    "मासिक कीमत (INR)",
  );
  assert.equal(
    translateUiText("hinglish", "Only jobs created and payment-verified before the latest suspension can be claimed. New orders and post-suspension payments remain blocked."),
    "Sirf latest suspension se pehle create aur payment-verified jobs hi claim ho sakti hain. Naye orders aur suspension ke baad ke payments blocked rahenge.",
  );
});

test("upload, payment and operational recovery errors are localized with dynamic values intact", () => {
  assert.equal(
    translateUiText("hindi", "Choose 1–{count} whole copies.", { count: 4 }),
    "1 से 4 तक पूरी कॉपी चुनें।",
  );
  assert.equal(
    translateUiText("hinglish", "Could not quote: {files}. Retry the batch quote.", { files: "report.pdf" }),
    "Quote nahi bana: report.pdf. Batch quote dobara try karein.",
  );
  assert.equal(
    translateUiText("hindi", "The shop is still reviewing your files. Payment will be ready when the review is complete."),
    "शॉप अभी आपकी फ़ाइलों की जाँच कर रही है। रिव्यू पूरा होने पर भुगतान उपलब्ध होगा।",
  );
  assert.equal(
    translateUiText("hinglish", "Saved privately on this device. Connect to a shop whenever you are ready to print."),
    "Is device par privately save ho gaya. Print ke liye ready hon toh kisi shop se connect karein.",
  );
  assert.equal(
    translateUiText("hindi", "Could not load platform operational settings."),
    "प्लेटफ़ॉर्म ऑपरेशनल सेटिंग लोड नहीं हो सकीं।",
  );
});

test("number, currency and date formatting use the selected locale", () => {
  assert.equal(typeof formatUiNumber(1234.5, "hindi"), "string");
  assert.match(formatUiCurrency(1234.5, "english", "INR"), /1,234\.50/);
  assert.equal(typeof formatUiDate("2026-11-26T12:00:00Z", "hindi", { timeZone: "Asia/Kolkata" }), "string");
});
