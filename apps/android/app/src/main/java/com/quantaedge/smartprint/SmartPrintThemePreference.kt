package com.quantaedge.smartprint

import android.content.Context
import androidx.appcompat.app.AppCompatDelegate

/**
 * Theme choice is intentionally stored outside customer authentication/session
 * preferences so sign-out never resets the user's appearance choice.
 */
enum class SmartPrintTheme(val storedValue: String) {
    LIGHT("light"),
    DARK("dark");

    companion object {
        fun fromStoredValue(value: String?): SmartPrintTheme =
            if (value == DARK.storedValue) DARK else LIGHT
    }
}

object SmartPrintThemePreference {
    const val PREFERENCES_NAME = "smartprint.ui-preferences"
    private const val THEME_KEY = "theme"
    private const val THEME_UPDATED_AT_KEY = "theme.updatedAt"
    private const val THEME_EXPLICIT_KEY = "theme.explicit"

    fun read(context: Context): SmartPrintTheme =
        SmartPrintTheme.fromStoredValue(
            context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
                .getString(THEME_KEY, SmartPrintTheme.LIGHT.storedValue)
        )

    fun readUpdatedAt(context: Context): Long =
        context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
            .getLong(THEME_UPDATED_AT_KEY, 0L)

    /**
     * Older versions did not record an explicit-selection marker. A stored Dark
     * choice is necessarily deliberate; an old Light value may have been only
     * the first-install default and is reconciled against the embedded preference.
     */
    fun hasExplicitSelection(context: Context): Boolean {
        val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
        return preferences.getBoolean(THEME_EXPLICIT_KEY, false) ||
            preferences.getString(THEME_KEY, null) == SmartPrintTheme.DARK.storedValue
    }

    fun nextUpdatedAt(context: Context, now: Long = System.currentTimeMillis()): Long =
        maxOf(1L, now, readUpdatedAt(context) + 1L)

    fun apply(context: Context) {
        val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
        if (!preferences.contains(THEME_KEY)) {
            preferences.edit()
                .putString(THEME_KEY, SmartPrintTheme.LIGHT.storedValue)
                .putLong(THEME_UPDATED_AT_KEY, 0L)
                .putBoolean(THEME_EXPLICIT_KEY, false)
                .commit()
        }
        applyMode(read(context))
    }

    /** Save an explicit native or embedded selection using its shared change timestamp. */
    fun save(
        context: Context,
        theme: SmartPrintTheme,
        updatedAtMillis: Long = nextUpdatedAt(context)
    ): Long {
        val updatedAt = maxOf(0L, updatedAtMillis)
        context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString(THEME_KEY, theme.storedValue)
            .putLong(THEME_UPDATED_AT_KEY, updatedAt)
            .putBoolean(THEME_EXPLICIT_KEY, true)
            .apply()
        applyMode(theme)
        return updatedAt
    }

    /**
     * Ignore stale WebView callbacks. The timestamp is created by the page at
     * selection time, so navigation/recreation cannot roll back a newer choice.
     */
    fun saveIfNewer(context: Context, theme: SmartPrintTheme, updatedAtMillis: Long): Boolean {
        val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
        val currentUpdatedAt = preferences.getLong(THEME_UPDATED_AT_KEY, 0L)
        val currentTheme = SmartPrintTheme.fromStoredValue(preferences.getString(THEME_KEY, null))
        val isExplicit = hasExplicitSelection(context)

        if (updatedAtMillis < currentUpdatedAt) return false
        if (updatedAtMillis == currentUpdatedAt && isExplicit && currentTheme != theme) return false
        if (updatedAtMillis == currentUpdatedAt && currentTheme == theme && isExplicit) return true

        save(context, theme, updatedAtMillis)
        return true
    }

    private fun applyMode(theme: SmartPrintTheme) {
        val mode = if (theme == SmartPrintTheme.DARK) {
            AppCompatDelegate.MODE_NIGHT_YES
        } else {
            AppCompatDelegate.MODE_NIGHT_NO
        }
        if (AppCompatDelegate.getDefaultNightMode() != mode) {
            AppCompatDelegate.setDefaultNightMode(mode)
        }
    }
}
