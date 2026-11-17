package com.quantaedge.smartprint

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.Paint
import android.net.Uri
import android.os.SystemClock
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import com.tom_roush.pdfbox.android.PDFBoxResourceLoader
import com.tom_roush.pdfbox.pdmodel.PDDocument
import com.tom_roush.pdfbox.pdmodel.PDPage
import com.tom_roush.pdfbox.pdmodel.PDPageContentStream
import com.tom_roush.pdfbox.pdmodel.common.PDRectangle
import com.tom_roush.pdfbox.pdmodel.font.PDType1Font
import com.tom_roush.pdfbox.text.PDFTextStripper
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.action.ViewActions.scrollTo
import androidx.test.espresso.action.ViewActions.swipeLeft
import androidx.test.espresso.action.ViewActions.swipeRight
import androidx.test.espresso.assertion.ViewAssertions.doesNotExist
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withId
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.FileOutputStream

@RunWith(AndroidJUnit4::class)
class DocumentViewerInstrumentedTest {
    private fun awaitVisibleText(
        scenario: ActivityScenario<MainActivity>,
        expected: String,
        timeoutMs: Long = 5_000L
    ) {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val deadline = SystemClock.uptimeMillis() + timeoutMs
        while (SystemClock.uptimeMillis() < deadline) {
            instrumentation.waitForIdleSync()
            var found = false
            scenario.onActivity { activity ->
                fun containsVisibleText(view: View): Boolean {
                    if (view is TextView &&
                        view.visibility == View.VISIBLE &&
                        view.isShown &&
                        view.text?.toString() == expected
                    ) return true
                    if (view is ViewGroup) {
                        for (index in 0 until view.childCount) {
                            if (containsVisibleText(view.getChildAt(index))) return true
                        }
                    }
                    return false
                }
                found = containsVisibleText(activity.window.decorView)
            }
            if (found) return
            SystemClock.sleep(100)
        }
        throw AssertionError("Expected visible native page counter '$expected' after Activity recreation.")
    }

    @Test
    fun opensPdfInsideSmartPrintAndNavigatesBetweenPages() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val source = File(context.filesDir, "documents/CI-in-app-reader-${System.currentTimeMillis()}.pdf")
        source.parentFile?.mkdirs()
        createPdf(source)

        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                onView(org.hamcrest.Matchers.allOf(
                    withContentDescription("My Files"),
                    androidx.test.espresso.matcher.ViewMatchers.isAssignableFrom(android.widget.LinearLayout::class.java)
                )).perform(click())
                onView(withText(source.name)).perform(scrollTo(), click())

