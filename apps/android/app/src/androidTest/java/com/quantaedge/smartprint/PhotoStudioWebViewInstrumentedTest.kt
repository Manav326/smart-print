package com.quantaedge.smartprint

import android.net.Uri
import android.os.SystemClock
import android.os.ParcelFileDescriptor
import android.view.MotionEvent
import android.view.WindowManager
import android.webkit.WebView
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.roundToInt

@RunWith(AndroidJUnit4::class)
class PhotoStudioWebViewInstrumentedTest {
    private fun getWebView(activity: MainActivity): WebView {
        return MainActivity::class.java.getDeclaredMethod("ensureWebView").apply {
            isAccessible = true
        }.invoke(activity) as WebView
    }

    private fun decodeJavascriptString(value: String): String =
        JSONArray("[$value]").getString(0)

    private fun evaluate(scenario: ActivityScenario<MainActivity>, script: String): String {
        val result = AtomicReference<String>()
        val completed = CountDownLatch(1)
        scenario.onActivity { activity ->
            getWebView(activity).evaluateJavascript(script) { value ->
                result.set(value ?: "null")
                completed.countDown()
            }
        }
        assertTrue("Android WebView JavaScript did not respond.", completed.await(12, TimeUnit.SECONDS))
        return result.get()
    }

    private fun waitFor(
        scenario: ActivityScenario<MainActivity>,
        expression: String,
        description: String
    ) {
        val deadline = SystemClock.uptimeMillis() + 20_000
        while (SystemClock.uptimeMillis() < deadline) {
            if (evaluate(scenario, "Boolean($expression)") == "true") return
            Thread.sleep(120)
        }
        val diagnostics = runCatching {
            evaluate(
                scenario,
                """JSON.stringify({
                    href: location.href,
                    readyState: document.readyState,
                    title: document.title,
                    body: document.body?.innerText?.slice(0, 300) ?? "",
                    openButton: Boolean(document.querySelector("#open-photo-studio")),
                    fixtureReady: document.querySelector("#photo-studio-result")?.dataset.ready ?? null,
                    dialogText: document.querySelector('[role="dialog"]')?.innerText?.slice(0, 900) ?? null,
                    activeTools: Array.from(document.querySelectorAll('[role="dialog"] nav[aria-label="Photo editing tools"] button'))
                      .map(button => ({ text: button.textContent?.trim() ?? "", pressed: button.getAttribute("aria-pressed") })),
                    ranges: Array.from(document.querySelectorAll('[role="dialog"] input[type="range"]'))
                      .map(range => {
                        const rect = range.getBoundingClientRect();
                        const label = range.closest("label");
                        return {
                          label: label?.childNodes?.[0]?.textContent?.trim() ?? "",
                          value: range.value, min: range.min, max: range.max, step: range.step,
                          rect: { left: rect.left, top: rect.top, width: rect.width, height: rect.height },
                          dpr: window.devicePixelRatio,
                          visualScale: window.visualViewport?.scale ?? 1
                        };
                      }),
                    touchTrace: window.__smartPrintTouchTrace?.slice(-80) ?? null,
                    dialogButtons: Array.from(document.querySelectorAll('[role="dialog"] button'))
                      .map(button => button.textContent?.replace(/\s+/g, " ").trim() ?? "").slice(0, 24)
                })"""
            )
        }.getOrElse { "WebView diagnostic read failed: ${it.message}" }
        throw AssertionError("Timed out waiting for Android Photo Studio: $description; document=$diagnostics")
    }

