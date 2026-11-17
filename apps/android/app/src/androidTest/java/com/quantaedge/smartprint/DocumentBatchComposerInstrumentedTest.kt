package com.quantaedge.smartprint

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.Paint
import android.graphics.pdf.PdfDocument
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import com.tom_roush.pdfbox.android.PDFBoxResourceLoader
import com.tom_roush.pdfbox.pdmodel.PDDocument
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

    private fun createPdf(file: File, pages: Int) {
        val document = PdfDocument()
        try {
            repeat(pages) { index ->
                val page = document.startPage(
                    PdfDocument.PageInfo.Builder(420, 594, index + 1).create()
                )
                page.canvas.drawColor(if (index % 2 == 0) Color.WHITE else Color.LTGRAY)
                page.canvas.drawText(
                    "Source page ${index + 1}",
                    36f,
                    72f,
                    Paint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 28f; color = Color.BLACK }
                )
                document.finishPage(page)
            }
            FileOutputStream(file).use { document.writeTo(it) }
        } finally {
            document.close()
        }
    }

    private fun assertPdfPageCount(file: File, expected: Int) {
        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use { renderer -> assertEquals(expected, renderer.pageCount) }
        }
    }
}
