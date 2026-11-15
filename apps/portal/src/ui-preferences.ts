import {
  nextThemePreferenceTimestamp,
  readThemePreference,
  resolveThemePreference,
  writeThemePreference,
  type NativeThemePreferenceSnapshot,
  type ThemePreferenceStorage,
} from "./theme-preference.ts";

const SIDEBAR_COLLAPSED_KEY = "qrtoprint.sidebar-collapsed";

type SmartPrintThemeBridge = {
  getTheme?: () => string;
  getThemeUpdatedAt?: () => number;
  hasExplicitSelection?: () => boolean;
  setTheme?: (theme: "light" | "dark", updatedAt: number) => void;
};

function getStorage(): ThemePreferenceStorage | null {
  if (typeof window === "undefined") return null;
  try { return window.localStorage; } catch { return null; }
}

function getNativeThemePreference(): NativeThemePreferenceSnapshot | null {
  if (typeof window === "undefined") return null;
  try {
    const bridge = (window as Window & { SmartPrintThemeNative?: SmartPrintThemeBridge }).SmartPrintThemeNative;
    const theme = bridge?.getTheme?.();
    if (theme !== "light" && theme !== "dark") return null;
    const updatedAt = Number(bridge?.getThemeUpdatedAt?.() ?? 0);
    return {
      theme,
      updatedAt: Number.isFinite(updatedAt) && updatedAt >= 0 ? Math.floor(updatedAt) : 0,
      explicit: bridge?.hasExplicitSelection?.() === true,
    };
  } catch {
    return null;
  }
}

export function readDarkModePreference(): boolean {
  return resolveThemePreference(readThemePreference(getStorage()), getNativeThemePreference()) === "dark";
}

export function applyThemePreference(dark: boolean): void {
  if (typeof document === "undefined") return;
  const theme = dark ? "dark" : "light";
  document.documentElement.dataset.theme = theme;
  document.documentElement.style.colorScheme = theme;
  const themeColor = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
  if (themeColor) themeColor.content = dark ? "#0c0f16" : "#f4f6fa";

  const storage = getStorage();
  const local = readThemePreference(storage);
  const native = getNativeThemePreference();
  const updatedAt = nextThemePreferenceTimestamp(Date.now(), local.updatedAt, native?.updatedAt ?? 0);
  writeThemePreference(storage, theme, updatedAt);

  // Android exposes a deliberately narrow theme-only bridge on every supported
  // WebView. It does not depend on WEB_MESSAGE_LISTENER support. The older
  // privileged bridge remains a compatibility fallback for already-running shells.
  try {
    const themeBridge = (window as Window & { SmartPrintThemeNative?: SmartPrintThemeBridge }).SmartPrintThemeNative;
    if (themeBridge?.setTheme) {
      themeBridge.setTheme(theme, updatedAt);
    } else {
      const nativeBridge = (window as Window & { SmartPrintNative?: { postMessage: (message: string) => void } }).SmartPrintNative;
      nativeBridge?.postMessage(`setTheme|${theme}`);
    }
  } catch {
    // Native synchronization is optional and never blocks a browser theme change.
  }
}

export function saveDarkModePreference(dark: boolean): void {
  const storage = getStorage();
  const local = readThemePreference(storage);
  const native = getNativeThemePreference();
  const updatedAt = nextThemePreferenceTimestamp(Date.now(), local.updatedAt, native?.updatedAt ?? 0);
  writeThemePreference(storage, dark ? "dark" : "light", updatedAt);
}

function readPreference(key: string): string | null {
  if (typeof window === "undefined") return null;
  try { return window.localStorage.getItem(key); } catch { return null; }
}

function writePreference(key: string, value: string): void {
  if (typeof window === "undefined") return;
  try { window.localStorage.setItem(key, value); } catch {
    // Preferences are optional; storage restrictions must not block the workspace.
  }
}

export function readSidebarCollapsedPreference(): boolean {
  return readPreference(SIDEBAR_COLLAPSED_KEY) === "true";
}
export function saveSidebarCollapsedPreference(collapsed: boolean): void {
  writePreference(SIDEBAR_COLLAPSED_KEY, String(collapsed));
}
