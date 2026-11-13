package com.quantaedge.smartprint

import android.content.Context
import androidx.core.content.FileProvider
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.json.JSONObject
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class PrivateStudioFileProviderInstrumentedTest {
    @Test
    fun photoStudioTransientPdfCanBeOpenedThroughTheDocumentsProvider() {
        assertTransientFileCanBeOpened("photo-studio-transient", "photo-studio-provider-test.pdf")
    }

    @Test
    fun idStudioTransientPdfRemainsAvailableThroughTheDocumentsProvider() {
        assertTransientFileCanBeOpened("id-studio-transient", "id-studio-provider-test.pdf")
    }


    @Test
    fun mixedPrivateEightFileBatchMaterializesFiveEncryptedStudioSheetsForUpload() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val isolatedFiles = File(context.cacheDir, "private-eight-file-batch-" + java.util.UUID.randomUUID()).apply { mkdirs() }
        val isolatedCache = File(isolatedFiles, "cache").apply { mkdirs() }
        val idStore = IdStudioStore(isolatedFiles, isolatedCache)
        val photoStore = PhotoStudioStore(isolatedFiles, isolatedCache)
        val sourceOne = testJpeg(android.graphics.Color.rgb(90, 120, 170))
        val sourceTwo = testJpeg(android.graphics.Color.rgb(170, 120, 90))
        val preparedPdf = createPdf(pageCount = 2)
        val ordinaryPdf = File(isolatedCache, "customer-multipage.pdf").apply { writeBytes(createPdf(pageCount = 4)) }
        val ordinaryPhotoOne = File(isolatedCache, "upload-photo-one.jpg").apply { writeBytes(sourceOne) }
        val ordinaryPhotoTwo = File(isolatedCache, "upload-photo-two.jpg").apply { writeBytes(sourceTwo) }
        val transientIds = mutableListOf<File>()
        val transientPhotos = mutableListOf<File>()

        try {
            repeat(2) { index ->
                val idPdf = IdCardPdfComposer.compose(sourceOne, sourceTwo)
                idStore.savePreparedOutput(
                    idPdf,
                    sourceOne,
                    sourceTwo,
                    JSONObject().put("title", "Prepared ID " + (index + 1))
                )
                idPdf.fill(0)
            }
            repeat(3) { index ->
                val pdfBytes = preparedPdf.copyOf()
                photoStore.savePreparedOutput(
                    pdfBytes,
                    sourceOne,
                    JSONObject()
                        .put("title", "Saved Photo Studio " + (index + 1))
                        .put("sourceName", "photo-" + (index + 1) + ".jpg")
                        .put("sourceMime", "image/jpeg")
                        .put("sizeCode", "35x45")
                        .put("photoCount", 18)
                        .put("photosPerPage", 9)
                        .put("pageCount", 2)
                        .put("resolutionDpi", 300)
                )
                pdfBytes.fill(0)
            }

            val preparedIds = idStore.listPrepared()
            val preparedPhotos = photoStore.listPrepared()
            assertEquals(2, preparedIds.size)
            assertEquals(3, preparedPhotos.size)
            assertTrue(preparedIds.all { it.extension == "spid" && !DocumentNamePolicy.isPdf(it) })
            assertTrue(preparedPhotos.all { it.extension == "spph" && !DocumentNamePolicy.isPdf(it) })

            val encryptedSelection = preparedIds + preparedPhotos + listOf(
                ordinaryPdf,
                ordinaryPhotoOne,
                ordinaryPhotoTwo
            )
            assertEquals(8, encryptedSelection.size)
            assertNull(
                PrintBatchSelectionPolicy.pickerValidationError(
                    encryptedSelection,
                    allowMultiple = true
                ) { file ->
                    idStore.isPreparedDocument(file) || photoStore.isPreparedDocument(file)
                }
            )

            preparedIds.forEach { transientIds += idStore.materializePreparedFile(it) }
            preparedPhotos.forEach { transientPhotos += photoStore.materializePdf(it) }
            val uploadSelection = transientIds + transientPhotos + listOf(
                ordinaryPdf,
                ordinaryPhotoOne,
                ordinaryPhotoTwo
            )

            assertEquals(8, uploadSelection.size)
            assertNull(PrintBatchSelectionPolicy.validationError(uploadSelection))
            assertTrue(uploadSelection.all(DocumentNamePolicy::isPrintable))
            assertEquals(6, uploadSelection.count(DocumentNamePolicy::isPdf))
            assertEquals(2, uploadSelection.count { DocumentNamePolicy.mimeType(it) == "image/jpeg" })
            assertEquals(2, transientIds.count { pdfPageCount(it) == 1 })
            assertEquals(3, transientPhotos.count { pdfPageCount(it) == 2 })
            assertEquals(4, pdfPageCount(ordinaryPdf))

            assertTrue("The encrypted ID vault entries must remain until upload completion.",
                preparedIds.all { it.isFile && !DocumentNamePolicy.isPdf(it) })
            assertTrue("The encrypted Photo Studio vault entries must remain until upload completion.",
                preparedPhotos.all { it.isFile && !DocumentNamePolicy.isPdf(it) })
            assertTrue(transientIds.all { it.parentFile?.name == "id-studio-transient" })
            assertTrue(transientPhotos.all { it.parentFile?.name == "photo-studio-transient" })
        } finally {
            transientIds.forEach(idStore::releaseTransientFile)
            transientPhotos.forEach(photoStore::releaseTransientFile)
            sourceOne.fill(0)
            sourceTwo.fill(0)
            preparedPdf.fill(0)
            isolatedFiles.deleteRecursively()
        }
    }

    private fun createPdf(pageCount: Int): ByteArray {
        val document = android.graphics.pdf.PdfDocument()
        try {
            repeat(pageCount) { index ->
                val page = document.startPage(
                    android.graphics.pdf.PdfDocument.PageInfo.Builder(595, 842, index + 1).create()
                )
                page.canvas.drawColor(android.graphics.Color.WHITE)
                page.canvas.drawText(
                    "Private batch page " + (index + 1),
                    48f,
                    72f,
                    android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
                        color = android.graphics.Color.BLACK
                        textSize = 24f
                    }
                )
                document.finishPage(page)
            }
            return java.io.ByteArrayOutputStream().use { output ->
                document.writeTo(output)
                output.toByteArray()
            }
        } finally {
            document.close()
        }
    }

    private fun testJpeg(color: Int): ByteArray {
        val bitmap = android.graphics.Bitmap.createBitmap(1000, 630, android.graphics.Bitmap.Config.ARGB_8888)
        return try {
            bitmap.eraseColor(color)
            java.io.ByteArrayOutputStream().use { output ->
                check(bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG, 94, output))
                output.toByteArray()
            }
        } finally {
            bitmap.recycle()
        }
    }

    private fun pdfPageCount(file: File): Int =
        android.os.ParcelFileDescriptor.open(file, android.os.ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            android.graphics.pdf.PdfRenderer(descriptor).use { renderer -> renderer.pageCount }
        }

    private fun assertTransientFileCanBeOpened(directoryName: String, fileName: String) {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val directory = File(context.cacheDir, directoryName).apply { mkdirs() }
        val file = File(directory, fileName)
        try {
            file.writeBytes("%PDF-1.4\nSmartPrint provider test".toByteArray(Charsets.US_ASCII))
            val uri = FileProvider.getUriForFile(
                context,
                "${context.packageName}.documents",
                file
            )

            assertEquals("content", uri.scheme)
            context.contentResolver.openInputStream(uri).use { input ->
                assertNotNull("The provider must resolve its configured transient cache root.", input)
                val header = ByteArray(5)
                assertEquals(5, input!!.read(header))
                assertEquals("%PDF-".toByteArray(Charsets.US_ASCII).toList(), header.toList())
            }
        } finally {
            file.delete()
        }
    }
}
