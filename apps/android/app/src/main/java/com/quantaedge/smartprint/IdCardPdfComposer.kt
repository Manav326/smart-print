package com.quantaedge.smartprint

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.graphics.pdf.PdfDocument
import java.io.ByteArrayOutputStream
import kotlin.math.max
import kotlin.math.min

/**
 * Creates a one-page A4 portrait sheet with the front above the reverse, both centred at exact physical card dimensions.
 * Source scans and generated PDF bytes remain in memory; IdStudioStore encrypts them before
 * they are written to persistent storage.
 */
object IdCardPdfComposer {
    const val MAX_OUTPUT_BYTES = 20L * 1024L * 1024L
    private const val MAX_DECODE_EDGE = 2800
    private const val PREVIEW_DECODE_EDGE = 900

    fun compose(
        frontBytes: ByteArray,
        backBytes: ByteArray,
        cardWidthMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_WIDTH_MM,
        cardHeightMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM,
        frontRotationDegrees: Int = 0,
        backRotationDegrees: Int = 0
    ): ByteArray {
        require(frontBytes.isNotEmpty() && backBytes.isNotEmpty()) { "Both sides of the card must be scanned." }
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
            drawCard(page.canvas, frontBytes, layout.front, frontRotationDegrees, "FRONT", layout.frontLabelTop, layout, MAX_DECODE_EDGE)
            drawCard(page.canvas, backBytes, layout.back, backRotationDegrees, "BACK", layout.backLabelTop, layout, MAX_DECODE_EDGE)
            document.finishPage(page)
            val output = ByteArrayOutputStream()
            document.writeTo(output)
            val bytes = output.toByteArray()
            require(bytes.size in 1..MAX_OUTPUT_BYTES) {
                "The prepared ID sheet is too large. Please scan the card again with a closer crop."
            }
            require(hasPdfSignature(bytes)) { "The prepared ID sheet could not be verified." }
            return bytes
        } finally {
            document.close()
        }
    }

    fun renderSheetPreview(
        frontBytes: ByteArray,
        backBytes: ByteArray,
        frontRotationDegrees: Int,
        backRotationDegrees: Int,
        cardWidthMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_WIDTH_MM,
        cardHeightMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM
    ): Bitmap {
        require(frontBytes.isNotEmpty() && backBytes.isNotEmpty()) { "Both sides of the card must be scanned." }
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
            drawCard(canvas, frontBytes, layout.front, frontRotationDegrees, "FRONT", layout.frontLabelTop, layout, PREVIEW_DECODE_EDGE)
            drawCard(canvas, backBytes, layout.back, backRotationDegrees, "BACK", layout.backLabelTop, layout, PREVIEW_DECODE_EDGE)
            return preview
        } catch (error: Exception) {
            preview.recycle()
            throw error
        }
    }

    fun renderSidePreview(sourceBytes: ByteArray, rotationDegrees: Int): Bitmap {
        require(sourceBytes.isNotEmpty()) { "The scanned card side is unavailable." }
        require(rotationDegrees in setOf(0, 180)) { "Card orientation must be 0 or 180 degrees." }
        val bitmap = decode(sourceBytes, PREVIEW_DECODE_EDGE)
        if (rotationDegrees == 0) return bitmap
        val matrix = Matrix().apply { postRotate(180f) }
        return try {
            Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true).also {
                if (it !== bitmap) bitmap.recycle()
            }
        } catch (error: Exception) {
            bitmap.recycle()
            throw error
        }
    }

    private fun drawCard(
        canvas: Canvas,
        sourceBytes: ByteArray,
        card: IdCardLayoutPolicy.CardRect,
        rotation: Int,
        label: String,
        labelTop: Float,
        layout: IdCardLayoutPolicy.Layout,
        maxDecodeEdge: Int
    ) {
        val bitmap = decode(sourceBytes, maxDecodeEdge)
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
                // FIT_CENTER preserves every pixel proportionally; it never stretches or crops source details.
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
                canvas.drawText(label, target.centerX(), labelTop, labelPaint)
            } finally {
                if (displayBitmap !== bitmap) displayBitmap.recycle()
            }
        } finally {
            bitmap.recycle()
        }
    }

    private fun decode(bytes: ByteArray, maxEdge: Int): Bitmap {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        require(bounds.outWidth > 0 && bounds.outHeight > 0) {
            "A scanned card side could not be decoded. Please scan that side again."
        }
        var sample = 1
        while (max(bounds.outWidth / sample, bounds.outHeight / sample) > maxEdge) sample *= 2
        return BitmapFactory.decodeByteArray(
            bytes, 0, bytes.size,
            BitmapFactory.Options().apply {
                inSampleSize = sample
                inPreferredConfig = Bitmap.Config.ARGB_8888
            }
        ) ?: error("A scanned card side could not be decoded. Please scan that side again.")
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
        canvas.drawLine(left, top, left + length, top, paint)
        canvas.drawLine(left, top, left, top + length, paint)
        canvas.drawLine(right - length, top, right, top + length, paint)
        canvas.drawLine(right, top, right, top + length, paint)
        canvas.drawLine(left, bottom, left + length, bottom, paint)
        canvas.drawLine(left, bottom - length, left, bottom, paint)
        canvas.drawLine(right - length, bottom, right, bottom, paint)
        canvas.drawLine(right, bottom - length, right, bottom, paint)
    }

    private fun hasPdfSignature(bytes: ByteArray): Boolean =
        bytes.size >= 5 && bytes.copyOfRange(0, 5).contentEquals("%PDF-".toByteArray(Charsets.US_ASCII))
}