                onView(withText("Page 1 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Next page")).perform(click())
                onView(withText("Page 2 of 2")).check(matches(isDisplayed()))
                it.recreate()
                awaitVisibleText(it, "Page 2 of 2")
                onView(withContentDescription("Previous page")).perform(click())
                onView(withText("Page 1 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Back to My Files")).perform(click())
                onView(withText(source.name)).check(matches(isDisplayed()))
            }
        } finally {
            source.delete()
        }
    }

    @Test
    fun rapidPdfPageRequestsOnlyDisplayTheLatestRequestedPage() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val source = File(context.filesDir, "documents/CI-reader-latest-page-${System.currentTimeMillis()}.pdf")
        source.parentFile?.mkdirs()
        createPdf(source, pageCount = 12)
        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                onView(org.hamcrest.Matchers.allOf(
                    withContentDescription("My Files"),
                    androidx.test.espresso.matcher.ViewMatchers.isAssignableFrom(android.widget.LinearLayout::class.java)
                )).perform(click())
                onView(withText(source.name)).perform(scrollTo(), click())
                waitForView(withText("Page 1 of 12"))

                scenario.onActivity { activity ->
                    val reader = MainActivity::class.java.getDeclaredField("documentViewer")
                        .apply { isAccessible = true }
                        .get(activity) ?: throw AssertionError("The native PDF reader was not opened.")
                    val showPage = reader.javaClass.getDeclaredMethod("showPdfPage", Int::class.javaPrimitiveType)
                        .apply { isAccessible = true }
                    // Queue multiple page requests on the UI thread without waiting for
                    // intermediate rendering. The latest request must win.
                    for (pageIndex in 1..11) showPage.invoke(reader, pageIndex)
                }

                waitForView(withText("Page 12 of 12"))
                waitForView(withContentDescription("PDF page 12 preview"))
                scenario.onActivity { activity ->
                    val reader = MainActivity::class.java.getDeclaredField("documentViewer")
                        .apply { isAccessible = true }
                        .get(activity) as SmartPrintDocumentViewer
                    assertEquals(11, reader.currentPosition())
                }
            }
        } finally {
            source.delete()
        }
    }

    @Test
    fun theFilmstripProvidesEveryPageOfALongerPdf() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val source = File(context.filesDir, "documents/CI-reader-all-pages-${System.currentTimeMillis()}.pdf")
        source.parentFile?.mkdirs()
        createPdf(source, pageCount = 8)
        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                onView(org.hamcrest.Matchers.allOf(
                    withContentDescription("My Files"),
                    androidx.test.espresso.matcher.ViewMatchers.isAssignableFrom(android.widget.LinearLayout::class.java)
                )).perform(click())
                onView(withText(source.name)).perform(scrollTo(), click())
                onView(withText("Page 1 of 8")).check(matches(isDisplayed()))
                onView(withContentDescription("Document page thumbnails")).perform(swipeLeft())
                onView(withContentDescription("Go to page 8")).perform(scrollTo(), click())
                onView(withText("Page 8 of 8")).check(matches(isDisplayed()))
            }
        } finally { source.delete() }
    }

    @Test
    fun opensCustomerUploadPdfInTheNativeReaderAndReturnsToTheUploadPage() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val previewDirectory = File(context.cacheDir, "customer-upload-pdf-preview")
        val source = File(context.filesDir, "documents/CI-customer-upload-preview-${System.currentTimeMillis()}.pdf")
        source.parentFile?.mkdirs()
        createPdf(source)

        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val sourceUri = androidx.core.content.FileProvider.getUriForFile(
                        context,
                        "${context.packageName}.documents",
                        source
                    )
                    val previewToken = MainActivity::class.java.getDeclaredMethod(
                        "createCustomerUploadPdfPreview",
                        Uri::class.java
                    ).apply { isAccessible = true }
                        .invoke(activity, sourceUri) as String
                    assertNotNull("A provider-backed PDF receives a preview token without being copied.", previewToken)
                    @Suppress("UNCHECKED_CAST")
                    val uriPreviews = MainActivity::class.java.getDeclaredField("customerUploadPdfPreviewUris")
                        .apply { isAccessible = true }
                        .get(activity) as MutableMap<String, Uri>
                    assertEquals(sourceUri, uriPreviews[previewToken])
                    @Suppress("UNCHECKED_CAST")
                    val previews = MainActivity::class.java.getDeclaredField("customerUploadPdfPreviews")
                        .apply { isAccessible = true }
                        .get(activity) as MutableMap<String, File>
                    assertFalse("The upload picker must not duplicate a full PDF just to preview it.", previewToken in previews)
                    MainActivity::class.java.getDeclaredMethod(
                        "showCustomerUploadPdfPreview",
                        String::class.java
                    ).apply { isAccessible = true }.invoke(activity, previewToken)
                    val materialized = previews[previewToken]
                    assertNotNull("Only explicit full-screen preview materializes a cache file.", materialized)
                    assertTrue(materialized!!.canonicalPath != source.canonicalPath)
                    assertEquals(source.length(), materialized.length())
                }

                waitForView(withText("Page 1 of 2"))
                onView(withContentDescription("Back to upload")).perform(click())
                scenario.onActivity { activity ->
                    val toolbar = MainActivity::class.java.getDeclaredField("toolbar")
                        .apply { isAccessible = true }
                        .get(activity) as com.google.android.material.appbar.MaterialToolbar
                    val viewer = MainActivity::class.java.getDeclaredField("documentViewer")
                        .apply { isAccessible = true }
                        .get(activity)
                    val pageContext = toolbar.findViewWithTag<android.widget.TextView>("smartprint-toolbar-page")
                    assertTrue(
                        "The Print destination is restored after closing PDF preview",
                        pageContext?.text?.toString()?.endsWith("PRINT") == true
                    )
                    val webView = MainActivity::class.java.getDeclaredField("webView")
                        .apply { isAccessible = true }
                        .get(activity) as? android.webkit.WebView
                    assertTrue("The customer upload WebView is visible after closing PDF preview", webView?.isShown == true)
                    assertNull("The native preview reader is closed", viewer)
                }
            }
        } finally {
            source.delete()
            previewDirectory.deleteRecursively()
        }
    }

    @Test
    fun extensionlessPdfCompletesPhonePickerHandoffAndRendersInNativeWebView() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val code = "P".repeat(32)
        val source = File(context.filesDir, "documents/CI-phone-picker-extensionless-${System.currentTimeMillis()}")
        source.parentFile?.mkdirs()
        createPdf(source, pageCount = 2)
        val previewDirectory = File(context.cacheDir, "customer-upload-pdf-preview")
        val ready = java.util.concurrent.CountDownLatch(1)
        val callbackReceived = java.util.concurrent.CountDownLatch(1)
        val tokenReceived = java.util.concurrent.CountDownLatch(1)
        val renderedReceived = java.util.concurrent.CountDownLatch(1)
        val selectedUris = java.util.concurrent.atomic.AtomicReference<Array<Uri>?>()
        val previewToken = java.util.concurrent.atomic.AtomicReference<String?>()
        val renderResponse = java.util.concurrent.atomic.AtomicReference<String?>()

        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                lateinit var webView: android.webkit.WebView
                lateinit var sourceUri: Uri
                scenario.onActivity { activity ->
                    val origin = BuildConfig.SERVICE_BASE_URL.trimEnd('/')
                    MainActivity::class.java.getDeclaredField("portalUrl").apply {
                        isAccessible = true
                        set(activity, "$origin/s/$code")
                    }
                    MainActivity::class.java.getDeclaredField("portalHost").apply {
                        isAccessible = true
                        set(activity, android.net.Uri.parse(origin).host)
                    }
                    MainActivity::class.java.getDeclaredMethod("showPrint").apply {
                        isAccessible = true
                        invoke(activity)
                    }

                    webView = MainActivity::class.java.getDeclaredField("webView")
                        .apply { isAccessible = true }
                        .get(activity) as android.webkit.WebView
                    sourceUri = androidx.core.content.FileProvider.getUriForFile(
                        context,
                        "${context.packageName}.documents",
                        source
                    )
                    assertEquals("content", sourceUri.scheme)

                    // Prime a real trusted-origin WebView page before the native
                    // document-picker result handler injects its preview metadata.
                    webView.loadDataWithBaseURL(
                        "$origin/s/$code",
                        "<!doctype html><html><body><script>window.__pickerHandoffReady=true;</script></body></html>",
                        "text/html",
                        "UTF-8",
                        null
                    )
                    val handler = android.os.Handler(android.os.Looper.getMainLooper())
                    var readyAttempts = 0
                    fun pollReady() {
                        webView.evaluateJavascript("document.readyState==='complete' && window.__pickerHandoffReady===true") { value ->
                            if (value == "true") {
                                ready.countDown()
                            } else if (readyAttempts++ < 100) {
                                handler.postDelayed({ pollReady() }, 50)
                            } else {
                                ready.countDown()
                            }
                        }
                    }
                    handler.postDelayed({ pollReady() }, 50)
                }

                assertTrue("The trusted Android WebView fixture did not become ready.", ready.await(10, java.util.concurrent.TimeUnit.SECONDS))
                scenario.onActivity { activity ->
                    MainActivity::class.java.getDeclaredField("fileChooserAllowsMultiple").apply {
                        isAccessible = true
                        setBoolean(activity, true)
                    }
                    MainActivity::class.java.getDeclaredField("fileCallback").apply {
                        isAccessible = true
                        set(activity, android.webkit.ValueCallback<Array<Uri>> { uris ->
                            selectedUris.set(uris)
                            callbackReceived.countDown()
                        })
                    }

                    // Exercise the same Android result handler used after the phone
                    // document picker returns, not the token helper in isolation.
                    MainActivity::class.java.getDeclaredMethod(
                        "deliverPhoneDocuments",
                        List::class.java
                    ).apply { isAccessible = true }.invoke(activity, listOf(sourceUri))

                    webView.evaluateJavascript("window.__smartPrintNativePdfPreviews?.[0] ?? null") { encoded ->
                        previewToken.set(
                            runCatching {
                                org.json.JSONTokener(encoded).nextValue() as? String
                            }.getOrNull()
                        )
                        tokenReceived.countDown()
                    }
                }

                assertTrue("The native picker result was not returned to WebView.", callbackReceived.await(10, java.util.concurrent.TimeUnit.SECONDS))
                assertEquals(listOf(sourceUri), selectedUris.get()?.toList())
                assertTrue("The native preview token was not injected into the selected-file WebView.", tokenReceived.await(5, java.util.concurrent.TimeUnit.SECONDS))
                val token = previewToken.get()
                assertNotNull("The extensionless PDF must receive a preview token through the complete picker handoff.", token)

                scenario.onActivity { activity ->
                    @Suppress("UNCHECKED_CAST")
                    val uriPreviews = MainActivity::class.java.getDeclaredField("customerUploadPdfPreviewUris")
                        .apply { isAccessible = true }
                        .get(activity) as MutableMap<String, Uri>
                    assertEquals("The preview token must refer to the original provider URI.", sourceUri, uriPreviews[token])
                    @Suppress("UNCHECKED_CAST")
                    val filePreviews = MainActivity::class.java.getDeclaredField("customerUploadPdfPreviews")
                        .apply { isAccessible = true }
                        .get(activity) as MutableMap<String, File>
                    assertTrue("A seekable phone PDF must not be duplicated for inline preview.", token !in filePreviews)
                    assertTrue("The original selected PDF remains unchanged.", source.isFile)

                    val html = """
                        <!doctype html><html><head><title>Waiting</title></head><body><script>
                        const bridge = window.SmartPrintNative;
                        bridge.addEventListener('message', event => {
                          const value = String(event.data || '');
                          if (value.startsWith('CUSTOMER_PDF_PREVIEW|') ||
                              value.startsWith('CUSTOMER_PDF_PREVIEW_FAILED|')) {
                            document.title = value;
                          }
                        });
                        bridge.postMessage('previewCustomerPdf|$token|0|2|A4|LANDSCAPE|TopLeft');
                        </script></body></html>
                    """.trimIndent()
                    webView.loadDataWithBaseURL(
                        "${BuildConfig.SERVICE_BASE_URL.trimEnd('/')}/s/$code",
                        html,
                        "text/html",
                        "UTF-8",
                        null
                    )

                    val handler = android.os.Handler(android.os.Looper.getMainLooper())
                    var attempts = 0
                    fun pollForPreview() {
                        webView.evaluateJavascript("document.title") { encodedTitle ->
                            val title = runCatching {
                                org.json.JSONTokener(encodedTitle).nextValue() as? String
                            }.getOrNull().orEmpty()
                            if (title.startsWith("CUSTOMER_PDF_PREVIEW|") || title.startsWith("CUSTOMER_PDF_PREVIEW_FAILED|")) {
                                renderResponse.set(title)
                                renderedReceived.countDown()
                            } else if (attempts++ < 150) {
                                handler.postDelayed({ pollForPreview() }, 100)
                            }
                        }
                    }
                    handler.postDelayed({ pollForPreview() }, 100)
                }

                assertTrue("The Android native preview bridge did not render the picker-selected extensionless PDF.", renderedReceived.await(20, java.util.concurrent.TimeUnit.SECONDS))
                val rendered = renderResponse.get().orEmpty()
                assertTrue("The picker-selected extensionless PDF must render in the native WebView: $rendered", rendered.startsWith("CUSTOMER_PDF_PREVIEW|"))
                val payload = org.json.JSONObject(rendered.removePrefix("CUSTOMER_PDF_PREVIEW|"))
                assertEquals(0, payload.getInt("pageIndex"))
                assertEquals(1, payload.getInt("pageCount"))
                assertEquals(2, payload.getInt("pagesPerSheet"))
                assertEquals("LANDSCAPE", payload.getString("orientation"))
                assertEquals("TopLeft", payload.getString("pagesPerSheetDirection"))
                assertTrue("The native upload preview must include a rendered image.", payload.getString("image").length > 100)
            }
        } finally {
            source.delete()
            previewDirectory.deleteRecursively()
        }
    }

    @Test
    fun multiplePageRotationsAreAskedOnlyWhenLeavingAndDiscardKeepsOriginal() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val source = File(context.filesDir, "documents/CI-reader-discard-${System.currentTimeMillis()}.pdf")
        source.parentFile?.mkdirs()
        createPdf(source, pageCount = 3)
        val originalHash = sha256(source)

        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                onView(org.hamcrest.Matchers.allOf(
                    withContentDescription("My Files"),
                    androidx.test.espresso.matcher.ViewMatchers.isAssignableFrom(android.widget.LinearLayout::class.java)
                )).perform(click())
                onView(withText(source.name)).perform(scrollTo(), click())
                waitForView(withText("Page 1 of 3"))

                onView(withContentDescription("Rotate page")).perform(click())
                waitForView(withContentDescription("PDF page 1 preview"))
                onView(withContentDescription("Next page")).perform(click())
                waitForView(withText("Page 2 of 3"))
                onView(withContentDescription("Rotate page")).perform(click())
                waitForView(withContentDescription("PDF page 2 preview"))
                onView(withContentDescription("Previous page")).perform(click())
                waitForView(withText("Page 1 of 3"))

                scenario.onActivity { activity ->
                    val reader = MainActivity::class.java.getDeclaredField("documentViewer")
                        .apply { isAccessible = true }
                        .get(activity) as SmartPrintDocumentViewer
                    assertEquals(mapOf(0 to 90, 1 to 90), reader.pendingRotationSnapshot())
                }
                onView(withText("Save PDF changes?")).check(doesNotExist())

                // Rotation is staged in memory; the decision is shown only on exit.
                onView(withContentDescription("Back to My Files")).perform(click())
                onView(withText("Save PDF changes?")).check(matches(isDisplayed()))
                onView(withText("Continue editing")).perform(click())
                waitForView(withText("Page 1 of 3"))

                // Activity recreation must not discard pending per-page edits.
                scenario.recreate()
                waitForView(withText("Page 1 of 3"))
                scenario.onActivity { activity ->
                    val reader = MainActivity::class.java.getDeclaredField("documentViewer")
                        .apply { isAccessible = true }
                        .get(activity) as SmartPrintDocumentViewer
                    assertEquals(mapOf(0 to 90, 1 to 90), reader.pendingRotationSnapshot())
                }

                onView(withContentDescription("Back to My Files")).perform(click())
                onView(withText("Save PDF changes?")).check(matches(isDisplayed()))
                onView(withText("Discard changes")).perform(click())
                onView(withText(source.name)).check(matches(isDisplayed()))
            }
            assertTrue(
                "Discard must leave the original PDF bytes unchanged.",
                originalHash.contentEquals(sha256(source))
            )
            assertFalse(
                "Discard must not create a rotated copy.",
                File(source.parentFile, source.nameWithoutExtension + "_rotated.pdf").exists()
            )
        } finally {
            source.delete()
        }
    }

    @Test
    fun savingAsCopyKeepsOriginalAndPreservesSelectablePdfContent() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val source = File(context.filesDir, "documents/CI-reader-copy-${System.currentTimeMillis()}.pdf")
        source.parentFile?.mkdirs()
        val output = File(source.parentFile, source.nameWithoutExtension + "_rotated.pdf")
        createPdf(source, pageCount = 2)
        output.delete()
        PDFBoxResourceLoader.init(context)
        PDDocument.load(source, com.tom_roush.pdfbox.io.MemoryUsageSetting.setupTempFileOnly()).use { sourcePdf ->
            val sourceText = PDFTextStripper().getText(sourcePdf)
            assertTrue("The source fixture must contain selectable text before rotation.", sourceText.contains("Reader page 1"))
            assertTrue("Later source-page text must be selectable before rotation.", sourceText.contains("Reader page 2"))
        }
        val originalHash = sha256(source)

        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                onView(org.hamcrest.Matchers.allOf(
                    withContentDescription("My Files"),
                    androidx.test.espresso.matcher.ViewMatchers.isAssignableFrom(android.widget.LinearLayout::class.java)
                )).perform(click())
                onView(withText(source.name)).perform(scrollTo(), click())
                waitForView(withText("Page 1 of 2"))
                onView(withContentDescription("Rotate page")).perform(click())
                waitForView(withContentDescription("PDF page 1 preview"))
                onView(withContentDescription("Next page")).perform(click())
                waitForView(withText("Page 2 of 2"))
                onView(withContentDescription("Rotate page")).perform(click())
                waitForView(withContentDescription("PDF page 2 preview"))
                onView(withContentDescription("Back to My Files")).perform(click())
                onView(withText("Save as a copy")).check(matches(isDisplayed()))
                onView(withText("Save as a copy")).perform(click())
            }

            val deadline = System.currentTimeMillis() + 20_000L
            while (!output.isFile && System.currentTimeMillis() < deadline) {
                androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().waitForIdleSync()
                SystemClock.sleep(100L)
            }
            assertTrue("Save as a copy must create a new PDF in the same folder.", output.isFile)
            com.tom_roush.pdfbox.android.PDFBoxResourceLoader.init(context)
            com.tom_roush.pdfbox.pdmodel.PDDocument.load(
                output,
                com.tom_roush.pdfbox.io.MemoryUsageSetting.setupTempFileOnly()
            ).use { document ->
                assertEquals(2, document.numberOfPages)
                assertEquals(90, document.getPage(0).rotation)
                assertEquals(90, document.getPage(1).rotation)
                val text = com.tom_roush.pdfbox.text.PDFTextStripper().getText(document)
                assertTrue("Selectable source text must remain in the rotated copy.", text.contains("Reader page 1"))
                assertTrue("Selectable text on later pages must be retained.", text.contains("Reader page 2"))
            }
            assertTrue("The original remains available.", source.isFile)
            assertTrue("Save as a copy must not modify the original.", originalHash.contentEquals(sha256(source)))
        } finally {
            source.delete()
            output.delete()
        }
    }

    @Test
    fun updatingOriginalRequiresExplicitConfirmationAndChangesOnlyChosenPages() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val source = File(context.filesDir, "documents/CI-reader-replace-${System.currentTimeMillis()}.pdf")
        source.parentFile?.mkdirs()
        createPdf(source, pageCount = 2)

        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                onView(org.hamcrest.Matchers.allOf(
                    withContentDescription("My Files"),
                    androidx.test.espresso.matcher.ViewMatchers.isAssignableFrom(android.widget.LinearLayout::class.java)
                )).perform(click())
                onView(withText(source.name)).perform(scrollTo(), click())
                waitForView(withText("Page 1 of 2"))
                onView(withContentDescription("Next page")).perform(click())
                waitForView(withText("Page 2 of 2"))
                onView(withContentDescription("Rotate page")).perform(click())
                waitForView(withContentDescription("PDF page 2 preview"))
                onView(withContentDescription("Back to My Files")).perform(click())
                onView(withText("Update original PDF")).perform(click())
                onView(withText("Replace original PDF?")).check(matches(isDisplayed()))
                onView(withText("Replace original")).perform(click())
            }

            val deadline = System.currentTimeMillis() + 20_000L
            var saved = false
            while (!saved && System.currentTimeMillis() < deadline) {
                saved = runCatching {
                    com.tom_roush.pdfbox.android.PDFBoxResourceLoader.init(context)
                    com.tom_roush.pdfbox.pdmodel.PDDocument.load(
                        source,
                        com.tom_roush.pdfbox.io.MemoryUsageSetting.setupTempFileOnly()
                    ).use { document ->
                        document.numberOfPages == 2 &&
                            document.getPage(0).rotation == 0 &&
                            document.getPage(1).rotation == 90
                    }
                }.getOrDefault(false)
                if (!saved) {
                    androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().waitForIdleSync()
                    SystemClock.sleep(100L)
                }
            }
            assertTrue("Updating original must apply the chosen rotation to that same file.", saved)
            assertFalse(
                "Updating original must not create an additional rotated copy.",
                File(source.parentFile, source.nameWithoutExtension + "_rotated.pdf").exists()
            )
        } finally {
            source.delete()
        }
    }

    private fun sha256(file: File): ByteArray =
        java.security.MessageDigest.getInstance("SHA-256").digest(file.readBytes())

    @Test
    fun opensTheTappedImageAndRestoresItsPositionAfterRecreation() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val directory = File(context.filesDir, "documents/CI-reader-images-${System.currentTimeMillis()}")
        directory.mkdirs()
        val first = File(directory, "first.png")
        val selected = File(directory, "selected.png")
        writeTestImage(first)
        writeTestImage(selected)
        first.setLastModified(1_000L)
        selected.setLastModified(2_000L)

        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                onView(org.hamcrest.Matchers.allOf(
                    withContentDescription("My Files"),
                    androidx.test.espresso.matcher.ViewMatchers.isAssignableFrom(android.widget.LinearLayout::class.java)
                )).perform(click())
                onView(withText(directory.name)).perform(scrollTo(), click())
                onView(withText(selected.name)).perform(scrollTo(), click())

                waitForView(withContentDescription("Image preview"))
                onView(withText("Photo 2 of 2")).check(matches(isDisplayed()))
                it.recreate()
                waitForView(withContentDescription("Image preview"))
                onView(withText("Photo 2 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Image preview")).perform(swipeRight())
                onView(withText("Photo 1 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Image preview")).perform(swipeLeft())
                onView(withText("Photo 2 of 2")).check(matches(isDisplayed()))
                // The PDF test separately covers the toolbar back control. After recreation,
                // use the system back route here to focus this regression on restored image state.
                androidx.test.espresso.Espresso.pressBack()
                onView(withText(selected.name)).check(matches(isDisplayed()))
            }
        } finally {
            directory.deleteRecursively()
        }
    }


    private fun waitForView(matcher: org.hamcrest.Matcher<android.view.View>) {
        val deadline = android.os.SystemClock.uptimeMillis() + 10_000L
        var lastFailure: Throwable? = null
        while (android.os.SystemClock.uptimeMillis() < deadline) {
            try {
                onView(matcher).check(matches(isDisplayed()))
                return
            } catch (error: androidx.test.espresso.NoMatchingViewException) {
                lastFailure = error
            } catch (error: AssertionError) {
                lastFailure = error
            }
            android.os.SystemClock.sleep(100L)
        }
        throw AssertionError("View did not become visible within 10 seconds.", lastFailure)
    }

    private fun writeTestImage(file: File) {
        val bitmap = Bitmap.createBitmap(640, 900, Bitmap.Config.ARGB_8888).apply {
            eraseColor(Color.rgb(245, 242, 235))
            val canvas = android.graphics.Canvas(this)
            canvas.drawColor(Color.rgb(245, 242, 235))
            canvas.drawCircle(320f, 450f, 140f, Paint(Paint.ANTI_ALIAS_FLAG).apply {
                color = Color.rgb(101, 87, 210)
            })
        }
        try {
            FileOutputStream(file).use { output ->
                assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG, 100, output))
            }
        } finally {
            bitmap.recycle()
        }
    }

    /**
     * Use actual PDF text operators so selectable-text preservation assertions
     * exercise rotation output rather than Android canvas glyph outlining.
     */
    private fun createPdf(file: File, pageCount: Int = 2) {
        val context = ApplicationProvider.getApplicationContext<Context>()
        PDFBoxResourceLoader.init(context)
        PDDocument().use { document ->
            repeat(pageCount) { index ->
                val page = PDPage(PDRectangle.A4)
                document.addPage(page)
                PDPageContentStream(document, page).use { content ->
                    content.beginText()
                    content.setFont(PDType1Font.HELVETICA, 28f)
                    content.newLineAtOffset(36f, 760f)
                    content.showText("Reader page ${index + 1}")
                    content.endText()
                }
            }
            document.save(file)
        }
    }
}
