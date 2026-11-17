package com.quantaedge.smartprint

import android.content.Context
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
import com.tom_roush.pdfbox.android.PDFBoxResourceLoader
import com.tom_roush.pdfbox.io.MemoryUsageSetting
import com.tom_roush.pdfbox.multipdf.PDFMergerUtility
import com.tom_roush.pdfbox.pdmodel.PDDocument
import com.tom_roush.pdfbox.pdmodel.PDPage
import com.tom_roush.pdfbox.pdmodel.PDPageContentStream
import com.tom_roush.pdfbox.pdmodel.common.PDRectangle
import com.tom_roush.pdfbox.pdmodel.graphics.image.LosslessFactory
import com.tom_roush.pdfbox.pdmodel.graphics.image.PDImageXObject
import java.io.File
import java.io.FileOutputStream
import java.util.Locale

data class PdfPageEdit(val sourceIndex: Int, val rotationDegrees: Int = 0)

/**
 * Prepares local print PDFs without rasterizing existing PDF pages.
 *
 * PDF sources are merged as PDF page objects (page content, fonts and vector
 * resources remain intact). JPEGs are embedded in their original compressed
 * form; PNGs are downsampled only when their longest edge exceeds the bounded
 * image size. All PDFBox scratch data uses temporary files so large batches do
 * not need to be materialized in heap memory.
 */
object DocumentBatchComposer {
    const val MAX_SOURCE_BYTES = 50L * 1024L * 1024L
    private const val MAX_EDIT_SOURCE_BYTES = 100L * 1024L * 1024L
    const val MAX_PRINT_PDF_BYTES = UploadLimits.MAX_FILE_BYTES
    const val MAX_PAGES = 500
    private const val MAX_RENDER_EDGE = 2000
    private const val MAX_COMBINE_IMAGE_EDGE = 2400
    private const val MAX_COMBINE_IMAGE_PIXELS = 50_000_000L
    private const val PAGE_WIDTH = 595
    private const val PAGE_HEIGHT = 842
    private const val PAGE_MARGIN = 20f

    @Volatile
    private var pdfBoxInitialized = false

    private fun ensurePdfBoxInitialized(context: Context) {
        if (pdfBoxInitialized) return
        synchronized(this) {
            if (!pdfBoxInitialized) {
                PDFBoxResourceLoader.init(context.applicationContext)
                pdfBoxInitialized = true
            }
        }
    }