    private fun openFixture(query: String): ActivityScenario<MainActivity> {
        assumeTrue(
            "Photo Studio WebView instrumentation is enabled only for the local .localqa package.",
            BuildConfig.PHOTO_STUDIO_QA_ENABLED
        )
        val scenario = ActivityScenario.launch(MainActivity::class.java)
        val resetDraftQuery = if (query.contains("?")) "&resetDraft=1" else "?resetDraft=1"
        val fixtureUrl = BuildConfig.SERVICE_BASE_URL.trimEnd('/') +
            "/tests/photo-studio.browser.html" + query + resetDraftQuery
        assertEquals("Android emulator Photo Studio QA must use ADB-reversed localhost.", "127.0.0.1", Uri.parse(fixtureUrl).host)
        assertEquals("Android emulator Photo Studio QA must use the isolated Vite preview port.", 3000, Uri.parse(fixtureUrl).port)
        scenario.onActivity { activity ->
            val trustedHost = Uri.parse(BuildConfig.SERVICE_BASE_URL).host
                ?: throw AssertionError("The configured SmartPrint QA portal has no host.")
            // Persist a QA-only station URL in Activity state so WebView.restoreState()
            // exercises the same connected-shop restoration path as a real customer session.
            MainActivity::class.java.getDeclaredField("portalUrl").apply {
                isAccessible = true
            }.set(activity, fixtureUrl)
            MainActivity::class.java.getDeclaredField("portalHost").apply {
                isAccessible = true
            }.set(activity, trustedHost)
            MainActivity::class.java.getDeclaredMethod("showPrint").apply {
                isAccessible = true
                invoke(activity)
            }
            val localFixtureAllowed = MainActivity::class.java.getDeclaredMethod(
                "allowedLocalDevelopmentEndpoint", Uri::class.java
            ).apply { isAccessible = true }.invoke(activity, Uri.parse(fixtureUrl)) as Boolean
            assertTrue("Only the explicitly enabled .localqa package may load its ADB-reversed localhost Photo Studio fixture.", localFixtureAllowed)
            getWebView(activity).loadUrl(fixtureUrl)
        }
        waitFor(
            scenario,
            "document.readyState === 'complete' && document.querySelector('#open-photo-studio') && document.querySelector('#photo-studio-result')?.dataset.ready === 'true'",
            "trusted local QA fixture to load"
        )
        // The one-time reset query is stripped by the fixture before the test opens
        // Photo Studio. Persist that clean URL so Activity recreation cannot reload
        // the reset flag and erase the draft whose restoration is under test.
        val cleanFixtureUrl = decodeJavascriptString(evaluate(scenario, "window.location.href"))
        assertFalse(
            "The one-time fixture reset flag must not survive the initial navigation.",
            cleanFixtureUrl.contains("resetDraft=")
        )
        scenario.onActivity { activity ->
            MainActivity::class.java.getDeclaredField("portalUrl").apply {
                isAccessible = true
            }.set(activity, cleanFixtureUrl)
        }
        assertEquals(
            "The Android WebView must use its native SmartPrint user-agent marker.",
            "true",
            evaluate(scenario, "navigator.userAgent.includes('SmartPrintAndroid/1')")
        )
        return scenario
    }

    private fun tapElement(scenario: ActivityScenario<MainActivity>, elementExpression: String) {
        val rectangle = evaluate(
            scenario,
            """(() => {
              const element = $elementExpression;
              if (!element) return null;
              element.scrollIntoView({ block: "center", inline: "center" });
              const rect = element.getBoundingClientRect();
              return JSON.stringify({
                x: rect.left + rect.width / 2,
                y: rect.top + rect.height / 2
              });
            })()"""
        )
        val bounds = JSONObject(decodeJavascriptString(rectangle))
        scenario.onActivity { activity ->
            val view = getWebView(activity)
            val density = activity.resources.displayMetrics.density
            val x = (bounds.getDouble("x") * density).toFloat().coerceIn(1f, (view.width - 2).toFloat())
            val y = (bounds.getDouble("y") * density).toFloat().coerceIn(1f, (view.height - 2).toFloat())
            val downTime = SystemClock.uptimeMillis()
            val down = MotionEvent.obtain(downTime, downTime, MotionEvent.ACTION_DOWN, x, y, 0)
            val up = MotionEvent.obtain(downTime, downTime + 70, MotionEvent.ACTION_UP, x, y, 0)
            try {
                view.dispatchTouchEvent(down)
                view.dispatchTouchEvent(up)
            } finally {
                down.recycle()
                up.recycle()
            }
        }
    }

    private fun tapCssSelector(scenario: ActivityScenario<MainActivity>, selector: String) {
        tapElement(scenario, "document.querySelector(" + JSONObject.quote(selector) + ")")
    }

    private fun tapCssPoint(scenario: ActivityScenario<MainActivity>, xCss: Double, yCss: Double) {
        val coordinates = evaluate(
            scenario,
            "JSON.stringify({ x: $xCss, y: $yCss })"
        )
        val point = JSONObject(decodeJavascriptString(coordinates))
        scenario.onActivity { activity ->
            val view = getWebView(activity)
            val density = activity.resources.displayMetrics.density
            val x = (point.getDouble("x") * density).toFloat().coerceIn(1f, (view.width - 2).toFloat())
            val y = (point.getDouble("y") * density).toFloat().coerceIn(1f, (view.height - 2).toFloat())
            val downTime = SystemClock.uptimeMillis()
            val down = MotionEvent.obtain(downTime, downTime, MotionEvent.ACTION_DOWN, x, y, 0)
            val up = MotionEvent.obtain(downTime, downTime + 70, MotionEvent.ACTION_UP, x, y, 0)
            try {
                view.dispatchTouchEvent(down)
                view.dispatchTouchEvent(up)
            } finally {
                down.recycle()
                up.recycle()
            }
        }
    }

