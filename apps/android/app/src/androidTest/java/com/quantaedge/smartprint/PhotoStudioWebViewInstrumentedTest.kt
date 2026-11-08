package com.quantaedge.smartprint

import android.content.Context
import android.webkit.WebView
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/**
 * Runs the shared React Photo Studio component inside SmartPrint's actual Android WebView.
 *
 * The fixture is served by the CI/local Vite process at the emulator host gateway. It uses
 * synthetic image data and an in-memory component callback; it never contacts a live shop,
 * submits a payment, dispatches a print job, or asks Android to access a physical printer.
 */
@RunWith(AndroidJUnit4::class)
class PhotoStudioWebViewInstrumentedTest {
    private val context: Context
        get() = InstrumentationRegistry.getInstrumentation().targetContext

    private val preferences
        get() = context.getSharedPreferences("smartprint.customer.last-shop", Context.MODE_PRIVATE)

    private var scenario: ActivityScenario<MainActivity>? = null
    private var originalValues: Map<String, String?> = emptyMap()

    @After
    fun restoreShopConnectionAndCloseActivity() {
        scenario?.close()
        scenario = null
        val editor = preferences.edit()
        listOf("portalUrl", "portalHost", "shopDisplayName", "shopLogoUrl", "shopAccentColor").forEach { key ->
            val value = originalValues[key]
            if (value == null) editor.remove(key) else editor.putString(key, value)
        }
        editor.commit()
    }

