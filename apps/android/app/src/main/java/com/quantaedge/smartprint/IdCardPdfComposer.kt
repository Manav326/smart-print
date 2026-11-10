package com.quantaedge.smartprint

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.graphics.pdf.PdfDocument
import java.io.File
import java.io.FileOutputStream
import kotlin.math.min

/**
 * Creates a one-page A4 portrait sheet with the front and reverse of a card side by side.
 * The card contents are kept proportional and unfiltered. Borders and cut guides are drawn
 * outside the source image; they never cover text, photographs, barcodes or security details.
 */
object IdCardPdfComposer {
    const val MAX_OUTPUT_BYTES = 20L * 1024L * 1024L
    private const val MAX_DECODE_EDGE = 2800

    fun compose(
        frontFile: File,
        backFile: File,
        output: File,
        cardWidthMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_WIDTH_MM,
        cardHeightMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM,
        frontRotationDegrees: Int = 0,
        backRotationDegrees: Int = 0
    ) {
        require(frontFile.isFile && backFile.isFile) { "Both sides of the card must be scanned." }
        require(frontRotationDegrees in setOf(0, 180) && backRotationDegrees in setOf(0, 180)) {
            "Card orientation must be 0 or 180 degrees."
        }
        val layout = IdCardLayoutPolicy.calculate(cardWidthMm, cardHeightMm)
        val document = PdfDocument()
        try {
            val page = document.startPage(
                PdfDocument.PageInfo.Builder(
                    IdCardLayoutPolicy.PAGE_WIDTH_POINTS.toInt(),
                    IdCardLayoutPolicy.PAGE_HEIGHT_POINTS.toInt(),
                    1
                ).create()
            )
            page.canvas.drawColor(Color.WHITE)
            drawCard(page.canvas, frontFile, layout.front, frontRotationDegrees, "FRONT", layout)
            drawCard(page.canvas, backFile, layout.back, backRotationDegrees, "BACK", layout)
            document.finishPage(page)

            output.parentFile?.let {
                check(it.exists() || it.mkdirs()) { "Private ID output folder could not be created." }
            }
            FileOutputStream(output).use { stream ->
                document.writeTo(stream)
                stream.fd.sync()
            }
            require(output.length() in 1..MAX_OUTPUT_BYTES) {
                "The prepared ID sheet is too large. Please scan the card again with a closer crop."
            }
            require(hasPdfSignature(output)) { "The prepared ID sheet could not be verified." }
        } catch (error: Exception) {
            output.delete()
            throw error
        } finally {
            document.close()
        }
    }

    fun renderSheetPreview(
        frontFile: File,
        backFile: File,
        frontRotationDegrees: Int,
        backRotationDegrees: Int,
        cardWidthMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_WIDTH_MM,
        cardHeightMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM
    ): Bitmap {
        require(frontFile.isFile && backFile.isFile) { "Both sides of the card must be scanned." }
        require(frontRotationDegrees in setOf(0, 180) && backRotationDegrees in setOf(0, 180)) {
            "Card orientation must be 0 or 180 degrees."
        }
        val layout = IdCardLayoutPolicy.calculate(cardWidthMm, cardHeightMm)
        val preview = Bitmap.createBitmap(
            IdCardLayoutPolicy.PAGE_WIDTH_POINTS.toInt(),
            IdCardLayoutPolicy.PAGE_HEIGHT_POINTS.toInt(),
            Bitmap.Config.ARGB_8888
        )
        try {
            val canvas = Canvas(preview)
            canvas.drawColor(Color.WHITE)
            drawCard(canvas, frontFile, layout.front, frontRotationDegrees, "FRONT", layout)
            drawCard(canvas, backFile, layout.back, backRotationDegrees, "BACK", layout)
            return preview
        } catch (error: Exception) {
            preview.recycle()
            throw error
        }
    }

    fun decodePreview(file: File, rotation: Int): Bitmap {
        var bitmap = ImagePdfComposer.decodePreviewBitmap(file, 700)
            ?: error("A scanned card side could not be decoded. Please scan that side again.")
        if (rotation == 180) {
            val matrix = Matrix().apply { postRotate(180f) }
            val rotated = Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
            if (rotated !== bitmap) {
                bitmap.recycle()
                bitmap = rotated
            }
        }
        return bitmap
    }

    private fun drawCard(
        canvas: Canvas,
        source: File,
        card: IdCardLayoutPolicy.CardRect,
        rotation: Int,
        label: String,
        layout: IdCardLayoutPolicy.Layout
    ) {
        val bitmap = ImagePdfComposer.decodePreviewBitmap(source, MAX_DECODE_EDGE)
            ?: error("A scanned card side could not be decoded. Please scan that side again.")
        try {
            val displayBitmap = if (rotation == 180) {
                val matrix = Matrix().apply { postRotate(180f) }
                Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
            } else bitmap
            try {
                val target = RectF(card.left, card.top, card.left + card.width, card.top + card.height)
                canvas.save()
                canvas.clipRect(target)
                canvas.drawColor(Color.WHITE)
                // FIT_CENTER preserves the whole scan. It never stretches or silently crops details.
                val scale = min(target.width() / displayBitmap.width, target.height() / displayBitmap.height)
                val width = displayBitmap.width * scale
                val height = displayBitmap.height * scale
                val imageRect = RectF(
                    target.centerX() - width / 2f,
                    target.centerY() - height / 2f,
                    target.centerX() + width / 2f,
                    target.centerY() + height / 2f
                )
                canvas.drawBitmap(displayBitmap, null, imageRect, Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG))
                canvas.restore()

                val border = Paint(Paint.ANTI_ALIAS_FLAG).apply {
                    color = Color.rgb(28, 34, 44)
                    style = Paint.Style.STROKE
                    strokeWidth = 0.8f
                }
                canvas.drawRect(target, border)
                drawCropGuides(canvas, target, layout.cropOffset, layout.cropLength)
                val labelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
                    color = Color.rgb(65, 72, 84)
                    textSize = 7f
                    typeface = Typeface.create(Typeface.DEFAULT, Typeface.BOLD)
                    textAlign = Paint.Align.CENTER
                }
                canvas.drawText(label, target.centerX(), layout.labelTop, labelPaint)
            } finally {
                if (displayBitmap !== bitmap) displayBitmap.recycle()
            }
        } finally {
            bitmap.recycle()
        }
    }

    private fun drawCropGuides(canvas: Canvas, card: RectF, offset: Float, length: Float) {
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.rgb(45, 52, 64)
            style = Paint.Style.STROKE
            strokeWidth = 0.65f
        }
        val left = card.left - offset
        val right = card.right + offset
        val top = card.top - offset
        val bottom = card.bottom + offset
        // Four clear L-shaped trim guides. They are outside the card boundary.
        canvas.drawLine(left, top, left + length, top, paint)
        canvas.drawLine(left, top, left, top + length, paint)
        canvas.drawLine(right - length, top, right, top, paint)
        canvas.drawLine(right, top, right, top + length, paint)
        canvas.drawLine(left, bottom, left + length, bottom, paint)
        canvas.drawLine(left, bottom - length, left, bottom, paint)
        canvas.drawLine(right - length, bottom, right, bottom, paint)
        canvas.drawLine(right, bottom - length, right, bottom, paint)
    }

    private fun hasPdfSignature(file: File): Boolean = runCatching {
        file.inputStream().use { input ->
            val header = ByteArray(5)
            input.read(header) == 5 && header.contentEquals("%PDF-".toByteArray(Charsets.US_ASCII))
        }
    }.getOrDefault(false)
}