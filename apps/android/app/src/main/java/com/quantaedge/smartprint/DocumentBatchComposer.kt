package com.quantaedge.smartprint

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.pdf.PdfDocument
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import java.io.File
import java.io.FileOutputStream

data class PdfPageEdit(val sourceIndex: Int, val rotationDegrees: Int = 0)

object DocumentBatchComposer {
    const val MAX_SOURCE_BYTES = 100L * 1024L * 1024L
    const val MAX_PRINT_PDF_BYTES = UploadLimits.MAX_FILE_BYTES
    const val MAX_PAGES = 500
    private const val MAX_RENDER_EDGE = 2000
    private const val PAGE_WIDTH = 595
    private const val PAGE_HEIGHT = 842
    private const val PAGE_MARGIN = 20f

    fun pageEdits(source: File): List<PdfPageEdit> {
        require(source.isFile && DocumentNamePolicy.isPdf(source)) {
            "Choose a saved PDF document."
        }
        ParcelFileDescriptor.open(source, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use { renderer ->
                require(renderer.pageCount in 1..MAX_PAGES) {
                    "The PDF must contain between 1 and $MAX_PAGES pages."
                }
                return (0 until renderer.pageCount).map { PdfPageEdit(it) }
            }
        }
    }

    fun renderPagePreview(source: File, edit: PdfPageEdit, maxEdge: Int = 720): Bitmap {
        require(maxEdge in 120..1200)
        ParcelFileDescriptor.open(source, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use { renderer ->
                require(edit.sourceIndex in 0 until renderer.pageCount)
                renderer.openPage(edit.sourceIndex).use { page ->
                    val longest = maxOf(page.width, page.height).coerceAtLeast(1)
                    val scale = maxEdge.toFloat() / longest
                    val width = (page.width * scale).toInt().coerceAtLeast(1)
                    val height = (page.height * scale).toInt().coerceAtLeast(1)
                    val bitmap = renderPdfPage(page, width, height)
                    val rotated = rotate(bitmap, edit.rotationDegrees)
                    if (rotated !== bitmap) bitmap.recycle()
                    return rotated
                }
            }
        }
    }

