package com.quantaedge.smartprint

import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.net.Uri
import android.webkit.ValueCallback
import android.webkit.WebSettings
import android.webkit.WebView
import java.io.ByteArrayInputStream
import android.webkit.WebViewClient
import android.webkit.WebResourceResponse
import android.webkit.WebResourceRequest
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.action.ViewActions.closeSoftKeyboard
import androidx.test.espresso.action.ViewActions.replaceText
import androidx.test.espresso.assertion.ViewAssertions.doesNotExist
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.isAssignableFrom
import androidx.test.espresso.matcher.ViewMatchers.withId
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withHint
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.hamcrest.Matchers.allOf
import org.junit.Test
import org.junit.runner.RunWith
import eightbitlab.com.blurview.BlurView
import android.view.View
import android.widget.LinearLayout
import android.view.ViewGroup
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue

@RunWith(AndroidJUnit4::class)
class MainActivityInstrumentedTest {
    private fun clickNavigationItem(description: String) {
        onView(allOf(withContentDescription(description), isAssignableFrom(LinearLayout::class.java))).perform(click())
    }

    private fun openNavigationDestination(itemId: Int, expectedText: String) {
        val description = when (itemId) {
            2 -> "My Files"
            3 -> "Print at a shop"
            4 -> "Activity"
            else -> throw IllegalArgumentException("Unknown navigation item: $itemId")
        }
        var lastFailure: androidx.test.espresso.NoMatchingViewException? = null
        repeat(3) {
            try {
                clickNavigationItem(description)
                onView(withText(expectedText)).check(matches(isDisplayed()))
                return
            } catch (failure: androidx.test.espresso.NoMatchingViewException) {
                lastFailure = failure
                // The hosted emulator can occasionally turn a delayed tap into a
                // long-press while the app is rendering its blur-backed navigation.
                // Retry the real menu tap; keep the destination assertion mandatory.
                Thread.sleep(250)
            }
        }
        throw lastFailure ?: AssertionError("Navigation did not open: $expectedText")
    }

    private fun openFilesTab() {
        // Target the actual menu item, then verify both user-facing content and its stable QA landmark.
        openNavigationDestination(2, "New folder")
        onView(withId(R.id.smartprint_files_heading)).check(matches(isDisplayed()))
    }

