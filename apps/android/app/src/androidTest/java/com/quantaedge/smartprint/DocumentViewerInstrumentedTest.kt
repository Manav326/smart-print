package com.quantaedge.smartprint

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.Paint
import android.graphics.pdf.PdfDocument
import android.net.Uri
import android.os.SystemClock
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.action.ViewActions.scrollTo
import androidx.test.espresso.action.ViewActions.swipeLeft
import androidx.test.espresso.action.ViewActions.swipeRight
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withId
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
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
        val source = File(context.cacheDir, "CI-customer-upload-preview-${System.currentTimeMillis()}.pdf")
        createPdf(source)

        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val previewToken = MainActivity::class.java.getDeclaredMethod(
                        "createCustomerUploadPdfPreview",
                        Uri::class.java
                    ).apply { isAccessible = true }
                        .invoke(activity, Uri.fromFile(source)) as String
                    assertNotNull("The Android document provider PDF is copied for preview", previewToken)
                    @Suppress("UNCHECKED_CAST")
                    val previews = MainActivity::class.java.getDeclaredField("customerUploadPdfPreviews")
                        .apply { isAccessible = true }
                        .get(activity) as MutableMap<String, File>
                    val cachedPdf = previews[previewToken]
                    assertNotNull("The preview token resolves to a cached PDF", cachedPdf)
                    assertTrue("The preview uses a separate bounded cache copy", cachedPdf!!.canonicalPath != source.canonicalPath)
                    assertEquals(source.length(), cachedPdf.length())
                    MainActivity::class.java.getDeclaredMethod(
                        "showCustomerUploadPdfPreview",
                        String::class.java
                    ).apply { isAccessible = true }.invoke(activity, previewToken)
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

    private fun createPdf(file: File, pageCount: Int = 2) {
        val document = PdfDocument()
        try {
            repeat(pageCount) { index ->
                val page = document.startPage(
                    PdfDocument.PageInfo.Builder(420, 594, index + 1).create()
                )
                page.canvas.drawColor(Color.WHITE)
                page.canvas.drawText(
                    "Reader page ${index + 1}",
                    36f,
                    72f,
                    Paint(Paint.ANTI_ALIAS_FLAG).apply {
                        textSize = 28f
                        color = Color.BLACK
                    }
                )
                document.finishPage(page)
            }
            FileOutputStream(file).use(document::writeTo)
        } finally {
            document.close()
        }
    }
}
