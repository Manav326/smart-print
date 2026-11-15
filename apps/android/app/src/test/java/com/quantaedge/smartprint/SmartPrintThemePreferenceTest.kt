package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SmartPrintThemePreferenceTest {
    @Test
    fun missingPreferenceDefaultsToLightWithoutMarkingAnExplicitSelection() {
        val context = androidx.test.core.app.ApplicationProvider.getApplicationContext<android.content.Context>()
        val preferences = context.getSharedPreferences(
            SmartPrintThemePreference.PREFERENCES_NAME,
            android.content.Context.MODE_PRIVATE
        )
        preferences.edit().clear().commit()

        SmartPrintThemePreference.apply(context)

        assertEquals(SmartPrintTheme.LIGHT, SmartPrintThemePreference.read(context))
        assertEquals(0L, SmartPrintThemePreference.readUpdatedAt(context))
        assertFalse(SmartPrintThemePreference.hasExplicitSelection(context))
    }

    @Test
    fun newerWebViewThemeWinsAndOlderCallbacksCannotRollItBack() {
        val context = androidx.test.core.app.ApplicationProvider.getApplicationContext<android.content.Context>()
        val preferences = context.getSharedPreferences(
            SmartPrintThemePreference.PREFERENCES_NAME,
            android.content.Context.MODE_PRIVATE
        )
        val previous = preferences.all.toMap()
        try {
            preferences.edit().clear().commit()
            SmartPrintThemePreference.apply(context)

            assertTrue(SmartPrintThemePreference.saveIfNewer(context, SmartPrintTheme.DARK, 200L))
            assertEquals(SmartPrintTheme.DARK, SmartPrintThemePreference.read(context))
            assertFalse(SmartPrintThemePreference.saveIfNewer(context, SmartPrintTheme.LIGHT, 100L))
            assertEquals(SmartPrintTheme.DARK, SmartPrintThemePreference.read(context))
            assertEquals(200L, SmartPrintThemePreference.readUpdatedAt(context))
        } finally {
            preferences.edit().clear().commit()
            preferences.edit().putAll(previous).commit()
            SmartPrintThemePreference.apply(context)
        }
    }
}
