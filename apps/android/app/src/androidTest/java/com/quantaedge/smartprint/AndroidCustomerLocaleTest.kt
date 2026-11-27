package com.quantaedge.smartprint

import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import android.content.Context

@RunWith(AndroidJUnit4::class)
class AndroidCustomerLocaleTest {
    private val context: Context
        get() = ApplicationProvider.getApplicationContext()

    @Before
    fun clearPreference() {
        context.getSharedPreferences("smartprint.android.customer-ui", Context.MODE_PRIVATE)
            .edit().clear().commit()
    }

    @After
    fun restoreDefault() {
        context.getSharedPreferences("smartprint.android.customer-ui", Context.MODE_PRIVATE)
            .edit().clear().commit()
    }

    @Test
    fun defaultsToEnglishAndPersistsHindiAndHinglish() {
        assertEquals(AndroidCustomerLocale.ENGLISH, AndroidCustomerLocale.current(context))
        AndroidCustomerLocale.save(context, AndroidCustomerLocale.HINDI)
        assertEquals(AndroidCustomerLocale.HINDI, AndroidCustomerLocale.current(context))
        assertEquals("होम", AndroidCustomerLocale.translate(context, "Home"))

        AndroidCustomerLocale.save(context, AndroidCustomerLocale.HINGLISH)
        assertEquals(AndroidCustomerLocale.HINGLISH, AndroidCustomerLocale.current(context))
        assertEquals("Home", AndroidCustomerLocale.translate(context, "Home"))
    }

    @Test
    fun unknownAndNonUiContentRemainUnchanged() {
        AndroidCustomerLocale.save(context, AndroidCustomerLocale.HINDI)
        assertEquals("internal_sync_state", AndroidCustomerLocale.translate(context, "internal_sync_state"))
        assertEquals("A customer supplied document title", AndroidCustomerLocale.translate(context, "A customer supplied document title"))
    }

    @Test
    fun embeddedPortalBootstrapUsesSharedLocalePreference() {
        AndroidCustomerLocale.save(context, AndroidCustomerLocale.HINDI)
        val script = AndroidCustomerLocale.documentStartScript(context)
        assertTrue(script.contains("smartprint-ui-locale"))
        assertTrue(script.contains("var l='hindi'"))
        assertTrue(script.contains("new StorageEvent('storage'"))
    }
}
