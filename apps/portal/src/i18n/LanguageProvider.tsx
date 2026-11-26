import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { DEFAULT_UI_LOCALE, resolveUiLocale, type UiLocale } from "./catalog";

type LocaleContextValue = {
  locale: UiLocale;
  setLocale: (locale: UiLocale) => void;
  ready: boolean;
};
const LocaleContext = createContext<LocaleContextValue | null>(null);

function localeStorageKey(pathname: string): string {
  if (/^\/s\//.test(pathname)) return "smartprint-customer-ui-locale";
  if (pathname === "/admin" || pathname.startsWith("/admin/")) return "smartprint-admin-ui-locale";
  if (pathname === "/shop" || pathname.startsWith("/shop/") || pathname === "/login" || pathname === "/signup") {
    return "smartprint-shop-ui-locale";
  }
  return "smartprint-public-ui-locale";
}

export function LanguageProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<UiLocale>(DEFAULT_UI_LOCALE);
  const [ready, setReady] = useState(false);
  const key = useMemo(() => localeStorageKey(typeof window === "undefined" ? "/" : window.location.pathname), []);

  const applyLocale = useCallback((next: UiLocale, persist: boolean) => {
    setLocaleState(next);
    if (typeof document !== "undefined") document.documentElement.lang = next === "english" ? "en" : "hi-Latn";
    if (persist && typeof window !== "undefined") {
      try { window.localStorage.setItem(key, next); } catch { /* Storage may be unavailable in private browsing. */ }
      if (!key.includes("customer")) {
        const secure = window.location.protocol === "https:" ? "; Secure" : "";
        document.cookie = key + "=" + next + "; Path=/; Max-Age=31536000; SameSite=Lax" + secure;
      }
    }
  }, [key]);

  const setLocale = useCallback((next: UiLocale) => applyLocale(next, true), [applyLocale]);

  useEffect(() => {
    let active = true;
    try {
      const saved = resolveUiLocale(window.localStorage.getItem(key));
      if (saved) applyLocale(saved, false);
    } catch { /* Continue with the default locale. */ }
    const onStorage = (event: StorageEvent) => {
      if (event.key === key) {
        const saved = resolveUiLocale(event.newValue);
        if (saved) applyLocale(saved, false);
      }
    };
    window.addEventListener("storage", onStorage);
    if (active) setReady(true);
    return () => {
      active = false;
      window.removeEventListener("storage", onStorage);
    };
  }, [applyLocale, key]);

  const value = useMemo(() => ({ locale, setLocale, ready }), [locale, setLocale, ready]);
  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}

export function useLocale() {
  const value = useContext(LocaleContext);
  if (!value) throw new Error("useLocale must be used inside LanguageProvider");
  return value;
}