    @Test
    fun sharedPhotoStudioEditsAndBuildsMultiPagePdfInsideAndroidWebView() {
        originalValues = listOf("portalUrl", "portalHost", "shopDisplayName", "shopLogoUrl", "shopAccentColor")
            .associateWith { preferences.getString(it, null) }

        // MainActivity intentionally permits debug-only private-LAN development URLs on port 3000.
        // 10.0.2.2 is the Android emulator's host-machine gateway, not a production endpoint.
        val fixtureUrl = "http://10.0.2.2:3000/tests/photo-studio.browser.html" +
            "?mode=shop&theme=dark&jobId=SP-ANDROID-WEBVIEW-QA"
        preferences.edit()
            .putString("portalUrl", fixtureUrl)
            .putString("portalHost", "10.0.2.2")
            .putString("shopDisplayName", "Photo Studio QA Shop")
            .apply()

        scenario = ActivityScenario.launch(MainActivity::class.java)

        waitForJavascript("document.title.includes('SmartPrint Photo Studio browser QA')", 60_000)
        waitForJavascript(
            "(() => { const button=document.querySelector('#open-photo-studio'); return !!button && !button.disabled; })()",
            60_000
        )

        val initial = evaluateJavascript(
            "JSON.stringify({ width: window.innerWidth, documentWidth: document.documentElement.scrollWidth, " +
                "bodyWidth: document.body.scrollWidth, mode: new URLSearchParams(location.search).get('mode') })"
        )
        val viewport = JSONObject(initial)
        assertEquals("shop", viewport.getString("mode"))
        assertTrue("The Photo Studio fixture must fit the Android viewport.", viewport.getInt("documentWidth") <= viewport.getInt("width") + 1)
        assertTrue("The Photo Studio body must fit the Android viewport.", viewport.getInt("bodyWidth") <= viewport.getInt("width") + 1)

        clickButton("Open Photo Studio")
        waitForJavascript("!!document.querySelector('[role=dialog][aria-modal=true]')", 20_000)
        val dialogText = evaluateJavascript("document.querySelector('[role=dialog]').innerText")
        assertTrue("The Android WebView must open the shop-owner editor mode.", dialogText.contains("SHOP · EDIT EXISTING JOB"))

        // Android system Back should dismiss only the editor and preserve the existing shop job.
        androidx.test.espresso.Espresso.pressBack()
        waitForJavascript("!document.querySelector('[role=dialog][aria-modal=true]')", 20_000)
        assertEquals("The native Back action must keep the existing job page open.", "/tests/photo-studio.browser.html", evaluateJavascript("location.pathname"))
        assertEquals("shop", evaluateJavascript("new URLSearchParams(location.search).get('mode')"))
        assertEquals("open-photo-studio", evaluateJavascript("document.activeElement?.id || ''"))

        clickButton("Open Photo Studio")
        waitForJavascript("!!document.querySelector('[role=dialog][aria-modal=true]')", 20_000)
        clickButton("Crop")
        setEditorControl("Zoom", "1.25")
        setEditorControl("Horizontal position", "48")
        setEditorControl("Vertical position", "52")

        clickButton("Looks")
        clickButton("Crisp")

        clickButton("Background")
        clickButton("Pick background from photo")
        evaluateJavascript(
            "(() => { const canvas=document.querySelector('[data-testid=photo-studio-preview-canvas]');" +
                "if(!canvas) throw new Error('Photo Studio preview canvas is missing');" +
                "const r=canvas.getBoundingClientRect();" +
                "canvas.dispatchEvent(new MouseEvent('click',{bubbles:true,clientX:r.left+r.width*(15/210)," +
                "clientY:r.top+r.height*(104/297)})); return true; })()"
        )
        val sampled = evaluateJavascript(
            "document.querySelector('[data-testid=photo-studio-background-state] strong')?.textContent || ''"
        )
        assertTrue("The Android background picker must sample a pixel from the source photo.", sampled.isNotBlank() && sampled != "Not selected")
        setEditorControl("Replacement background", "#dcecff")

        clickButton("Sheet")
        setEditorControl("Photo size", "35x45")
        setEditorControl("Number of photos", "17")
        setEditorControl("Photos per A4 page", "9")
        clickFirstEditorCheckbox()

        setEditorControl("Name for form (optional)", "Android WebView QA")
        setEditorControl("Date of birth (optional)", "1998-05-17")
        clickButton("Text")
        setEditorControl("Caption font size", "7")
        setEditorControl("Caption colour", "#202020")
        clickButton("Sheet")

        val summary = evaluateJavascript("document.querySelector('[data-testid=photo-studio-sheet-summary]')?.innerText || ''")
        assertTrue("The WebView must calculate two A4 pages for 17 photos at nine per page.", summary.contains("A4 pages in this job") && summary.contains("2"))
        assertTrue("The WebView must identify the final partial page as eight photos.", summary.contains("Final A4 page: 8 photos"))

        clickButton("Next A4 page")
        val finalPageLabel = evaluateJavascript("document.querySelector('[data-testid=photo-studio-preview-canvas]')?.getAttribute('aria-label') || ''")
        assertTrue("The Android WebView must navigate to page two of two.", finalPageLabel.contains("page 2 of 2 with 8 photos"))
        clickButton("Previous A4 page")

        clickButton("Save sheet to this job")
        waitForJavascript(
            "(() => { const node=document.querySelector('#photo-studio-result'); if(!node?.textContent) return false;" +
                "try { return JSON.parse(node.textContent).pageCount===2; } catch (_) { return false; } })()",
            90_000
        )

        val resultText = evaluateJavascript("document.querySelector('#photo-studio-result').textContent")
        val result = JSONObject(resultText)
        assertEquals("shop", result.getString("mode"))
        assertEquals("shared-component-only; persistence is verified by the separate shop-app browser test", result.getString("harness"))
        assertEquals("SP-ANDROID-WEBVIEW-QA", result.getString("sourceJobId"))
        assertEquals("application/pdf", result.getString("mediaType"))
        assertEquals("35x45", result.getString("sizeCode"))
        assertEquals(17, result.getInt("photoCount"))
        assertEquals(9, result.getInt("photosPerPage"))
        assertEquals(2, result.getInt("pageCount"))
        assertEquals(2, result.getInt("pageImageCount"))
        assertTrue("The prepared PDF must use exact A4 point dimensions.", result.getBoolean("pdfHasExactA4MediaBox"))
        assertTrue("The uploaded photo must remain byte-for-byte unchanged.", result.getBoolean("sourceIntegrityPreserved"))
        assertTrue("The photo background picker must be reflected in the editor result.", result.getBoolean("backgroundSampled"))
        assertTrue("Optional name and DOB fields must be included after opt-in.", result.getBoolean("captionsIncluded"))
        assertTrue("The generated PDF must contain page data.", result.getLong("outputBytes") > 1_000L)

        val pages = result.getJSONArray("pages")
        assertEquals(2, pages.length())
        val expectedCounts = listOf(9, 8)
        for (index in 0 until pages.length()) {
            val page = pages.getJSONObject(index)
            assertEquals(expectedCounts[index], page.getInt("photoCount"))
            assertEquals(2480, page.getInt("width"))
            assertEquals(3508, page.getInt("height"))
            assertTrue("Page " + (index + 1) + " must contain image data in every expected photo tile.", page.getBoolean("allPhotoCentresContainImage"))
            assertTrue("Page " + (index + 1) + " must preserve blank cutting gaps.", page.getBoolean("allInterPhotoGapsRemainBlank"))
            assertTrue("Page " + (index + 1) + " must embed its exact reviewed JPEG payload in the PDF.", page.getBoolean("jpegPayloadEmbeddedInPdf"))
        }

        val pageErrors = JSONArray(evaluateJavascript("JSON.stringify(window.__photoStudioQaPageErrors || [])"))
        assertEquals("The Android WebView must not produce uncaught Photo Studio errors.", 0, pageErrors.length())

        // Verify that Android can background and resume the WebView without losing the result.
        scenario!!.moveToState(androidx.lifecycle.Lifecycle.State.CREATED)
        scenario!!.moveToState(androidx.lifecycle.Lifecycle.State.RESUMED)
        waitForJavascript("!!document.querySelector('#photo-studio-result')?.textContent", 20_000)
        val afterResume = JSONObject(evaluateJavascript("document.querySelector('#photo-studio-result').textContent"))
        assertEquals(17, afterResume.getInt("photoCount"))
        assertEquals(2, afterResume.getInt("pageCount"))

        // Exercise the customer-facing mode in the same real Android WebView.
        val customerFixtureUrl = fixtureUrl.replace(
            "mode=shop&theme=dark",
            "mode=customer&theme=light"
        )
        scenario!!.onActivity { activity ->
            val field = MainActivity::class.java.getDeclaredField("webView")
            field.isAccessible = true
            val view = field.get(activity) as? WebView
                ?: throw AssertionError("MainActivity did not keep its customer/shop WebView.")
            view.loadUrl(customerFixtureUrl)
        }
        waitForJavascript(
            "new URLSearchParams(location.search).get('mode') === 'customer' && " +
                "(() => { const button=document.querySelector('#open-photo-studio'); return !!button && !button.disabled; })()",
            60_000
        )
        clickButton("Open Photo Studio")
        waitForJavascript("!!document.querySelector('[role=dialog][aria-modal=true]')", 20_000)
        val customerDialogText = evaluateJavascript("document.querySelector('[role=dialog]').innerText")
        assertTrue("The Android WebView must open the customer editor mode.", customerDialogText.contains("CUSTOMER · BEFORE UPLOAD"))

        clickButton("Sheet")
        setEditorControl("Photo size", "35x45")
        setEditorControl("Number of photos", "10")
        setEditorControl("Photos per A4 page", "9")
        val customerSummary = evaluateJavascript("document.querySelector('[data-testid=photo-studio-sheet-summary]')?.innerText || ''")
        assertTrue("The customer flow must calculate two A4 pages.", customerSummary.contains("A4 pages in this job") && customerSummary.contains("2"))
        assertTrue("The customer flow must show its one-photo final page.", customerSummary.contains("Final A4 page: 1 photos"))

        clickButton("Next A4 page")
        val customerFinalPageLabel = evaluateJavascript("document.querySelector('[data-testid=photo-studio-preview-canvas]')?.getAttribute('aria-label') || ''")
        assertTrue("The customer WebView must preview the final partial page.", customerFinalPageLabel.contains("page 2 of 2 with 1 photos"))
        clickButton("Previous A4 page")
        clickButton("Prepare A4 sheet")
        waitForJavascript(
            "(() => { const node=document.querySelector('#photo-studio-result'); if(!node?.textContent) return false;" +
                "try { return JSON.parse(node.textContent).pageCount===2; } catch (_) { return false; } })()",
            90_000
        )

        val customerResult = JSONObject(evaluateJavascript("document.querySelector('#photo-studio-result').textContent"))
        assertEquals("customer", customerResult.getString("mode"))
        assertTrue("The customer flow must not be assigned an existing shop job.", customerResult.isNull("sourceJobId"))
        assertEquals("application/pdf", customerResult.getString("mediaType"))
        assertEquals("35x45", customerResult.getString("sizeCode"))
        assertEquals(10, customerResult.getInt("photoCount"))
        assertEquals(9, customerResult.getInt("photosPerPage"))
        assertEquals(2, customerResult.getInt("pageCount"))
        assertEquals(2, customerResult.getInt("pageImageCount"))
        assertTrue("The customer PDF must use exact A4 page dimensions.", customerResult.getBoolean("pdfHasExactA4MediaBox"))
        assertTrue("The customer photo source must remain byte-for-byte unchanged.", customerResult.getBoolean("sourceIntegrityPreserved"))
        assertTrue("The customer PDF must contain page data.", customerResult.getLong("outputBytes") > 1_000L)

        val customerPages = customerResult.getJSONArray("pages")
        assertEquals(2, customerPages.length())
        val customerExpectedCounts = listOf(9, 1)
        for (index in 0 until customerPages.length()) {
            val page = customerPages.getJSONObject(index)
            assertEquals(customerExpectedCounts[index], page.getInt("photoCount"))
            assertEquals(2480, page.getInt("width"))
            assertEquals(3508, page.getInt("height"))
            assertTrue("Customer page " + (index + 1) + " must contain every expected photo tile.", page.getBoolean("allPhotoCentresContainImage"))
            assertTrue("Customer page " + (index + 1) + " must preserve cutting gaps.", page.getBoolean("allInterPhotoGapsRemainBlank"))
            assertTrue("Customer page " + (index + 1) + " must embed its reviewed JPEG payload in the PDF.", page.getBoolean("jpegPayloadEmbeddedInPdf"))
        }

        val customerErrors = JSONArray(evaluateJavascript("JSON.stringify(window.__photoStudioQaPageErrors || [])"))
        assertEquals("The customer Android WebView must not produce uncaught Photo Studio errors.", 0, customerErrors.length())
    }

