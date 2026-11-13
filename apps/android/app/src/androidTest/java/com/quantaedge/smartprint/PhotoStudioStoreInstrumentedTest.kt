package com.quantaedge.smartprint

import android.graphics.Bitmap
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import androidx.test.core.app.ApplicationProvider
import android.content.Context
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class PhotoStudioStoreInstrumentedTest {
    @Test
    fun preparedPhotoSheetAndItsOriginalRemainEncryptedAcrossRenameAndEdit() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val isolatedFiles = File(context.cacheDir, "photo-store-test-" + UUID.randomUUID()).apply { mkdirs() }
        val isolatedCache = File(isolatedFiles, "cache").apply { mkdirs() }
        val store = PhotoStudioStore(isolatedFiles, isolatedCache)
        val sourceBytes = testJpeg()
        val pdfBytes = testPdf()

        val metadata = JSONObject()
            .put("title", "Passport photo")
            .put("sourceName", "passport.jpg")
            .put("sourceMime", "image/jpeg")
            .put("sizeCode", "35x45")
            .put("photoCount", 13)
            .put("photosPerPage", 10)
            .put("pageCount", 2)
            .put("resolutionDpi", 300)
            .put("editorSettings", JSONObject().put("zoom", 1.2).put("focusX", 40))

        val prepared = store.savePreparedOutput(pdfBytes, sourceBytes, metadata)
        val id = prepared.name.removePrefix(PhotoStudioStore.FILE_PREFIX).removeSuffix(".spph")
        val encryptedPdf = prepared.readBytes()
        val encryptedSource = File(store.root, "$id.source").readBytes()
        val encryptedMetadata = File(store.root, "$id.meta").readBytes()

        assertTrue("Prepared PDFs use the Photo Studio vault format.",
            encryptedPdf.take(4).toByteArray().contentEquals(byteArrayOf(83, 80, 80, 72)))
        assertTrue("Original photos use the Photo Studio vault format.",
            encryptedSource.take(4).toByteArray().contentEquals(byteArrayOf(83, 80, 80, 72)))
        assertTrue("Metadata is encrypted separately.",
            encryptedMetadata.take(4).toByteArray().contentEquals(byteArrayOf(83, 80, 80, 72)))
        assertEquals(1, store.listPrepared().size)
        assertEquals(13, store.readMetadata(prepared).optInt("photoCount"))
        assertEquals(2, store.readMetadata(prepared).optInt("pageCount"))
        assertEquals(300, store.readMetadata(prepared).optInt("resolutionDpi"))
        assertTrue("The original source photo survives app recreation.",
            PhotoStudioStore(isolatedFiles, isolatedCache).readOriginalPhoto(prepared).contentEquals(sourceBytes))

        val renamed = store.renamePrepared(prepared, "New passport photo")
        assertEquals("New passport photo", renamed.optString("title"))
        val createdUtc = renamed.optLong("createdUtc")
        val transientPdf = store.materializePdf(prepared)
        assertTrue("Only the short-lived print copy contains a readable PDF header.",
            transientPdf.inputStream().use { input ->
                val header = ByteArray(5)
                input.read(header) == 5 && header.contentEquals("%PDF-".toByteArray(Charsets.US_ASCII))
            })
        val thumb = store.createThumbnailBase64(prepared)
        assertTrue("A page thumbnail can be generated from the private PDF.", !thumb.isNullOrBlank())
        store.releaseTransientFile(transientPdf)

        val editedMetadata = JSONObject()
            .put("title", "This replacement title must not overwrite the user title")
            .put("sourceName", "passport-edited.jpg")
            .put("sourceMime", "image/jpeg")
            .put("sizeCode", "25x35")
            .put("photoCount", 24)
            .put("photosPerPage", 20)
            .put("pageCount", 2)
            .put("resolutionDpi", 300)
            .put("editorSettings", JSONObject().put("zoom", 1.5).put("focusX", 55))
        store.updatePreparedOutput(id, testPdf(), sourceBytes, editedMetadata)

        val afterEdit = store.readMetadata(prepared)
        assertEquals("Editing updates the same private document.", id, afterEdit.optString("id"))
        assertEquals("Rename is retained through editing.", "New passport photo", afterEdit.optString("title"))
        assertEquals(createdUtc, afterEdit.optLong("createdUtc"))
        assertEquals("25x35", afterEdit.optString("sizeCode"))
        assertEquals(24, afterEdit.optInt("photoCount"))
        assertEquals(20, afterEdit.optInt("photosPerPage"))
        assertEquals(2, afterEdit.optInt("pageCount"))
        assertEquals(300, afterEdit.optInt("resolutionDpi"))
        assertEquals(1.5, afterEdit.getJSONObject("editorSettings").getDouble("zoom"), 0.001)

        val reloaded = PhotoStudioStore(isolatedFiles, isolatedCache)
        assertTrue(reloaded.findById(id)?.let(reloaded::isPreparedDocument) == true)
        assertEquals(24, reloaded.readMetadata(prepared).optInt("photoCount"))
        reloaded.deletePrepared(prepared)
        assertTrue("Deleting a private photo sheet also removes its encrypted source and metadata.",
            !File(store.root, "$id.source").exists() && !File(store.root, "$id.meta").exists())
        assertFalse(prepared.exists())
        isolatedFiles.deleteRecursively()
    }

    @Test
    fun privatePhotoMetadataDropsTransferPayloadsAndRepairsPreviouslySavedDocuments() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val isolatedFiles = File(context.cacheDir, "photo-store-metadata-" + UUID.randomUUID()).apply { mkdirs() }
        val isolatedCache = File(isolatedFiles, "cache").apply { mkdirs() }
        val store = PhotoStudioStore(isolatedFiles, isolatedCache)
        val supplied = JSONObject()
            .put("title", "Large private photo")
            .put("sourceName", "large-photo.jpg")
            .put("sourceMime", "image/jpeg")
            .put("sizeCode", "35x45")
            .put("photoCount", 8)
            .put("photosPerPage", 8)
            .put("pageCount", 1)
            .put("resolutionDpi", 300)
            .put("sourceBase64", "S".repeat(96 * 1024))
            .put("pdfBase64", "P".repeat(96 * 1024))

        val prepared = store.savePreparedOutput(testPdf(), testJpeg(), supplied)
        val id = prepared.name.removePrefix(PhotoStudioStore.FILE_PREFIX).removeSuffix(".spph")
        val metadataFile = File(store.root, "$id.meta")
        val savedMetadata = store.readMetadata(prepared)
        assertFalse(savedMetadata.has("sourceBase64"))
        assertFalse(savedMetadata.has("pdfBase64"))
        assertTrue("Persisted metadata must stay small even when transfer payloads are large.",
            metadataFile.length() < 64L * 1024L)

        // Recreate the metadata format written by the old implementation, then
        // verify that opening the document repairs it transparently.
        val legacyMetadata = JSONObject(savedMetadata.toString())
            .put("sourceBase64", "S".repeat(96 * 1024))
            .put("pdfBase64", "P".repeat(96 * 1024))
        val writer = PhotoStudioStore::class.java.getDeclaredMethod(
            "writeEncryptedAtomically", File::class.java, ByteArray::class.java
        ).apply { isAccessible = true }
        writer.invoke(store, metadataFile, legacyMetadata.toString().toByteArray(Charsets.UTF_8))

        val repaired = store.readMetadata(prepared)
        assertEquals("Large private photo", repaired.optString("title"))
        assertFalse(repaired.has("sourceBase64"))
        assertFalse(repaired.has("pdfBase64"))
        assertTrue("Legacy transfer data must be removed after migration.",
            metadataFile.length() < 64L * 1024L)
        assertTrue("The saved PDF remains previewable after metadata repair.",
            !store.createThumbnailBase64(prepared).isNullOrBlank())

        store.deletePrepared(prepared)
        isolatedFiles.deleteRecursively()
    }

    @Test
    fun photoQuantityUsesTheGeneratedMaximumAtTheStorageBoundary() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val isolatedFiles = File(context.cacheDir, "photo-store-count-" + UUID.randomUUID()).apply { mkdirs() }
        val isolatedCache = File(isolatedFiles, "cache").apply { mkdirs() }
        val store = PhotoStudioStore(isolatedFiles, isolatedCache)
        val maximum = UploadLimits.MAX_PHOTO_COUNT

        fun metadata(photoCount: Int) = JSONObject()
            .put("title", "Boundary test")
            .put("sizeCode", "35x45")
            .put("photoCount", photoCount)
            .put("photosPerPage", 25)
            .put("pageCount", (photoCount + 24) / 25)
            .put("resolutionDpi", 300)

        val accepted = store.savePreparedOutput(testPdf(), testJpeg(), metadata(maximum))
        assertEquals(maximum, store.readMetadata(accepted).optInt("photoCount"))

        var rejected = false
        try {
            store.savePreparedOutput(testPdf(), testJpeg(), metadata(maximum + 1))
        } catch (_: IllegalArgumentException) {
            rejected = true
        }
        assertTrue("A quantity above the shared photo-count limit must be rejected.", rejected)
        assertEquals("The rejected sheet must not be persisted.", 1, store.listPrepared().size)

        store.deletePrepared(accepted)
        isolatedFiles.deleteRecursively()
    }

    @Test
    fun privatePhotoMetadataRejectsInvalidQuantityAndLayout() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val isolatedFiles = File(context.cacheDir, "photo-store-invalid-" + UUID.randomUUID()).apply { mkdirs() }
        val isolatedCache = File(isolatedFiles, "cache").apply { mkdirs() }
        val store = PhotoStudioStore(isolatedFiles, isolatedCache)
        val invalid = JSONObject()
            .put("title", "Invalid")
            .put("sizeCode", "35x45")
            .put("photoCount", 26)
            .put("photosPerPage", 26)
            .put("pageCount", 1)
            .put("resolutionDpi", 300)
        var rejected = false
        try {
            store.savePreparedOutput(testPdf(), testJpeg(), invalid)
        } catch (_: IllegalArgumentException) {
            rejected = true
        }
        assertTrue("A layout cannot exceed the selected photo size's maximum per A4 sheet.", rejected)
        assertTrue("Rejected data is not persisted.", store.listPrepared().isEmpty())
        isolatedFiles.deleteRecursively()
    }

    private fun testPdf(): ByteArray {
        val image = testJpeg()
        return IdCardPdfComposer.compose(image, image)
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
                "PHOTO",
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
