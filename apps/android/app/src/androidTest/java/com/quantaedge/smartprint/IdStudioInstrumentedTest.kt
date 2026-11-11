package com.quantaedge.smartprint

import android.content.Context
import android.graphics.Bitmap
import android.view.WindowManager
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File

@RunWith(AndroidJUnit4::class)
class IdStudioInstrumentedTest {
    @Test
    fun preparedIdCanBeCreatedViewedAndKeptPrivateWithoutAConnectedShop() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        context.getSharedPreferences("smartprint.customer.last-shop", Context.MODE_PRIVATE)
            .edit()
            .clear()
            .commit()

        val idStore = IdStudioStore(context.filesDir, context.cacheDir)
        val draftId = idStore.createDraft()
        val front = idStore.saveScan(draftId, "front", ByteArrayInputStream(testJpeg()))
        val back = idStore.saveScan(draftId, "back", ByteArrayInputStream(testJpeg()))
        val frontVaultBytes = File(idStore.draftDirectory(draftId), "front.jpg").readBytes()
        assertTrue("Saved scans are AES-GCM vault envelopes.", frontVaultBytes.take(4).toByteArray().contentEquals(byteArrayOf(83, 80, 73, 68)))
        val pdfBytes = IdCardPdfComposer.compose(
            idStore.readScanBytes(draftId, "front"),
            idStore.readScanBytes(draftId, "back")
        )
        val prepared = idStore.savePreparedOutput(pdfBytes)

        try {
            val encryptedPreparedBytes = prepared.readBytes()
            assertTrue("Prepared PDFs are AES-GCM vault envelopes.", encryptedPreparedBytes.take(4).toByteArray().contentEquals(byteArrayOf(83, 80, 73, 68)))
            val transientPdf = idStore.materializePreparedFile(prepared)
            assertTrue("Only the short-lived viewer copy contains a PDF header.",
                transientPdf.inputStream().use { input ->
                    val header = ByteArray(5)
                    input.read(header) == 5 && header.contentEquals("%PDF-".toByteArray(Charsets.US_ASCII))
                })
            val reopenedStore = IdStudioStore(context.filesDir, context.cacheDir)
            assertTrue("A recent picker file survives Activity recreation.", transientPdf.isFile)
            transientPdf.setLastModified(System.currentTimeMillis() - 60L * 60L * 1000L)
            IdStudioStore(context.filesDir, context.cacheDir)
            assertFalse("Expired plaintext working copies are removed on next launch.", transientPdf.exists())
            reopenedStore.releaseTransientFile(transientPdf)
            idStore.deleteDraft(draftId)

            val documents = LocalDocumentStore(context.filesDir)
            assertFalse(
                "Prepared IDs must not appear in My Files.",
                documents.list(documents.root).any { it.canonicalPath == prepared.canonicalPath }
            )
            assertTrue("The prepared ID remains in app-private ID Studio storage.", idStore.isPreparedDocument(prepared))

            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    val portalUrl = MainActivity::class.java.getDeclaredField("portalUrl")
                        .apply { isAccessible = true }
                        .get(activity)
                    assertNull("This test has no connected shop.", portalUrl)

                    MainActivity::class.java.getDeclaredMethod("showIdStudioHome")
                        .apply { isAccessible = true }
                        .invoke(activity)

                    assertTrue(
                        "ID Studio must protect the screen while private ID material is visible.",
                        activity.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE != 0
                    )
                }

                onView(withText("Create a new ID card")).check(matches(isDisplayed()))
                val displayName = prepared.name.removeSuffix(".spid") + ".pdf"
                onView(withText(displayName)).check(matches(isDisplayed()))
                onView(withText("View")).perform(click())
                onView(withText("Page 1 of 1")).check(matches(isDisplayed()))

                scenario.onActivity { activity ->
                    assertTrue(
                        "The in-app ID viewer must keep screen capture disabled.",
                        activity.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE != 0
                    )
                }

                onView(withContentDescription("Back to ID Studio")).perform(click())
                onView(withText(displayName)).check(matches(isDisplayed()))
            }
        } finally {
            idStore.deletePrepared(prepared)
            if (idStore.draftExists(draftId)) idStore.deleteDraft(draftId)
        }
    }

    private fun testJpeg(): ByteArray {
        val bitmap = Bitmap.createBitmap(1000, 630, Bitmap.Config.ARGB_8888)
        try {
            bitmap.eraseColor(android.graphics.Color.rgb(242, 240, 234))
            val canvas = android.graphics.Canvas(bitmap)
            canvas.drawRect(
                20f, 20f, 980f, 610f,
                android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
                    color = android.graphics.Color.rgb(40, 70, 120)
                    style = android.graphics.Paint.Style.STROKE
                    strokeWidth = 4f
                }
            )
            canvas.drawText(
                "FRONT OR BACK",
                80f,
                180f,
                android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
                    color = android.graphics.Color.BLACK
                    textSize = 42f
                }
            )
            return ByteArrayOutputStream().use { output ->
                check(bitmap.compress(Bitmap.CompressFormat.JPEG, 95, output))
                output.toByteArray()
            }
        } finally {
            bitmap.recycle()
        }
    }
}