    private fun tapPhotoSheetPoint(scenario: ActivityScenario<MainActivity>, xMm: Double, yMm: Double) {
        val x = xMm / 210.0
        val y = yMm / 297.0
        val coordinates = evaluate(
            scenario,
            """(() => {
              const canvas = document.querySelector('[role="dialog"] canvas');
              const rect = canvas.getBoundingClientRect();
              return JSON.stringify({
                x: rect.left + rect.width * $x,
                y: rect.top + rect.height * $y
              });
            })()"""
        )
        val point = JSONObject(decodeJavascriptString(coordinates))
        tapCssPoint(scenario, point.getDouble("x"), point.getDouble("y"))
    }

    private fun tapButtonText(scenario: ActivityScenario<MainActivity>, prefix: String) {
        val quoted = JSONObject.quote(prefix)
        // React may still be committing the tool-panel update immediately after a
        // native range gesture. Wait for the actual action by its rendered text,
        // then scroll it into view before dispatching the Android touch event.
        waitFor(
            scenario,
            """Array.from(document.querySelectorAll('[role="dialog"] button'))
                .some(button => (button.textContent ?? "").replace(/\s+/g, " ").trim().includes($quoted))""",
            "Photo Studio button '$prefix' to become available"
        )
        tapElement(
            scenario,
            """Array.from(document.querySelectorAll('[role="dialog"] button'))
                .find(button => (button.textContent ?? "").replace(/\s+/g, " ").trim().includes($quoted))"""
        )
    }

    private fun setLabelValue(
        scenario: ActivityScenario<MainActivity>,
        labelText: String,
        value: String
    ) {
        val label = JSONObject.quote(labelText)
        val nextValue = JSONObject.quote(value)
        waitFor(
            scenario,
            """(() => {
              const dialog = document.querySelector('[role="dialog"]');
              const label = Array.from(dialog?.querySelectorAll('label') ?? [])
                .find(item => item.textContent.trim().startsWith($label));
              return Boolean(label?.querySelector('input, select'));
            })()""",
            "Photo Studio field '$labelText' to become available"
        )
        val changed = evaluate(
            scenario,
            """(() => {
              const dialog = document.querySelector('[role="dialog"]');
              const label = Array.from(dialog?.querySelectorAll('label') ?? [])
                .find(item => item.textContent.trim().startsWith($label));
              const control = label?.querySelector('input, select');
              if (!control) return false;
              const prototype = control instanceof HTMLSelectElement
                ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
              const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set;
              if (!setter) return false;
              setter.call(control, $nextValue);
              control.dispatchEvent(new Event('input', { bubbles: true }));
              control.dispatchEvent(new Event('change', { bubbles: true }));
              control.blur();
              return control.value === $nextValue;
            })()"""
        )
        assertEquals("Could not set Photo Studio field: " + labelText, "true", changed)
    }

