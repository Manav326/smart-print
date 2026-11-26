import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { DEFAULT_UI_LOCALE, getUiLocaleLanguage, resolveUiLocale, type UiLocale } from "./catalog";

const STORAGE_KEY = "smartprint-ui-locale";
const LEGACY_STORAGE_KEYS = [
  "smartprint-customer-ui-locale",
  "smartprint-admin-ui-locale",
  "smartprint-shop-ui-locale",
  "smartprint-public-ui-locale",
] as const;

type LocaleContextValue = {
  locale: UiLocale;
  setLocale: (locale: UiLocale) => void;
  ready: boolean;
};
const LocaleContext = createContext<LocaleContextValue | null>(null);

function readInitialLocale(): UiLocale {
  if (typeof window === "undefined") return DEFAULT_UI_LOCALE;
  try {
    const current = resolveUiLocale(window.localStorage.getItem(STORAGE_KEY));
    if (current) return current;
    for (const key of LEGACY_STORAGE_KEYS) {
      const legacy = resolveUiLocale(window.localStorage.getItem(key));
      if (legacy) return legacy;
    }
  } catch {
    // Continue to the cookie fallback when browser storage is unavailable.
  }
  try {
    const cookie = document.cookie.split("; ").find(item => item.startsWith(STORAGE_KEY + "="));
    const saved = cookie ? resolveUiLocale(decodeURIComponent(cookie.slice(STORAGE_KEY.length + 1))) : null;
    if (saved) return saved;
  } catch {
    // The first-use default remains safe when cookies are unavailable too.
  }
  return DEFAULT_UI_LOCALE;
}

function persistLocale(locale: UiLocale): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(STORAGE_KEY, locale);
    for (const key of LEGACY_STORAGE_KEYS) window.localStorage.removeItem(key);
  } catch {
    // The UI still changes for this session when storage is blocked or unavailable.
  }
  try {
    const secure = window.location.protocol === "https:" ? "; Secure" : "";
    document.cookie = STORAGE_KEY + "=" + locale + "; Path=/; Max-Age=31536000; SameSite=Lax" + secure;
  } catch {
    // Cookie persistence is optional; the active React state remains authoritative.
  }
}

export function LanguageProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<UiLocale>(readInitialLocale);
  const [ready, setReady] = useState(false);

  const applyLocale = useCallback((next: UiLocale, persist: boolean) => {
    setLocaleState(next);
    if (typeof document !== "undefined") {
      document.documentElement.lang = getUiLocaleLanguage(next);
      document.documentElement.dir = "ltr";
    }
    if (persist) persistLocale(next);
  }, []);

  const setLocale = useCallback((next: UiLocale) => applyLocale(next, true), [applyLocale]);

  useEffect(() => {
    applyLocale(locale, false);
    setReady(true);
    const onStorage = (event: StorageEvent) => {
      if (event.key === STORAGE_KEY) {
        applyLocale(event.newValue === null ? DEFAULT_UI_LOCALE : resolveUiLocale(event.newValue) ?? locale, false);
      }
    };
    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, [applyLocale, locale]);

  const value = useMemo(() => ({ locale, setLocale, ready }), [locale, setLocale, ready]);
  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}

export function useLocale() {
  const value = useContext(LocaleContext);
  if (!value) throw new Error("useLocale must be used inside LanguageProvider");
  return value;
}