    fun pageEdits(source: File): List<PdfPageEdit> {
        require(source.isFile && DocumentNamePolicy.isPdf(source)) {
            "Choose a saved PDF document."
        }
        require(source.length() in 1..MAX_EDIT_SOURCE_BYTES) {
            "The source PDF must be no larger than 100 MB."
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
     * Merges 2–20 source files in their selected order. Combined source bytes are
     * checked before parsing; a batch over 50 MiB is rejected with an explicit
     * message. PDFs stay vector PDFs instead of expanding into page-sized bitmaps.
     */
    fun compose(context: Context, files: List<File>, output: File): Int {
        validateSources(files)
        ensurePdfBoxInitialized(context)

        val totalPages = files.sumOf { file ->
            if (DocumentNamePolicy.mimeType(file) == "application/pdf") pdfPageCount(file) else 1
        }
        require(totalPages in 1..MAX_PAGES) {
            "A combined print PDF can contain up to $MAX_PAGES pages."
        }

        val generatedImagePdfs = mutableListOf<File>()
        try {
            val merger = PDFMergerUtility()
            merger.setDocumentMergeMode(PDFMergerUtility.DocumentMergeMode.OPTIMIZE_RESOURCES_MODE)

            files.forEach { file ->
                when (DocumentNamePolicy.mimeType(file)) {
                    "application/pdf" -> merger.addSource(file)
                    "image/jpeg", "image/png" -> {
                        val imagePdf = createImagePdf(context, file)
                        generatedImagePdfs.add(imagePdf)
                        merger.addSource(imagePdf)
                    }
                    else -> error("Unsupported batch file: ${file.name}. Use PDF, JPG, JPEG or PNG.")
                }
            }

            output.parentFile?.let {
                check(it.exists() || it.mkdirs()) { "The PDF output folder could not be created." }
            }
            merger.destinationFileName = output.absolutePath
            merger.mergeDocuments(MemoryUsageSetting.setupTempFileOnly())

            val outputBytes = output.length()
            require(outputBytes in 1..MAX_PRINT_PDF_BYTES) {
                "The combined PDF output is ${formatMegabytes(outputBytes)}; the maximum output size is 50 MB. Reduce image resolution or split the batch."
            }
            return totalPages
        } catch (error: Exception) {
            output.delete()
            throw error
        } finally {
            generatedImagePdfs.forEach { it.delete() }
        }
    }

    /**
     * Applies display rotation metadata to selected PDF pages without rasterizing
     * page contents. Text, vector paths, fonts and embedded images remain intact.
     */
    fun rotatePages(
        context: Context,
        source: File,
        rotations: Map<Int, Int>,
        output: File
    ): Int {
        require(source.isFile && DocumentNamePolicy.isPdf(source)) {
            "Choose a saved PDF document."
        }
        require(source.length() in 1..MAX_EDIT_SOURCE_BYTES) {
            "The source PDF must be no larger than 100 MB."
        }
        require(rotations.isNotEmpty() && rotations.size <= MAX_PAGES) {
            "Select at least one rotated page."
        }
        require(rotations.all { (index, degrees) ->
            index >= 0 && degrees in setOf(90, 180, 270)
        }) {
            "A page rotation contains an invalid page or angle."
        }

        ensurePdfBoxInitialized(context)
        output.parentFile?.let {
            check(it.exists() || it.mkdirs()) { "The rotated PDF output folder could not be created." }
        }

        try {
            PDDocument.load(source, MemoryUsageSetting.setupTempFileOnly()).use { document ->
                require(document.numberOfPages in 1..MAX_PAGES) {
                    "The PDF must contain between 1 and $MAX_PAGES pages."
                }
                require(rotations.keys.all { it in 0 until document.numberOfPages }) {
                    "The PDF changed while it was being edited. Reopen it and try again."
                }
                rotations.forEach { (index, degrees) ->
                    val page = document.getPage(index)
                    page.rotation = ((page.rotation + degrees) % 360 + 360) % 360
                }
                document.save(output)
                require(output.length() in 1..MAX_EDIT_SOURCE_BYTES) {
                    "The updated PDF exceeds the 100 MB document limit."
                }
                return document.numberOfPages
            }
        } catch (error: Exception) {
            output.delete()
            throw error
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
        require(source.length() in 1..MAX_EDIT_SOURCE_BYTES) {
            "The source PDF must be no larger than 100 MB."
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
        require(files.all { it.length() in 1..MAX_PRINT_PDF_BYTES }) {
            "Each selected file must be no larger than 50 MB."
        }
        val totalBytes = files.sumOf { it.length() }
        require(totalBytes in 1..MAX_SOURCE_BYTES) {
            "The selected files total ${formatMegabytes(totalBytes)}. The combined source limit is 50 MB. Remove files or choose smaller files to continue."
        }
    }

    private fun pdfPageCount(file: File): Int =
        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use { renderer ->
                require(renderer.pageCount > 0) { "The PDF ${file.name} has no pages." }
                renderer.pageCount
            }
        }

    private fun createImagePdf(context: Context, imageFile: File): File {
        val temporaryPdf = File.createTempFile("smartprint-combine-image-", ".pdf", context.cacheDir)
        try {
            PDDocument(MemoryUsageSetting.setupTempFileOnly()).use { document ->
                val image = when (DocumentNamePolicy.mimeType(imageFile)) {
                    "image/jpeg" -> {
                        validateJpegForCombine(imageFile)
                        PDImageXObject.createFromFileByContent(imageFile, document)
                    }
                    "image/png" -> {
                        val bounds = readCombineImageBounds(imageFile)
                        require(bounds.outWidth.toLong() * bounds.outHeight.toLong() <= MAX_COMBINE_IMAGE_PIXELS) {
                            "The image ${imageFile.name} exceeds the 50-megapixel image limit."
                        }
                        var sample = 1
                        while (maxOf(bounds.outWidth / sample, bounds.outHeight / sample) > MAX_COMBINE_IMAGE_EDGE) {
                            sample *= 2
                        }
                        val bitmap = BitmapFactory.decodeFile(
                            imageFile.absolutePath,
                            BitmapFactory.Options().apply {
                                inSampleSize = sample
                                inPreferredConfig = Bitmap.Config.ARGB_8888
                            }
                        ) ?: error("Could not decode ${imageFile.name}.")
                        try {
                            LosslessFactory.createFromImage(document, bitmap)
                        } finally {
                            bitmap.recycle()
                        }
                    }
                    else -> error("Unsupported image: ${imageFile.name}.")
                }

                val landscape = image.width > image.height
                val pageWidth = if (landscape) PAGE_HEIGHT.toFloat() else PAGE_WIDTH.toFloat()
                val pageHeight = if (landscape) PAGE_WIDTH.toFloat() else PAGE_HEIGHT.toFloat()
                val page = PDPage(PDRectangle(pageWidth, pageHeight))
                document.addPage(page)

                val availableWidth = pageWidth - PAGE_MARGIN * 2
                val availableHeight = pageHeight - PAGE_MARGIN * 2
                val scale = minOf(
                    availableWidth / image.width.toFloat(),
                    availableHeight / image.height.toFloat()
                )
                val width = image.width * scale
                val height = image.height * scale
                val left = (pageWidth - width) / 2f
                val bottom = (pageHeight - height) / 2f
                PDPageContentStream(document, page).use { stream ->
                    stream.drawImage(image, left, bottom, width, height)
                }
                document.save(temporaryPdf)
            }
            return temporaryPdf
        } catch (error: Exception) {
            temporaryPdf.delete()
            throw error
        }
    }

    private fun readCombineImageBounds(imageFile: File): BitmapFactory.Options {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(imageFile.absolutePath, bounds)
        require(bounds.outWidth > 0 && bounds.outHeight > 0) {
            "Could not read image dimensions for ${imageFile.name}."
        }
        return bounds
    }

    /**
     * JPEG bytes remain compressed and are embedded without re-encoding, but a
     * small sampled decode first validates that the source is decodable and
     * enforces the same pixel ceiling as the upload analyzer.
     */
    private fun validateJpegForCombine(imageFile: File) {
        val bounds = readCombineImageBounds(imageFile)
        require(bounds.outWidth.toLong() * bounds.outHeight.toLong() <= MAX_COMBINE_IMAGE_PIXELS) {
            "The image ${imageFile.name} exceeds the 50-megapixel image limit."
        }
        var sample = 1
        while (maxOf(bounds.outWidth / sample, bounds.outHeight / sample) > 256) sample *= 2
        val validationBitmap = BitmapFactory.decodeFile(
            imageFile.absolutePath,
            BitmapFactory.Options().apply {
                inSampleSize = sample
                inPreferredConfig = Bitmap.Config.RGB_565
            }
        ) ?: error("Could not decode ${imageFile.name}.")
        validationBitmap.recycle()
    }

    private fun formatMegabytes(bytes: Long): String =
        String.format(Locale.ROOT, "%.1f MB", bytes.toDouble() / (1024.0 * 1024.0))

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
            "The prepared PDF exceeds the 50 MB per-document print limit. Remove pages or prepare a smaller batch."
        }
    }
}
