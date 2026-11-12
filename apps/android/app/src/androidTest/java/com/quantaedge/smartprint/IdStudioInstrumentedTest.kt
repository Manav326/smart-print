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
import org.json.JSONObject
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
        val frontBytes = idStore.readScanBytes(draftId, "front")
        val backBytes = idStore.readScanBytes(draftId, "back")
        val pdfBytes = IdCardPdfComposer.compose(frontBytes, backBytes)
        val prepared = idStore.savePreparedOutput(
            pdfBytes,
            frontBytes,
            backBytes,
            JSONObject().put("title", "School ID")
                .put("cardWidthMm", IdCardLayoutPolicy.DEFAULT_CARD_WIDTH_MM.toDouble())
                .put("cardHeightMm", IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM.toDouble())
                .put("frontRotation", 0)
                .put("backRotation", 0)
        )

        try {
            val encryptedPreparedBytes = prepared.readBytes()
            assertTrue("Prepared PDFs are AES-GCM vault envelopes.", encryptedPreparedBytes.take(4).toByteArray().contentEquals(byteArrayOf(83, 80, 73, 68)))
            assertTrue("New ID documents retain both encrypted original scans.", idStore.canEditPreparedDocument(prepared))
            assertTrue("ID library entries can render an A4 thumbnail for print selection.", idStore.createThumbnailBase64(prepared)?.isNotBlank() == true)
            val a4BrandPreview = IdCardPdfComposer.renderSheetPreview(
                frontBytes, backBytes, 0, 0,
                previewWidthPixels = 900,
                brandMark = context.getDrawable(R.drawable.smartprint_mark_watermark)
            )
            try {
                assertTrue("A4 ID branding is rendered on the sheet preview without changing the saved print PDF.",
                    a4BrandPreview.width == 900 && a4BrandPreview.height > a4BrandPreview.width)
            } finally {
                a4BrandPreview.recycle()
            }
            val cardFaceThumbnail = android.util.Base64.decode(
                idStore.createCardFaceThumbnailBase64(prepared, "front") ?: error("The ID front-face preview could not be rendered."),
                android.util.Base64.DEFAULT
            )
            try {
                val thumbnailBounds = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
                android.graphics.BitmapFactory.decodeByteArray(cardFaceThumbnail, 0, cardFaceThumbnail.size, thumbnailBounds)
                assertTrue("The ID Studio library thumbnail displays the physical card face in landscape.",
                    thumbnailBounds.outWidth > thumbnailBounds.outHeight)
                assertTrue("The ID card face thumbnail retains useful display detail.", thumbnailBounds.outWidth >= 500)
            } finally {
                cardFaceThumbnail.fill(0)
            }
            assertTrue("The title is saved independently of the opaque file name.", idStore.readMetadata(prepared).optString("title") == "School ID")
            idStore.renamePrepared(prepared, "Student ID")
            assertTrue("Renaming persists in encrypted metadata.", idStore.readMetadata(prepared).optString("title") == "Student ID")
            val editableFront = idStore.readPreparedSideBytes(prepared, "front")
            val editableBack = idStore.readPreparedSideBytes(prepared, "back")
            try {
                assertTrue("The original front scan can be reopened.", editableFront.contentEquals(frontBytes))
                assertTrue("The original back scan can be reopened.", editableBack.contentEquals(backBytes))
                val editedPdf = IdCardPdfComposer.compose(editableFront, editableBack)
                idStore.updatePreparedOutput(
                    prepared,
                    editedPdf,
                    editableFront,
                    editableBack,
                    JSONObject().put("title", "Replacement title")
                        .put("cardWidthMm", IdCardLayoutPolicy.DEFAULT_CARD_WIDTH_MM.toDouble())
                        .put("cardHeightMm", IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM.toDouble())
                        .put("frontRotation", 0)
                        .put("backRotation", 0)
                )
            } finally {
                editableFront.fill(0)
                editableBack.fill(0)
            }
            assertTrue("An edit updates the existing document without replacing its title.", idStore.readMetadata(prepared).optString("title") == "Student ID")
            val transientPdf = idStore.materializePreparedFile(prepared)
            assertTrue("Only the short-lived viewer copy contains a PDF header.",
                transientPdf.inputStream().use { input ->
                    val header = ByteArray(5)
                    input.read(header) == 5 && header.contentEquals("%PDF-".toByteArray(Charsets.US_ASCII))
                })
            val reopenedStore = IdStudioStore(context.filesDir, context.cacheDir)
            assertTrue("Original scans remain editable after store recreation.", reopenedStore.canEditPreparedDocument(prepared))
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
                onView(withText("Student ID")).check(matches(isDisplayed()))
                onView(withText("View")).perform(click())
                onView(withContentDescription("ID front card preview. Pinch to zoom.")).check(matches(isDisplayed()))
                onView(withText("View mode")).check(matches(isDisplayed()))
                onView(withText("Prepare A4 print sheet")).check(doesNotExist())

                scenario.onActivity { activity ->
                    assertTrue(
                        "Only the explicitly watermarked ID preview allows capture.",
                        activity.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0
                    )
                }

                onView(withContentDescription("Back to ID Studio")).perform(click())
                onView(withText("Student ID")).check(matches(isDisplayed()))
                scenario.onActivity { activity ->
                    assertTrue(
                        "ID Studio library remains protected after leaving the preview.",
                        activity.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE != 0
                    )
                }
            }
        } finally {
            idStore.deletePrepared(prepared)
            if (idStore.draftExists(draftId)) idStore.deleteDraft(draftId)
        }
    }

    @Test
    fun idReviewDefaultsToPhysicalCardAndOffersA4SheetView() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        context.getSharedPreferences("smartprint.customer.last-shop", Context.MODE_PRIVATE)
            .edit().clear().commit()
        val store = IdStudioStore(context.filesDir, context.cacheDir)
        val draftId = store.createDraft()
        store.saveScan(draftId, "front", ByteArrayInputStream(testJpeg()))
        store.saveScan(draftId, "back", ByteArrayInputStream(testJpeg()))

        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    MainActivity::class.java.getDeclaredField("idStudioDraftId").apply {
                        isAccessible = true
                    }.set(activity, draftId)
                    MainActivity::class.java.getDeclaredMethod("showIdStudioReview").apply {
                        isAccessible = true
                    }.invoke(activity)
                }
                onView(withContentDescription("ID front card preview. Pinch to zoom."))
                    .check(matches(isDisplayed()))
                onView(withText("Front")).check(matches(isDisplayed()))
                onView(withText("Back")).check(matches(isDisplayed()))
                onView(withText("Save")).check(matches(isDisplayed()))
                onView(withText("A4 sheet")).perform(click())
                onView(withContentDescription("A4 ID front and back print sheet preview. Pinch to zoom."))
                    .check(matches(isDisplayed()))
                onView(withText("Save")).check(matches(isDisplayed()))
                onView(withText("ID card")).perform(click())
                onView(withContentDescription("ID front card preview. Pinch to zoom."))
                    .check(matches(isDisplayed()))
            }
        } finally {
            store.deleteDraft(draftId)
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
