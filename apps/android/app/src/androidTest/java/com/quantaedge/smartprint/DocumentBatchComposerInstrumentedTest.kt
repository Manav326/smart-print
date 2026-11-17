package com.quantaedge.smartprint

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import com.tom_roush.pdfbox.android.PDFBoxResourceLoader
import com.tom_roush.pdfbox.pdmodel.PDDocument
import com.tom_roush.pdfbox.pdmodel.PDPage
import com.tom_roush.pdfbox.pdmodel.PDPageContentStream
import com.tom_roush.pdfbox.pdmodel.common.PDRectangle
import com.tom_roush.pdfbox.pdmodel.font.PDType1Font
import com.tom_roush.pdfbox.text.PDFTextStripper
import java.io.File
import java.io.FileOutputStream
import java.io.RandomAccessFile

class DocumentBatchComposerInstrumentedTest {
    private val context: Context
        get() = ApplicationProvider.getApplicationContext()

    @Test
    fun combinesMultiplePdfsAndImagesIntoOneOrderedPrintPdf() {
        val sourcePdf = File(context.cacheDir, "batch-source-without-extension")
        val image = File(context.cacheDir, "batch-photo.png")
        val output = File(context.cacheDir, "batch-output.pdf")
        try {
            createPdf(sourcePdf, pages = 2)
            val bitmap = Bitmap.createBitmap(480, 640, Bitmap.Config.ARGB_8888)
            bitmap.eraseColor(Color.LTGRAY)
            FileOutputStream(image).use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
            bitmap.recycle()

            assertEquals(3, DocumentBatchComposer.compose(context, listOf(sourcePdf, image), output))
            assertPdfPageCount(output, 3)
            assertPdfPageCount(sourcePdf, 2)
            PDFBoxResourceLoader.init(context)
            PDDocument.load(output).use { merged ->
                val text = PDFTextStripper().getText(merged)
                assertTrue("Vector PDF text must remain selectable after combining.", text.contains("Source page 1"))
                assertTrue("The second source page must remain vector text.", text.contains("Source page 2"))
            }
            assertTrue(image.isFile)
        } finally {
            sourcePdf.delete()
            image.delete()
            output.delete()
        }
    }

    @Test
    fun combinesValidSourcesWhoseTotalIsExactly50MiB() {
        val first = File(context.cacheDir, "batch-limit-first.pdf")
        val second = File(context.cacheDir, "batch-limit-second.pdf")
        val output = File(context.cacheDir, "batch-limit-output.pdf")
        val perFileLimit = 25L * 1024L * 1024L
        try {
            createPdf(first, pages = 1)
            createPdf(second, pages = 1)
            padPdfWithWhitespace(first, perFileLimit)
            padPdfWithWhitespace(second, perFileLimit)

            assertEquals(perFileLimit * 2L, first.length() + second.length())
            assertEquals(2, DocumentBatchComposer.compose(context, listOf(first, second), output))
            assertPdfPageCount(output, 2)
            assertTrue("The merged result must remain below the 50 MB upload limit.", output.length() in 1..(50L * 1024L * 1024L))
        } finally {
            first.delete()
            second.delete()
            output.delete()
        }
    }

    private fun padPdfWithWhitespace(file: File, targetBytes: Long) {
        RandomAccessFile(file, "rw").use { random ->
            val originalBytes = random.length()
            require(originalBytes < targetBytes)
            random.setLength(targetBytes)
            random.seek(originalBytes)
            val padding = ByteArray(64 * 1024) { ' '.code.toByte() }
            var remaining = targetBytes - originalBytes
            while (remaining > 0) {
                val count = minOf(remaining, padding.size.toLong()).toInt()
                random.write(padding, 0, count)
                remaining -= count
            }
        }
    }