    private fun setRangeValue(
        scenario: ActivityScenario<MainActivity>,
        labelText: String,
        value: Int
    ) {
        val label = JSONObject.quote(labelText)
        // React swaps tool panels asynchronously after the real Android touch event.
        // Wait for the target control instead of parsing evaluateJavascript("null") as JSON.
        waitFor(
            scenario,
            """(() => {
              const dialog = document.querySelector('[role="dialog"]');
              const label = Array.from(dialog?.querySelectorAll('label') ?? [])
                .find(item => item.textContent.trim().startsWith($label));
              return Boolean(label?.querySelector('input[type="range"]'));
            })()""",
            "Photo Studio range '$labelText' to become available"
        )
        val rectText = evaluate(
            scenario,
            """(() => {
              const dialog = document.querySelector('[role="dialog"]');
              const label = Array.from(dialog?.querySelectorAll('label') ?? [])
                .find(item => item.textContent.trim().startsWith($label));
              const range = label?.querySelector('input[type="range"]');
              if (!range) return null;

              // The controls live inside an independently scrollable tool panel.
              // Scroll the actual input into the visible clipping region before
              // collecting coordinates; getBoundingClientRect() alone also returns
              // coordinates for content hidden outside that panel.
              range.scrollIntoView({ block: "center", inline: "nearest", behavior: "instant" });

              const rect = range.getBoundingClientRect();
              const min = Number(range.min || 0);
              const max = Number(range.max || 100);
              const current = Number(range.value);
              const thumbCssWidth = Number.parseFloat(
                getComputedStyle(range, "::-webkit-slider-thumb").width
              );
              const thumbInset = Math.min(
                Number.isFinite(thumbCssWidth) && thumbCssWidth > 0 ? thumbCssWidth / 2 : 8,
                rect.width / 2
              );
              const travelWidth = Math.max(1, rect.width - thumbInset * 2);
              const point = value => rect.left + thumbInset +
                (max === min ? 0.5 : (value - min) / (max - min)) * travelWidth;
              const startX = point(current);
              const endX = point($value);
              const y = rect.top + rect.height / 2;
              const panel = range.closest('[class*="toolPanel"]');
              const panelRect = panel?.getBoundingClientRect();
              const visible = rect.width > 0 && rect.height > 0 &&
                rect.left >= 0 && rect.right <= window.innerWidth &&
                rect.top >= 0 && rect.bottom <= window.innerHeight &&
                (!panelRect || (
                  rect.left >= panelRect.left && rect.right <= panelRect.right &&
                  rect.top >= panelRect.top && rect.bottom <= panelRect.bottom
                ));
              const startHit = document.elementFromPoint(startX, y);
              const endHit = document.elementFromPoint(endX, y);
              const hitIsRange = hit => hit === range || Boolean(hit && range.contains(hit));
              return JSON.stringify({
                startX, endX, y, min, max, current,
                thumbCssWidth: Number.isFinite(thumbCssWidth) ? thumbCssWidth : null,
                thumbInset, travelWidth,
                devicePixelRatio: window.devicePixelRatio,
                visualViewportScale: window.visualViewport?.scale ?? 1,
                rangeRect: { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height },
                panelRect: panelRect ? { left: panelRect.left, top: panelRect.top, right: panelRect.right, bottom: panelRect.bottom } : null,
                visible,
                startHit: startHit ? startHit.tagName + (startHit.id ? "#" + startHit.id : "") : null,
                endHit: endHit ? endHit.tagName + (endHit.id ? "#" + endHit.id : "") : null,
                startHitIsRange: hitIsRange(startHit),
                endHitIsRange: hitIsRange(endHit)
              });
            })()"""
        )
        val bounds = JSONObject(decodeJavascriptString(rectText))
        val minimum = bounds.getDouble("min")
        val maximum = bounds.getDouble("max")
        require(value.toDouble() in minimum..maximum) {
            "Photo Studio range value $value is outside $labelText bounds $minimum..$maximum."
        }
        assertTrue(
            "Photo Studio range '$labelText' was not scrolled fully into the visible tool panel: $bounds",
            bounds.getBoolean("visible")
        )
        assertTrue(
            "Native touch start/end coordinates do not hit the '$labelText' range input: $bounds",
            bounds.getBoolean("startHitIsRange") && bounds.getBoolean("endHitIsRange")
        )
        var screenOriginX = 0
        var screenOriginY = 0
        var viewWidth = 0
        var viewHeight = 0
        scenario.onActivity { activity ->
            val view = getWebView(activity)
            val location = IntArray(2)
            view.getLocationOnScreen(location)
            screenOriginX = location[0]
            screenOriginY = location[1]
            viewWidth = view.width
            viewHeight = view.height
        }

        // getBoundingClientRect() is expressed in CSS viewport pixels.
        // Convert to display pixels, then add the WebView's screen origin:
        // UiAutomation injects into Android's input dispatcher, not into the
        // WebView's local dispatchTouchEvent method.
        val scale = bounds.getDouble("devicePixelRatio") *
            bounds.getDouble("visualViewportScale")
        val startLocalX = bounds.getDouble("startX") * scale
        val endLocalX = bounds.getDouble("endX") * scale
        val localY = bounds.getDouble("y") * scale
        require(
            startLocalX in 1.0..(viewWidth - 2).toDouble() &&
            endLocalX in 1.0..(viewWidth - 2).toDouble() &&
            localY in 1.0..(viewHeight - 2).toDouble()
        ) {
            "Photo Studio range '$labelText' mapped outside the WebView bounds " +
                "(view=${viewWidth}x${viewHeight}, scale=$scale, geometry=$bounds)."
        }
        val startScreenX = (screenOriginX + startLocalX).toFloat()
        val endScreenX = (screenOriginX + endLocalX).toFloat()
        val screenY = (screenOriginY + localY).toFloat()
        val displayMetrics = InstrumentationRegistry.getInstrumentation()
            .targetContext.resources.displayMetrics
        require(
            startScreenX in 1f..(displayMetrics.widthPixels - 2).toFloat() &&
            endScreenX in 1f..(displayMetrics.widthPixels - 2).toFloat() &&
            screenY in 1f..(displayMetrics.heightPixels - 2).toFloat()
        ) {
            "Photo Studio range '$labelText' mapped outside the device display " +
                "(${displayMetrics.widthPixels}x${displayMetrics.heightPixels}, " +
                "origin=${screenOriginX},${screenOriginY}, scale=$scale, geometry=$bounds)."
        }

        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val automation = instrumentation.uiAutomation

        // Record trusted DOM input events at the document boundary. This
        // distinguishes a real Android gesture that lands on the slider from
        // an event that the emulator injects elsewhere or WebView drops.
        val traceInstalled = evaluate(
            scenario,
            """(() => {
              const dialog = document.querySelector('[role="dialog"]');
              const label = Array.from(dialog?.querySelectorAll('label') ?? [])
                .find(item => item.textContent.trim().startsWith($label));
              const range = label?.querySelector('input[type="range"]');
              if (!range) return false;
              window.__smartPrintTouchTrace = [];
              const eventTypes = [
                "pointerdown", "pointermove", "pointerup", "pointercancel",
                "touchstart", "touchmove", "touchend", "input", "change"
              ];
              for (const type of eventTypes) {
                document.addEventListener(type, event => {
                  const path = typeof event.composedPath === "function" ? event.composedPath() : [];
                  const target = event.target;
                  window.__smartPrintTouchTrace.push({
                    type,
                    trusted: event.isTrusted,
                    target: target instanceof Element
                      ? target.tagName + (target.id ? "#" + target.id : "") + (typeof target.className === "string" && target.className ? "." + target.className.trim().replace(/\s+/g, ".") : "")
                      : String(target),
                    pathHitsRange: path.includes(range),
                    pointerType: "pointerType" in event ? event.pointerType : null,
                    clientX: "clientX" in event ? event.clientX : null,
                    clientY: "clientY" in event ? event.clientY : null,
                    value: range.value
                  });
                  if (window.__smartPrintTouchTrace.length > 120) {
                    window.__smartPrintTouchTrace.splice(0, 40);
                  }
                }, true);
              }
              return true;
            })()"""
        )
        assertEquals("Could not install native range event diagnostics.", "true", traceInstalled)

        // Android's input command injects a SOURCE_TOUCHSCREEN finger stream
        // through InputManager, matching the emulator shell/ADB touch tooling.
        val x1 = startScreenX.roundToInt()
        val x2 = endScreenX.roundToInt()
        val y = screenY.roundToInt()
        val command = "input swipe $x1 $y $x2 $y 650 && echo __SMARTPRINT_TOUCH_EXIT__0 || echo __SMARTPRINT_TOUCH_EXIT__1"
        val shellOutput = ParcelFileDescriptor.AutoCloseInputStream(
            automation.executeShellCommand(command)
        ).bufferedReader().use { it.readText() }
        assertTrue(
            "Android input swipe command failed for '$labelText' from ($x1,$y) to ($x2,$y): $shellOutput; " +
                "geometry=$bounds; view=${viewWidth}x${viewHeight}; origin=${screenOriginX},${screenOriginY}",
            shellOutput.contains("__SMARTPRINT_TOUCH_EXIT__0")
        )

        // Give Chromium a frame to commit pointer/input/change events before
        // the existing bounded assertion verifies the controlled React value.
        instrumentation.waitForIdleSync()
        waitFor(
            scenario,
            """(() => {
              const dialog = document.querySelector('[role="dialog"]');
              const label = Array.from(dialog?.querySelectorAll('label') ?? [])
                .find(item => item.textContent.trim().startsWith($label));
              const range = label?.querySelector('input[type="range"]');
              const actual = Number(range?.value);
              // Android WebView range thumbs quantize to physical touch coordinates.
              // Allow at most one declared step of rounding while still requiring
              // the real native gesture to move the control.
              const step = Number(range?.step || 1);
              return Number.isFinite(actual) && Math.abs(actual - $value) <= step;
            })()""",
            "$labelText range to reach $value (within one native slider step) through touch input"
        )
    }