    private fun waitForJavascript(condition: String, timeoutMillis: Long) {
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis)
        var last = ""
        while (System.nanoTime() < deadline) {
            last = runCatching { evaluateJavascript(condition) }.getOrDefault("false")
            if (last == "true") return
            Thread.sleep(250)
        }
        throw AssertionError("Timed out waiting for Android WebView condition: $condition (last value: $last)")
    }

    private fun evaluateJavascript(script: String): String {
        val activeScenario = scenario ?: throw AssertionError("Android ActivityScenario was not launched.")
        val result = AtomicReference<String?>(null)
        val completed = CountDownLatch(1)
        activeScenario.onActivity { activity ->
            val field = MainActivity::class.java.getDeclaredField("webView")
            field.isAccessible = true
            val view = field.get(activity) as? WebView
                ?: throw AssertionError("MainActivity did not create its customer/shop WebView.")
            view.evaluateJavascript(script) { raw ->
                result.set(raw)
                completed.countDown()
            }
        }
        assertTrue("Android WebView JavaScript did not return within 15 seconds.", completed.await(15, TimeUnit.SECONDS))
        val raw = result.get() ?: throw AssertionError("Android WebView returned a null JavaScript result.")
        return runCatching { JSONTokener(raw).nextValue()?.toString() ?: "" }.getOrDefault(raw)
    }

    private fun clickButton(label: String) {
        val quoted = JSONObject.quote(label)
        val script = """
            (() => {
              const buttons = [...document.querySelectorAll('button')];
              const button = buttons.find(item =>
                (item.getAttribute('aria-label') || '').trim() === $quoted ||
                (item.innerText || '').trim() === $quoted ||
                (item.innerText || '').includes($quoted)
              );
              if (!button || button.disabled) throw new Error('Photo Studio button not found or disabled: ' + $quoted);
              button.focus();
              button.click();
              return true;
            })()
        """.trimIndent()
        assertEquals("true", evaluateJavascript(script))
    }

    private fun setEditorControl(label: String, value: String) {
        val quotedLabel = JSONObject.quote(label)
        val quotedValue = JSONObject.quote(value)
        val script = """
            (() => {
              const labels = [...document.querySelectorAll('[role="dialog"] label')];
              const label = labels.find(item => (item.innerText || '').trim().startsWith($quotedLabel));
              const control = label?.querySelector('input, select, textarea');
              if (!control) throw new Error('Photo Studio control not found: ' + $quotedLabel);
              const prototype = control instanceof HTMLSelectElement ? HTMLSelectElement.prototype :
                control instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
              const setter = Object.getOwnPropertyDescriptor(prototype, 'value').set;
              setter.call(control, $quotedValue);
              control.dispatchEvent(new Event('input', { bubbles: true }));
              control.dispatchEvent(new Event('change', { bubbles: true }));
              control.blur();
              return true;
            })()
        """.trimIndent()
        assertEquals("true", evaluateJavascript(script))
    }

    private fun clickFirstEditorCheckbox() {
        val script = """
            (() => {
              const label = [...document.querySelectorAll('[role="dialog"] label')]
                .find(item => (item.innerText || '').includes('Add name and DOB to each photo'));
              const checkbox = label?.querySelector('input[type="checkbox"]');
              if (!checkbox) throw new Error('Photo Studio caption opt-in checkbox not found');
              if (!checkbox.checked) checkbox.click();
              return checkbox.checked;
            })()
        """.trimIndent()
        assertEquals("true", evaluateJavascript(script))
    }
}
