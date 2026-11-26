export type UiLocale = "english" | "hinglish";
export type MessageValues = Record<string, string | number>;

const english = {
  "common.language": "Language",
  "common.english": "English",
  "common.hinglish": "Hinglish",
  "common.save": "Save",
  "common.cancel": "Cancel",
  "common.close": "Close",
  "common.continue": "Continue",
  "common.retry": "Try again",
  "common.loading": "Loading…",
  "nav.orderJourney": "Order Journey",
  "nav.overview": "Overview",
  "nav.printQueue": "Print Queue",
  "nav.printers": "Printers",
  "nav.qrStations": "QR Stations",
  "nav.printAgent": "Print Agent",
  "nav.pricing": "Pricing",
  "nav.subscription": "Subscription",
  "nav.payments": "Payments",
  "nav.customers": "Customers",
  "nav.reports": "Reports",
  "nav.settings": "Settings",
  "nav.dailyWork": "DAILY WORK",
  "nav.devices": "DEVICES & CONNECTIVITY",
  "nav.business": "BUSINESS",
  "nav.insights": "INSIGHTS",
  "auth.signIn": "Sign in",
  "auth.createAccount": "Create account",
  "auth.email": "Email address",
  "auth.password": "Password",
  "auth.forgotPassword": "Forgot password?",
  "auth.signOut": "Sign out",
  "auth.backToWebsite": "Back to website",
  "customer.welcome": "Welcome to {shopName}",
  "customer.uploadFiles": "Upload files",
  "customer.selectFiles": "Select files",
  "customer.copies": "Number of copies",
  "customer.colour": "Colour",
  "customer.blackWhite": "Black & white",
  "customer.privatePrinting": "Private Printing",
  "customer.standardPrinting": "Standard Printing",
  "customer.uploading": "Uploading…",
  "customer.readyCollection": "Your print is ready for collection.",
  "customer.startNewSession": "Start a new session",
  "customer.payment": "Payment",
  "customer.upiPayment": "UPI payment",
  "customer.cashCounter": "Cash at counter",
  "customer.submitReference": "Submit payment reference",
  "customer.orderReceived": "Order received",
  "customer.awaitingReview": "The shop is reviewing your files.",
  "customer.awaitingPayment": "Your files are accepted. Complete payment to continue.",
  "customer.printing": "Your print is in progress.",
  "customer.paymentVerified": "Payment verified.",
  "customer.shopCopyRemoved": "Shop copy removed",
  "journey.received": "Received",
  "journey.review": "Review",
  "journey.payment": "Payment",
  "journey.printing": "Printing",
  "journey.ready": "Ready",
  "photo.crop": "Crop",
  "photo.look": "Photo look",
  "photo.textLayers": "Text layers",
  "photo.background": "Photo background",
  "photo.brightness": "Brightness",
  "photo.contrast": "Contrast",
  "photo.saturation": "Saturation",
  "photo.copies": "Number of copies",
  "photo.savePrivate": "Save private sheet",
  "photo.createOrder": "Create photo order",
  "photo.saveToJob": "Save sheet to this job",
  "photo.prepareA4": "Prepare A4 sheet",
  "photo.cancel": "Cancel editing",
  "errors.network": "Connection problem. Check your internet and try again.",
  "errors.generic": "Something went wrong. Please try again.",
} as const;