    private fun clickButton(scenario: ActivityScenario<MainActivity>, prefix: String) {
        // Use a real Android MotionEvent against the native WebView for each visible tool/button.
        tapButtonText(scenario, prefix)
        androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().waitForIdleSync()

        // React state updates are asynchronous relative to Android's UI queue. Tool
        // and look buttons expose aria-pressed so the next assertion must not race
        // the panel swap (for example, Looks -> Brightness).
        val quoted = JSONObject.quote(prefix)
        val hasPressedState = evaluate(
            scenario,
            """Array.from(document.querySelectorAll('[role=dialog] button'))
                .some(button => button.innerText.trim().startsWith($quoted) && button.hasAttribute('aria-pressed'))"""
        )
        if (hasPressedState == "true") {
            waitFor(
                scenario,
                """Array.from(document.querySelectorAll('[role=dialog] button'))
                    .some(button => button.innerText.trim().startsWith($quoted) && button.getAttribute('aria-pressed') === 'true')""",
                "Photo Studio control '$prefix' to become active"
            )
        }
    }

    private fun isSecure(scenario: ActivityScenario<MainActivity>): Boolean {
        val secure = AtomicReference<Boolean>()
        scenario.onActivity { activity ->
            secure.set(
                activity.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE != 0
            )
        }
        return secure.get() == true
    }

