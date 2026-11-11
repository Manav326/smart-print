package com.quantaedge.smartprint

import android.app.Activity
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.drawable.GradientDrawable
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import android.view.GestureDetector
import android.view.Gravity
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.widget.AppCompatImageView
import androidx.exifinterface.media.ExifInterface
import java.io.File
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

/**
 * Full-screen, read-only document reader for files in SmartPrint's private library.
 * Rendering is performed off the UI thread and thumbnails are kept to a small window.
 */
class SmartPrintDocumentViewer(
    context: Context,
    startFile: File,
    private val imageSiblings: List<File>,
    startPosition: Int? = null,
    private val onNavigateImage: (File) -> Unit
) : LinearLayout(context) {
    companion object {
        private const val MAX_PAGE_EDGE = 2200
        private const val MAX_IMAGE_EDGE = 2600
        private const val THUMBNAIL_EDGE = 96
        private const val IMAGE_THUMBNAIL_WINDOW = 4
    }

    private data class Thumbnail(val index: Int, val bitmap: Bitmap)
    private data class RenderedDocument(
        val pageCount: Int,
        val index: Int,
        val bitmap: Bitmap,
        val thumbnails: List<Thumbnail>
    )

    private val host = context as? Activity ?: error("The document reader requires an Activity.")
    private val worker: ExecutorService = Executors.newSingleThreadExecutor()
    private val disposed = AtomicBoolean(false)
    private val stage = FrameLayout(context)
    private val pageImage = ZoomableDocumentImageView(context) { direction ->
        if (isPdf) showPdfPage(currentPage + direction)
        else navigateImage(currentImageIndex + direction)
    }
    private val feedback = TextView(context).apply {
        gravity = Gravity.CENTER
        textSize = 13f
        setTextColor(Color.WHITE)
        setPadding(dp(18), dp(10), dp(18), dp(10))
        background = roundedBackground(Color.argb(205, 20, 22, 32), dp(18))
    }
    private val pageCounter = TextView(context).apply {
        gravity = Gravity.CENTER
        textSize = 12f
        setTextColor(getColor(R.color.qp_text))
        background = SmartPrintGlass.surface(context, 14, R.color.qp_glass_card)
        setPadding(dp(10), dp(7), dp(10), dp(7))
        includeFontPadding = false
    }
    private val previousPage = readerControl("←", "Previous page")
    private val nextPage = readerControl("→", "Next page")
    private val rotatePage = readerControl("⟳", "Rotate view")
    private val filmstrip = HorizontalScrollView(context).apply {
        contentDescription = "Document page thumbnails"
        isHorizontalScrollBarEnabled = false
        clipToPadding = false
        overScrollMode = View.OVER_SCROLL_NEVER
        setPadding(dp(10), dp(5), dp(10), dp(7))
        visibility = View.GONE
    }
    private val thumbnailRow = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
    }

    private var currentFile = startFile
    private var currentPage = startPosition?.coerceAtLeast(0) ?: 0
    private var pageCount = 0
    private var rotationDegrees = 0
    private var renderVersion = 0
    // The current file path is authoritative for images; list positions can change if
    // another photo is imported, renamed or removed while the viewer is closed.
    private var currentImageIndex = imageSiblings.indexOfFirst { sameFile(it, startFile) }.coerceAtLeast(0)
    private val displayedBitmaps = mutableListOf<Bitmap>()
    private val isPdf = startFile.extension.equals("pdf", ignoreCase = true)

    init {
        orientation = LinearLayout.VERTICAL
        background = SmartPrintGlass.backdrop(context)
        clipToPadding = false

        if (isPdf) {
            val pageControls = LinearLayout(context).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(8), 0, dp(8), 0)
            }
            previousPage.setOnClickListener { showPdfPage(currentPage - 1) }
            nextPage.setOnClickListener { showPdfPage(currentPage + 1) }
            rotatePage.setOnClickListener {
                rotationDegrees = (rotationDegrees + 90) % 360
                renderCurrentPdfPage()
            }
            pageControls.contentDescription = "Document navigation"
            pageControls.addView(previousPage, LinearLayout.LayoutParams(dp(44), dp(42)))
            pageControls.addView(pageCounter, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(8); marginEnd = dp(8) })
            pageControls.addView(rotatePage, LinearLayout.LayoutParams(dp(44), dp(42)))
            pageControls.addView(nextPage, LinearLayout.LayoutParams(dp(44), dp(42)).apply { marginStart = dp(8) })
            addView(pageControls, LinearLayout.LayoutParams(-1, dp(52)))
        } else if (imageSiblings.size > 1) {
            pageCounter.text = "Photo ${currentImageIndex + 1} of ${imageSiblings.size}"
            pageCounter.gravity = Gravity.CENTER_VERTICAL
            pageCounter.setPadding(dp(16), 0, dp(16), 0)
            addView(pageCounter, LinearLayout.LayoutParams(-1, dp(30)))
        }

        stage.setBackgroundColor(Color.rgb(24, 26, 36))
        stage.addView(pageImage, FrameLayout.LayoutParams(-1, -1))
        stage.addView(feedback, FrameLayout.LayoutParams(-2, -2, Gravity.CENTER))
        addView(stage, LinearLayout.LayoutParams(-1, 0, 1f))

        filmstrip.addView(thumbnailRow, FrameLayout.LayoutParams(-2, -1))
        addView(filmstrip, LinearLayout.LayoutParams(-1, dp(if (isPdf) 88 else 82)))

        if (isPdf) {
            showLoading("Opening PDF…")
            loadPdfPageCount()
        } else {
            if (imageSiblings.size > 1) showLoading("Opening image…") else showLoading("Opening image…")
            loadImage(currentImageIndex)
        }
    }

    fun currentPosition(): Int = if (isPdf) currentPage else currentImageIndex

    fun dispose() {
        if (!disposed.compareAndSet(false, true)) return
        renderVersion++
        worker.shutdownNow()
        clearDisplayedBitmaps()
    }

    private fun loadPdfPageCount() {
        submitRender {
            try {
                val count = ParcelFileDescriptor.open(
                    currentFile,
                    ParcelFileDescriptor.MODE_READ_ONLY
                ).use { descriptor ->
                    PdfRenderer(descriptor).use { renderer -> renderer.pageCount }
                }
                require(count > 0) { "This PDF has no pages to display." }
                host.runOnUiThread {
                    if (disposed.get()) return@runOnUiThread
                    pageCount = count
                    currentPage = currentPage.coerceIn(0, count - 1)
                    updatePdfControls()
                    renderCurrentPdfPage()
                }
            } catch (error: Exception) {
                showRenderError(++renderVersion, error)
            }
        }
    }

    private fun showPdfPage(index: Int) {
        if (!isPdf || pageCount <= 0 || disposed.get()) return
        val target = index.coerceIn(0, pageCount - 1)
        if (target == currentPage) return
        currentPage = target
        rotationDegrees = 0
        updatePdfControls()
        renderCurrentPdfPage()
    }

    private fun updatePdfControls() {
        pageCounter.text = "Page ${if (pageCount == 0) 1 else currentPage + 1} of ${max(1, pageCount)}"
        previousPage.isEnabled = currentPage > 0
        nextPage.isEnabled = pageCount > 0 && currentPage < pageCount - 1
        previousPage.alpha = if (previousPage.isEnabled) 1f else 0.32f
        nextPage.alpha = if (nextPage.isEnabled) 1f else 0.32f
        rotatePage.alpha = if (pageCount > 0) 1f else 0.32f
    }

    private fun renderCurrentPdfPage() {
        if (disposed.get() || pageCount <= 0) return
        val request = ++renderVersion
        val file = currentFile
        val requestedPage = currentPage
        val requestedRotation = rotationDegrees
        showLoading("Rendering page ${requestedPage + 1}…")
        submitRender {
            try {
                val rendered = renderPdfFrame(file, requestedPage, requestedRotation)
                host.runOnUiThread {
                    if (disposed.get() || request != renderVersion) {
                        recycle(rendered)
                        return@runOnUiThread
                    }
                    clearDisplayedBitmaps()
                    pageCount = rendered.pageCount
                    currentPage = rendered.index
                    displayedBitmaps.add(rendered.bitmap)
                    pageImage.setDocumentBitmap(rendered.bitmap)
                    rendered.thumbnails.forEach { displayedBitmaps.add(it.bitmap) }
                    showFeedback(false)
                    updatePdfControls()
                    showThumbnails(rendered.thumbnails, isPdf = true)
                }
            } catch (error: Exception) {
                showRenderError(request, error)
            }
        }
    }

    private fun renderPdfFrame(file: File, requestedPage: Int, rotation: Int): RenderedDocument {
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use { renderer ->
                require(renderer.pageCount > 0) { "This PDF has no pages to display." }
                val index = requestedPage.coerceIn(0, renderer.pageCount - 1)
                val pageBitmap = renderer.openPageBitmap(index, MAX_PAGE_EDGE)
                val displayBitmap = try {
                    rotateBitmap(pageBitmap, rotation).also {
                        if (it !== pageBitmap) pageBitmap.recycle()
                    }
                } catch (error: Exception) {
                    if (!pageBitmap.isRecycled) pageBitmap.recycle()
                    throw error
                }

                val thumbnails = mutableListOf<Thumbnail>()
                try {
                    for (pageIndex in 0 until renderer.pageCount) {
                        val raw = renderer.openPageBitmap(pageIndex, THUMBNAIL_EDGE, Bitmap.Config.RGB_565)
                        val thumb = try {
                            rotateBitmap(raw, if (pageIndex == index) rotation else 0).also {
                                if (it !== raw) raw.recycle()
                            }
                        } catch (error: Exception) {
                            if (!raw.isRecycled) raw.recycle()
                            throw error
                        }
                        thumbnails.add(Thumbnail(pageIndex, thumb))
                    }
                    RenderedDocument(renderer.pageCount, index, displayBitmap, thumbnails)
                } catch (error: Exception) {
                    if (!displayBitmap.isRecycled) displayBitmap.recycle()
                    thumbnails.forEach { if (!it.bitmap.isRecycled) it.bitmap.recycle() }
                    throw error
                }
            }
        }
    }

    private fun PdfRenderer.openPageBitmap(index: Int, maxEdge: Int, config: Bitmap.Config = Bitmap.Config.ARGB_8888): Bitmap {
        openPage(index).use { page ->
            val scale = min(1f, maxEdge.toFloat() / max(page.width, page.height).toFloat())
            val width = max(1, (page.width * scale).toInt())
            val height = max(1, (page.height * scale).toInt())
            // PdfRenderer.Page.render requires ARGB_8888 on supported Android
            // versions. Convert the completed thumbnail only after rendering.
            val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
            bitmap.eraseColor(Color.WHITE)
            val transform = Matrix().apply {
                setScale(width.toFloat() / page.width, height.toFloat() / page.height)
            }
            try {
                page.render(bitmap, null, transform, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                if (config == Bitmap.Config.ARGB_8888) return bitmap
                val converted = bitmap.copy(config, false)
                    ?: throw IllegalStateException("The page thumbnail could not be converted.")
                bitmap.recycle()
                return converted
            } catch (error: Exception) {
                if (!bitmap.isRecycled) bitmap.recycle()
                throw error
            }
        }
    }

    private fun loadImage(index: Int) {
        if (imageSiblings.isEmpty() || index !in imageSiblings.indices || disposed.get()) return
        currentImageIndex = index
        currentFile = imageSiblings[index]
        val request = ++renderVersion
        if (imageSiblings.size > 1) pageCounter.text = "Photo ${index + 1} of ${imageSiblings.size}"
        showLoading("Opening image…")
        submitRender {
            try {
                val rendered = renderImageFrame(index)
                host.runOnUiThread {
                    if (disposed.get() || request != renderVersion) {
                        recycle(rendered)
                        return@runOnUiThread
                    }
                    clearDisplayedBitmaps()
                    displayedBitmaps.add(rendered.bitmap)
                    pageImage.setDocumentBitmap(rendered.bitmap)
                    pageImage.contentDescription = "Image preview"
                    rendered.thumbnails.forEach { displayedBitmaps.add(it.bitmap) }
                    showFeedback(false)
                    showThumbnails(rendered.thumbnails, isPdf = false)
                    if (imageSiblings.size > 1) pageCounter.text = "Photo ${currentImageIndex + 1} of ${imageSiblings.size}"
                }
            } catch (error: Exception) {
                showRenderError(request, error)
            }
        }
    }

    private fun renderImageFrame(index: Int): RenderedDocument {
        val file = imageSiblings[index]
        val mainBitmap = decodeImage(file, MAX_IMAGE_EDGE)
        val thumbnails = mutableListOf<Thumbnail>()
        return try {
            val first = max(0, index - IMAGE_THUMBNAIL_WINDOW)
            val last = min(imageSiblings.lastIndex, index + IMAGE_THUMBNAIL_WINDOW)
            for (imageIndex in first..last) {
                try {
                    val thumbnail = decodeImage(imageSiblings[imageIndex], THUMBNAIL_EDGE)
                    thumbnails.add(Thumbnail(imageIndex, thumbnail))
                } catch (_: Exception) {
                    // A damaged neighbouring photo must not prevent opening the selected one.
                }
            }
            RenderedDocument(imageSiblings.size, index, mainBitmap, thumbnails)
        } catch (error: Exception) {
            if (!mainBitmap.isRecycled) mainBitmap.recycle()
            thumbnails.forEach { if (!it.bitmap.isRecycled) it.bitmap.recycle() }
            throw error
        }
    }

    private fun decodeImage(file: File, maxEdge: Int): Bitmap {
        require(file.isFile && file.length() > 0) { "This image is empty or unavailable." }
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.absolutePath, bounds)
        require(bounds.outWidth > 0 && bounds.outHeight > 0) { "This image could not be decoded." }
        var sample = 1
        while (max(bounds.outWidth / sample, bounds.outHeight / sample) > maxEdge) sample *= 2
        val decoded = BitmapFactory.decodeFile(
            file.absolutePath,
            BitmapFactory.Options().apply {
                inSampleSize = sample
                inPreferredConfig = Bitmap.Config.ARGB_8888
            }
        ) ?: error("This image could not be decoded.")
        val orientation = runCatching {
            ExifInterface(file.absolutePath).getAttributeInt(
                ExifInterface.TAG_ORIENTATION,
                ExifInterface.ORIENTATION_NORMAL
            )
        }.getOrDefault(ExifInterface.ORIENTATION_NORMAL)
        val degrees = when (orientation) {
            ExifInterface.ORIENTATION_ROTATE_90, ExifInterface.ORIENTATION_TRANSPOSE -> 90
            ExifInterface.ORIENTATION_ROTATE_180, ExifInterface.ORIENTATION_FLIP_VERTICAL -> 180
            ExifInterface.ORIENTATION_ROTATE_270, ExifInterface.ORIENTATION_TRANSVERSE -> 270
            else -> 0
        }
        val mirrored = orientation == ExifInterface.ORIENTATION_FLIP_HORIZONTAL ||
            orientation == ExifInterface.ORIENTATION_FLIP_VERTICAL ||
            orientation == ExifInterface.ORIENTATION_TRANSPOSE ||
            orientation == ExifInterface.ORIENTATION_TRANSVERSE
        if (degrees == 0 && !mirrored) return decoded
        val matrix = Matrix().apply {
            if (mirrored) preScale(-1f, 1f)
            if (degrees != 0) postRotate(degrees.toFloat())
        }
        return try {
            Bitmap.createBitmap(decoded, 0, 0, decoded.width, decoded.height, matrix, true)
                .also { if (it !== decoded) decoded.recycle() }
        } catch (error: Exception) {
            decoded.recycle()
            throw error
        }
    }

    private fun rotateBitmap(bitmap: Bitmap, degrees: Int): Bitmap {
        val normalized = ((degrees % 360) + 360) % 360
        if (normalized == 0) return bitmap
        val matrix = Matrix().apply { postRotate(normalized.toFloat()) }
        return Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
    }

    private fun showThumbnails(thumbnails: List<Thumbnail>, isPdf: Boolean) {
        thumbnailRow.removeAllViews()
        if (thumbnails.isEmpty() || (isPdf && pageCount <= 1) || (!isPdf && imageSiblings.size <= 1)) {
            filmstrip.visibility = View.GONE
            return
        }
        filmstrip.visibility = View.VISIBLE
        thumbnails.forEach { thumbnail ->
            val frame = LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                gravity = Gravity.CENTER
                setPadding(dp(4), dp(3), dp(4), dp(3))
                contentDescription = if (isPdf) {
                    "Go to page ${thumbnail.index + 1}"
                } else {
                    "Open photo ${thumbnail.index + 1}"
                }
                isClickable = true
                isFocusable = true
                val selectable = android.util.TypedValue()
                if (context.theme.resolveAttribute(
                        android.R.attr.selectableItemBackground,
                        selectable,
                        true
                    )
                ) setBackgroundResource(selectable.resourceId)
                setOnClickListener {
                    if (isPdf) showPdfPage(thumbnail.index) else navigateImage(thumbnail.index)
                }
            }
            val preview = ImageView(context).apply {
                setImageBitmap(thumbnail.bitmap)
                scaleType = if (isPdf) ImageView.ScaleType.FIT_CENTER else ImageView.ScaleType.CENTER_CROP
                setBackgroundColor(if (isPdf) Color.WHITE else Color.rgb(38, 40, 52))
                contentDescription = if (isPdf) {
                    "Page ${thumbnail.index + 1} thumbnail"
                } else {
                    "Photo ${thumbnail.index + 1} thumbnail"
                }
            }
            frame.addView(
                preview,
                LinearLayout.LayoutParams(dp(62), dp(58))
            )
            if (isPdf) {
                frame.addView(
                    TextView(context).apply {
                        text = (thumbnail.index + 1).toString()
                        textSize = 9f
                        gravity = Gravity.CENTER
                        setTextColor(getColor(R.color.qp_muted))
                    },
                    LinearLayout.LayoutParams(-1, dp(15))
                )
            } else {
                val active = thumbnail.index == currentImageIndex
                frame.addView(View(context).apply {
                    setBackgroundColor(if (active) getColor(R.color.qp_primary) else Color.TRANSPARENT)
                }, LinearLayout.LayoutParams(-1, dp(3)))
            }
            thumbnailRow.addView(
                frame,
                LinearLayout.LayoutParams(dp(70), dp(if (isPdf) 82 else 66)).apply {
                    marginEnd = dp(5)
                }
            )
            if (thumbnail.index == currentPage && isPdf || thumbnail.index == currentImageIndex && !isPdf) {
                filmstrip.post {
                    val target = frame.left - (filmstrip.width - frame.width) / 2
                    filmstrip.smoothScrollTo(target.coerceAtLeast(0), 0)
                }
            }
        }
    }

    private fun navigateImage(index: Int) {
        if (isPdf || disposed.get() || index !in imageSiblings.indices || index == currentImageIndex) return
        loadImage(index)
        onNavigateImage(imageSiblings[index])
    }

    private fun showLoading(message: String) {
        feedback.setOnClickListener(null)
        feedback.isClickable = false
        feedback.isFocusable = false
        feedback.contentDescription = null
        feedback.text = message
        feedback.visibility = View.VISIBLE
    }

    private fun showRenderError(request: Int, error: Exception) {
        host.runOnUiThread {
            if (disposed.get() || request != renderVersion) return@runOnUiThread
            clearDisplayedBitmaps()
            feedback.text = "Could not open this document. Tap to try again."
            feedback.contentDescription = "Retry opening document"
            feedback.setOnClickListener {
                if (isPdf) {
                    if (pageCount <= 0) loadPdfPageCount() else renderCurrentPdfPage()
                } else {
                    loadImage(currentImageIndex)
                }
            }
            feedback.isClickable = true
            feedback.isFocusable = true
            feedback.visibility = View.VISIBLE
        }
    }

    private fun showFeedback(visible: Boolean) {
        feedback.visibility = if (visible) View.VISIBLE else View.GONE
        if (!visible) {
            feedback.setOnClickListener(null)
            feedback.isClickable = false
            feedback.isFocusable = false
            feedback.contentDescription = null
        }
    }

    private fun clearDisplayedBitmaps() {
        pageImage.setDocumentBitmap(null)
        thumbnailRow.removeAllViews()
        displayedBitmaps.forEach { if (!it.isRecycled) it.recycle() }
        displayedBitmaps.clear()
    }

    private fun recycle(rendered: RenderedDocument) {
        if (!rendered.bitmap.isRecycled) rendered.bitmap.recycle()
        rendered.thumbnails.forEach { if (!it.bitmap.isRecycled) it.bitmap.recycle() }
    }

    private fun submitRender(task: () -> Unit) {
        if (disposed.get()) return
        try {
            worker.execute { if (!disposed.get()) task() }
        } catch (_: RejectedExecutionException) {
            // The reader was closed while a render was being queued.
        }
    }

    private fun readerControl(label: String, description: String): TextView {
        val selectable = android.util.TypedValue()
        val ripple = if (context.theme.resolveAttribute(
                android.R.attr.selectableItemBackgroundBorderless,
                selectable,
                true
            )
        ) selectable.resourceId else 0
        return TextView(context).apply {
            text = label
            textSize = 19f
            gravity = Gravity.CENTER
            minWidth = dp(44)
            minHeight = dp(42)
            includeFontPadding = false
            contentDescription = description
            setTextColor(getColor(R.color.qp_text))
            background = SmartPrintGlass.surface(context, 12, R.color.qp_glass_card)
            elevation = dp(1).toFloat()
            isClickable = true
            isFocusable = true
        }
    }

    private fun roundedBackground(color: Int, radius: Int) =
        GradientDrawable().apply {
            setColor(color)
            cornerRadius = radius.toFloat()
        }

    private fun sameFile(left: File, right: File): Boolean =
        runCatching { left.canonicalPath == right.canonicalPath }.getOrDefault(false)

    private fun getColor(resourceId: Int): Int = host.getColor(resourceId)
    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()
}