    @Test
    fun rejectsCombinedSourcesOver50MiBBeforeStartingPdfWork() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val first = File(context.cacheDir, "batch-over-limit-first.pdf")
        val second = File(context.cacheDir, "batch-over-limit-second.pdf")
        val output = File(context.cacheDir, "batch-over-limit-output.pdf")
        try {
            first.writeBytes("%PDF-1.4\\n".toByteArray())
            second.writeBytes("%PDF-1.4\\n".toByteArray())
            RandomAccessFile(first, "rw").use { it.setLength(25L * 1024L * 1024L) }
            RandomAccessFile(second, "rw").use { it.setLength(25L * 1024L * 1024L + 1L) }

            val error = runCatching {
                DocumentBatchComposer.compose(context, listOf(first, second), output)
            }.exceptionOrNull()

            assertTrue("The 50 MiB combined input limit must be explicit: ${error?.message}", error is IllegalArgumentException)
            assertTrue(error?.message.orEmpty().contains("combined source limit is 50 MB"))
            assertTrue("An over-limit batch must not leave a partial output.", !output.exists())
        } finally {
            first.delete()
            second.delete()
            output.delete()
        }
    }

    @Test
    fun savesPageRemovalRotationAndReorderingAsNewPdfWithoutChangingSource() {
        val source = File(context.cacheDir, "edit-source.pdf")
        val edited = File(context.cacheDir, "edit-result.pdf")
        try {
            createPdf(source, pages = 3)
            val plan = listOf(
                PdfPageEdit(sourceIndex = 2, rotationDegrees = 90),
                PdfPageEdit(sourceIndex = 0, rotationDegrees = 0)
            )
            assertEquals(2, DocumentBatchComposer.editPdf(source, plan, edited))
            assertPdfPageCount(edited, 2)
            assertPdfPageCount(source, 3)

            val preview = DocumentBatchComposer.renderPagePreview(
                source,
                PdfPageEdit(sourceIndex = 0, rotationDegrees = 90)
            )
            try {
                assertTrue(preview.width > preview.height)
            } finally {
                preview.recycle()
            }
        } finally {
            source.delete()
            edited.delete()
        }
    }

    @Test
    fun rotatesPdfLargerThan50MiBWithoutRasterizingItsContent() {
        val source = File(context.cacheDir, "rotate-over-50mb-source.pdf")
        val output = File(context.cacheDir, "rotate-over-50mb-output.pdf")
        try {
            createPdf(source, pages = 2)
            val batchLimit = 50L * 1024L * 1024L
            padPdfWithWhitespace(source, batchLimit + 1024L * 1024L)
            assertTrue("The rotation fixture must exceed the print-batch limit.", source.length() > batchLimit)
            PDDocument.load(source, com.tom_roush.pdfbox.io.MemoryUsageSetting.setupTempFileOnly()).use { sourcePdf ->
                assertTrue(
                    "The source fixture must contain selectable PDF text before rotation.",
                    PDFTextStripper().getText(sourcePdf).contains("Source page 1")
                )
                assertTrue(
                    "The second source page must contain selectable PDF text before rotation.",
                    PDFTextStripper().getText(sourcePdf).contains("Source page 2")
                )
            }

            assertEquals(
                2,
                DocumentBatchComposer.rotatePages(
                    context,
                    source,
                    mapOf(0 to 90, 1 to 270),
                    output
                )
            )
            assertTrue("Rotation must create a separate PDF output.", output.isFile)
            assertTrue("Rotation output must be a valid PDF.", DocumentNamePolicy.isPdf(output))

            PDFBoxResourceLoader.init(context)
            PDDocument.load(output, com.tom_roush.pdfbox.io.MemoryUsageSetting.setupTempFileOnly()).use { document ->
                assertEquals(2, document.numberOfPages)
                assertEquals(90, document.getPage(0).rotation)
                assertEquals(270, document.getPage(1).rotation)
                val text = PDFTextStripper().getText(document)
                assertTrue("Page one text must remain selectable after rotation.", text.contains("Source page 1"))
                assertTrue("Page two text must remain selectable after rotation.", text.contains("Source page 2"))
            }
            assertTrue("The source remains unchanged.", source.length() > batchLimit)
        } finally {
            source.delete()
            output.delete()
        }
    }

    /**
     * Generate real PDF text operators. Android PdfDocument canvas text may be
     * stored as glyph outlines, which is unsuitable for a selectable-text test.
     */
    private fun createPdf(file: File, pages: Int) {
        PDFBoxResourceLoader.init(context)
        PDDocument().use { document ->
            repeat(pages) { index ->
                val page = PDPage(PDRectangle.A4)
                document.addPage(page)
                PDPageContentStream(document, page).use { content ->
                    content.beginText()
                    content.setFont(PDType1Font.HELVETICA, 28f)
                    content.newLineAtOffset(36f, 760f)
                    content.showText("Source page ${index + 1}")
                    content.endText()
                }
            }
            document.save(file)
        }
    }

    private fun assertPdfPageCount(file: File, expected: Int) {
        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use { renderer -> assertEquals(expected, renderer.pageCount) }
        }
    }
}