    @Test
    fun customerPhotoStudioRunsInsideAndroidWebViewAndProducesExactMultiPagePdf() {
        val scenario = openFixture("?mode=customer&theme=light")
        try {
            tapCssSelector(scenario, "#open-photo-studio")
            waitFor(scenario, "document.querySelector('[role=dialog]') !== null", "Photo Studio dialog")
            waitFor(scenario, "Boolean(window.SmartPrintNative)", "trusted native bridge")
            waitFor(
                scenario,
                "document.querySelector('[role=dialog]')?.getAttribute('aria-busy') === 'false'",
                "decoded source image"
            )
            val secureDeadline = SystemClock.uptimeMillis() + 5_000
            while (!isSecure(scenario) && SystemClock.uptimeMillis() < secureDeadline) Thread.sleep(100)
            assertTrue("Photo Studio must protect the native Android window while displaying a photo.", isSecure(scenario))

            clickButton(scenario, "Crop")
            setRangeValue(scenario, "Zoom", 1)
            setRangeValue(scenario, "Horizontal position", 50)
            setRangeValue(scenario, "Vertical position", 50)
            clickButton(scenario, "Reset crop")

            clickButton(scenario, "Looks")
            clickButton(scenario, "Warm")
            clickButton(scenario, "Crisp")
            clickButton(scenario, "Black & white")
            setRangeValue(scenario, "Brightness", 110)
            setRangeValue(scenario, "Contrast", 120)
            setRangeValue(scenario, "Colour", 90)
            clickButton(scenario, "Reset")

            clickButton(scenario, "Background")
            clickButton(scenario, "Pick background from photo")
            tapPhotoSheetPoint(scenario, 5.0, 10.0)
            assertEquals(
                "A4 page margin must not sample a background colour.",
                "true",
                evaluate(
                    scenario,
                    "document.querySelector('[role=dialog]')?.innerText.includes('Not selected')"
                )
            )
            tapPhotoSheetPoint(scenario, 13.5, 10.0)
            waitFor(
                scenario,
                "document.querySelector('[role=dialog]')?.innerText.includes('#D8E7F5')",
                "sampled synthetic background colour"
            )
            setLabelValue(scenario, "Replacement background", "#f3f6fa")
            setRangeValue(scenario, "Background tolerance", 65)
            clickButton(scenario, "Reset background")

            val captionsEnabled = evaluate(
                scenario,
                """(() => {
                  const checkbox = Array.from(document.querySelectorAll('[role="dialog"] label'))
                    .find(item => item.textContent.includes('Add name and DOB to each photo'))
                    ?.querySelector('input[type="checkbox"]');
                  if (!checkbox) return false;
                  checkbox.checked = true;
                  checkbox.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                })()"""
            )
            assertEquals("true", captionsEnabled)
            setLabelValue(scenario, "Name for form (optional)", "Android Photo QA")
            setLabelValue(scenario, "Date of birth (optional)", "2000-02-29")
            clickButton(scenario, "Text")
            setLabelValue(scenario, "Caption font size", "7")
            setLabelValue(scenario, "Caption colour", "#ff0000")
            clickButton(scenario, "Sheet")

            setLabelValue(scenario, "Photo size", "35x45")
            setLabelValue(scenario, "Number of photos", "1.5")
            waitFor(
                scenario,
                "Array.from(document.querySelectorAll('[role=dialog] input')).find(input => input.getAttribute('aria-label') === 'Number of photos')?.value === '1.5' && document.querySelector('[role=dialog]')?.innerText.includes('Enter a whole number from 1 to 100') && Array.from(document.querySelectorAll('[role=dialog] button')).find(button => button.innerText.includes('Prepare A4 sheet'))?.disabled === true",
                "fractional quantities to remain visible, invalid and blocked without crashing Photo Studio"
            )
            setLabelValue(scenario, "Number of photos", "50")
            setLabelValue(scenario, "Photos per A4 page", "9")
            waitFor(
                scenario,
                "Array.from(document.querySelectorAll('[class*=sheetSummary] > div')).some(row => row.textContent.includes('A4 pages in this job') && row.querySelector('strong')?.textContent === '6')",
                "six-page count for 50 photos at nine per page"
            )

            repeat(5) { clickButton(scenario, "Next A4 page") }
            waitFor(
                scenario,
                "document.querySelector('[role=dialog]')?.innerText.includes('Page 6 of 6')",
                "last page navigation"
            )
            tapButtonText(scenario, "Prepare A4 sheet")
            waitFor(
                scenario,
                "document.querySelector('#photo-studio-result')?.textContent.includes('application/pdf')",
                "generated Android photo PDF"
            )
            val resultText = evaluate(
                scenario,
                "JSON.stringify(JSON.parse(document.querySelector('#photo-studio-result').textContent))"
            )
            val result = JSONObject(decodeJavascriptString(resultText))
            assertEquals("customer", result.getString("mode"))
            assertEquals("application/pdf", result.getString("mediaType"))
            assertEquals("35x45", result.getString("sizeCode"))
            assertEquals(50, result.getInt("photoCount"))
            assertEquals(9, result.getInt("photosPerPage"))
            assertEquals(6, result.getInt("pageCount"))
            assertEquals(6, result.getInt("mediaBoxCount"))
            assertEquals(6, result.getInt("previewPageCount"))
            assertTrue(result.getLong("bytes") in 1L..(20L * 1024L * 1024L))

            val rasterSizes = result.getJSONArray("pdfImageSizes")
            assertEquals(6, rasterSizes.length())
            repeat(rasterSizes.length()) { page ->
                val size = rasterSizes.getJSONArray(page)
                assertEquals(2480, size.getInt(0))
                assertEquals(3508, size.getInt(1))
            }

            val embeddedPages = result.getJSONArray("embeddedPreviewPages")
            assertEquals(6, embeddedPages.length())
            repeat(embeddedPages.length()) { page ->
                assertTrue("PDF page " + (page + 1) + " must embed its exact prepared raster.", embeddedPages.getBoolean(page))
            }

            val pixelProbes = result.getJSONArray("pixelProbes")
            assertEquals(50, pixelProbes.length())
            var finalPageTiles = 0
            repeat(pixelProbes.length()) { index ->
                val probe = pixelProbes.getJSONObject(index)
                val pixels = probe.getJSONArray("rgb")
                assertTrue((0 until pixels.length()).any { pixels.getInt(it) < 245 })
                if (probe.getInt("page") == 6) {
                    assertEquals(finalPageTiles, probe.getInt("slot"))
                    finalPageTiles += 1
                }
            }
            assertEquals(5, finalPageTiles)
            val unused = result.getJSONArray("finalUnusedSlotRgb")
            assertTrue("The final unused grid slot must remain white.", (0 until unused.length()).all { unused.getInt(it) > 245 })
            assertTrue("Caption colour must be present in the generated Android PDF.", pixelProbes.getJSONObject(0).getInt("captionRedPixels") > 0)

            waitFor(scenario, "document.querySelector('[role=dialog]') === null", "Photo Studio close after apply")
            val releaseDeadline = SystemClock.uptimeMillis() + 5_000
            while (isSecure(scenario) && SystemClock.uptimeMillis() < releaseDeadline) Thread.sleep(100)
            assertFalse("Native screenshot protection must be released after Photo Studio closes.", isSecure(scenario))
        } finally {
            scenario.close()
        }
    }

