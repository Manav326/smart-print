package com.quantaedge.smartprint

/**
 * Runs before the portal's React application when document-start scripts are
 * supported, or from onPageFinished on older WebView providers.
 *
 * The embedded browser and native shell share a monotonic preference timestamp.
 * This also migrates the old qrtoprint.theme key without discarding saved choices.
 */
internal object SmartPrintThemeWebBootstrap {
    fun script(): String = """
        (() => {
          const validTheme = value => value === "light" || value === "dark";
          let localTheme = null;
          let localUpdatedAt = 0;
          try {
            const canonical = localStorage.getItem("smartprint.theme");
            const legacy = localStorage.getItem("qrtoprint.theme");
            localTheme = validTheme(canonical) ? canonical : (validTheme(legacy) ? legacy : null);
            const timestamp = Number(localStorage.getItem("smartprint.theme.updatedAt"));
            localUpdatedAt = Number.isFinite(timestamp) && timestamp >= 0 ? Math.floor(timestamp) : 0;
          } catch {}

          let nativeTheme = null;
          let nativeUpdatedAt = 0;
          let nativeExplicit = false;
          try {
            const native = window.SmartPrintThemeNative;
            const candidate = native && native.getTheme();
            nativeTheme = validTheme(candidate) ? candidate : null;
            const timestamp = Number(native && native.getThemeUpdatedAt());
            nativeUpdatedAt = Number.isFinite(timestamp) && timestamp >= 0 ? Math.floor(timestamp) : 0;
            nativeExplicit = !!(native && native.hasExplicitSelection());
          } catch {}

          let selectedTheme = "light";
          if (!localTheme) selectedTheme = nativeTheme || "light";
          else if (!nativeTheme) selectedTheme = localTheme;
          else if (localUpdatedAt > nativeUpdatedAt) selectedTheme = localTheme;
          else if (nativeUpdatedAt > localUpdatedAt) selectedTheme = nativeTheme;
          else selectedTheme = nativeExplicit ? nativeTheme : localTheme;

          const updatedAt = Math.max(1, Date.now(), localUpdatedAt + 1, nativeUpdatedAt + 1);
          try {
            localStorage.setItem("smartprint.theme", selectedTheme);
            localStorage.setItem("smartprint.theme.updatedAt", String(updatedAt));
            localStorage.removeItem("qrtoprint.theme");
          } catch {}
          try {
            const native = window.SmartPrintThemeNative;
            if (native && native.setTheme) native.setTheme(selectedTheme, updatedAt);
          } catch {}

          const applyTheme = () => {
            const root = document.documentElement;
            if (!root) {
              document.addEventListener("DOMContentLoaded", applyTheme, { once: true });
              return;
            }
            root.dataset.theme = selectedTheme;
            root.style.colorScheme = selectedTheme;
            const color = document.querySelector('meta[name="theme-color"]');
            if (color) color.content = selectedTheme === "dark" ? "#0c0f16" : "#f4f6fa";
          };
          applyTheme();
        })();
    """.trimIndent()
}