    @Test
    fun appearanceDefaultsToLightAndPersistsAcrossSignOutAndActivityRestart() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        context.getSharedPreferences(
            SmartPrintThemePreference.PREFERENCES_NAME,
            Context.MODE_PRIVATE
        ).edit().clear().commit()
        SmartPrintThemePreference.apply(context)
        assertEquals(SmartPrintTheme.LIGHT, SmartPrintThemePreference.read(context))

        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val nightMode = activity.resources.configuration.uiMode and
                        android.content.res.Configuration.UI_MODE_NIGHT_MASK
                    assertEquals(android.content.res.Configuration.UI_MODE_NIGHT_NO, nightMode)
                }
                onView(withContentDescription("Account")).perform(click())
                onView(withText("Appearance")).check(matches(isDisplayed()))
                onView(withText("Light theme")).check(matches(isDisplayed()))
                onView(withText("Dark theme")).perform(click())
            }

            assertEquals(SmartPrintTheme.DARK, SmartPrintThemePreference.read(context))
            // The app's customer sign-out clears authentication only, not UI preferences.
            CustomerAccountStore(context).clear()
            assertEquals(SmartPrintTheme.DARK, SmartPrintThemePreference.read(context))

            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val nightMode = activity.resources.configuration.uiMode and
                        android.content.res.Configuration.UI_MODE_NIGHT_MASK
                    assertEquals(android.content.res.Configuration.UI_MODE_NIGHT_YES, nightMode)
                }
            }
        } finally {
            SmartPrintThemePreference.save(context, SmartPrintTheme.LIGHT)
        }
    }


    @Test
    fun toolbarKeepsSmartPrintByQuantaEdgeBrandAndModernBackIcon() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            onView(withContentDescription("Account")).perform(click())
            scenario.onActivity { activity ->
                val toolbar = MainActivity::class.java.getDeclaredField("toolbar").apply { isAccessible = true }
                    .get(activity) as com.google.android.material.appbar.MaterialToolbar
                assertEquals("SmartPrint", toolbar.title.toString())
                assertTrue(toolbar.subtitle.toString().contains("by QuantaEdge"))
                assertTrue(toolbar.subtitle.toString().contains("Account"))
                assertEquals("Back", toolbar.navigationContentDescription)
                assertNotNull(toolbar.navigationIcon)
            }
        }
    }

    @Test
    fun fileCheckboxesAppearOnlyInExplicitSelectionModeAndSelectAllIsFolderScoped() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val documents = File(context.filesDir, "documents")
        val folder = File(documents, "CI-selection-${System.currentTimeMillis()}")
        val nested = File(folder, "nested")
        val first = File(folder, "first.pdf")
        val second = File(folder, "second.pdf")
        nested.mkdirs()
        first.writeText("%PDF-1.4 selection test")
        second.writeText("%PDF-1.4 selection test")
        File(nested, "nested.pdf").writeText("%PDF-1.4 nested selection test")
        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                openNavigationDestination(2, "New folder")
                onView(withText(folder.name)).perform(click())
                onView(withText(first.name)).check(matches(isDisplayed()))
                onView(withContentDescription("Select " + first.name)).check(doesNotExist())
                onView(withContentDescription("Enter file selection mode")).perform(click())
                onView(withContentDescription("Select " + first.name)).check(matches(isDisplayed()))
                onView(withText("Select all")).perform(click())
                onView(withText("2 selected")).check(matches(isDisplayed()))
                onView(withText("Deselect all")).check(matches(isDisplayed()))
                onView(withContentDescription("Finish file selection")).perform(click())
                onView(withContentDescription("Select " + first.name)).check(doesNotExist())
                onView(withText("2 selected")).check(matches(isDisplayed()))
            }
        } finally {
            folder.deleteRecursively()
        }
    }

    @Test
    fun shopWifiVerificationUsesBrandedBottomSheet() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            // Inspect the real BottomSheetDialog view tree directly. Espresso's RootViewPicker
            // requires the emulator window to own focus, which is not guaranteed for a
            // headless emulator even while ActivityScenario has the activity RESUMED.
            val dialogReference = AtomicReference<com.google.android.material.bottomsheet.BottomSheetDialog>()
            scenario.onActivity { activity ->
                val dialog = MainActivity::class.java.getDeclaredMethod(
                    "showShopWifiVerificationResult", String::class.java, String::class.java, Boolean::class.javaPrimitiveType!!
                ).apply { isAccessible = true }.invoke(
                    activity, "Shop Wi-Fi verified",
                    "This phone reached the shop computer over the selected network.", true
                ) as com.google.android.material.bottomsheet.BottomSheetDialog
                dialogReference.set(dialog)
            }

            val dialog = dialogReference.get()
            assertNotNull("The Wi-Fi verification sheet must be created.", dialog)
            assertTrue("The Wi-Fi verification sheet must be shown.", dialog.isShowing)
            val root = dialog.window?.decorView
            assertNotNull("The Wi-Fi verification sheet must have a window view.", root)

            fun collectViews(view: View): List<View> {
                val result = mutableListOf(view)
                if (view is ViewGroup) {
                    for (index in 0 until view.childCount) {
                        result += collectViews(view.getChildAt(index))
                    }
                }
                return result
            }

            val views = collectViews(root!!)
            assertTrue(
                "The sheet must show the verified title.",
                views.any { it is android.widget.TextView && it.text.toString() == "Shop Wi-Fi verified" }
            )
            assertTrue("The sheet must show the verified Wi-Fi icon.", views.any {
                it.contentDescription?.toString() == "Wi-Fi verified"
            })
            val done = views.firstOrNull {
                it is android.widget.TextView && it.text.toString() == "Done"
            }
            assertNotNull("The sheet must include its Done action.", done)
            androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().runOnMainSync {
                done!!.performClick()
            }
            assertTrue("The Done action must dismiss the verification sheet.", !dialog.isShowing)
        }
    }

    @Test
    fun readableTextRolesMeetContrastInLightAndDarkThemes() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        fun luminance(color: Int): Double {
            fun channel(value: Int): Double {
                val normalized = value / 255.0
                return if (normalized <= 0.04045) normalized / 12.92
                else Math.pow((normalized + 0.055) / 1.055, 2.4)
            }
            return 0.2126 * channel(android.graphics.Color.red(color)) +
                0.7152 * channel(android.graphics.Color.green(color)) +
                0.0722 * channel(android.graphics.Color.blue(color))
        }
        fun contrast(foreground: Int, background: Int): Double {
            val first = luminance(foreground)
            val second = luminance(background)
            return (maxOf(first, second) + 0.05) / (minOf(first, second) + 0.05)
        }

        try {
            SmartPrintThemePreference.save(context, SmartPrintTheme.LIGHT)
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val surface = activity.getColor(R.color.qp_surface)
                    assertTrue("Light primary text contrast", contrast(activity.getColor(R.color.qp_text), surface) >= 4.5)
                    assertTrue("Light muted text contrast", contrast(activity.getColor(R.color.qp_muted), surface) >= 4.5)
                    assertTrue("Light primary button contrast", contrast(activity.getColor(R.color.qp_primary_content), activity.getColor(R.color.qp_primary)) >= 4.5)
                    assertTrue("Light danger button contrast", contrast(activity.getColor(R.color.qp_danger_content), activity.getColor(R.color.qp_danger)) >= 4.5)
                    assertTrue("Light hero secondary text contrast", contrast(activity.getColor(R.color.qp_hero_muted), activity.getColor(R.color.qp_hero)) >= 4.5)
                }
            }

            SmartPrintThemePreference.save(context, SmartPrintTheme.DARK)
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val surface = activity.getColor(R.color.qp_surface)
                    assertTrue("Dark primary text contrast", contrast(activity.getColor(R.color.qp_text), surface) >= 4.5)
                    assertTrue("Dark muted text contrast", contrast(activity.getColor(R.color.qp_muted), surface) >= 4.5)
                    assertTrue("Dark primary button contrast", contrast(activity.getColor(R.color.qp_primary_content), activity.getColor(R.color.qp_primary)) >= 4.5)
                    assertTrue("Dark danger button contrast", contrast(activity.getColor(R.color.qp_danger_content), activity.getColor(R.color.qp_danger)) >= 4.5)
                    assertTrue("Dark hero secondary text contrast", contrast(activity.getColor(R.color.qp_hero_muted), activity.getColor(R.color.qp_hero)) >= 4.5)
                }
            }
        } finally {
            SmartPrintThemePreference.save(context, SmartPrintTheme.LIGHT)
        }
    }


    @Test
    fun qrScannerUsesTheSavedLightAndDarkThemeForItsNativeChrome() {
        val context = ApplicationProvider.getApplicationContext<Context>()

        fun verifyTheme(theme: SmartPrintTheme, expectedNightMode: Int, useLightSystemBars: Boolean) {
            SmartPrintThemePreference.save(context, theme)
            ActivityScenario.launch(QrScannerActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val nightMode = activity.resources.configuration.uiMode and
                        android.content.res.Configuration.UI_MODE_NIGHT_MASK
                    assertEquals(expectedNightMode, nightMode)

                    // Android 15 may report transparent raw bar colours after enforcing edge-to-edge.
                    // Verify the content backdrop behind those bars and the system icon contrast flags.
                    val content = activity.findViewById<ViewGroup>(android.R.id.content)
                    val scannerRoot = content.getChildAt(0)
                    val backdrop = scannerRoot.background as? android.graphics.drawable.LayerDrawable
                    assertTrue("Scanner background must extend behind edge-to-edge system bars", backdrop != null)
                    val surface = backdrop!!.getDrawable(0) as? android.graphics.drawable.GradientDrawable
                    assertTrue("Scanner backdrop must start with the themed surface", surface != null)
                    assertEquals(activity.getColor(R.color.qp_surface), surface!!.colors?.firstOrNull())

                    val bars = androidx.core.view.WindowInsetsControllerCompat(
                        activity.window,
                        activity.window.decorView
                    )
                    assertEquals(useLightSystemBars, bars.isAppearanceLightStatusBars)
                    assertEquals(useLightSystemBars, bars.isAppearanceLightNavigationBars)
                }
            }
        }

        try {
            // The theme/backdrop contract is independent of camera access. Avoid
            // granting/revoking a runtime permission from inside the instrumentation
            // process; Android terminates the app UID when runtime grants change.
            verifyTheme(SmartPrintTheme.LIGHT, android.content.res.Configuration.UI_MODE_NIGHT_NO, true)
            verifyTheme(SmartPrintTheme.DARK, android.content.res.Configuration.UI_MODE_NIGHT_YES, false)
        } finally {
            SmartPrintThemePreference.save(context, SmartPrintTheme.LIGHT)
        }
    }

    @Test
    fun documentViewerUsesThemeAwareTextForItsPageCounter() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val firstImage = File(context.cacheDir, "smartprint-theme-viewer-first.png")
        val secondImage = File(context.cacheDir, "smartprint-theme-viewer-second.png")
        fun writeImage(file: File) {
            val bitmap = Bitmap.createBitmap(16, 16, Bitmap.Config.ARGB_8888)
            try {
                file.outputStream().use { stream ->
                    assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG, 100, stream))
                }
            } finally {
                bitmap.recycle()
            }
        }
        writeImage(firstImage)
        writeImage(secondImage)

        fun verify(theme: SmartPrintTheme) {
            SmartPrintThemePreference.save(context, theme)
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val viewer = SmartPrintDocumentViewer(
                        activity,
                        firstImage,
                        listOf(firstImage, secondImage),
                        0
                    ) {}
                    try {
                        val counterField = SmartPrintDocumentViewer::class.java
                            .getDeclaredField("pageCounter")
                            .apply { isAccessible = true }
                        val counter = counterField.get(viewer) as android.widget.TextView
                        assertEquals("Photo 1 of 2", counter.text.toString())
                        assertEquals(activity.getColor(R.color.qp_text), counter.currentTextColor)
                    } finally {
                        viewer.dispose()
                    }
                }
            }
        }

        try {
            verify(SmartPrintTheme.LIGHT)
            verify(SmartPrintTheme.DARK)
        } finally {
            SmartPrintThemePreference.save(context, SmartPrintTheme.LIGHT)
            firstImage.delete()
            secondImage.delete()
        }
    }

    @Test
    fun acceptedShopQrIsSavedAsTheSelectedShopAndOpenedInTheWebView() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val shopPreferences = context.getSharedPreferences(
            "smartprint.customer.last-shop",
            Context.MODE_PRIVATE
        )
        val previousPreferences = shopPreferences.all.toMap()
        shopPreferences.edit().clear().commit()

        // Keep production QR trust rules strict: the emulator's isolated Photo Studio
        // fixture origin is loopback and must not be accepted as a real shop QR.
        val serviceOrigin = "https://service.smartprint.example"
        val stationUrl = serviceOrigin + "/s/" + "Q".repeat(32)
        val stationHost = Uri.parse(stationUrl).host.orEmpty()
        val acceptedRoute = QrScanRoutingPolicy.route(
            rawValue = stationUrl,
            purpose = QrScanPurpose.SHOP_CONNECTION,
            expectedServiceUrl = serviceOrigin,
            allowLocalDevelopment = false
        ) as? QrScanRoute.ConnectToShop
            ?: throw AssertionError("The synthetic production-origin station QR must pass the configured-origin routing policy: station=$stationUrl")
        assertEquals("The QR routing policy must preserve the canonical station URL.", stationUrl, acceptedRoute.link.url)
        val pageLoaded = CountDownLatch(1)
        val requestedWebViewUrl = AtomicReference<String>()
        val interceptedMainFrameUrl = AtomicReference<String>()
        val pageStartedUrl = AtomicReference<String>()
        val pageFinishedUrl = AtomicReference<String>()
        val mainFrameLoadError = AtomicReference<String>()

        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val webView = MainActivity::class.java.getDeclaredMethod("ensureWebView")
                        .apply { isAccessible = true }
                        .invoke(activity) as WebView
                    webView.webViewClient = object : WebViewClient() {
                        override fun shouldInterceptRequest(
                            view: WebView,
                            request: WebResourceRequest
                        ): WebResourceResponse? {
                            if (!request.isForMainFrame) return null
                            interceptedMainFrameUrl.set(request.url.toString())
                            val html = "<!doctype html><html><head><title>SmartPrint QR handoff QA</title></head><body>Station link received</body></html>"
                            return WebResourceResponse(
                                "text/html",
                                "UTF-8",
                                200,
                                "OK",
                                mapOf("Cache-Control" to "no-store"),
                                ByteArrayInputStream(html.toByteArray(Charsets.UTF_8))
                            )
                        }

                        override fun onPageStarted(view: WebView, url: String, favicon: Bitmap?) {
                            pageStartedUrl.set(url)
                        }

                        override fun onPageFinished(view: WebView, url: String) {
                            pageFinishedUrl.set(url)
                            if (url == stationUrl) pageLoaded.countDown()
                        }

                        override fun onReceivedError(
                            view: WebView,
                            request: WebResourceRequest,
                            error: android.webkit.WebResourceError
                        ) {
                            if (request.isForMainFrame) {
                                mainFrameLoadError.set("${error.errorCode}: ${error.description}")
                            }
                        }
                    }

                    // Exercise the same post-validation handoff used by the accepted
                    // QR route. The routing policy above remains pinned to HTTPS.
                    MainActivity::class.java.getDeclaredMethod(
                        "confirmShop",
                        StationQrLink::class.java
                    ).apply { isAccessible = true }
                        .invoke(activity, acceptedRoute.link)
                    requestedWebViewUrl.set(webView.url)
                }

                val pageFinished = pageLoaded.await(20, TimeUnit.SECONDS)
                assertTrue(
                    "The accepted station URL must finish loading in the SmartPrint WebView. " +
                        "intercepted=${interceptedMainFrameUrl.get()}, " +
                        "started=${pageStartedUrl.get()}, finished=${pageFinishedUrl.get()}, " +
                        "loadError=${mainFrameLoadError.get()}, requested=${requestedWebViewUrl.get()}",
                    pageFinished
                )

                scenario.onActivity { activity ->
                    val webView = MainActivity::class.java.getDeclaredMethod("ensureWebView")
                        .apply { isAccessible = true }
                        .invoke(activity) as WebView
                    val savedUrl = MainActivity::class.java.getDeclaredField("portalUrl")
                        .apply { isAccessible = true }
                        .get(activity) as? String
                    val savedHost = MainActivity::class.java.getDeclaredField("portalHost")
                        .apply { isAccessible = true }
                        .get(activity) as? String

                    assertEquals(stationUrl, savedUrl)
                    assertEquals(stationHost, savedHost)
                    assertEquals(stationUrl, shopPreferences.getString("portalUrl", null))
                    assertEquals(stationHost, shopPreferences.getString("portalHost", null))
                    assertTrue("The selected shop WebView must be visible.", webView.isShown)
                    assertEquals(
                        "The accepted station URL must be loaded into the existing SmartPrint WebView.",
                        stationUrl,
                        webView.url
                    )
                    assertEquals(
                        "The URL ends at the public station code.",
                        "Q".repeat(32),
                        Uri.parse(webView.url).lastPathSegment
                    )
                    assertTrue(
                        "The public station URL must not contain a customer session secret.",
                        !webView.url.orEmpty().contains("sessionToken", ignoreCase = true)
                    )
                }
            }
        } finally {
            val editor = shopPreferences.edit().clear()
            previousPreferences.forEach { (key, value) ->
                when (value) {
                    is String -> editor.putString(key, value)
                    is Boolean -> editor.putBoolean(key, value)
                    is Int -> editor.putInt(key, value)
                    is Long -> editor.putLong(key, value)
                    is Float -> editor.putFloat(key, value)
                    is Set<*> -> editor.putStringSet(key, value.filterIsInstance<String>().toSet())
                }
            }
            editor.commit()
        }
    }
    @Test
    fun launcherIntentOpensMainActivityAndInflatesSmartPrintShell() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val launcherIntent = context.packageManager.getLaunchIntentForPackage(context.packageName)
            ?: throw AssertionError("SmartPrint must be discoverable through an Android launcher intent.")

        assertEquals(android.content.Intent.ACTION_MAIN, launcherIntent.action)
        assertTrue(
            "The app's launch intent must include CATEGORY_LAUNCHER.",
            launcherIntent.hasCategory(android.content.Intent.CATEGORY_LAUNCHER)
        )
        assertEquals(
            "The launcher must resolve to MainActivity.",
            MainActivity::class.java.name,
            launcherIntent.component?.className
        )

        ActivityScenario.launch<MainActivity>(launcherIntent).use { scenario ->
            scenario.onActivity { activity ->
                assertTrue("MainActivity must remain open after launcher startup.", !activity.isFinishing)
                assertTrue("MainActivity must not be destroyed during startup.", !activity.isDestroyed)

                val nativeRoot = activity.findViewById<ViewGroup>(R.id.smartprint_native_root)
                    ?: throw AssertionError("The launcher must inflate #smartprint_native_root.")
                assertTrue("#smartprint_native_root must be visible after launcher startup.", nativeRoot.isShown)
                assertTrue("#smartprint_native_root must contain the native SmartPrint shell.", nativeRoot.childCount > 0)

                val toolbar = MainActivity::class.java.getDeclaredField("toolbar")
                    .apply { isAccessible = true }
                    .get(activity) as com.google.android.material.appbar.MaterialToolbar
                assertEquals("SmartPrint", toolbar.title.toString())
            }
        }
    }

    @Test
    fun rendersCompactSmartPrintHomeAndFourDestinations() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            onView(withText("Your space")).check(matches(isDisplayed()))
            onView(withId(R.id.smartprint_home_heading)).check(matches(isDisplayed()))
            onView(withText("Recent")).check(matches(isDisplayed()))
            onView(withText("Scan document")).check(matches(isDisplayed()))
            onView(withText("Choose from phone")).check(matches(isDisplayed()))
            onView(withContentDescription("Shop connection")).check(matches(isDisplayed()))
            scenario.onActivity { activity ->
                val customBar = MainActivity::class.java.getDeclaredField("bottomNavigationContent").apply {
                    isAccessible = true
                }.get(activity) as LinearLayout
                assertNotNull(activity.findViewById<View>(R.id.smartprint_native_root))
                assertNotNull(activity.findViewById<View>(R.id.smartprint_nav_home))
                assertNotNull(activity.findViewById<View>(R.id.smartprint_nav_files))
                assertNotNull(activity.findViewById<View>(R.id.smartprint_home_heading))
                assertEquals(4, customBar.childCount)
                val labels = (0 until customBar.childCount).map { index ->
                    (customBar.getChildAt(index) as LinearLayout).getChildAt(1)
                }
                assertEquals("Home", (labels[0] as android.widget.TextView).text.toString())
                assertEquals(View.VISIBLE, labels[0].visibility)
                if (activity.resources.configuration.screenWidthDp < 480) {
                    labels.drop(1).forEach { assertEquals(View.GONE, it.visibility) }
                } else {
                    labels.forEach { assertEquals(View.VISIBLE, it.visibility) }
                }
            }
        }
    }

    @Test
    fun webViewAllowsOnlyTheAppGatedLocalAgentMixedContentException() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                val webView = MainActivity::class.java.getDeclaredMethod("ensureWebView").apply {
                    isAccessible = true
                }.invoke(activity) as WebView
                assertEquals(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW, webView.settings.mixedContentMode)
            }
        }
    }

    @Test
    fun printPickerOffersBrowsePhoneAlongsideSmartPrintMyFiles() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                MainActivity::class.java.getDeclaredField("fileChooserAllowsMultiple").apply {
                    isAccessible = true
                    setBoolean(activity, true)
                }
                MainActivity::class.java.getDeclaredMethod(
                    "showPicker",
                    Boolean::class.javaPrimitiveType!!
                ).apply {
                    isAccessible = true
                    invoke(activity, true)
                }
            }
            onView(withText("SmartPrint My Files")).check(matches(isDisplayed()))
            onView(withText("Browse phone")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun browsePhoneSelectionPassesOriginalContentUrisToTheWebView() {
        val source = Uri.parse("content://com.android.providers.downloads.documents/document/42")
        val received = AtomicReference<Array<Uri>?>(null)
        val callbackDelivered = java.util.concurrent.CountDownLatch(1)
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                MainActivity::class.java.getDeclaredField("fileCallback").apply {
                    isAccessible = true
                    set(activity, ValueCallback<Array<Uri>> { value ->
                        received.set(value)
                        callbackDelivered.countDown()
                    })
                }
                MainActivity::class.java.getDeclaredField("fileChooserAllowsMultiple").apply {
                    isAccessible = true
                    setBoolean(activity, true)
                }
                MainActivity::class.java.getDeclaredMethod(
                    "deliverPhoneDocuments",
                    List::class.java
                ).apply {
                    isAccessible = true
                    invoke(activity, listOf(source))
                }
            }
            assertTrue(
                "The selected phone documents are returned after preview metadata is ready.",
                callbackDelivered.await(5, java.util.concurrent.TimeUnit.SECONDS)
            )
            assertEquals(listOf(source), received.get()?.toList())
        }
    }

    @Test
    fun connectedShopWithoutLogoUsesAnAccessibleStorefrontFallback() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                MainActivity::class.java.getDeclaredField("portalUrl").apply {
                    isAccessible = true
                    set(activity, BuildConfig.SERVICE_BASE_URL + "/s/" + "A".repeat(32))
                }
                val method = MainActivity::class.java.getDeclaredMethod(
                    "saveCustomerShopProfile",
                    String::class.java
                ).apply { isAccessible = true }
                val profile = Uri.encode(
                    """{"displayName":"Riverside Print Shop","logoUrl":"","accentColor":"#7258E8"}"""
                )
                method.invoke(activity, profile)
            }
            onView(withText("Riverside Print Shop")).check(matches(isDisplayed()))
            onView(withContentDescription("Connected shop icon")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun trustedWebViewBridgeStoresTheShopProfileReturnedByTheCustomerSession() {
        val code = "B".repeat(32)
        val responseReceived = java.util.concurrent.CountDownLatch(1)
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                MainActivity::class.java.getDeclaredField("portalUrl").apply {
                    isAccessible = true
                    set(activity, BuildConfig.SERVICE_BASE_URL + "/s/" + code)
                }
                MainActivity::class.java.getDeclaredField("portalHost").apply {
                    isAccessible = true
                    set(activity, android.net.Uri.parse(BuildConfig.SERVICE_BASE_URL).host)
                }
                MainActivity::class.java.getDeclaredMethod("showPrint").apply {
                    isAccessible = true
                    invoke(activity)
                }
                val page = MainActivity::class.java.getDeclaredMethod("ensureWebView").apply {
                    isAccessible = true
                }.invoke(activity) as WebView
                val html = """
                    <!doctype html><html><head><title>Waiting</title></head><body><script>
                    const bridge = window.SmartPrintNative;
                    bridge.addEventListener('message', event => { document.title = event.data; });
                    const payload = encodeURIComponent(JSON.stringify({
                      displayName: 'Bridge Test Print Shop',
                      logoUrl: 'https://shop-logo.invalid/logo.png',
                      accentColor: '#7258E8'
                    }));
                    bridge.postMessage('setCustomerShopProfile|$code|' + payload);
                    </script></body></html>
                """.trimIndent()
                page.loadDataWithBaseURL(
                    BuildConfig.SERVICE_BASE_URL + "/s/" + code,
                    html,
                    "text/html",
                    "UTF-8",
                    null
                )
                val handler = android.os.Handler(android.os.Looper.getMainLooper())
                var attempts = 0
                fun pollForBridgeReply() {
                    page.evaluateJavascript("document.title") { title ->
                        if (title == "\"OK\"") {
                            responseReceived.countDown()
                        } else if (attempts++ < 80) {
                            handler.postDelayed({ pollForBridgeReply() }, 100)
                        }
                    }
                }
                handler.postDelayed({ pollForBridgeReply() }, 100)
            }
            assertTrue("The native profile bridge did not return OK.", responseReceived.await(10, java.util.concurrent.TimeUnit.SECONDS))
            scenario.onActivity { activity ->
                val saved = activity.getSharedPreferences("smartprint.customer.last-shop", Context.MODE_PRIVATE)
                assertEquals("Bridge Test Print Shop", saved.getString("shopDisplayName", null))
                assertEquals("https://shop-logo.invalid/logo.png", saved.getString("shopLogoUrl", null))
                MainActivity::class.java.getDeclaredMethod("showHome").apply {
                    isAccessible = true
                    invoke(activity)
                }
            }
            onView(withText("Bridge Test Print Shop")).check(matches(isDisplayed()))
            onView(withContentDescription("Connected shop icon")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun printUsesTheSharedAndroidPortalWebViewAndHasNoBackArrowInItsHeader() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                MainActivity::class.java.getDeclaredMethod("showPrint").apply {
                    isAccessible = true
                    invoke(activity)
                }
                val toolbar = MainActivity::class.java.getDeclaredField("toolbar").apply {
                    isAccessible = true
                }.get(activity) as com.google.android.material.appbar.MaterialToolbar
                assertEquals(null, toolbar.navigationIcon)
                val page = MainActivity::class.java.getDeclaredMethod("ensureWebView").apply {
                    isAccessible = true
                }.invoke(activity) as WebView
                assertTrue(
                    "Android print must reuse the shared customer portal's Android presentation",
                    page.settings.userAgentString.contains("SmartPrintAndroid/1")
                )
            }
        }
    }

    @Test
    fun filesActionsHaveDedicatedAccessibleIconsAndTheFolderUpControlIsAnIcon() {
        val folderName = "CI Action Icons " + System.currentTimeMillis()
        ActivityScenario.launch(MainActivity::class.java).use {
            openFilesTab()
            onView(withContentDescription("Scan document into this folder")).check(matches(isDisplayed()))
            onView(withContentDescription("Create a new folder")).perform(click())
            onView(withHint("Name")).perform(replaceText(folderName), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withText(folderName)).perform(click())
            onView(withContentDescription("Go up one folder")).check(matches(isDisplayed()))
            onView(withContentDescription("Scan document into this folder")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun renderedNavigationSwitchesBetweenPortraitBottomBarAndLandscapeRail() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                val navigation = MainActivity::class.java.getDeclaredField("navigation").apply {
                    isAccessible = true
                }.get(activity)
                assertTrue("Portrait retains the bottom navigation selection model", navigation is com.google.android.material.bottomnavigation.BottomNavigationView)
                val customBar = MainActivity::class.java.getDeclaredField("bottomNavigationContent").apply {
                    isAccessible = true
                }.get(activity) as LinearLayout
                assertEquals(View.VISIBLE, customBar.visibility)
                assertEquals(LinearLayout.HORIZONTAL, customBar.orientation)
                assertEquals(4, customBar.childCount)
                val firstItem = customBar.getChildAt(0) as LinearLayout
                assertEquals(LinearLayout.HORIZONTAL, firstItem.orientation)
                assertEquals(View.VISIBLE, firstItem.getChildAt(1).visibility)
                if (activity.resources.configuration.screenWidthDp < 480) {
                    val inactiveItem = customBar.getChildAt(1) as LinearLayout
                    assertEquals(View.GONE, inactiveItem.getChildAt(1).visibility)
                }
                activity.requestedOrientation = android.content.pm.ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
            }

            var landscapeRailVisible = false
            val landscapeDeadline = android.os.SystemClock.uptimeMillis() + 8000
            while (!landscapeRailVisible && android.os.SystemClock.uptimeMillis() < landscapeDeadline) {
                androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().waitForIdleSync()
                scenario.onActivity { activity ->
                    if (activity.resources.configuration.orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE) {
                        val navigation = MainActivity::class.java.getDeclaredField("navigation").apply {
                            isAccessible = true
                        }.get(activity)
                        landscapeRailVisible = navigation is com.google.android.material.navigationrail.NavigationRailView
                    }
                }
                if (!landscapeRailVisible) Thread.sleep(100)
            }
            assertTrue("Landscape must recreate the activity with a left navigation rail", landscapeRailVisible)

            scenario.onActivity { activity ->
                activity.requestedOrientation = android.content.pm.ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
            }
            var portraitBarVisible = false
            val portraitDeadline = android.os.SystemClock.uptimeMillis() + 8000
            while (!portraitBarVisible && android.os.SystemClock.uptimeMillis() < portraitDeadline) {
                androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().waitForIdleSync()
                scenario.onActivity { activity ->
                    if (activity.resources.configuration.orientation == android.content.res.Configuration.ORIENTATION_PORTRAIT) {
                        val navigation = MainActivity::class.java.getDeclaredField("navigation").apply {
                            isAccessible = true
                        }.get(activity)
                        portraitBarVisible = navigation is com.google.android.material.bottomnavigation.BottomNavigationView
                    }
                }
                if (!portraitBarVisible) Thread.sleep(100)
            }
            assertTrue("Portrait must restore bottom navigation", portraitBarVisible)
        }
    }

    @Test
    fun nativeShellUsesTwoRealBlurOverlaysForItsNavigationChrome() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                val content = activity.findViewById<ViewGroup>(android.R.id.content)
                fun countBlurViews(view: View): Int {
                    if (view is BlurView) return 1
                    if (view !is ViewGroup) return 0
                    var count = 0
                    for (index in 0 until view.childCount) count += countBlurViews(view.getChildAt(index))
                    return count
                }
                assertEquals(2, countBlurViews(content))
            }
        }
    }

    @Test
    fun guestAccountShowsCompactSignInForm() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        CustomerAccountStore(context).clear()
        ActivityScenario.launch(MainActivity::class.java).use {
            onView(withContentDescription("Account")).perform(click())
            onView(withText("Guest access is ready")).check(matches(isDisplayed()))
            onView(withHint("Email address")).check(matches(isDisplayed()))
            onView(withHint("Password")).check(matches(isDisplayed()))
            onView(withText("Create account")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun scanDestinationUsesFocusedQrEntry() {
        ActivityScenario.launch(MainActivity::class.java).use {
            // Verify the customer route after a real bottom-navigation tap.
            openNavigationDestination(3, "Connect to a shop")
            onView(withText("Scan Shop QR")).check(matches(isDisplayed()))
            onView(withText("Open My Files")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun systemBackReturnsToPreviouslySelectedDestination() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        CustomerAccountStore(context).clear()
        ActivityScenario.launch(MainActivity::class.java).use {
            openNavigationDestination(4, "Print history")
            onView(withContentDescription("Account")).perform(click())
            onView(withText("Guest access is ready")).check(matches(isDisplayed()))
            androidx.test.espresso.Espresso.pressBack()
            onView(withText("Print history")).check(matches(isDisplayed()))
        }
    }


    @Test
    fun systemBackFromPrintTabReturnsHome() {
        ActivityScenario.launch(MainActivity::class.java).use {
            openNavigationDestination(3, "Connect to a shop")
            androidx.test.espresso.Espresso.pressBack()
            onView(withText("Your space")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun foldersCanOpenAndExpandInline() {
        val folderName = "CI School " + System.currentTimeMillis()
        ActivityScenario.launch(MainActivity::class.java).use {
            openFilesTab()
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText(folderName), closeSoftKeyboard())
            onView(withText("Save")).perform(click())

            onView(withText(folderName)).check(matches(isDisplayed()))
            onView(withText(folderName)).perform(click())
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText("Nested"), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withText("Nested")).check(matches(isDisplayed()))
            onView(withText("Scan here")).check(matches(isDisplayed()))
            it.recreate()
            onView(withText("Scan here")).check(matches(isDisplayed()))
            onView(withContentDescription("Go up one folder")).perform(click())
            onView(withContentDescription("Expand $folderName")).perform(click())
            onView(withText("Nested")).check(matches(isDisplayed()))
            onView(withContentDescription("Collapse $folderName")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun systemBackFromNestedFolderReturnsToItsParent() {
        val folderName = "CI Back Folder " + System.currentTimeMillis()
        ActivityScenario.launch(MainActivity::class.java).use {
            openFilesTab()
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText(folderName), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withText(folderName)).perform(click())
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText("Nested"), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withText("Nested")).check(matches(isDisplayed()))
            onView(withText("Nested")).perform(click())
            onView(withText("Scan here")).check(matches(isDisplayed()))

            androidx.test.espresso.Espresso.pressBack()
            onView(withText("Scan here")).check(matches(isDisplayed()))
            onView(withText("Nested")).check(matches(isDisplayed()))
            androidx.test.espresso.Espresso.pressBack()
            onView(withText(folderName)).check(matches(isDisplayed()))
        }
    }

    @Test
    fun fileContextActionsUseBottomSheet() {
        val folderName = "CI Sheet Folder " + System.currentTimeMillis()
        ActivityScenario.launch(MainActivity::class.java).use {
            openFilesTab()
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText(folderName), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withContentDescription("More options for " + folderName)).perform(click())
            onView(withText("Folder actions")).check(matches(isDisplayed()))
            onView(withText("Rename")).check(matches(isDisplayed()))
            onView(withText("Delete")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun unconnectedCustomerCanOpenWifiVerificationWithoutConnectingAShop() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val shopPreferences = context.getSharedPreferences(
            "smartprint.customer.last-shop",
            Context.MODE_PRIVATE
        )
        val qaPreferences = context.getSharedPreferences(
            "smartprint.qa.instrumentation",
            Context.MODE_PRIVATE
        )
        val previousPauseCamera = qaPreferences.getBoolean("pauseQrCameraForPurposeTest", false)
        val previousPreferences = shopPreferences.all.toMap()
        val instrumentation = androidx.test.platform.app.InstrumentationRegistry.getInstrumentation()

        try {
            // Start from a genuinely unconnected phone. The test does not create a
            // customer session or station QR before opening the Wi-Fi readiness flow.
            shopPreferences.edit().clear().commit()
            qaPreferences.edit().putBoolean("pauseQrCameraForPurposeTest", true).commit()

            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val portalField = MainActivity::class.java.getDeclaredField("portalUrl").apply {
                        isAccessible = true
                    }
                    assertEquals(null, portalField.get(activity))
                    MainActivity::class.java.getDeclaredMethod("showScanPrint").apply {
                        isAccessible = true
                    }.invoke(activity)
                }

                val scannerMonitor = instrumentation.addMonitor(
                    QrScannerActivity::class.java.name,
                    null,
                    false
                )
                try {
                    onView(withText("Scan Shop QR")).check(matches(isDisplayed()))
                    onView(withText("Check Wi-Fi")).check(matches(isDisplayed()))
                    onView(withText("Check Wi-Fi")).perform(click())

                    val launched = instrumentation.waitForMonitorWithTimeout(scannerMonitor, 10_000)
                    val scannerActivity = launched as? QrScannerActivity
                        ?: throw AssertionError("The unconnected Wi-Fi check did not launch QrScannerActivity.")
                    assertEquals(
                        "An unconnected phone must launch the Wi-Fi-readiness purpose, not shop connection.",
                        "wifi_readiness",
                        scannerActivity.intent.getStringExtra(QrScannerActivity.EXTRA_SCAN_PURPOSE)
                    )
                    onView(withText("Choose from photos")).check(matches(isDisplayed()))
                    onView(withContentDescription("Choose a QR from a photo or screenshot"))
                        .check(matches(isDisplayed()))
                    assertEquals(
                        "Wi-Fi verification must not create or change the customer's saved shop.",
                        null,
                        shopPreferences.getString("portalUrl", null)
                    )
                    onView(withContentDescription("Close QR scanner")).perform(click())
                } finally {
                    instrumentation.removeMonitor(scannerMonitor)
                }

                // The alternate Scan & Print screen must expose the same independent action.
                scenario.onActivity { activity ->
                    MainActivity::class.java.getDeclaredMethod("showScanPrint").apply {
                        isAccessible = true
                    }.invoke(activity)
                }
                onView(withText("Check Wi-Fi")).check(matches(isDisplayed()))
                assertEquals(null, shopPreferences.getString("portalUrl", null))
            }
        } finally {
            try {
                qaPreferences.edit().putBoolean("pauseQrCameraForPurposeTest", previousPauseCamera).commit()
            } finally {
                val editor = shopPreferences.edit().clear()
                previousPreferences.forEach { (key, value) ->
                    when (value) {
                        is String -> editor.putString(key, value)
                        is Boolean -> editor.putBoolean(key, value)
                        is Int -> editor.putInt(key, value)
                        is Long -> editor.putLong(key, value)
                        is Float -> editor.putFloat(key, value)
                        is Set<*> -> editor.putStringSet(key, value.filterIsInstance<String>().toSet())
                    }
                }
                editor.commit()
            }
        }
    }

    @Test
    fun connectedShopHasDedicatedWifiCheckWithoutStartingAShopSwitch() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val shopPreferences = context.getSharedPreferences(
            "smartprint.customer.last-shop",
            Context.MODE_PRIVATE
        )
        val qaPreferences = context.getSharedPreferences(
            "smartprint.qa.instrumentation",
            Context.MODE_PRIVATE
        )
        val previousPauseCamera = qaPreferences.getBoolean("pauseQrCameraForPurposeTest", false)
        val previousPreferences = shopPreferences.all.toMap()
        val stationUrl = BuildConfig.SERVICE_BASE_URL.trimEnd('/') + "/s/" + "S".repeat(32)
        val stationHost = Uri.parse(stationUrl).host.orEmpty()
        val instrumentation = androidx.test.platform.app.InstrumentationRegistry.getInstrumentation()

        try {
            // This test verifies the Wi-Fi-check menu entry and scanner purpose, not
            // camera recognition. A physical phone can see an unrelated QR nearby and
            // correctly finish scanning before Espresso inspects the instruction.
            // This verifies the Wi-Fi-check entry point and purpose, not camera
            // recognition. Pause only the local-QA camera so an unrelated QR code
            // in the physical environment cannot finish the scanner before inspection.
            qaPreferences.edit().putBoolean("pauseQrCameraForPurposeTest", true).commit()

            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    shopPreferences.edit()
                        .putString("portalUrl", stationUrl)
                        .putString("portalHost", stationHost)
                        .putString("shopDisplayName", "Wi-Fi Check Test Shop")
                        .commit()

                    MainActivity::class.java.getDeclaredField("portalUrl").apply {
                        isAccessible = true
                    }.set(activity, stationUrl)
                    MainActivity::class.java.getDeclaredField("portalHost").apply {
                        isAccessible = true
                    }.set(activity, stationHost)
                    MainActivity::class.java.getDeclaredField("shopDisplayName").apply {
                        isAccessible = true
                    }.set(activity, "Wi-Fi Check Test Shop")
                    MainActivity::class.java.getDeclaredMethod("showHome").apply {
                        isAccessible = true
                    }.invoke(activity)
                    MainActivity::class.java.getDeclaredMethod("showShopManager").apply {
                        isAccessible = true
                    }.invoke(activity)
                }

                val scannerMonitor = instrumentation.addMonitor(
                    QrScannerActivity::class.java.name,
                    null,
                    false
                )
                try {
                    onView(withText("Check Wi-Fi")).check(matches(isDisplayed()))
                    onView(withText("Check Wi-Fi")).perform(click())

                    val launched = instrumentation.waitForMonitorWithTimeout(scannerMonitor, 10_000)
                    val scannerActivity = launched as? QrScannerActivity
                        ?: throw AssertionError("The Check shop Wi-Fi action did not launch QrScannerActivity.")

                    assertEquals(
                        "The connected-shop Wi-Fi action must launch the Wi-Fi-readiness scan purpose.",
                        "wifi_readiness",
                        scannerActivity.intent.getStringExtra(QrScannerActivity.EXTRA_SCAN_PURPOSE)
                    )

                    val inspection = AtomicReference<Pair<Boolean, Boolean>?>(null)
                    scannerActivity.runOnUiThread {
                        fun containsVisibleText(view: View, expected: String): Boolean {
                            if (view is android.widget.TextView &&
                                view.isShown &&
                                view.text?.toString() == expected
                            ) return true
                            if (view is ViewGroup) {
                                for (index in 0 until view.childCount) {
                                    if (containsVisibleText(view.getChildAt(index), expected)) return true
                                }
                            }
                            return false
                        }
                        inspection.set(
                            containsVisibleText(
                                scannerActivity.window.decorView,
                                "Scan the Wi-Fi QR on the shop computer to check the connection."
                            ) to containsVisibleText(
                                scannerActivity.window.decorView,
                                QrScannerActivity.QA_CAMERA_PAUSED_LABEL
                            )
                        )
                    }
                    instrumentation.waitForIdleSync()
                    val visibleState = inspection.get()
                        ?: throw AssertionError("The QR scanner view hierarchy was not inspected.")
                    assertTrue(
                        "The Wi-Fi scanner must display the simple Wi-Fi QR instruction.",
                        visibleState.first
                    )
                    assertTrue(
                        "The isolated QA scanner must display its camera-paused state.",
                        visibleState.second
                    )

                    onView(withContentDescription("Close QR scanner")).perform(click())
                } finally {
                    instrumentation.removeMonitor(scannerMonitor)
                }

                // Closing the native camera Activity can leave the underlying app window
                // temporarily without focus on some OEM builds. Check the actual resumed
                // MainActivity view hierarchy rather than relying on Espresso root focus.
                var shopScreenRestored = false
                val restoreDeadline = System.currentTimeMillis() + 10_000
                while (!shopScreenRestored && System.currentTimeMillis() < restoreDeadline) {
                    instrumentation.waitForIdleSync()
                    scenario.onActivity { activity ->
                        fun containsVisibleShopName(view: View): Boolean {
                            if (view is android.widget.TextView &&
                                view.text?.toString() == "Wi-Fi Check Test Shop" && view.isShown
                            ) return true
                            if (view is ViewGroup) {
                                for (index in 0 until view.childCount) {
                                    if (containsVisibleShopName(view.getChildAt(index))) return true
                                }
                            }
                            return false
                        }
                        shopScreenRestored = activity.lifecycle.currentState.isAtLeast(
                            androidx.lifecycle.Lifecycle.State.RESUMED
                        ) && containsVisibleShopName(activity.window.decorView)
                    }
                    if (!shopScreenRestored) Thread.sleep(100)
                }
                assertTrue(
                    "Closing the QR scanner must return to the resumed shop home screen without changing the selected shop.",
                    shopScreenRestored
                )

                scenario.onActivity { activity ->
                    val currentPortalUrl = MainActivity::class.java.getDeclaredField("portalUrl").apply {
                        isAccessible = true
                    }.get(activity)
                    assertEquals(stationUrl, currentPortalUrl)
                    assertEquals(stationUrl, shopPreferences.getString("portalUrl", null))
                    assertEquals(stationHost, shopPreferences.getString("portalHost", null))
                    assertEquals("Wi-Fi Check Test Shop", shopPreferences.getString("shopDisplayName", null))
                }
            }
        } finally {
            try {
                qaPreferences.edit().putBoolean("pauseQrCameraForPurposeTest", previousPauseCamera).commit()
            } finally {
                val editor = shopPreferences.edit().clear()
                previousPreferences.forEach { (key, value) ->
                    when (value) {
                        is String -> editor.putString(key, value)
                        is Boolean -> editor.putBoolean(key, value)
                        is Int -> editor.putInt(key, value)
                        is Long -> editor.putLong(key, value)
                        is Float -> editor.putFloat(key, value)
                        is Set<*> -> editor.putStringSet(key, value.filterIsInstance<String>().toSet())
                    }
                }
                editor.commit()
            }
        }
    }

}

@RunWith(AndroidJUnit4::class)
class QrScannerPermissionInstrumentedTest {
    @Test
    fun explainsCameraPermissionBeforeStartingTheScanner() {
        // The CI emulator installs this app with runtime permissions denied.
        // Do not change a package permission from the instrumentation process:
        // Android kills the app UID when a runtime permission grant changes.
        ActivityScenario.launch(QrScannerActivity::class.java).use {
            onView(withText("Scan Shop QR")).check(matches(isDisplayed()))
            onView(withText("Scan the Shop QR to send your documents."))
                .check(matches(isDisplayed()))
            onView(withText("Camera access needed")).check(matches(isDisplayed()))
            onView(withText("Allow camera to scan live, or choose a photo below."))
                .check(matches(isDisplayed()))
            onView(withText("Allow camera")).check(matches(isDisplayed()))
        }
    }
}
