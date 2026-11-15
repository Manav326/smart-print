package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class WebViewThemeBootstrapPolicyTest {
    @Test
    fun themePreferenceSyncWorksAcrossAllWebViewCapabilityCombinations() {
        for (documentStartAvailable in listOf(false, true)) {
            for (webMessageListenerAvailable in listOf(false, true)) {
                val plan = WebViewThemeBootstrapPolicy.create(
                    documentStartScriptAvailable = documentStartAvailable,
                    webMessageListenerAvailable = webMessageListenerAvailable
                )

                assertEquals(documentStartAvailable, plan.injectAtDocumentStart)
                assertEquals(!documentStartAvailable, plan.applyAfterPageFinished)
                assertTrue("Theme persistence must not depend on WebMessageListener.", plan.installThemeJavascriptInterface)
                assertEquals(webMessageListenerAvailable, plan.webMessageListenerAvailable)
            }
        }
    }

    @Test
    fun bootstrapMigratesLegacyKeyAndNeverForcesAWebViewReload() {
        val script = SmartPrintThemeWebBootstrap.script()

        assertTrue(script.contains("smartprint.theme"))
        assertTrue(script.contains("qrtoprint.theme"))
        assertTrue(script.contains("SmartPrintThemeNative"))
        assertTrue(script.contains("nativeUpdatedAt"))
        assertFalse(script.contains("window.location.reload"))
    }
}
