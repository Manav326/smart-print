package com.quantaedge.smartprint

import android.content.Context
import android.os.SystemClock
import android.webkit.WebView
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

@RunWith(AndroidJUnit4::class)
class SmartPrintThemeWebViewInstrumentedTest {
    private fun evaluate(scenario: ActivityScenario<MainActivity>, script: String): String {
        val result = AtomicReference<String>()
        val completed = CountDownLatch(1)
        scenario.onActivity { activity ->
            val webView = MainActivity::class.java.getDeclaredMethod("ensureWebView")
                .apply { isAccessible = true }
                .invoke(activity) as WebView
            webView.evaluateJavascript(script) { value ->
                result.set(value ?: "null")
                completed.countDown()
            }
        }
        assertTrue("WebView JavaScript evaluation timed out.", completed.await(5, TimeUnit.SECONDS))
        val encoded = result.get()
        return JSONArray("[$encoded]").getString(0)
    }

    @Test
    fun themeOnlyJavascriptBridgePersistsChoiceAndKeepsItAfterReload() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val preferences = context.getSharedPreferences(
            SmartPrintThemePreference.PREFERENCES_NAME,
            Context.MODE_PRIVATE
        )
        val hadTheme = preferences.contains("theme")
        val previousTheme = preferences.getString("theme", null)
        val hadUpdatedAt = preferences.contains("theme.updatedAt")
        val previousUpdatedAt = preferences.getLong("theme.updatedAt", 0L)
        val hadExplicit = preferences.contains("theme.explicit")
        val previousExplicit = preferences.getBoolean("theme.explicit", false)
        try {
            preferences.edit().clear().commit()
            SmartPrintThemePreference.apply(context)
            assertEquals(SmartPrintTheme.LIGHT, SmartPrintThemePreference.read(context))
            assertEquals(0L, SmartPrintThemePreference.readUpdatedAt(context))
            assertFalse(SmartPrintThemePreference.hasExplicitSelection(context))

            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val webView = MainActivity::class.java.getDeclaredMethod("ensureWebView")
                        .apply { isAccessible = true }
                        .invoke(activity) as WebView
                    webView.loadDataWithBaseURL(
                        BuildConfig.SERVICE_BASE_URL,
                        "<!doctype html><html><head><meta name=\"theme-color\" content=\"#f4f6fa\"></head><body>theme bridge test</body></html>",
                        "text/html",
                        "UTF-8",
                        null
                    )
                }

                SystemClock.sleep(350)
                val setResult = evaluate(
                    scenario,
                    """(() => {
                      localStorage.setItem("qrtoprint.theme", "dark");
                      const bridge = window.SmartPrintThemeNative;
                      if (!bridge) return "missing";
                      bridge.setTheme("dark", Date.now() + 1000);
                      return "available";
                    })()"""
                )
                assertEquals("available", setResult)
                SystemClock.sleep(250)
                assertEquals(SmartPrintTheme.DARK, SmartPrintThemePreference.read(context))
                assertTrue(SmartPrintThemePreference.hasExplicitSelection(context))
                assertTrue(SmartPrintThemePreference.readUpdatedAt(context) > 0L)

                scenario.onActivity { activity ->
                    val webView = MainActivity::class.java.getDeclaredMethod("ensureWebView")
                        .apply { isAccessible = true }
                        .invoke(activity) as WebView
                    webView.reload()
                }
                SystemClock.sleep(450)

                val snapshot = JSONObject(evaluate(
                    scenario,
                    """JSON.stringify({
                      native: window.SmartPrintThemeNative.getTheme(),
                      local: localStorage.getItem("smartprint.theme"),
                      legacy: localStorage.getItem("qrtoprint.theme"),
                      page: document.documentElement.dataset.theme
                    })"""
                ))
                assertEquals("dark", snapshot.getString("native"))
                assertEquals("dark", snapshot.getString("local"))
                assertTrue(snapshot.isNull("legacy"))
                assertEquals("dark", snapshot.getString("page"))
            }
        } finally {
            val restore = preferences.edit().clear()
            if (hadTheme) restore.putString("theme", previousTheme)
            if (hadUpdatedAt) restore.putLong("theme.updatedAt", previousUpdatedAt)
            if (hadExplicit) restore.putBoolean("theme.explicit", previousExplicit)
            restore.commit()
            SmartPrintThemePreference.apply(context)
        }
    }
}
