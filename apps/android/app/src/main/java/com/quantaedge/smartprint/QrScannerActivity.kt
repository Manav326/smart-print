package com.quantaedge.smartprint

import android.Manifest
import android.content.res.ColorStateList
import android.animation.ValueAnimator
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.animation.LinearInterpolator
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.ExperimentalGetImage
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.google.android.material.button.MaterialButton
import com.google.mlkit.vision.barcode.BarcodeScanner
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode as MlBarcode
import com.google.mlkit.vision.common.InputImage
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * On-device QR scanner. Camera frames are processed locally by ML Kit and
 * never saved or uploaded. Only the decoded QR string is returned to MainActivity.
 */
@OptIn(ExperimentalGetImage::class)
class QrScannerActivity : AppCompatActivity() {
    companion object {
        const val EXTRA_RAW_VALUE = "qrtoprint.qr.raw_value"
        const val EXTRA_SCAN_PURPOSE = "smartprint.qr.scan_purpose"
        const val EXTRA_QA_PAUSE_CAMERA = "smartprint.qr.qa_pause_camera"
        const val QA_CAMERA_PAUSED_LABEL = "Camera preview paused for QR UI verification."
    }

    private lateinit var permissionLauncher: ActivityResultLauncher<String>
    private lateinit var previewHost: FrameLayout
    private lateinit var previewView: PreviewView
    private lateinit var overlay: ScanFrameView
    private lateinit var statusText: TextView
    private lateinit var torchButton: MaterialButton
    private lateinit var scanner: BarcodeScanner
    private lateinit var scanPurpose: QrScanPurpose
    private val analyzerExecutor = Executors.newSingleThreadExecutor()
    private val hasReturnedResult = AtomicBoolean(false)

    private var cameraProvider: ProcessCameraProvider? = null
    private var camera: Camera? = null
    private var imageAnalysis: ImageAnalysis? = null
    private var cameraBindInProgress = false
    private var permissionWasRequested = false
    private var torchEnabled = false

    private fun isQaCameraPaused(): Boolean {
        val localQaPackage = packageName == "com.quantaedge.smartprint.localqa" ||
            packageName.startsWith("com.quantaedge.smartprint.localqa.")
        return BuildConfig.DEBUG && localQaPackage &&
            intent.getBooleanExtra(EXTRA_QA_PAUSE_CAMERA, false)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        SmartPrintThemePreference.apply(this)
        super.onCreate(savedInstanceState)
        scanPurpose = QrScanPurpose.fromWireValue(intent.getStringExtra(EXTRA_SCAN_PURPOSE))
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.statusBarColor = getColor(R.color.qp_surface)
        window.navigationBarColor = getColor(R.color.qp_surface)
        val useLightSystemBars =
            (resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_NO
        WindowInsetsControllerCompat(window, window.decorView).apply {
            isAppearanceLightStatusBars = useLightSystemBars
            isAppearanceLightNavigationBars = useLightSystemBars
        }

        permissionLauncher = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) bindCamera() else showPermissionPanel()
        }

        scanner = BarcodeScanning.getClient(
            BarcodeScannerOptions.Builder()
                .setBarcodeFormats(MlBarcode.FORMAT_QR_CODE)
                .build()
        )