    @Test
    fun shopOwnerCanEditAnExistingPhotoJobInsideAndroidWebViewAndSaveReplacementPdf() {
        val scenario = openFixture("?mode=shop&theme=dark")
        try {
            tapCssSelector(scenario, "#open-photo-studio")
            waitFor(scenario, "document.querySelector('[role=dialog]') !== null", "shop Photo Studio dialog")
            waitFor(
                scenario,
                "document.querySelector('[role=dialog]')?.getAttribute('aria-busy') === 'false'",
                "shop-owned source photo to decode"
            )
            assertTrue(
                "Shop mode must identify this as editing the existing job.",
                evaluate(scenario, "document.querySelector('[role=dialog]')?.innerText.includes('SHOP · EDIT EXISTING JOB')") == "true"
            )
            assertTrue(
                "Shop mode must explain that the prepared PDF replaces the unpaid job file.",
                evaluate(scenario, "document.querySelector('[role=dialog]')?.innerText.includes('Save your prepared multi-page A4 photo PDF to this existing unpaid job.')") == "true"
            )
            assertTrue(
                "The shop owner must have the existing-job save action.",
                evaluate(scenario, "Array.from(document.querySelectorAll('[role=dialog] button')).some(button => button.innerText.trim().startsWith('Save sheet to this job'))") == "true"
            )

            val secureDeadline = SystemClock.uptimeMillis() + 5_000
            while (!isSecure(scenario) && SystemClock.uptimeMillis() < secureDeadline) Thread.sleep(100)
            assertTrue("The shop owner's sensitive photo preview must be protected by FLAG_SECURE.", isSecure(scenario))

            setLabelValue(scenario, "Photo size", "35x45")
            setLabelValue(scenario, "Number of photos", "1.5")
            waitFor(
                scenario,
                "Array.from(document.querySelectorAll('[role=dialog] input')).find(input => input.getAttribute('aria-label') === 'Number of photos')?.value === '1.5' && document.querySelector('[role=dialog]')?.innerText.includes('Enter a whole number from 1 to 100')",
                "fractional shop photo quantity to stay visible with validation"
            )
            setLabelValue(scenario, "Number of photos", "14")
            setLabelValue(scenario, "Photos per A4 page", "9")
            waitFor(
                scenario,
                "Array.from(document.querySelectorAll('[class*=sheetSummary] > div')).some(row => row.textContent.includes('A4 pages in this job') && row.querySelector('strong')?.textContent === '2')",
                "two-page replacement sheet for the shop-owned job"
            )

            tapButtonText(scenario, "Save sheet to this job")
            waitFor(
                scenario,
                "document.querySelector('#photo-studio-result')?.textContent.includes('application/pdf')",
                "replacement PDF to be prepared for the existing shop job"
            )
            val resultText = evaluate(
                scenario,
                "JSON.stringify(JSON.parse(document.querySelector('#photo-studio-result').textContent))"
            )
            val result = JSONObject(decodeJavascriptString(resultText))
            assertEquals("shop", result.getString("mode"))
            assertEquals("application/pdf", result.getString("mediaType"))
            assertEquals("35x45", result.getString("sizeCode"))
            assertEquals(14, result.getInt("photoCount"))
            assertEquals(9, result.getInt("photosPerPage"))
            assertEquals(2, result.getInt("pageCount"))
            assertEquals(2, result.getInt("mediaBoxCount"))
            assertEquals(2, result.getInt("previewPageCount"))
            assertTrue(result.getLong("bytes") in 1L..(20L * 1024L * 1024L))

            waitFor(scenario, "document.querySelector('[role=dialog]') === null", "shop Photo Studio close after save")
            assertTrue("Screenshot protection must be released after the shop owner saves the sheet.", !isSecure(scenario))
            assertEquals(
                "The original uploaded photo must remain available for a safe follow-up edit.",
                "true",
                evaluate(scenario, "document.querySelector('#open-photo-studio')?.disabled === false")
            )
            tapCssSelector(scenario, "#open-photo-studio")
            waitFor(scenario, "document.querySelector('[role=dialog]') !== null", "reopening the unchanged shop source photo")
            waitFor(
                scenario,
                "document.querySelector('[role=dialog] canvas') !== null && document.querySelector('[role=dialog]')?.getAttribute('aria-busy') === 'false'",
                "unchanged shop source photo to remain editable"
            )
        } finally {
            scenario.close()
        }
    }

