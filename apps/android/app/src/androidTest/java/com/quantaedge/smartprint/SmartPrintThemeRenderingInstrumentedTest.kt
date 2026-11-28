package com.quantaedge.smartprint

import android.content.Context
import android.content.res.Configuration
import android.graphics.Color
import android.os.Build
import android.util.TypedValue
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class SmartPrintThemeRenderingInstrumentedTest {
    @Test
    fun explicitLightAndDarkPalettesAreNotPartiallyForceDarkened() {
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
            listOf(SmartPrintTheme.LIGHT, SmartPrintTheme.DARK).forEach { expectedTheme ->
                SmartPrintThemePreference.save(context, expectedTheme)
                ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                    scenario.onActivity { activity ->
                        val isDark = (activity.resources.configuration.uiMode and
                            Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
                        assertEquals(expectedTheme == SmartPrintTheme.DARK, isDark)

                        val expectedSurface = if (isDark) "#0C0F16" else "#F4F6FA"
                        val expectedCard = if (isDark) "#141A25" else "#FFFFFF"
                        assertEquals(Color.parseColor(expectedSurface), activity.getColor(R.color.qp_surface))
                        assertEquals(Color.parseColor(expectedCard), activity.getColor(R.color.qp_card))

                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                            val forceDarkAllowed = TypedValue()
                            assertTrue(
                                "Theme.SmartPrint must explicitly declare forceDarkAllowed.",
                                activity.theme.resolveAttribute(
                                    android.R.attr.forceDarkAllowed,
                                    forceDarkAllowed,
                                    true
                                )
                            )
                            assertEquals(
                                "Android Force Dark must not override SmartPrint's complete palette.",
                                0,
                                forceDarkAllowed.data
                            )
                        }
                    }
                }
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