        buildScannerUi()
        when {
            isQaCameraPaused() -> showQaCameraPausedPanel()
            hasCameraPermission() -> bindCamera()
            else -> showPermissionPanel()
        }
    }

    override fun onResume() {
        super.onResume()
        if (::previewHost.isInitialized && !isQaCameraPaused() &&
            hasCameraPermission() && camera == null && !cameraBindInProgress
        ) {
            bindCamera()
        }
    }

    private fun showQaCameraPausedPanel() {
        camera = null
        previewHost.removeAllViews()
        val panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(dp(24), dp(24), dp(24), dp(24))
        }
        panel.addView(label(
            QA_CAMERA_PAUSED_LABEL,
            14,
            getColor(R.color.qp_muted),
            true
        ).apply {
            gravity = Gravity.CENTER
            textAlignment = View.TEXT_ALIGNMENT_CENTER
        })
        previewHost.addView(panel, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT
        ))
        torchButton.isEnabled = false
        statusText.text = "Camera is paused by isolated QA verification."
    }

    override fun onDestroy() {
        imageAnalysis?.clearAnalyzer()
        cameraProvider?.unbindAll()
        scanner.close()
        analyzerExecutor.shutdown()
        super.onDestroy()
    }

    private fun buildScannerUi() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = SmartPrintGlass.backdrop(this@QrScannerActivity)
            setPadding(dp(18), 0, dp(18), 0)
        }

        val top = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(9), 0, dp(14))
        }
        val close = textButton("×", 30).apply {
            contentDescription = "Close QR scanner"
            setOnClickListener { finish() }
        }
        top.addView(close, LinearLayout.LayoutParams(dp(48), dp(48)))
        val titleColumn = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(8), 0, 0, 0)
        }
        titleColumn.addView(label(scanPurpose.eyebrow, 9, getColor(R.color.qp_primary), true))
        titleColumn.addView(label(scanPurpose.title, 18, getColor(R.color.qp_text), true))
        titleColumn.addView(label(
            scanPurpose.instruction,
            11,
            getColor(R.color.qp_muted),
            false
        ).apply {
            maxLines = 2
            ellipsize = android.text.TextUtils.TruncateAt.END
            setPadding(0, dp(2), 0, 0)
        })
        top.addView(titleColumn, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        root.addView(top, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ))

        previewHost = FrameLayout(this).apply {
            background = SmartPrintGlass.surface(this@QrScannerActivity, 22)
            clipToOutline = true
        }
        previewView = PreviewView(this).apply {
            scaleType = PreviewView.ScaleType.FILL_CENTER
            implementationMode = PreviewView.ImplementationMode.COMPATIBLE
            keepScreenOn = true
        }
        overlay = ScanFrameView(this)
        root.addView(previewHost, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f
        ).apply { topMargin = dp(4); bottomMargin = dp(14) })

        statusText = label(
            scanPurpose.alignmentHint,
            13, getColor(R.color.qp_muted), false
        ).apply {
            gravity = Gravity.CENTER
            background = SmartPrintGlass.surface(this@QrScannerActivity, 15, R.color.qp_glass_inset)
            setPadding(dp(12), dp(10), dp(12), dp(12))
        }
        root.addView(statusText, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ))

        val bottom = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(4), 0, dp(20))
        }
        torchButton = MaterialButton(this).apply {
            text = "Flashlight"
            isAllCaps = false
            cornerRadius = dp(16)
            setTextColor(getColor(R.color.qp_text))
            backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_glass_inset))
            strokeColor = ColorStateList.valueOf(getColor(R.color.qp_glass_border))
            strokeWidth = dp(1)
            isEnabled = false
            setOnClickListener { toggleTorch() }
        }
        bottom.addView(torchButton, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT, dp(48)
        ))
        val privacy = label(
            "Scanned on device. Nothing saved.",
            11, getColor(R.color.qp_muted), false
        ).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(13), 0, 0, 0) }
        bottom.addView(privacy, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        root.addView(bottom, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ))

        ViewCompat.setOnApplyWindowInsetsListener(root) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.setPadding(dp(18), bars.top + dp(4), dp(18), bars.bottom)
            insets
        }
        setContentView(root)
        ViewCompat.requestApplyInsets(root)
    }

    private fun bindCamera() {
        if (cameraBindInProgress || camera != null || !hasCameraPermission()) return
        cameraBindInProgress = true
        showCameraPreview()

        val providerFuture = ProcessCameraProvider.getInstance(this)
        providerFuture.addListener({
            cameraBindInProgress = false
            if (isFinishing || isDestroyed || !hasCameraPermission()) return@addListener
            try {
                val provider = providerFuture.get()
                cameraProvider = provider

                val preview = Preview.Builder().build().also {
                    it.setSurfaceProvider(previewView.surfaceProvider)
                }
                val analysis = ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .build()
                imageAnalysis = analysis
                analysis.setAnalyzer(analyzerExecutor) { proxy ->
                    if (hasReturnedResult.get()) {
                        proxy.close()
                        return@setAnalyzer
                    }
                    val mediaImage = proxy.image
                    if (mediaImage == null) {
                        proxy.close()
                        return@setAnalyzer
                    }
                    try {
                        scanner.process(InputImage.fromMediaImage(mediaImage, proxy.imageInfo.rotationDegrees))
                            .addOnSuccessListener { barcodes ->
                                val rawValue = barcodes.firstOrNull {
                                    it.format == MlBarcode.FORMAT_QR_CODE && !it.rawValue.isNullOrBlank()
                                }?.rawValue?.trim()
                                if (!rawValue.isNullOrBlank() && hasReturnedResult.compareAndSet(false, true)) {
                                    runOnUiThread {
                                        setResult(
                                            RESULT_OK,
                                            Intent()
                                                .putExtra(EXTRA_RAW_VALUE, rawValue)
                                                .putExtra(EXTRA_SCAN_PURPOSE, scanPurpose.wireValue)
                                        )
                                        finish()
                                    }
                                }
                            }
                            .addOnCompleteListener { proxy.close() }
                    } catch (_: Exception) {
                        proxy.close()
                    }
                }

                provider.unbindAll()
                camera = provider.bindToLifecycle(
                    this,
                    CameraSelector.DEFAULT_BACK_CAMERA,
                    preview,
                    analysis
                )
                val hasFlash = camera?.cameraInfo?.hasFlashUnit() == true
                torchButton.isEnabled = hasFlash
                if (!hasFlash) torchEnabled = false
                statusText.text = if (hasFlash) {
                    scanPurpose.alignmentHint + " Flashlight available."
                } else {
                    scanPurpose.alignmentHint
                }
            } catch (_: Exception) {
                camera = null
                statusText.text = "Camera unavailable."
                showRetryButton()
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun showCameraPreview() {
        previewHost.removeAllViews()
        previewHost.addView(previewView, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT
        ))
        previewHost.addView(overlay, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT
        ))
    }

    private fun showPermissionPanel() {
        camera = null
        previewHost.removeAllViews()
        val permanentlyDenied = permissionWasRequested &&
            !shouldShowRequestPermissionRationale(Manifest.permission.CAMERA)
        val panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(dp(26), dp(24), dp(26), dp(24))
        }
        panel.addView(label("▦", 48, getColor(R.color.qp_primary), false).apply { gravity = Gravity.CENTER })
        panel.addView(label("Camera access needed", 20, getColor(R.color.qp_text), true).apply {
            gravity = Gravity.CENTER
            setPadding(0, dp(12), 0, dp(8))
        })
        panel.addView(label(
            "Allow camera for QR scanning.",
            13, getColor(R.color.qp_muted), false
        ).apply { gravity = Gravity.CENTER; textAlignment = View.TEXT_ALIGNMENT_CENTER })
        val action = MaterialButton(this).apply {
            text = if (permanentlyDenied) "Open settings" else "Allow camera"
            isAllCaps = false
            cornerRadius = dp(12)
            minHeight = dp(46)
            insetTop = 0
            insetBottom = 0
            backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
            setTextColor(getColor(R.color.qp_primary_content))
            setOnClickListener {
                if (permanentlyDenied) {
                    startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + packageName)))
                } else {
                    permissionWasRequested = true
                    permissionLauncher.launch(Manifest.permission.CAMERA)
                }
            }
        }
        panel.addView(action, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dp(50)
        ).apply { topMargin = dp(18) })
        previewHost.addView(panel, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT
        ))
        torchButton.isEnabled = false
        statusText.text = "Camera permission required."
    }

    private fun showRetryButton() {
        val retry = MaterialButton(this).apply {
            text = "Try again"
            isAllCaps = false
            minHeight = dp(44)
            insetTop = 0
            insetBottom = 0
            cornerRadius = dp(12)
            backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_card))
            strokeColor = ColorStateList.valueOf(getColor(R.color.qp_outline))
            strokeWidth = dp(1)
            setTextColor(getColor(R.color.qp_text))
            setOnClickListener {
                camera = null
                bindCamera()
            }
        }
        previewHost.addView(retry, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER
        ))
    }

    private fun toggleTorch() {
        val target = camera ?: return
        if (!target.cameraInfo.hasFlashUnit()) return
        torchEnabled = !torchEnabled
        target.cameraControl.enableTorch(torchEnabled)
            .addListener({
                torchButton.text = if (torchEnabled) "Flashlight on" else "Flashlight"
            }, ContextCompat.getMainExecutor(this))
    }

    private fun hasCameraPermission() =
        ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

    private fun label(value: String, size: Int, color: Int, bold: Boolean) =
        TextView(this).apply {
            text = value
            textSize = size.toFloat()
            setTextColor(color)
            if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD)
        }

    private fun textButton(value: String, size: Int) = TextView(this).apply {
        text = value
        textSize = size.toFloat()
        gravity = Gravity.CENTER
        setTextColor(getColor(R.color.qp_text))
        background = android.graphics.drawable.GradientDrawable().apply {
            setColor(getColor(R.color.qp_card))
            cornerRadius = dp(14).toFloat()
            setStroke(dp(1), getColor(R.color.qp_outline))
        }
        isClickable = true
        isFocusable = true
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()

    private class ScanFrameView(context: android.content.Context) : View(context) {
        private val cornerPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = ContextCompat.getColor(context, R.color.qp_primary)
            style = Paint.Style.STROKE
            strokeWidth = dp(3).toFloat()
            strokeCap = Paint.Cap.ROUND
        }
        private val beamPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.WHITE
            style = Paint.Style.STROKE
            strokeWidth = dp(2).toFloat()
            strokeCap = Paint.Cap.ROUND
        }
        private var beamPosition = 0.18f
        private val animator = ValueAnimator.ofFloat(0.12f, 0.88f).apply {
            duration = 1600L
            repeatMode = ValueAnimator.REVERSE
            repeatCount = ValueAnimator.INFINITE
            interpolator = LinearInterpolator()
            addUpdateListener {
                beamPosition = it.animatedValue as Float
                invalidate()
            }
        }

        override fun onAttachedToWindow() {
            super.onAttachedToWindow()
            animator.start()
        }

        override fun onDetachedFromWindow() {
            animator.cancel()
            super.onDetachedFromWindow()
        }

        override fun onDraw(canvas: Canvas) {
            super.onDraw(canvas)
            val frameWidth = width * 0.78f
            val frameHeight = height * 0.48f
            val left = (width - frameWidth) / 2f
            val top = (height - frameHeight) / 2f
            val right = left + frameWidth
            val bottom = top + frameHeight
            val length = dp(30).toFloat()

            canvas.drawLine(left, top, left + length, top, cornerPaint)
            canvas.drawLine(left, top, left, top + length, cornerPaint)
            canvas.drawLine(right - length, top, right, top, cornerPaint)
            canvas.drawLine(right, top, right, top + length, cornerPaint)
            canvas.drawLine(left, bottom - length, left, bottom, cornerPaint)
            canvas.drawLine(left, bottom, left + length, bottom, cornerPaint)
            canvas.drawLine(right - length, bottom, right, bottom, cornerPaint)
            canvas.drawLine(right, bottom - length, right, bottom, cornerPaint)

            val beamY = top + frameHeight * beamPosition
            canvas.drawLine(left + dp(12), beamY, right - dp(12), beamY, beamPaint)
        }

        private fun dp(value: Int) = value * resources.displayMetrics.density
    }
}
