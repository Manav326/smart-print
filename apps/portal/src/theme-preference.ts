export type ThemeMode = "light" | "dark";

export const THEME_PREFERENCE_KEY = "smartprint.theme";
export const LEGACY_THEME_PREFERENCE_KEY = "qrtoprint.theme";
export const THEME_UPDATED_AT_KEY = "smartprint.theme.updatedAt";

export interface ThemePreferenceStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export interface ThemePreferenceSnapshot {
  theme: ThemeMode | null;
  updatedAt: number;
}

export interface NativeThemePreferenceSnapshot extends ThemePreferenceSnapshot {
  explicit: boolean;
}

export function parseThemeMode(value: string | null | undefined): ThemeMode | null {
  return value === "light" || value === "dark" ? value : null;
}

function parseUpdatedAt(value: string | null): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= 0 ? Math.floor(parsed) : 0;
}

/** Read the canonical key, migrating the previous QRtoPrint key only when needed. */
export function readThemePreference(storage: ThemePreferenceStorage | null): ThemePreferenceSnapshot {
  if (!storage) return { theme: null, updatedAt: 0 };
  try {
    const canonical = parseThemeMode(storage.getItem(THEME_PREFERENCE_KEY));
    const legacy = canonical ? null : parseThemeMode(storage.getItem(LEGACY_THEME_PREFERENCE_KEY));
    const theme = canonical ?? legacy;
    const updatedAt = parseUpdatedAt(storage.getItem(THEME_UPDATED_AT_KEY));

    if (!canonical && legacy) {
      // Preserve both old Light and Dark choices. The canonical value is written
      // first; optional timestamp/cleanup writes must not hide a successful read.
      storage.setItem(THEME_PREFERENCE_KEY, legacy);
      try { storage.setItem(THEME_UPDATED_AT_KEY, String(updatedAt)); } catch { /* timestamp is optional */ }
      try { storage.removeItem(LEGACY_THEME_PREFERENCE_KEY); } catch { /* best effort cleanup */ }
    } else if (canonical) {
      try { storage.removeItem(LEGACY_THEME_PREFERENCE_KEY); } catch { /* best effort cleanup */ }
    }
    return { theme, updatedAt };
  } catch {
    // Storage read failures are non-fatal; first-install Light remains the default.
    return { theme: null, updatedAt: 0 };
  }
}

export function writeThemePreference(
  storage: ThemePreferenceStorage | null,
  theme: ThemeMode,
  updatedAt: number,
): boolean {
  if (!storage) return false;
  try {
    storage.setItem(THEME_PREFERENCE_KEY, theme);
    storage.setItem(THEME_UPDATED_AT_KEY, String(Math.max(0, Math.floor(updatedAt))));
    storage.removeItem(LEGACY_THEME_PREFERENCE_KEY);
    return true;
  } catch {
    // A blocked or full browser store must not stop the app from rendering.
    return false;
  }
}

/**
 * Android's native preference and the embedded web preference are reconciled by
 * their last-change timestamps. For old installs without timestamps, an existing
 * web choice wins unless the native choice was explicitly saved in the native UI.
 */
export function resolveThemePreference(
  local: ThemePreferenceSnapshot,
  native: NativeThemePreferenceSnapshot | null,
): ThemeMode {
  if (!local.theme) return native?.theme ?? "light";
  if (!native?.theme) return local.theme;
  if (local.updatedAt > native.updatedAt) return local.theme;
  if (native.updatedAt > local.updatedAt) return native.theme;
  return native.explicit ? native.theme : local.theme;
}

export function nextThemePreferenceTimestamp(now: number, localUpdatedAt: number, nativeUpdatedAt: number): number {
  return Math.max(1, Math.floor(now), Math.floor(localUpdatedAt) + 1, Math.floor(nativeUpdatedAt) + 1);
}
