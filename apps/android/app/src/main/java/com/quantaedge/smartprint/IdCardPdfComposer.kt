package com.quantaedge.smartprint

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import androidx.exifinterface.media.ExifInterface
import java.io.ByteArrayInputStream
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.graphics.drawable.Drawable
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
    private const val PREVIEW_DECODE_EDGE = 2400

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
        cardHeightMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM,
        previewWidthPixels: Int = 1190,
        brandMark: Drawable? = null
    ): Bitmap {
        require(previewWidthPixels in 595..1800) { "The A4 preview size is outside the supported range." }
        require(frontBytes.isNotEmpty() && backBytes.isNotEmpty()) { "Both sides of the card must be scanned." }
        require(frontRotationDegrees in setOf(0, 180) && backRotationDegrees in setOf(0, 180)) {
            "Card orientation must be 0 or 180 degrees."
        }
        val layout = IdCardLayoutPolicy.calculate(cardWidthMm, cardHeightMm)
        val scale = previewWidthPixels.toFloat() / IdCardLayoutPolicy.PAGE_WIDTH_POINTS
        val preview = Bitmap.createBitmap(
            previewWidthPixels,
            (IdCardLayoutPolicy.PAGE_HEIGHT_POINTS * scale).toInt(),
            Bitmap.Config.ARGB_8888
        )
        try {
            val canvas = Canvas(preview)
            canvas.scale(scale, scale)
            canvas.drawColor(Color.WHITE)
            drawCard(canvas, frontBytes, layout.front, frontRotationDegrees, "FRONT", layout.frontLabelTop, layout, PREVIEW_DECODE_EDGE, brandMark)
            drawCard(canvas, backBytes, layout.back, backRotationDegrees, "BACK", layout.backLabelTop, layout, PREVIEW_DECODE_EDGE, brandMark)
            return preview
        } catch (error: Exception) {
            preview.recycle()
            throw error
        }
    }

    fun sourceResolutionDpi(
        sourceBytes: ByteArray,
        cardWidthMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_WIDTH_MM,
        cardHeightMm: Float = IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM
    ): Int {
        require(sourceBytes.isNotEmpty()) { "The scanned card side is unavailable." }
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(sourceBytes, 0, sourceBytes.size, bounds)
        require(bounds.outWidth > 0 && bounds.outHeight > 0) { "The scanned card side could not be read." }
        val longPixels = max(bounds.outWidth, bounds.outHeight).toFloat()
        val shortPixels = min(bounds.outWidth, bounds.outHeight).toFloat()
        val longInches = max(cardWidthMm, cardHeightMm) / 25.4f
        val shortInches = min(cardWidthMm, cardHeightMm) / 25.4f
        return min(longPixels / longInches, shortPixels / shortInches).toInt()
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
        maxDecodeEdge: Int,
        brandMark: Drawable? = null
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
                if (brandMark != null) drawPreviewBranding(canvas, target, brandMark)

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

    private fun drawPreviewBranding(canvas: Canvas, target: RectF, brandMark: Drawable) {
        canvas.save()
        canvas.clipRect(target)
        // A large, softly faded mark spans the face without obscuring text.
        val markSize = min(target.width() * 0.78f, target.height() * 1.22f)
        val markRect = RectF(
            target.centerX() - markSize / 2f,
            target.centerY() - markSize / 2f,
            target.centerX() + markSize / 2f,
            target.centerY() + markSize / 2f
        )
        val layer = canvas.saveLayerAlpha(target, 24)
        brandMark.setBounds(
            markRect.left.toInt(), markRect.top.toInt(),
            markRect.right.toInt(), markRect.bottom.toInt()
        )
        brandMark.draw(canvas)
        canvas.restoreToCount(layer)

        val wordmark = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.argb(34, 40, 55, 78)
            textSize = target.height() * 0.060f
            typeface = Typeface.create(Typeface.DEFAULT, Typeface.BOLD)
            textAlign = Paint.Align.CENTER
        }
        canvas.drawText(
            "SmartPrint by QuantaEdge",
            target.centerX(),
            target.centerY() + target.height() * 0.37f,
            wordmark
        )
        canvas.restore()
    }

    private fun decode(bytes: ByteArray, maxEdge: Int): Bitmap {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        require(bounds.outWidth > 0 && bounds.outHeight > 0) {
            "This ID photo could not be decoded. Choose another image."
        }
        var sample = 1
        while (max(bounds.outWidth / sample, bounds.outHeight / sample) > maxEdge) sample *= 2
        val decoded = BitmapFactory.decodeByteArray(
            bytes, 0, bytes.size,
            BitmapFactory.Options().apply {
                inSampleSize = sample
                inPreferredConfig = Bitmap.Config.ARGB_8888
            }
        ) ?: error("This ID photo could not be decoded. Choose another image.")

        // Phone gallery images often store portrait/landscape orientation in EXIF
        // instead of rotating the pixels. Apply it before layout and preview so an
        // imported image prints the same way it appears in the gallery.
        val orientation = runCatching {
            ExifInterface(ByteArrayInputStream(bytes)).getAttributeInt(
                ExifInterface.TAG_ORIENTATION,
                ExifInterface.ORIENTATION_NORMAL
            )
        }.getOrDefault(ExifInterface.ORIENTATION_NORMAL)
        val transform = Matrix()
        when (orientation) {
            ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> transform.setScale(-1f, 1f)
            ExifInterface.ORIENTATION_ROTATE_180 -> transform.setRotate(180f)
            ExifInterface.ORIENTATION_FLIP_VERTICAL -> transform.setScale(1f, -1f)
            ExifInterface.ORIENTATION_TRANSPOSE -> {
                transform.setRotate(90f)
                transform.postScale(-1f, 1f)
            }
            ExifInterface.ORIENTATION_ROTATE_90 -> transform.setRotate(90f)
            ExifInterface.ORIENTATION_TRANSVERSE -> {
                transform.setRotate(270f)
                transform.postScale(-1f, 1f)
            }
            ExifInterface.ORIENTATION_ROTATE_270 -> transform.setRotate(270f)
            else -> return decoded
        }
        return try {
            Bitmap.createBitmap(decoded, 0, 0, decoded.width, decoded.height, transform, true).also {
                if (it !== decoded) decoded.recycle()
            }
        } catch (error: Exception) {
            decoded.recycle()
            throw error
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