/** Matrix-based image canvas with pinch zoom, panning, double-tap fit and swipe navigation. */
internal class ZoomableDocumentImageView(
    context: Context,
    private val onSwipe: (Int) -> Unit
) : AppCompatImageView(context) {
    private val displayMatrix = Matrix()
    private var minimumScale = 1f
    private var currentScale = 1f
    private var lastX = 0f
    private var lastY = 0f

    private val scaleDetector = ScaleGestureDetector(
        context,
        object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(detector: ScaleGestureDetector): Boolean {
                val target = (currentScale * detector.scaleFactor).coerceIn(minimumScale, minimumScale * 6f)
                displayMatrix.postScale(target / currentScale, target / currentScale, detector.focusX, detector.focusY)
                currentScale = target
                constrainToViewport()
                imageMatrix = displayMatrix
                return true
            }
        }
    )

    private val gestureDetector = GestureDetector(
        context,
        object : GestureDetector.SimpleOnGestureListener() {
            override fun onDown(event: MotionEvent): Boolean = true
            override fun onDoubleTap(event: MotionEvent): Boolean {
                fitToView()
                return true
            }
            override fun onFling(
                first: MotionEvent?,
                second: MotionEvent,
                velocityX: Float,
                velocityY: Float
            ): Boolean {
                if (
                    first != null &&
                    currentScale <= minimumScale * 1.05f &&
                    abs(first.x - second.x) > dp(55) &&
                    abs(first.x - second.x) > abs(first.y - second.y) * 1.2f
                ) {
                    onSwipe(if (first.x > second.x) 1 else -1)
                    return true
                }
                return false
            }
        }
    )

    init {
        scaleType = ScaleType.MATRIX
        adjustViewBounds = false
        isFocusable = true
        contentDescription = "Document preview"
    }

    fun setDocumentBitmap(bitmap: Bitmap?) {
        setImageBitmap(bitmap)
        if (bitmap != null) post { fitToView() } else {
            displayMatrix.reset()
            currentScale = 1f
            minimumScale = 1f
            imageMatrix = displayMatrix
        }
    }

    private fun fitToView() {
        val image = drawable ?: return
        if (width <= 0 || height <= 0 || image.intrinsicWidth <= 0 || image.intrinsicHeight <= 0) return
        minimumScale = min(
            width.toFloat() / image.intrinsicWidth.toFloat(),
            height.toFloat() / image.intrinsicHeight.toFloat()
        ).coerceAtLeast(0.01f)
        currentScale = minimumScale
        displayMatrix.reset()
        displayMatrix.postScale(minimumScale, minimumScale)
        displayMatrix.postTranslate(
            (width - image.intrinsicWidth * minimumScale) / 2f,
            (height - image.intrinsicHeight * minimumScale) / 2f
        )
        imageMatrix = displayMatrix
    }

    private fun constrainToViewport() {
        val image = drawable ?: return
        val values = FloatArray(9)
        displayMatrix.getValues(values)
        val scale = values[Matrix.MSCALE_X]
        val scaledWidth = image.intrinsicWidth * scale
        val scaledHeight = image.intrinsicHeight * scale
        var x = values[Matrix.MTRANS_X]
        var y = values[Matrix.MTRANS_Y]
        x = if (scaledWidth <= width) (width - scaledWidth) / 2f
        else x.coerceIn(width - scaledWidth, 0f)
        y = if (scaledHeight <= height) (height - scaledHeight) / 2f
        else y.coerceIn(height - scaledHeight, 0f)
        values[Matrix.MTRANS_X] = x
        values[Matrix.MTRANS_Y] = y
        displayMatrix.setValues(values)
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        if (width != oldWidth || height != oldHeight) fitToView()
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        gestureDetector.onTouchEvent(event)
        scaleDetector.onTouchEvent(event)
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                lastX = event.x
                lastY = event.y
            }
            MotionEvent.ACTION_MOVE -> {
                if (event.pointerCount == 1 && !scaleDetector.isInProgress) {
                    displayMatrix.postTranslate(event.x - lastX, event.y - lastY)
                    constrainToViewport()
                    imageMatrix = displayMatrix
                }
                lastX = event.x
                lastY = event.y
            }
            MotionEvent.ACTION_UP -> performClick()
            MotionEvent.ACTION_CANCEL -> Unit
        }
        return true
    }

    override fun performClick(): Boolean {
        super.performClick()
        return true
    }

    private fun dp(value: Int): Float = value * resources.displayMetrics.density
}