    /**
     * Makes one ordered print-ready PDF from PDF and image sources. PDF page order is
     * preserved; images become pages. Original files are never modified or deleted.
     */
    fun compose(files: List<File>, output: File): Int {
        validateSources(files)
        val document = PdfDocument()
        var pageNumber = 0
        try {
            files.forEach { file ->
                when (DocumentNamePolicy.mimeType(file)) {
                    "application/pdf" -> {
                        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
                            PdfRenderer(descriptor).use { renderer ->
                                require(renderer.pageCount > 0 && pageNumber + renderer.pageCount <= MAX_PAGES) {
                                    "A print batch can contain up to $MAX_PAGES pages."
                                }
                                for (index in 0 until renderer.pageCount) {
                                    renderer.openPage(index).use { page ->
                                        val size = renderDimensions(page.width, page.height)
                                        val bitmap = renderPdfPage(page, size.first, size.second)
                                        try {
                                            appendPage(document, bitmap, ++pageNumber)
                                        } finally {
                                            bitmap.recycle()
                                        }
                                    }
                                }
                            }
                        }
                    }
                    "image/jpeg", "image/png" -> {
                        val bitmap = decodeImage(file)
                        try {
                            require(pageNumber < MAX_PAGES) {
                                "A print batch can contain up to $MAX_PAGES pages."
                            }
                            appendPage(document, bitmap, ++pageNumber)
                        } finally {
                            bitmap.recycle()
                        }
                    }
                    else -> error("Unsupported batch file: ${file.name}. Use PDF, JPG, JPEG or PNG.")
                }
            }
            require(pageNumber > 0) { "The selected files contain no printable pages." }
            writeDocument(document, output)
            return pageNumber
        } catch (error: Exception) {
            output.delete()
            throw error
        } finally {
            document.close()
        }
    }

    /**
     * Applies page deletion, ordering and 90-degree rotation to a saved PDF.
     * Writes a new PDF; the source remains untouched.
     */
    fun editPdf(source: File, edits: List<PdfPageEdit>, output: File): Int {
        require(source.isFile && DocumentNamePolicy.isPdf(source)) {
            "Choose a saved PDF document."
        }
        require(source.length() in 1..MAX_SOURCE_BYTES) {
            "The source PDF must be between 1 byte and 100 MB."
        }
        require(edits.isNotEmpty() && edits.size <= MAX_PAGES) {
            "Keep between 1 and $MAX_PAGES pages in the edited PDF."
        }
        require(edits.map { it.sourceIndex }.distinct().size == edits.size) {
            "A source page cannot be included more than once."
        }
        require(edits.all { it.sourceIndex >= 0 && it.rotationDegrees in setOf(0, 90, 180, 270) }) {
            "A page edit contains an invalid page or rotation."
        }

        val document = PdfDocument()
        try {
            ParcelFileDescriptor.open(source, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
                PdfRenderer(descriptor).use { renderer ->
                    require(renderer.pageCount in 1..MAX_PAGES)
                    require(edits.all { it.sourceIndex < renderer.pageCount }) {
                        "The PDF changed while it was being edited. Reopen the editor."
                    }
                    edits.forEachIndexed { position, edit ->
                        renderer.openPage(edit.sourceIndex).use { page ->
                            val size = renderDimensions(page.width, page.height)
                            val original = renderPdfPage(page, size.first, size.second)
                            val rendered = rotate(original, edit.rotationDegrees)
                            if (rendered !== original) original.recycle()
                            try {
                                appendPage(document, rendered, position + 1)
                            } finally {
                                rendered.recycle()
                            }
                        }
                    }
                }
            }
            writeDocument(document, output)
            return edits.size
        } catch (error: Exception) {
            output.delete()
            throw error
        } finally {
            document.close()
        }
    }

    private fun validateSources(files: List<File>) {
        require(files.size in 2..20) {
            "Select between 2 and 20 documents to prepare a print batch."
        }
        require(files.all(DocumentNamePolicy::isBatchSource)) {
            "Print batches support PDF, JPG, JPEG and PNG files."
        }
        require(files.sumOf { it.length() } in 1..MAX_SOURCE_BYTES) {
            "The selected files must total no more than 100 MB."
        }
    }

    private fun renderDimensions(width: Int, height: Int): Pair<Int, Int> {
        val scale = MAX_RENDER_EDGE.toFloat() / maxOf(width, height).coerceAtLeast(1)
        return (width * scale).toInt().coerceAtLeast(1) to
            (height * scale).toInt().coerceAtLeast(1)
    }

    private fun renderPdfPage(page: PdfRenderer.Page, width: Int, height: Int): Bitmap {
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        bitmap.eraseColor(Color.WHITE)
        val transform = Matrix().apply {
            setScale(width.toFloat() / page.width, height.toFloat() / page.height)
        }
        try {
            page.render(bitmap, null, transform, PdfRenderer.Page.RENDER_MODE_FOR_PRINT)
            return bitmap
        } catch (error: Exception) {
            bitmap.recycle()
            throw error
        }
    }

    private fun decodeImage(file: File): Bitmap {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.absolutePath, bounds)
        require(bounds.outWidth > 0 && bounds.outHeight > 0) {
            "Could not read ${file.name}."
        }
        var sample = 1
        while (maxOf(bounds.outWidth / sample, bounds.outHeight / sample) > MAX_RENDER_EDGE) {
            sample *= 2
        }
        return BitmapFactory.decodeFile(file.absolutePath, BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.ARGB_8888
        }) ?: error("Could not decode ${file.name}.")
    }

    private fun rotate(bitmap: Bitmap, degrees: Int): Bitmap {
        val normalized = ((degrees % 360) + 360) % 360
        if (normalized == 0) return bitmap
        val matrix = Matrix().apply { postRotate(normalized.toFloat()) }
        return Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
    }

    private fun appendPage(document: PdfDocument, bitmap: Bitmap, pageNumber: Int) {
        val landscape = bitmap.width > bitmap.height
        val pageWidth = if (landscape) PAGE_HEIGHT else PAGE_WIDTH
        val pageHeight = if (landscape) PAGE_WIDTH else PAGE_HEIGHT
        val page = document.startPage(
            PdfDocument.PageInfo.Builder(pageWidth, pageHeight, pageNumber).create()
        )
        val availableWidth = pageWidth - PAGE_MARGIN * 2
        val availableHeight = pageHeight - PAGE_MARGIN * 2
        val scale = minOf(availableWidth / bitmap.width, availableHeight / bitmap.height)
        val width = bitmap.width * scale
        val height = bitmap.height * scale
        val left = (pageWidth - width) / 2f
        val top = (pageHeight - height) / 2f
        page.canvas.drawColor(Color.WHITE)
        page.canvas.drawBitmap(
            bitmap,
            null,
            RectF(left, top, left + width, top + height),
            Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
        )
        document.finishPage(page)
    }

    private fun writeDocument(document: PdfDocument, output: File) {
        output.parentFile?.let {
            check(it.exists() || it.mkdirs()) { "PDF output folder could not be created." }
        }
        FileOutputStream(output).use { stream ->
            document.writeTo(stream)
            stream.fd.sync()
        }
        require(output.length() in 1..MAX_PRINT_PDF_BYTES) {
            "The prepared PDF exceeds the current ${MAX_PRINT_PDF_BYTES / (1024L * 1024L)} MB per-document print limit. Remove pages or prepare a smaller batch."
        }
    }
}
