package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Test

class AndroidCustomerLocaleCopyTest {
    @Test
    fun translatesVisibleNavigationAndActions() {
        assertEquals("होम", AndroidCustomerLocale.translate(AndroidCustomerLocale.HINDI, "Home"))
        assertEquals("फ़ाइलें", AndroidCustomerLocale.translate(AndroidCustomerLocale.HINDI, "Files"))
        assertEquals("Document scan karein", AndroidCustomerLocale.translate(AndroidCustomerLocale.HINGLISH, "Scan document"))
    }

    @Test
    fun preservesEnglishAndUntranslatedNonUiContent() {
        assertEquals("Home", AndroidCustomerLocale.translate(AndroidCustomerLocale.ENGLISH, "Home"))
        assertEquals("internal_sync_state", AndroidCustomerLocale.translate(AndroidCustomerLocale.HINDI, "internal_sync_state"))
        assertEquals("customer-file-2026.pdf", AndroidCustomerLocale.translate(AndroidCustomerLocale.HINDI, "customer-file-2026.pdf"))
    }

    @Test
    fun invalidLocaleFallsBackToEnglish() {
        assertEquals("Home", AndroidCustomerLocale.translate("unknown", "Home"))
        assertTrue(AndroidCustomerLocale.documentStartScript("unknown").contains("var l='english'"))
    }

    private fun assertTrue(value: Boolean) {
        org.junit.Assert.assertTrue(value)
    }
}
