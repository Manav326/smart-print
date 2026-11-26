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

function persistLocale(locale: UiLocale): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(STORAGE_KEY, locale);
    for (const key of LEGACY_STORAGE_KEYS) window.localStorage.removeItem(key);
  } catch {
    // The UI still changes for this session when storage is blocked or unavailable.
  }
  const secure = window.location.protocol === "https:" ? "; Secure" : "";
  document.cookie = STORAGE_KEY + "=" + locale + "; Path=/; Max-Age=31536000; SameSite=Lax" + secure;
}

export function LanguageProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<UiLocale>(DEFAULT_UI_LOCALE);
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
    let saved: UiLocale | null = null;
    try {
      saved = resolveUiLocale(window.localStorage.getItem(STORAGE_KEY));
      if (!saved) {
        for (const key of LEGACY_STORAGE_KEYS) {
          saved = resolveUiLocale(window.localStorage.getItem(key));
          if (saved) break;
        }
      }
    } catch {
      // Keep the safe English default if browser storage is unavailable.
    }
    if (saved) applyLocale(saved, false);
    else applyLocale(DEFAULT_UI_LOCALE, false);
    setReady(true);

    const onStorage = (event: StorageEvent) => {
      if (event.key === STORAGE_KEY && event.newValue !== null) {
        const next = resolveUiLocale(event.newValue);
        if (next) applyLocale(next, false);
      }
    };
    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, [applyLocale]);

  const value = useMemo(() => ({ locale, setLocale, ready }), [locale, setLocale, ready]);
  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}

export function useLocale() {
  const value = useContext(LocaleContext);
  if (!value) throw new Error("useLocale must be used inside LanguageProvider");
  return value;
}