const hinglish: Record<keyof typeof english, string> = {
  "common.language": "Language chunein",
  "common.english": "English",
  "common.hinglish": "Hinglish",
  "common.save": "Save karein",
  "common.cancel": "Cancel karein",
  "common.close": "Band karein",
  "common.continue": "Aage badhein",
  "common.retry": "Dobara try karein",
  "common.loading": "Load ho raha hai…",
  "nav.orderJourney": "Order ka safar",
  "nav.overview": "Main jaankari",
  "nav.printQueue": "Print queue",
  "nav.printers": "Printers",
  "nav.qrStations": "QR stations",
  "nav.printAgent": "Print Agent",
  "nav.pricing": "Rate list",
  "nav.subscription": "Subscription",
  "nav.payments": "Payments",
  "nav.customers": "Customers",
  "nav.reports": "Reports",
  "nav.settings": "Settings",
  "nav.dailyWork": "ROZ KA KAAM",
  "nav.devices": "DEVICES AUR CONNECTION",
  "nav.business": "BUSINESS",
  "nav.insights": "REPORTS AUR JAANKARI",
  "auth.signIn": "Login karein",
  "auth.createAccount": "Account banayein",
  "auth.email": "Email address",
  "auth.password": "Password",
  "auth.forgotPassword": "Password bhool gaye?",
  "auth.signOut": "Logout karein",
  "auth.backToWebsite": "Website par wapas jaayein",
  "customer.welcome": "{shopName} mein aapka swagat hai",
  "customer.uploadFiles": "Files upload karein",
  "customer.selectFiles": "Files chunein",
  "customer.copies": "Kitni copies chahiye",
  "customer.colour": "Colour",
  "customer.blackWhite": "Black & white",
  "customer.privatePrinting": "Private Printing",
  "customer.standardPrinting": "Normal Printing",
  "customer.uploading": "Upload ho raha hai…",
  "customer.readyCollection": "Aapka print lene ke liye taiyaar hai.",
  "customer.startNewSession": "Naya print session shuru karein",
  "customer.payment": "Payment",
  "customer.upiPayment": "UPI se payment",
  "customer.cashCounter": "Counter par cash dein",
  "customer.submitReference": "Payment reference submit karein",
  "customer.orderReceived": "Order mil gaya",
  "customer.awaitingReview": "Shop aapki files check kar rahi hai.",
  "customer.awaitingPayment": "Files accept ho gayi hain. Aage badhne ke liye payment karein.",
  "customer.printing": "Aapka print ho raha hai.",
  "customer.paymentVerified": "Payment verify ho gaya.",
  "customer.shopCopyRemoved": "Shop se file ki copy hata di gayi hai",
  "journey.received": "Order mil gaya",
  "journey.review": "Files check karein",
  "journey.payment": "Payment",
  "journey.printing": "Printing",
  "journey.ready": "Print taiyaar",
  "photo.crop": "Crop karein",
  "photo.look": "Photo ka look",
  "photo.textLayers": "Text layers",
  "photo.background": "Photo background",
  "photo.brightness": "Brightness",
  "photo.contrast": "Contrast",
  "photo.saturation": "Saturation",
  "photo.copies": "Kitni copies chahiye",
  "photo.savePrivate": "Private sheet save karein",
  "photo.createOrder": "Photo order banayein",
  "photo.saveToJob": "Is job mein sheet save karein",
  "photo.prepareA4": "A4 sheet taiyaar karein",
  "photo.cancel": "Editing cancel karein",
  "errors.network": "Connection mein problem hai. Internet check karke dobara try karein.",
  "errors.generic": "Kuch problem hui. Please dobara try karein.",
};

export const UI_LOCALE_OPTIONS = [
  { id: "english" as const, label: "English", nativeLabel: "English", lang: "en" },
  { id: "hinglish" as const, label: "Hinglish", nativeLabel: "Hinglish", lang: "hi-Latn" },
];

export const DEFAULT_UI_LOCALE: UiLocale = "hinglish";

export function resolveUiLocale(value: unknown): UiLocale | null {
  const normalized = String(value ?? "").trim().toLowerCase();
  if (normalized === "english" || normalized === "en") return "english";
  if (normalized === "hinglish" || normalized === "hindi" || normalized === "hi" || normalized === "hi-latn") return "hinglish";
  return null;
}

export function normalizeUiLocale(value: unknown): UiLocale {
  return resolveUiLocale(value) ?? DEFAULT_UI_LOCALE;
}

export function translateUi(localeValue: unknown, key: keyof typeof english | string, values: MessageValues = {}): string {
  const locale = normalizeUiLocale(localeValue);
  const template = locale === "english"
    ? (english as Record<string, string>)[key] ?? key
    : hinglish[key as keyof typeof english] ?? (english as Record<string, string>)[key] ?? key;
  return template.replace(/\{([a-zA-Z][a-zA-Z0-9_]*)\}/g, (match, name: string) =>
    Object.prototype.hasOwnProperty.call(values, name) ? String(values[name]) : match
  );
}

export const UI_MESSAGE_KEYS = Object.keys(english);