    @Test
    fun androidSystemBackClosesPhotoStudioBeforeLeavingTheCustomerStation() {
        val scenario = openFixture("?mode=customer&theme=dark")
        try {
            tapCssSelector(scenario, "#open-photo-studio")
            waitFor(scenario, "document.querySelector('[role=dialog]') !== null", "Photo Studio dialog")
            val originalPath = decodeJavascriptString(evaluate(scenario, "window.location.pathname"))
            val secureDeadline = SystemClock.uptimeMillis() + 5_000
            while (!isSecure(scenario) && SystemClock.uptimeMillis() < secureDeadline) Thread.sleep(100)
            assertTrue(isSecure(scenario))

            scenario.onActivity { activity -> activity.onBackPressedDispatcher.onBackPressed() }
            waitFor(scenario, "document.querySelector('[role=dialog]') === null", "system Back to close Photo Studio")
            assertEquals(originalPath, decodeJavascriptString(evaluate(scenario, "window.location.pathname")))
            val releaseDeadline = SystemClock.uptimeMillis() + 5_000
            while (isSecure(scenario) && SystemClock.uptimeMillis() < releaseDeadline) Thread.sleep(100)
            assertFalse(isSecure(scenario))
        } finally {
            scenario.close()
        }
    }

    @Test
    fun photoStudioModalStateAndSecureFlagSurviveActivityRecreation() {
        val scenario = openFixture("?mode=customer&theme=dark")
        try {
            tapCssSelector(scenario, "#open-photo-studio")
            waitFor(scenario, "document.querySelector('[role=dialog]') !== null", "Photo Studio dialog")
            setLabelValue(scenario, "Number of photos", "17")
            waitFor(
                scenario,
                "Array.from(document.querySelectorAll('[class*=sheetSummary] > div')).some(row => row.textContent.includes('A4 pages in this job') && row.querySelector('strong')?.textContent === '1')",
                "photo count before Activity recreation"
            )
            val secureDeadline = SystemClock.uptimeMillis() + 5_000
            while (!isSecure(scenario) && SystemClock.uptimeMillis() < secureDeadline) Thread.sleep(100)
            assertTrue(isSecure(scenario))

            scenario.recreate()
            waitFor(scenario, "document.querySelector('[role=dialog]') !== null", "Photo Studio after Activity recreation")
            assertTrue("The modal's screenshot protection must be restored with the Activity.", isSecure(scenario))
            assertEquals(
                "true",
                evaluate(
                    scenario,
                    "Array.from(document.querySelectorAll('[role=dialog] label')).find(label => label.textContent.trim().startsWith('Number of photos'))?.querySelector('input')?.value === '17'"
                )
            )
        } finally {
            scenario.close()
        }
    }
}
