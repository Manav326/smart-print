package com.quantaedge.smartprint

import android.content.ClipData
import android.content.ClipDescription
import android.content.Intent
import android.content.pm.PackageManager
import android.database.Cursor
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import android.view.Gravity
import android.view.Menu
import android.content.res.ColorStateList
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.webkit.CookieManager
import android.webkit.SslErrorHandler
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.EditText
import android.text.InputType
import android.widget.CheckBox
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.FileProvider
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.documentfile.provider.DocumentFile
import androidx.webkit.JavaScriptReplyProxy
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import com.google.android.material.appbar.MaterialToolbar
import com.google.android.material.bottomnavigation.BottomNavigationView
import com.google.android.material.navigation.NavigationBarView
import com.google.android.material.navigationrail.NavigationRailView
import com.google.android.material.button.MaterialButton
import com.google.android.material.card.MaterialCardView
import com.google.android.material.bottomsheet.BottomSheetDialog
import eightbitlab.com.blurview.BlurTarget
import eightbitlab.com.blurview.BlurView
import com.google.mlkit.vision.documentscanner.GmsDocumentScannerOptions
import com.google.mlkit.vision.documentscanner.GmsDocumentScanning
import com.google.mlkit.vision.documentscanner.GmsDocumentScanningResult
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileInputStream
import java.text.SimpleDateFormat
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Date
import java.util.Locale
import java.util.UUID
import java.net.HttpURLConnection
import java.net.URL
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class MainActivity : AppCompatActivity() {
    companion object {
        private const val HOME = 1
        private const val FILES = 2
        private const val SCAN_PRINT = 3
        private const val ACTIVITY = 4
        private const val SWITCH_SHOP = 91
        private const val OPEN_WITH = 92
        private const val MAX_TREE_FILES = 500
        private const val MAX_TREE_DEPTH = 12
        private const val FOLDER_PICKER_ACCEPT = "application/x-smartprint-folder-selection"
        private const val MAX_CUSTOMER_UPLOAD_PREVIEW_BYTES = 20L * 1024L * 1024L
        private const val MAX_CUSTOMER_UPLOAD_PREVIEW_BATCH_BYTES = 50L * 1024L * 1024L
    }

    private lateinit var store: LocalDocumentStore
    private lateinit var idStudioStore: IdStudioStore
    private lateinit var customerAccountStore: CustomerAccountStore
    private lateinit var customerPrintSessionStore: CustomerPrintSessionStore
    private lateinit var toolbar: MaterialToolbar
    private lateinit var content: FrameLayout
    private lateinit var navigation: NavigationBarView
    private lateinit var bottomGlass: BlurView
    private var shopDisplayName: String? = null
    private var shopLogoUrl: String? = null
    private var shopAccentColor: String? = null
    private lateinit var accountAction: ImageView
    private var documentViewer: SmartPrintDocumentViewer? = null
    private var viewingDocument: File? = null
    private var documentViewerReturnToIdStudio = false
    private var privateIdViewerOpen = false
    private var idStudioScreenOpen = false
    private var idStudioDraftId: String? = null
    private var pendingIdStudioSide: String? = null
    private var idStudioFrontRotation = 0
    private var idStudioBackRotation = 0
    private var customerUploadPdfPreviewOpen = false
    private val customerUploadPdfPreviews = java.util.concurrent.ConcurrentHashMap<String, File>()
    private var webView: WebView? = null
    private var folderPath = ""
    private var pickerPath = ""
    private var pickerMode = false
    private var pendingImportDestinationPath = ""
    private var portalUrl: String? = null
    private var portalHost: String? = null
    private var photoStudioOpen = false
    private var fileCallback: ValueCallback<Array<Uri>>? = null
    private val pendingLocalNetworkPermissionReplies = mutableListOf<JavaScriptReplyProxy>()
    private var localNetworkPermissionRequestInProgress = false
    private var exportFile: File? = null
    private var exportFolder: File? = null
    private var archiveFiles: List<File> = emptyList()
    private var accountCreateMode = false
    private var editingPdf: File? = null
    private val selectedFilePaths = linkedSetOf<String>()
    private val chooserSelectedFilePaths = linkedSetOf<String>()
    private val chooserSelectedFolderPaths = linkedSetOf<String>()
    private val expandedFolderPaths = linkedSetOf<String>()
    private var fileChooserAllowsMultiple = false
    private var fileChooserSelectFolders = false
    private var pendingScanNeedsDestination = false
    private var pendingScanDestinationPath = ""
    private var profileVisible = false
    private var profileReturnDestination = HOME
    private var librarySearchQuery = ""
    private var librarySelectionMode = false
    private lateinit var bottomNavigationContent: LinearLayout
    private val bottomNavigationItemViews = linkedMapOf<Int, Triple<View, ImageView, TextView>>()
    private var webViewThemeBootstrapInstalled = false

    private lateinit var importFiles: ActivityResultLauncher<Array<String>>
    private lateinit var browsePhoneDocuments: ActivityResultLauncher<Array<String>>
    private lateinit var localNetworkPermissionLauncher: ActivityResultLauncher<String>
    private lateinit var importTree: ActivityResultLauncher<Uri?>
    private lateinit var exportOne: ActivityResultLauncher<String>
    private lateinit var exportTree: ActivityResultLauncher<Uri?>
    private lateinit var exportArchive: ActivityResultLauncher<String>
    private lateinit var scannerLauncher: ActivityResultLauncher<Intent>
    private lateinit var documentScanLauncher: ActivityResultLauncher<IntentSenderRequest>

    override fun onCreate(savedInstanceState: Bundle?) {
        // Restore the explicit app preference before AppCompat inflates theme-aware UI.
        SmartPrintThemePreference.apply(this)
        super.onCreate(savedInstanceState)
        photoStudioOpen = savedInstanceState?.getBoolean("photoStudioOpen") ?: false
        if (photoStudioOpen) window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (handleBackNavigation()) return

                // Let the activity finish normally when the user is already at the home root.
                // Temporarily disable this callback so the dispatcher can reach its platform fallback.
                isEnabled = false
                try {
                    onBackPressedDispatcher.onBackPressed()
                } finally {
                    isEnabled = true
                }
            }
        })
        WindowCompat.setDecorFitsSystemWindows(window, false)
        val lightSystemBars = (resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) ==
            android.content.res.Configuration.UI_MODE_NIGHT_NO
        window.statusBarColor = getColor(R.color.qp_surface)
        window.navigationBarColor = getColor(R.color.qp_surface)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            isAppearanceLightStatusBars = lightSystemBars
            isAppearanceLightNavigationBars = lightSystemBars
        }
        store = LocalDocumentStore(filesDir)
        idStudioStore = IdStudioStore(filesDir)
        idStudioDraftId = savedInstanceState?.getString("idStudioDraftId")
        pendingIdStudioSide = savedInstanceState?.getString("pendingIdStudioSide")
        idStudioFrontRotation = savedInstanceState?.getInt("idStudioFrontRotation", 0) ?: 0
        idStudioBackRotation = savedInstanceState?.getInt("idStudioBackRotation", 0) ?: 0
        idStudioScreenOpen = savedInstanceState?.getBoolean("idStudioScreenOpen") ?: false
        customerAccountStore = CustomerAccountStore(this)
        customerPrintSessionStore = CustomerPrintSessionStore(this)
        pendingScanNeedsDestination = savedInstanceState?.getBoolean("pendingScanNeedsDestination") ?: false
        pendingScanDestinationPath = savedInstanceState?.getString("pendingScanDestinationPath").orEmpty()
        folderPath = savedInstanceState?.getString("folderPath").orEmpty()
        folderPath = runCatching { store.pathOf(store.folder(folderPath)) }.getOrDefault("")
        pendingImportDestinationPath = savedInstanceState?.getString("pendingImportDestinationPath").orEmpty()
        librarySearchQuery = savedInstanceState?.getString("librarySearchQuery").orEmpty()
        registerFileActions()
        buildLayout()
        showHome()
        val savedShop = getSharedPreferences("smartprint.customer.last-shop", MODE_PRIVATE)
        portalUrl = savedInstanceState?.getString("portalUrl")
            ?: savedShop.getString("portalUrl", null)
        portalHost = savedInstanceState?.getString("portalHost")
            ?: savedShop.getString("portalHost", null)
            ?: portalUrl?.let { Uri.parse(it).host }
        shopDisplayName = savedShop.getString("shopDisplayName", null)
        shopLogoUrl = savedShop.getString("shopLogoUrl", null)
        shopAccentColor = savedShop.getString("shopAccentColor", null)
        portalUrl?.let { url ->
            val view = ensureWebView()
            val webState = savedInstanceState?.getBundle("webViewState")
            val restored = webState?.let { view.restoreState(it) != null } ?: false
            if (!restored) view.loadUrl(url)
        }

        val restoredNavigation = savedInstanceState?.getInt("selectedNavigation", HOME) ?: HOME
        when (restoredNavigation) {
            FILES -> showFiles()
            SCAN_PRINT -> showScanPrint()
            ACTIVITY -> showActivity()
            else -> if (savedInstanceState == null && portalUrl != null) showPrint() else showHome()
        }
        val restoredDocumentPath = savedInstanceState?.getString("viewingDocumentPath")
        if (!restoredDocumentPath.isNullOrBlank()) {
            val restoredDocument = runCatching {
                File(restoredDocumentPath).canonicalFile.also { file ->
                    if (!runCatching { store.pathOf(file) }.isSuccess &&
                        !idStudioStore.isPreparedDocument(file)) error("Document is outside private storage.")
                }
            }.getOrNull()?.takeIf { it.isFile }
            if (restoredDocument != null) {
                folderPath = restoredDocument.parentFile?.let { folder ->
                    runCatching { store.pathOf(folder) }.getOrDefault("")
                }.orEmpty()
                if (idStudioStore.isPreparedDocument(restoredDocument)) showIdStudioHome() else showFiles()
                showDocumentViewer(
                    restoredDocument,
                    savedInstanceState?.getInt("viewerPosition", 0) ?: 0
                )
            }
        } else if (savedInstanceState?.getBoolean("idStudioScreenOpen") == true) {
            showIdStudioHome()
        } else if (savedInstanceState?.getBoolean("profileVisible") == true) {
            profileReturnDestination = savedInstanceState.getInt(
                "profileReturnDestination",
                navigation.selectedItemId
            )
            showProfile()
        }
        handleShareIntent(intent)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean("pendingScanNeedsDestination", pendingScanNeedsDestination)
        outState.putString("pendingScanDestinationPath", pendingScanDestinationPath)
        outState.putString("folderPath", folderPath)
        outState.putString("pendingImportDestinationPath", pendingImportDestinationPath)
        outState.putString("librarySearchQuery", librarySearchQuery)
        outState.putInt("selectedNavigation", if (::navigation.isInitialized) navigation.selectedItemId else HOME)
        outState.putBoolean("profileVisible", profileVisible)
        outState.putInt("profileReturnDestination", profileReturnDestination)
        outState.putString("viewingDocumentPath", viewingDocument?.absolutePath?.takeUnless { customerUploadPdfPreviewOpen })
        outState.putInt("viewerPosition", documentViewer?.currentPosition() ?: 0)
        outState.putString("portalUrl", portalUrl)
        outState.putString("portalHost", portalHost)
        outState.putBoolean("photoStudioOpen", photoStudioOpen)
        outState.putString("idStudioDraftId", idStudioDraftId)
        outState.putString("pendingIdStudioSide", pendingIdStudioSide)
        outState.putInt("idStudioFrontRotation", idStudioFrontRotation)
        outState.putInt("idStudioBackRotation", idStudioBackRotation)
        outState.putBoolean("idStudioScreenOpen", idStudioScreenOpen)
        webView?.let { view ->
            val webState = Bundle()
            view.saveState(webState)
            outState.putBundle("webViewState", webState)
        }
        super.onSaveInstanceState(outState)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleShareIntent(intent)
    }

    private fun handleBackNavigation(): Boolean {
        when {
            photoStudioOpen -> {
                webView?.evaluateJavascript(
                    "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));",
                    null
                )
            }
            documentViewer != null -> if (customerUploadPdfPreviewOpen) closeCustomerUploadPdfPreview() else closeDocumentViewer()
            idStudioScreenOpen -> showIdStudioHome()
            profileVisible -> returnFromProfile()
            pickerMode && pickerPath.isNotBlank() -> { pickerPath = parent(pickerPath); refreshLibrary(preserveScroll = false) }
            pickerMode -> cancelPicker()
            editingPdf != null -> showFiles()
            navigation.selectedItemId == SCAN_PRINT && webView?.canGoBack() == true -> webView?.goBack()
            navigation.selectedItemId == FILES && folderPath.isNotEmpty() -> {
                folderPath = parent(folderPath)
                showFiles()
            }
            navigation.selectedItemId == SCAN_PRINT -> showHome()
            navigation.selectedItemId != HOME -> showHome()
            folderPath.isNotEmpty() -> { folderPath = parent(folderPath); showFiles() }
            else -> return false
        }
        return true
    }

    private fun registerFileActions() {
        localNetworkPermissionLauncher = registerForActivityResult(
            ActivityResultContracts.RequestPermission()
        ) { granted ->
            completeLocalNetworkPermissionRequest(granted)
        }
        browsePhoneDocuments = registerForActivityResult(
            ActivityResultContracts.OpenMultipleDocuments()
        ) { uris ->
            if (!uris.isNullOrEmpty()) deliverPhoneDocuments(uris)
        }
        scannerLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            if (result.resultCode == RESULT_OK) {
                val scanResult = result.data
                val purpose = QrScanPurpose.fromWireValue(
                    scanResult?.getStringExtra(QrScannerActivity.EXTRA_SCAN_PURPOSE)
                )
                handleScannedQr(
                    scanResult?.getStringExtra(QrScannerActivity.EXTRA_RAW_VALUE),
                    purpose
                )
            }
        }
        documentScanLauncher = registerForActivityResult(
            ActivityResultContracts.StartIntentSenderForResult()
        ) { result ->
            if (result.resultCode == RESULT_OK) {
                try {
                    val scan = GmsDocumentScanningResult.fromActivityResultIntent(result.data)
                        ?: error("The scan could not be read.")
                    val idSide = pendingIdStudioSide
                    if (idSide != null) {
                        pendingIdStudioSide = null
                        saveIdStudioScan(scan, idSide)
                    } else if (pendingScanNeedsDestination) showScanDestinationPicker(scan)
                    else saveDocumentScan(scan, pendingScanDestinationPath)
                } catch (e: Exception) {
                    if (pendingIdStudioSide != null || idStudioDraftId != null) {
                        pendingIdStudioSide = null
                        toast("ID scan could not be saved: " + message(e))
                        if (idStudioDraftId?.let(idStudioStore::hasFront) == true) showIdStudioBackStep()
                        else showIdStudioHome()
                    } else {
                        pendingScanNeedsDestination = false
                        pendingScanDestinationPath = ""
                        toast("Could not save the scan: " + message(e))
                    }
                }
            } else {
                val idSide = pendingIdStudioSide
                pendingIdStudioSide = null
                if (idSide != null) {
                    toast("Scan cancelled. Your existing private ID scans are unchanged.")
                    if (idStudioDraftId?.let(idStudioStore::hasFront) == true) showIdStudioBackStep()
                    else showIdStudioHome()
                } else {
                    pendingScanNeedsDestination = false
                    pendingScanDestinationPath = ""
                }
            }
        }
        importFiles = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
            if (uris.isNullOrEmpty()) return@registerForActivityResult
            var ok = 0
            var failed = 0
            uris.forEach { uri ->
                try { importUri(uri, pendingImportDestinationPath); ok++ }
                catch (_: Exception) { failed++ }
            }
            pendingImportDestinationPath = ""
            reportImport(ok, failed)
            refreshLibrary()
        }
        importTree = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
            if (uri == null) return@registerForActivityResult
            try {
                try { contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
                catch (_: SecurityException) { }
                val tree = DocumentFile.fromTreeUri(this, uri) ?: error("Folder is unavailable.")
                require(tree.isDirectory) { "Select a folder." }
                val counts = intArrayOf(0, 0)
                val completed = importDirectory(
                    tree, if (pickerMode) pickerPath else folderPath, 0, counts,
                    FolderTransferBudget(MAX_TREE_FILES, MAX_TREE_DEPTH)
                )
                if (!completed) counts[1]++
                reportImport(counts[0], counts[1])
            } catch (e: Exception) { toast("Folder import failed: ${message(e)}") }
            refreshLibrary()
        }
        exportOne = registerForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri ->
            val source = exportFile
            exportFile = null
            if (uri == null || source == null) return@registerForActivityResult
            try {
                val output = contentResolver.openOutputStream(uri, "w") ?: error("Destination unavailable.")
                output.use { out -> FileInputStream(source).use { it.copyTo(out) } }
                toast("Exported ${source.name}")
            } catch (e: Exception) { toast("Export failed: ${message(e)}") }
        }
        exportTree = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
            val source = exportFolder
            exportFolder = null
            if (uri == null || source == null) {
                if (source?.parentFile?.canonicalPath == cacheDir.canonicalPath) source.deleteRecursively()
                return@registerForActivityResult
            }
            try {
                val destination = DocumentFile.fromTreeUri(this, uri) ?: error("Destination unavailable.")
                require(destination.isDirectory) { "Select a destination folder." }
                val count = intArrayOf(0)
                val complete = copyFolder(
                    source, destination, 0, count, FolderTransferBudget(MAX_TREE_FILES, MAX_TREE_DEPTH)
                )
                toast(
                    if (complete) "Exported ${count[0]} file(s)."
                    else "Exported ${count[0]} file(s); remaining items skipped at the safe folder-transfer limit."
                )
            } catch (e: Exception) { toast("Folder export failed: ${message(e)}") }
            finally {
                if (source.parentFile?.canonicalPath == cacheDir.canonicalPath) source.deleteRecursively()
            }
        }
        exportArchive = registerForActivityResult(ActivityResultContracts.CreateDocument("application/zip")) { uri ->
            val files = archiveFiles
            archiveFiles = emptyList()
            if (uri == null || files.isEmpty()) return@registerForActivityResult
            try {
                val output = contentResolver.openOutputStream(uri, "w") ?: error("Destination unavailable.")
                ZipOutputStream(output.buffered()).use { zip ->
                    files.forEach { source ->
                        require(source.isFile && source.exists()) { "${source.name} is no longer available." }
                        val relativePath = store.pathOf(source).replace("\\", "/")
                        zip.putNextEntry(ZipEntry(relativePath))
                        FileInputStream(source).use { it.copyTo(zip) }
                        zip.closeEntry()
                    }
                }
                toast("Exported ${files.size} file(s) as a ZIP archive.")
            } catch (e: Exception) { toast("ZIP export failed: ${message(e)}") }
        }
    }

    override fun onDestroy() {
        documentViewer?.dispose()
        documentViewer = null
        clearCustomerUploadPdfPreviews()
        window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)
        super.onDestroy()
    }

    private fun completeLocalNetworkPermissionRequest(granted: Boolean) {
        localNetworkPermissionRequestInProgress = false
        val response = if (granted) "localNetworkAccess:granted" else "localNetworkAccess:denied"
        val replies = pendingLocalNetworkPermissionReplies.toList()
        pendingLocalNetworkPermissionReplies.clear()
        replies.forEach { reply ->
            runCatching { reply.postMessage(response) }
        }
    }

    private fun requestLocalNetworkAccess(replyProxy: JavaScriptReplyProxy) {
        val permission = LocalNetworkPermissionPolicy.permissionForDevice(
            Build.VERSION.SDK_INT,
            applicationInfo.targetSdkVersion
        )
        if (permission == null) {
            replyProxy.postMessage("localNetworkAccess:not_required")
            return
        }
        if (checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED) {
            replyProxy.postMessage("localNetworkAccess:granted")
            return
        }

        pendingLocalNetworkPermissionReplies.add(replyProxy)
        if (localNetworkPermissionRequestInProgress) return
        localNetworkPermissionRequestInProgress = true

        val preferences = getSharedPreferences("smartprint.local-network", MODE_PRIVATE)
        val requestedBefore = preferences.getBoolean("nearbyWifiPermissionRequested", false)
        val shouldExplain = shouldShowRequestPermissionRationale(permission)
        if (requestedBefore && !shouldExplain) {
            // Android no longer offers the runtime prompt after repeated denial. The
            // customer can grant Nearby devices from this app's Android settings.
            completeLocalNetworkPermissionRequest(false)
            return
        }

        val launchPermissionRequest = {
            preferences.edit().putBoolean("nearbyWifiPermissionRequested", true).apply()
            localNetworkPermissionLauncher.launch(permission)
        }
        if (!requestedBefore || shouldExplain) {
            AlertDialog.Builder(this)
                .setTitle("Connect to shop Wi-Fi")
                .setMessage("Allow SmartPrint to check the shop computer and send selected documents directly over the shop Wi-Fi. Cloud-assisted upload remains available.")
                .setPositiveButton("Continue") { _, _ -> launchPermissionRequest() }
                .setNegativeButton("Not now") { _, _ -> completeLocalNetworkPermissionRequest(false) }
                .setOnCancelListener { completeLocalNetworkPermissionRequest(false) }
                .show()
        } else {
            launchPermissionRequest()
        }
    }

    private fun buildLayout() {
        window.statusBarColor = getColor(R.color.qp_surface)
        window.navigationBarColor = getColor(R.color.qp_surface)
        val backdrop = SmartPrintGlass.backdrop(this)
        val root = FrameLayout(this).apply {
            id = R.id.smartprint_native_root
            clipChildren = false
            clipToPadding = false
            background = backdrop
        }

        val blurTarget = BlurTarget(this)
        content = FrameLayout(this).apply {
            clipToPadding = false
            setPadding(0, dp(64), 0, dp(76))
            setBackgroundColor(android.graphics.Color.TRANSPARENT)
        }
        blurTarget.addView(content, FrameLayout.LayoutParams(-1, -1))
        root.addView(blurTarget, FrameLayout.LayoutParams(-1, -1))

        toolbar = MaterialToolbar(this).apply {
            title = "smartprint"
            subtitle = "BY QUANTAEDGE · HOME"
            setTitleTextColor(getColor(R.color.qp_text))
            setSubtitleTextColor(getColor(R.color.qp_muted))
            setTitleTextAppearance(this@MainActivity, R.style.TextAppearance_SmartPrint_Toolbar)
            setSubtitleTextAppearance(this@MainActivity, R.style.TextAppearance_SmartPrint_ToolbarSubtitle)
            setBackgroundColor(android.graphics.Color.TRANSPARENT)
            elevation = 0f
            menu.add(Menu.NONE, SWITCH_SHOP, Menu.NONE, "Manage connected shop").apply {
                setIcon(android.R.drawable.ic_menu_manage)
                setContentDescription("Manage connected shop")
                setTooltipText("Open, switch or disconnect this shop")
                setShowAsAction(android.view.MenuItem.SHOW_AS_ACTION_NEVER)
                isVisible = false
                setOnMenuItemClickListener { showShopManager(); true }
            }
            menu.add(Menu.NONE, OPEN_WITH, Menu.NONE, "Open with another app").apply {
                setShowAsAction(android.view.MenuItem.SHOW_AS_ACTION_NEVER)
                isVisible = false
                setOnMenuItemClickListener {
                    viewingDocument?.let(::openDocumentExternally)
                    true
                }
            }
            accountAction = ImageView(this@MainActivity).apply {
                contentDescription = "Account"
                setImageResource(R.drawable.ic_account)
                imageTintList = ColorStateList.valueOf(this@MainActivity.getColor(R.color.qp_text))
                scaleType = ImageView.ScaleType.CENTER_INSIDE
                setPadding(dp(12), dp(12), dp(12), dp(12))
                val selectableBackground = android.util.TypedValue()
                if (this@MainActivity.theme.resolveAttribute(
                        android.R.attr.selectableItemBackgroundBorderless,
                        selectableBackground,
                        true
                    )
                ) setBackgroundResource(selectableBackground.resourceId)
                isClickable = true
                isFocusable = true
                setOnClickListener { showProfile() }
            }
            addView(accountAction, androidx.appcompat.widget.Toolbar.LayoutParams(
                dp(44), dp(44), Gravity.END or Gravity.CENTER_VERTICAL
            ))
        }

        val topGlass = BlurView(this).apply {
            foreground = SmartPrintGlass.edge(this@MainActivity)
            addView(toolbar, FrameLayout.LayoutParams(-1, dp(56), Gravity.BOTTOM))
        }
        root.addView(topGlass, FrameLayout.LayoutParams(-1, dp(64), Gravity.TOP))

        val useRail = ResponsiveLayoutPolicy.useNavigationRail(resources.configuration.orientation)
        navigation = if (useRail) NavigationRailView(this) else BottomNavigationView(this)
        navigation.apply {
            menu.add(Menu.NONE, HOME, 0, "Home").apply {
                setIcon(R.drawable.ic_nav_home); setContentDescription("Home")
            }
            menu.add(Menu.NONE, FILES, 1, "Files").apply {
                setIcon(R.drawable.ic_nav_files); setContentDescription("My Files")
            }
            menu.add(Menu.NONE, SCAN_PRINT, 2, "Print").apply {
                setIcon(R.drawable.ic_nav_scan_print); setContentDescription("Print at a shop")
            }
            menu.add(Menu.NONE, ACTIVITY, 3, "Activity").apply {
                setIcon(R.drawable.ic_nav_activity); setContentDescription("Activity")
            }
            labelVisibilityMode = if (useRail || ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(
                    resources.configuration.screenWidthDp
                )) NavigationBarView.LABEL_VISIBILITY_LABELED
                else NavigationBarView.LABEL_VISIBILITY_SELECTED
            (this as? BottomNavigationView)?.setItemHorizontalTranslationEnabled(false)
            setItemTextAppearanceActive(R.style.TextAppearance_SmartPrint_Navigation)
            setItemTextAppearanceInactive(R.style.TextAppearance_SmartPrint_Navigation)
            itemIconSize = dp(21)
            setItemPaddingTop(dp(4))
            setItemPaddingBottom(dp(4))
            minimumHeight = dp(if (useRail) 80 else 64)
            backgroundTintList = ColorStateList.valueOf(android.graphics.Color.TRANSPARENT)
            setBackgroundColor(android.graphics.Color.TRANSPARENT)
            itemIconTintList = ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
                intArrayOf(getColor(R.color.qp_primary), getColor(R.color.qp_muted))
            )
            itemTextColor = itemIconTintList
            itemRippleColor = ColorStateList.valueOf(getColor(R.color.qp_soft))
            elevation = 0f
            setOnItemSelectedListener { item ->
                when (item.itemId) {
                    HOME -> { showHome(); true }
                    FILES -> { showFiles(); true }
                    SCAN_PRINT -> { showScanPrint(); true }
                    ACTIVITY -> { showActivity(); true }
                    else -> false
                }
            }
        }

        if (!useRail) {
            bottomNavigationContent = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER
                setPadding(dp(4), 0, dp(4), 0)
                clipChildren = false
                clipToPadding = false
            }
            bottomNavigationItemViews.clear()
            addBottomNavigationItem(HOME, "Home", "Home", R.drawable.ic_nav_home)
            addBottomNavigationItem(FILES, "Files", "My Files", R.drawable.ic_nav_files)
            addBottomNavigationItem(SCAN_PRINT, "Print", "Print at a shop", R.drawable.ic_nav_scan_print)
            addBottomNavigationItem(ACTIVITY, "Activity", "Activity", R.drawable.ic_nav_activity)
            updateBottomNavigationSelection(navigation.selectedItemId)
        }
        bottomGlass = BlurView(this).apply {
            foreground = SmartPrintGlass.edge(this@MainActivity)
            if (!useRail) addView(bottomNavigationContent, FrameLayout.LayoutParams(-1, dp(66), Gravity.BOTTOM))
        }
        if (useRail) {
            root.addView(navigation, FrameLayout.LayoutParams(dp(80), -1, Gravity.START or Gravity.TOP).apply {
                topMargin = dp(64)
            })
        } else {
            root.addView(bottomGlass, FrameLayout.LayoutParams(-1, dp(76), Gravity.BOTTOM))
        }

        ViewCompat.setOnApplyWindowInsetsListener(root) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
        setContentView(root)
        SmartPrintGlass.bindBlur(topGlass, blurTarget, this, R.color.qp_glass_top_overlay, backdrop)
        SmartPrintGlass.bindBlur(bottomGlass, blurTarget, this, R.color.qp_glass_bottom_overlay, backdrop)
        ViewCompat.requestApplyInsets(root)
    }

    private fun addBottomNavigationItem(destinationId: Int, label: String, description: String, iconResource: Int) {
        val item = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            setPadding(dp(11), 0, dp(11), 0)
            minimumWidth = dp(48)
            minimumHeight = dp(48)
            id = when (destinationId) {
                HOME -> R.id.smartprint_nav_home
                FILES -> R.id.smartprint_nav_files
                else -> View.NO_ID
            }
            contentDescription = description
            isClickable = true
            isFocusable = true
            val selectable = android.util.TypedValue()
            if (this@MainActivity.theme.resolveAttribute(android.R.attr.selectableItemBackgroundBorderless, selectable, true)) {
                foreground = this@MainActivity.getDrawable(selectable.resourceId)
            }
            setOnClickListener { navigation.selectedItemId = destinationId }
        }
        val icon = ImageView(this).apply {
            setImageResource(iconResource)
            scaleType = ImageView.ScaleType.CENTER_INSIDE
        }
        val title = TextView(this).apply {
            text = label
            textSize = 11f
            includeFontPadding = false
            setTypeface(typeface, android.graphics.Typeface.BOLD)
            setPadding(dp(7), 0, 0, 0)
            maxLines = 1
        }
        item.addView(icon, LinearLayout.LayoutParams(dp(21), dp(21)))
        item.addView(title, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        bottomNavigationItemViews[destinationId] = Triple(item, icon, title)
        bottomNavigationContent.addView(item, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(52)))
    }

    private fun updateBottomNavigationSelection(selectedId: Int) {
        if (!::bottomNavigationContent.isInitialized) return
        val showEveryLabel = ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(resources.configuration.screenWidthDp)
        bottomNavigationItemViews.forEach { (id, views) ->
            val (item, icon, title) = views
            val selected = id == selectedId
            item.isSelected = selected
            item.background = if (selected) roundedDrawable(getColor(R.color.qp_soft), dp(16))
                else android.graphics.drawable.ColorDrawable(android.graphics.Color.TRANSPARENT)
            icon.imageTintList = ColorStateList.valueOf(getColor(if (selected) R.color.qp_primary else R.color.qp_muted))
            title.visibility = if (showEveryLabel || selected) View.VISIBLE else View.GONE
            title.setTextColor(getColor(if (selected) R.color.qp_primary else R.color.qp_muted))
        }
    }

    private fun setToolbarPage(title: String) {
        toolbar.title = "SmartPrint"
        toolbar.subtitle = "by QuantaEdge  ·  " + title.trim()
    }

    private fun setToolbarBackAction(description: String, action: () -> Unit) {
        toolbar.navigationIcon = getDrawable(R.drawable.ic_arrow_back)
        toolbar.setNavigationIconTint(getColor(R.color.qp_text))
        toolbar.navigationContentDescription = description
        toolbar.setNavigationOnClickListener { action() }
    }

    private fun clearLibrarySelection() {
        librarySelectionMode = false
        selectedFilePaths.clear()
    }

    private fun setBottomNavigationVisible(visible: Boolean) {
        val visibility = if (visible) View.VISIBLE else View.GONE
        val rail = navigation is NavigationRailView
        navigation.visibility = if (rail) visibility else View.GONE
        if (::bottomNavigationContent.isInitialized) bottomNavigationContent.visibility = if (visible && !rail) View.VISIBLE else View.GONE
        if (::bottomGlass.isInitialized) bottomGlass.visibility = if (visible && !rail) View.VISIBLE else View.GONE
        if (::content.isInitialized) content.setPadding(
            if (visible && rail) dp(80) else 0,
            dp(64),
            0,
            if (visible && !rail) dp(76) else dp(8)
        )
    }

    private fun resetToolbar(title: String) {
        profileVisible = false
        setToolbarPage(title)
        toolbar.navigationIcon = null
        toolbar.navigationContentDescription = null
        toolbar.setNavigationOnClickListener(null)
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = portalUrl != null
        toolbar.menu.findItem(OPEN_WITH)?.isVisible = false
        accountAction.visibility = View.VISIBLE
        setBottomNavigationVisible(true)
    }

    private fun showProfile() {
        clearLibrarySelection()
        pickerMode = false
        if (!profileVisible) profileReturnDestination = navigation.selectedItemId
        profileVisible = true
        setToolbarPage("Account")
        setToolbarBackAction("Back", ::returnFromProfile)
        accountAction.visibility = View.GONE
        setBottomNavigationVisible(false)
        content.removeAllViews()
        content.addView(accountView(), frameParams())
    }

    private fun returnFromProfile() {
        if (!profileVisible) return
        profileVisible = false
        when (profileReturnDestination) {
            FILES -> showFiles()
            SCAN_PRINT -> if (portalUrl != null) showPrint() else showScanPrint()
            ACTIVITY -> showActivity()
            else -> showHome()
        }
    }

    private fun accountView(): View {
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(18), dp(18), dp(28))
        }
        scroll.addView(column)
        val auth = customerAccountStore.read()

        val appearanceCard = card()
        val appearanceContent = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(14), dp(13), dp(14), dp(13))
        }
        appearanceContent.addView(text("Appearance", 15, R.color.qp_text, true))
        appearanceContent.addView(text(
            "Choose how SmartPrint looks on this device. This setting is kept when you sign out.",
            11, R.color.qp_muted, false
        ).apply { setPadding(0, dp(4), 0, dp(12)) })
        val currentTheme = SmartPrintThemePreference.read(this)
        val themeChoices = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
        }
        themeChoices.addView(button("Light theme", currentTheme == SmartPrintTheme.LIGHT).apply {
            setOnClickListener { chooseTheme(SmartPrintTheme.LIGHT) }
        }, LinearLayout.LayoutParams(0, dp(42), 1f))
        themeChoices.addView(button("Dark theme", currentTheme == SmartPrintTheme.DARK).apply {
            setOnClickListener { chooseTheme(SmartPrintTheme.DARK) }
        }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(8) })
        appearanceContent.addView(themeChoices)
        appearanceCard.addView(appearanceContent)
        column.addView(appearanceCard, linearParams().apply { bottomMargin = dp(16) })

        column.addView(text(
            if (auth == null) "Guest access is ready" else "Smart Print customer",
            12, R.color.qp_muted, false
        ).apply { setPadding(0, dp(4), 0, dp(16)) })

        if (auth != null) {
            val profileCard = card()
            val profile = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(14), dp(14), dp(14), dp(14))
            }
            val avatar = TextView(this).apply {
                text = auth.displayName.trim().take(1).uppercase(Locale.ROOT).ifBlank { "S" }
                textSize = 19f
                setTypeface(typeface, android.graphics.Typeface.BOLD)
                gravity = Gravity.CENTER
                setTextColor(getColor(R.color.qp_primary_content))
                background = roundedDrawable(getColor(R.color.qp_primary), dp(18))
            }
            profile.addView(avatar, LinearLayout.LayoutParams(dp(48), dp(48)))
            val details = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(12), 0, 0, 0)
            }
            details.addView(text(auth.displayName.ifBlank { "Smart Print customer" }, 16, R.color.qp_text, true))
            details.addView(text(auth.email, 12, R.color.qp_muted, false).apply {
                setPadding(0, dp(3), 0, 0)
            })
            profile.addView(details, LinearLayout.LayoutParams(0, -2, 1f))
            profileCard.addView(profile)
            column.addView(profileCard, linearParams())
            column.addView(button("Print history", true).apply {
                setOnClickListener { showActivity() }
            }, linearParams().apply { topMargin = dp(12) })
            column.addView(button("Sign out", false).apply {
                setOnClickListener {
                    showConfirmSheet("Sign out?", "Local files stay on this phone.", "Sign out") {
                        signOutCustomer()
                    }
                }
            }, linearParams().apply { topMargin = dp(8) })
            return scroll
        }

        val modeRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        modeRow.addView(button("Sign in", !accountCreateMode).apply {
            setOnClickListener { accountCreateMode = false; showProfile() }
        }, LinearLayout.LayoutParams(0, dp(42), 1f))
        modeRow.addView(button("Create account", accountCreateMode).apply {
            setOnClickListener { accountCreateMode = true; showProfile() }
        }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(8) })
        column.addView(modeRow, linearParams().apply { bottomMargin = dp(12) })

        val formCard = card()
        val form = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(14), dp(14), dp(14), dp(14))
        }
        val displayName = EditText(this).apply {
            hint = "Name"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_WORDS
            visibility = if (accountCreateMode) View.VISIBLE else View.GONE
            maxLines = 1
            setTextColor(getColor(R.color.qp_text))
            setHintTextColor(getColor(R.color.qp_muted))
            background = roundedDrawable(getColor(R.color.qp_surface), dp(10), getColor(R.color.qp_outline))
            setPadding(dp(12), dp(11), dp(12), dp(11))
        }
        val email = EditText(this).apply {
            hint = "Email address"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_EMAIL_ADDRESS
            maxLines = 1
            setTextColor(getColor(R.color.qp_text))
            setHintTextColor(getColor(R.color.qp_muted))
            background = roundedDrawable(getColor(R.color.qp_surface), dp(10), getColor(R.color.qp_outline))
            setPadding(dp(12), dp(11), dp(12), dp(11))
        }
        val password = EditText(this).apply {
            hint = if (accountCreateMode) "Password · 12+ chars" else "Password"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            maxLines = 1
            setTextColor(getColor(R.color.qp_text))
            setHintTextColor(getColor(R.color.qp_muted))
            background = roundedDrawable(getColor(R.color.qp_surface), dp(10), getColor(R.color.qp_outline))
            setPadding(dp(12), dp(11), dp(12), dp(11))
        }
        if (accountCreateMode) form.addView(displayName, linearParams().apply { bottomMargin = dp(8) })
        form.addView(email, linearParams().apply { bottomMargin = dp(8) })
        form.addView(password, linearParams())
        form.addView(button(if (accountCreateMode) "Create account" else "Sign in", true).apply {
            setOnClickListener {
                val name = displayName.text.toString().trim()
                val emailValue = email.text.toString().trim()
                val passwordValue = password.text.toString()
                if (emailValue.isBlank() || passwordValue.isBlank() || (accountCreateMode && name.isBlank())) {
                    toast("Complete the required fields.")
                    return@setOnClickListener
                }
                authenticateCustomer(name, emailValue, passwordValue, accountCreateMode)
            }
        }, linearParams().apply { topMargin = dp(12) })
        formCard.addView(form)
        column.addView(formCard, linearParams())
        if (accountCreateMode) {
            column.addView(text("12+ chars · Upper · Lower · Number · Symbol", 11, R.color.qp_muted, false)
                .apply { setPadding(dp(3), dp(9), dp(3), 0) })
        }
        return scroll
    }

    private fun chooseTheme(theme: SmartPrintTheme) {
        // Keep an embedded customer WebView in sync without navigating away from
        // an in-progress upload or print review.
        val value = theme.storedValue
        webView?.evaluateJavascript(
            """
                (() => {
                  try { localStorage.setItem('qrtoprint.theme', '$value'); } catch {}
                  document.documentElement.dataset.theme = '$value';
                  document.documentElement.style.colorScheme = '$value';
                  window.dispatchEvent(new CustomEvent('smartprint-theme-change', { detail: '$value' }));
                })();
            """.trimIndent(),
            null
        )
        SmartPrintThemePreference.save(this, theme)
    }

    private fun accountServiceUrl(): String = BuildConfig.SERVICE_BASE_URL

    private fun authenticateCustomer(name: String, email: String, password: String, create: Boolean) {
        toast(if (create) "Creating your Smart Print account…" else "Signing in…")
        Thread {
            try {
                val auth = if (create) {
                    CustomerAccountClient.register(accountServiceUrl(), name, email, password)
                } else {
                    CustomerAccountClient.signIn(accountServiceUrl(), email, password)
                }
                val saved = auth.copy(displayName = if (create) name else auth.displayName.ifBlank { email.substringBefore('@') })
                customerAccountStore.save(saved)
                runOnUiThread {
                    accountCreateMode = false
                    showProfile()
                    toast(if (create) "Your Smart Print account is ready." else "You're signed in.")
                }
            } catch (e: Exception) {
                runOnUiThread { toast("Account request failed: ${message(e)}") }
            }
        }.start()
    }

    private fun signOutCustomer() {
        val auth = customerAccountStore.read()
        customerAccountStore.clear()
        showProfile()
        if (auth != null) Thread {
            try { CustomerAccountClient.logout(accountServiceUrl(), auth) } catch (_: Exception) { }
        }.start()
        toast("Signed out. Your local documents are unchanged.")
    }

    private fun showHome() {
        idStudioScreenOpen = false
        privateIdViewerOpen = false
        documentViewerReturnToIdStudio = false
        applySensitiveWindowProtection()
        clearLibrarySelection()
        pickerMode = false
        resetToolbar("Home")
        navigation.menu.findItem(HOME).isChecked = true
        updateBottomNavigationSelection(HOME)
        content.removeAllViews()
        content.addView(homeView(), frameParams())
    }

    private fun homeView(): View {
        val scroll = ScrollView(this).apply {
            isFillViewport = true
            clipToPadding = false
        }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(15), dp(18), dp(24))
        }
        scroll.addView(column)

        val recentFiles = store.root.walkTopDown().filter { it.isFile }.toList()
        val used = android.text.format.Formatter.formatShortFileSize(this, store.storageUsageBytes())
        val heading = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        heading.addView(text("Your space", 23, R.color.qp_text, true).apply {
            id = R.id.smartprint_home_heading
        })
        heading.addView(text("${recentFiles.size} files · $used used", 12, R.color.qp_muted, false).apply {
            setPadding(0, dp(3), 0, 0)
        })
        column.addView(heading, linearParams().apply { bottomMargin = dp(16) })

        val quickActions = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        quickActions.addView(button("Scan document", true).apply {
            icon = getDrawable(R.drawable.ic_document_scan)
            iconTint = ColorStateList.valueOf(getColor(android.R.color.white))
            iconPadding = dp(8)
            iconGravity = MaterialButton.ICON_GRAVITY_TEXT_START
            setOnClickListener { startDocumentScan() }
        }, LinearLayout.LayoutParams(-1, dp(48)))
        quickActions.addView(button("Prepare ID card", false).apply {
            icon = getDrawable(R.drawable.ic_file)
            iconTint = ColorStateList.valueOf(getColor(R.color.qp_primary))
            iconPadding = dp(8)
            iconGravity = MaterialButton.ICON_GRAVITY_TEXT_START
            setOnClickListener { showIdStudioHome() }
        }, LinearLayout.LayoutParams(-1, dp(48)).apply { topMargin = dp(9) })
        quickActions.addView(button("Choose from phone", false).apply {
            icon = getDrawable(R.drawable.ic_file)
            iconTint = ColorStateList.valueOf(getColor(R.color.qp_primary))
            iconPadding = dp(8)
            iconGravity = MaterialButton.ICON_GRAVITY_TEXT_START
            setOnClickListener {
                folderPath = ""
                pendingImportDestinationPath = ""
                importFiles.launch(arrayOf("*/*"))
                navigation.selectedItemId = FILES
            }
        }, LinearLayout.LayoutParams(-1, dp(48)).apply { topMargin = dp(9) })
        column.addView(quickActions, linearParams())

        val shopCard = card().apply {
            strokeWidth = dp(1)
            setOnClickListener { if (portalUrl != null) showShopManager() else showScanPrint() }
        }
        val shopRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(13), dp(12), dp(13), dp(12))
        }
        val shopGlyph = ImageView(this).apply {
            setImageResource(R.drawable.ic_store)
            imageTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
            scaleType = ImageView.ScaleType.CENTER_CROP
            setPadding(dp(10), dp(10), dp(10), dp(10))
            background = roundedDrawable(getColor(R.color.qp_soft), dp(13))
            contentDescription = if (portalUrl == null) "Shop connection" else "Connected shop icon"
        }
        if (portalUrl != null) loadShopLogo(shopGlyph)
        shopRow.addView(shopGlyph, LinearLayout.LayoutParams(dp(46), dp(46)))
        val shopCopy = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(11), 0, 0, 0)
        }
        shopCopy.addView(text(
            if (portalUrl == null) "Print at a shop" else shopDisplayName ?: "Connected shop",
            14, R.color.qp_text, true
        ).apply { maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END })
        shopCopy.addView(text(
            if (portalUrl == null) "Scan a shop QR" else "Connected · tap to manage",
            12, R.color.qp_muted, false
        ).apply { setPadding(0, dp(2), 0, 0) })
        shopRow.addView(shopCopy, LinearLayout.LayoutParams(0, -2, 1f))
        shopRow.addView(text(if (portalUrl == null) "›" else "⋮", 22, R.color.qp_muted, false))
        shopCard.addView(shopRow)
        column.addView(shopCard, linearParams().apply { topMargin = dp(12); bottomMargin = dp(20) })

        val recentHeader = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        recentHeader.addView(text("Recent", 17, R.color.qp_text, true),
            LinearLayout.LayoutParams(0, -2, 1f))
        recentHeader.addView(text("See all", 12, R.color.qp_primary, true).apply {
            setOnClickListener { showFiles() }
            isClickable = true
        })
        column.addView(recentHeader, linearParams().apply { bottomMargin = dp(8) })

        val recent = recentFiles.sortedByDescending { it.lastModified() }.take(6)
        if (recent.isEmpty()) {
            column.addView(text("No files yet", 13, R.color.qp_muted, false).apply {
                setPadding(dp(2), dp(16), 0, dp(16))
            })
        } else {
            recent.forEach { file ->
                column.addView(entryView(file, false, showActions = false), linearParams().apply { bottomMargin = dp(4) })
            }
        }
        return scroll
    }

    private fun showScanPrint() {
        clearLibrarySelection()
        pickerMode = false
        if (portalUrl != null) { showPrint(); return }
        resetToolbar("Print")
        navigation.menu.findItem(SCAN_PRINT).isChecked = true
        updateBottomNavigationSelection(SCAN_PRINT)
        content.removeAllViews()
        content.addView(scanPrintView(), frameParams())
    }

    private fun scanPrintView(): View {
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setBackgroundColor(android.graphics.Color.TRANSPARENT)
            setPadding(dp(24), dp(34), dp(24), dp(28))
        }
        val icon = ImageView(this).apply {
            setImageResource(R.drawable.ic_nav_scan_print)
            imageTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
            setPadding(dp(20), dp(20), dp(20), dp(20))
            background = roundedDrawable(getColor(R.color.qp_soft), dp(24))
        }
        column.addView(icon, LinearLayout.LayoutParams(dp(88), dp(88)))
        column.addView(text("Connect to a shop", 22, R.color.qp_text, true).apply {
            gravity = Gravity.CENTER
            setPadding(0, dp(18), 0, dp(6))
        })
        column.addView(text("Scan the counter QR code.", 13, R.color.qp_muted, false).apply {
            gravity = Gravity.CENTER
        })
        column.addView(button("Scan QR code", true).apply {
            setOnClickListener { scanQr() }
        }, LinearLayout.LayoutParams(-1, dp(48)).apply { topMargin = dp(22) })
        column.addView(text(
            "Already on the shop network? You can verify phone access without connecting your shop account.",
            12, R.color.qp_muted, false
        ).apply {
            gravity = Gravity.CENTER
            setPadding(0, dp(18), 0, dp(4))
        })
        column.addView(button("Check shop Wi-Fi", false).apply {
            setOnClickListener { scanQr(QrScanPurpose.WIFI_READINESS) }
        }, LinearLayout.LayoutParams(-1, dp(44)).apply { topMargin = dp(4) })
        column.addView(button("Open My Files", false).apply {
            setOnClickListener { showFiles() }
        }, LinearLayout.LayoutParams(-1, dp(44)).apply { topMargin = dp(8) })
        return column
    }

    private fun showActivity() {
        clearLibrarySelection()
        pickerMode = false
        resetToolbar("Activity")
        navigation.menu.findItem(ACTIVITY).isChecked = true
        updateBottomNavigationSelection(ACTIVITY)
        content.removeAllViews()
        val scroll = ScrollView(this).apply {
            isFillViewport = true
            setBackgroundColor(android.graphics.Color.TRANSPARENT)
        }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(android.graphics.Color.TRANSPARENT)
            setPadding(dp(18), dp(16), dp(18), dp(24))
        }
        scroll.addView(column)
        column.addView(text("Print history", 22, R.color.qp_text, true))
        val auth = customerAccountStore.read()
        if (auth == null) {
            column.addView(text("Sign in to sync jobs.", 13, R.color.qp_muted, false)
                .apply { setPadding(0, dp(5), 0, dp(16)) })
            column.addView(button("Sign in", true).apply {
                setOnClickListener { showProfile() }
            }, linearParams())
        } else {
            column.addView(text(auth.email, 12, R.color.qp_muted, false)
                .apply { setPadding(0, dp(4), 0, dp(14)) })
            val history = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            history.addView(text("Loading…", 12, R.color.qp_muted, false))
            column.addView(history, linearParams())
            loadCustomerHistory(history)
        }
        content.addView(scroll, frameParams())
    }

    private fun activityStatusLabel(status: String): String =
        CustomerOrderStatusPresentation.activityStatusLabel(status)

    private fun loadCustomerHistory(container: LinearLayout) {
        Thread {
            try {
                var auth = customerAccountStore.read() ?: error("Sign in to view history.")
                fun fetchHistory(): List<CustomerPrintHistoryItem> {
                    if (CustomerAccountClient.accessTokenIsExpired(auth)) {
                        auth = CustomerAccountClient.refresh(accountServiceUrl(), auth)
                        customerAccountStore.save(auth)
                    }
                    return try {
                        CustomerAccountClient.history(accountServiceUrl(), auth.accessToken)
                    } catch (error: CustomerApiException) {
                        if (error.statusCode != 401) throw error
                        auth = CustomerAccountClient.refresh(accountServiceUrl(), auth)
                        customerAccountStore.save(auth)
                        CustomerAccountClient.history(accountServiceUrl(), auth.accessToken)
                    }
                }
                val jobs = fetchHistory()
                runOnUiThread {
                    if (navigation.selectedItemId != ACTIVITY || isFinishing) return@runOnUiThread
                    container.removeAllViews()
                    if (jobs.isEmpty()) {
                        container.addView(text("No jobs yet", 13, R.color.qp_muted, false)
                            .apply { setPadding(0, dp(14), 0, dp(14)) })
                        return@runOnUiThread
                    }

                    val filters = LinearLayout(this).apply {
                        orientation = LinearLayout.VERTICAL
                        setPadding(0, 0, 0, dp(12))
                    }
                    val query = EditText(this).apply {
                        hint = "Search batch, order, file or shop name"
                        setSingleLine(true)
                        inputType = InputType.TYPE_CLASS_TEXT
                        textSize = 12f
                        setTextColor(getColor(R.color.qp_text))
                        setHintTextColor(getColor(R.color.qp_muted))
                        setPadding(dp(12), 0, dp(12), 0)
                        background = roundedDrawable(getColor(R.color.qp_card), 10, getColor(R.color.qp_outline))
                    }
                    val filterActions = LinearLayout(this).apply {
                        orientation = LinearLayout.HORIZONTAL
                        gravity = Gravity.CENTER_VERTICAL
                    }
                    var selectedDate: String? = null
                    val dateButton = button("Any date", false)
                    val clearButton = button("Clear", false)
                    val results = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
                    val grouped = jobs.groupBy { it.batchId?.takeIf(String::isNotBlank) ?: it.id }
                        .values
                        .sortedByDescending { batch -> batch.maxOfOrNull { runCatching { Instant.parse(it.createdUtc).toEpochMilli() }.getOrDefault(0L) } ?: 0L }

                    fun renderCards() {
                        results.removeAllViews()
                        val needle = query.text.toString().trim().lowercase(Locale.ROOT)
                        val matches = grouped.filter { batch ->
                            val dateMatch = selectedDate == null || batch.any { job ->
                                runCatching { Instant.parse(job.createdUtc).atZone(ZoneId.systemDefault()).toLocalDate().toString() == selectedDate }.getOrDefault(false)
                            }
                            val searchMatch = needle.isBlank() || batch.any { job ->
                                listOf(job.batchId.orEmpty(), job.batchName.orEmpty(), job.batchGroupName.orEmpty(),
                                    job.jobNumber, job.originalFileName, job.shopName)
                                    .any { it.lowercase(Locale.ROOT).contains(needle) }
                            }
                            dateMatch && searchMatch
                        }
                        if (matches.isEmpty()) {
                            results.addView(text("No matching batches", 12, R.color.qp_muted, false)
                                .apply { gravity = Gravity.CENTER; setPadding(0, dp(20), 0, dp(20)) })
                            return
                        }
                        matches.forEach { batch ->
                            val item = card()
                            val body = LinearLayout(this).apply {
                                orientation = LinearLayout.VERTICAL
                                setPadding(dp(13), dp(12), dp(13), dp(12))
                            }
                            val status = CustomerOrderStatusPresentation.batchSummaryStatus(batch.map { it.status })
                            val tagPalette = when (status.uppercase(Locale.ROOT)) {
                                "COMPLETED", "PDF_EXPORTED" -> if ((resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES)
                                    0xFF173D2E.toInt() to 0xFF9BE5B8.toInt() else 0xFFE5F7EC.toInt() to 0xFF20774D.toInt()
                                "REJECTED", "PAYMENT_REJECTED", "REFUNDED", "CANCELLED", "FAILED" -> if ((resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES)
                                    0xFF49232C.toInt() to 0xFFFFA8B2.toInt() else 0xFFFCE8EB.toInt() to 0xFFAB3347.toInt()
                                "PRINTING", "PRINT_SUBMITTING", "SPOOLER_ACCEPTED", "LOCAL_QUEUE_READY" -> if ((resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES)
                                    0xFF1B3550.toInt() to 0xFF9DD0FF.toInt() else 0xFFE6F2FF.toInt() to 0xFF275F93.toInt()
                                else -> if ((resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES)
                                    0xFF3B315B.toInt() to 0xFFD2C7FF.toInt() else 0xFFF0EDFF.toInt() to 0xFF5B43B7.toInt()
                            }
                            val top = LinearLayout(this).apply {
                                orientation = LinearLayout.HORIZONTAL
                                gravity = Gravity.CENTER_VERTICAL
                            }
                            val identity = batch.first()
                            val batchTitle = identity.batchName?.takeIf(String::isNotBlank)
                                ?: identity.batchGroupName?.takeIf(String::isNotBlank)
                                ?: identity.batchId?.let { "Batch · " + it.take(8).uppercase(Locale.ROOT) }
                                ?: "Order · " + identity.jobNumber
                            top.addView(text(batchTitle, 13, R.color.qp_text, true), LinearLayout.LayoutParams(0, -2, 1f))
                            top.addView(text(activityStatusLabel(status), 9, R.color.qp_primary, true).apply {
                                setPadding(dp(8), dp(5), dp(8), dp(5))
                                setTextColor(tagPalette.second)
                                background = roundedDrawable(tagPalette.first, 10)
                            })
                            body.addView(top)
                            val date = runCatching { Instant.parse(identity.createdUtc).atZone(ZoneId.systemDefault())
                                .format(DateTimeFormatter.ofPattern("dd MMM yyyy · hh:mm a", Locale.ROOT)) }
                                .getOrDefault("Date unavailable")
                            body.addView(text(identity.shopName + " · " + date, 10, R.color.qp_muted, false)
                                .apply { setPadding(0, dp(5), 0, dp(5)) })
                            body.addView(text(batch.size.toString() + " file(s) · " +
                                batch.sumOf { it.pageCount } + " pages", 10, R.color.qp_muted, false))
                            val photoLayouts = batch.filter { it.printKind == "PHOTO" }
                            if (photoLayouts.isNotEmpty()) {
                                val layoutSummary = photoLayouts.joinToString(" · ") { photo ->
                                    val density = photo.photosPerPage?.let { "$it/A4" } ?: "layout not recorded"
                                    "${photo.photoCount} × ${photo.photoSizeCode ?: "photo"} · ${density} · ${photo.pageCount} A4 page(s)"
                                }
                                body.addView(text("Photo layout · " + layoutSummary, 10, R.color.qp_muted, false)
                                    .apply { setPadding(0, dp(5), 0, 0) })
                            }
                            val paid = batch.sumOf { it.paidAmountMinor }
                            val refunded = batch.sumOf { it.refundedAmountMinor }
                            val net = maxOf(0L, paid - refunded)
                            body.addView(text("Paid " + "%.2f".format(Locale.ROOT, paid / 100.0) +
                                " · Returned " + "%.2f".format(Locale.ROOT, refunded / 100.0) +
                                " · Net " + "%.2f".format(Locale.ROOT, net / 100.0) + " " + identity.currency,
                                10, R.color.qp_muted, false).apply { setPadding(0, dp(5), 0, dp(3)) })
                            body.addView(button("View details", false).apply {
                                setOnClickListener { showCustomerJobDetails(batch) }
                            }, linearParams().apply { topMargin = dp(7) })
                            item.addView(body)
                            results.addView(item, linearParams().apply { bottomMargin = dp(8) })
                        }
                    }

                    dateButton.setOnClickListener {
                        val calendar = java.util.Calendar.getInstance()
                        android.app.DatePickerDialog(this, { _, year, month, day ->
                            selectedDate = "%04d-%02d-%02d".format(Locale.ROOT, year, month + 1, day)
                            dateButton.text = "%02d %s %04d".format(Locale.ROOT, day,
                                java.text.DateFormatSymbols.getInstance(Locale.ROOT).shortMonths[month], year)
                            renderCards()
                        }, calendar.get(java.util.Calendar.YEAR), calendar.get(java.util.Calendar.MONTH),
                            calendar.get(java.util.Calendar.DAY_OF_MONTH)).show()
                    }
                    clearButton.setOnClickListener {
                        selectedDate = null
                        query.setText("")
                        dateButton.text = "Any date"
                        renderCards()
                    }
                    filterActions.addView(dateButton, LinearLayout.LayoutParams(0, dp(40), 1f))
                    filterActions.addView(clearButton, LinearLayout.LayoutParams(dp(90), dp(40)).apply { marginStart = dp(8) })
                    filters.addView(query, linearParams().apply { bottomMargin = dp(8) })
                    filters.addView(filterActions, linearParams())
                    container.addView(filters, linearParams())
                    container.addView(results, linearParams())
                    query.addTextChangedListener(object : android.text.TextWatcher {
                        override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
                        override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = renderCards()
                        override fun afterTextChanged(s: android.text.Editable?) = Unit
                    })
                    renderCards()
                }
            } catch (e: Exception) {
                runOnUiThread {
                    if (navigation.selectedItemId != ACTIVITY || isFinishing) return@runOnUiThread
                    container.removeAllViews()
                    container.addView(text("History unavailable.", 13, R.color.qp_muted, false))
                    container.addView(button("Retry", false).apply {
                        setOnClickListener { showActivity() }
                    }, linearParams().apply { topMargin = dp(8) })
                }
            }
        }.start()
    }

    private fun showCustomerJobDetails(batch: List<CustomerPrintHistoryItem>) {
        if (batch.isEmpty()) return
        Thread {
            try {
                var auth = customerAccountStore.read() ?: error("Sign in to view order details.")
                if (CustomerAccountClient.accessTokenIsExpired(auth)) {
                    auth = CustomerAccountClient.refresh(accountServiceUrl(), auth)
                    customerAccountStore.save(auth)
                }
                val detailResponses = batch.map { job ->
                    try {
                        CustomerAccountClient.jobStatus(accountServiceUrl(), auth.accessToken, job.id)
                    } catch (error: CustomerApiException) {
                        if (error.statusCode != 401) throw error
                        auth = CustomerAccountClient.refresh(accountServiceUrl(), auth)
                        customerAccountStore.save(auth)
                        CustomerAccountClient.jobStatus(accountServiceUrl(), auth.accessToken, job.id)
                    }
                }
                runOnUiThread {
                    if (isFinishing) return@runOnUiThread
                    val detailScroll = ScrollView(this).apply { isFillViewport = true }
                    val detailColumn = LinearLayout(this).apply {
                        orientation = LinearLayout.VERTICAL
                        setPadding(dp(16), dp(12), dp(16), dp(18))
                    }
                    val batchPayment = detailResponses.asSequence()
                        .mapNotNull { it.optJSONObject("batchPayment") }
                        .maxByOrNull { it.optString("updatedUtc") }
                    if (batchPayment != null) {
                        val batchPaymentId = batchPayment.optString("id")
                        val allocatedFileCount = detailResponses.count { response ->
                            response.optJSONObject("payment")?.optString("batchPaymentId") == batchPaymentId
                        }.coerceAtLeast(1)
                        detailColumn.addView(text(
                            CustomerOrderStatusPresentation.batchPaymentSummary(
                                batchPayment.optString("status"),
                                batchPayment.optLong("amountMinor"),
                                batchPayment.optString("currency", batch.first().currency),
                                allocatedFileCount
                            ),
                            10, R.color.qp_muted, false
                        ).apply { setPadding(0, 0, 0, dp(8)) })
                    }
                    detailResponses.forEachIndexed { index, response ->
                        val summary = response.optJSONObject("job") ?: JSONObject()
                        val timeline = response.optJSONArray("timeline") ?: org.json.JSONArray()
                        val historyItem = batch[index]
                        detailColumn.addView(text(historyItem.originalFileName, 14, R.color.qp_text, true)
                            .apply { setPadding(0, 0, 0, dp(5)) })
                        detailColumn.addView(text("Order " + historyItem.jobNumber + " · " +
                            activityStatusLabel(summary.optString("status", historyItem.status)), 11, R.color.qp_primary, true))
                        detailColumn.addView(text(summary.optString("pageCount", historyItem.pageCount.toString()) +
                            " pages · " + summary.optInt("copies", historyItem.copies) + " copies · " +
                            summary.optString("paperSize", "A4"), 10, R.color.qp_muted, false)
                            .apply { setPadding(0, dp(4), 0, dp(5)) })
                        if (historyItem.printKind == "PHOTO" || summary.optString("printKind") == "PHOTO") {
                            val density = summary.takeIf { it.has("photosPerPage") && !it.isNull("photosPerPage") }
                                ?.optInt("photosPerPage")
                                ?.takeIf { it > 0 } ?: historyItem.photosPerPage
                            val densityLabel = density?.let { "$it per A4 page" } ?: "Layout density not recorded for this older order"
                            detailColumn.addView(text(
                                "${summary.optInt("photoCount", historyItem.photoCount)} ${summary.optString("photoSizeCode", historyItem.photoSizeCode ?: "photo")} · ${densityLabel} · ${historyItem.pageCount} A4 PDF page(s)",
                                10, R.color.qp_muted, false
                            ).apply { setPadding(0, 0, 0, dp(5)) })
                        }
                        val payment = response.optJSONObject("payment")
                        val paymentStatus = payment?.optString("status")?.takeIf { it.isNotBlank() }
                            ?: historyItem.paymentStatus
                        val allocatedAmountMinor = payment?.optLong("amountMinor") ?: historyItem.paidAmountMinor
                        val refundedAmountMinor = payment?.optLong("refundedAmountMinor") ?: historyItem.refundedAmountMinor
                        val paymentSummary = CustomerOrderStatusPresentation.filePaymentSummary(
                            paymentStatus, allocatedAmountMinor, refundedAmountMinor, historyItem.currency
                        )
                        if (paymentSummary != null) detailColumn.addView(text(
                            paymentSummary, 10, R.color.qp_muted, false
                        ))
                        detailColumn.addView(text("Progress", 11, R.color.qp_text, true)
                            .apply { setPadding(0, dp(8), 0, dp(4)) })
                        for (eventIndex in 0 until timeline.length()) {
                            val event = timeline.optJSONObject(eventIndex) ?: continue
                            val timestamp = runCatching {
                                Instant.parse(event.optString("createdUtc")).atZone(ZoneId.systemDefault())
                                    .format(DateTimeFormatter.ofPattern("dd MMM · hh:mm a", Locale.ROOT))
                            }.getOrDefault("")
                            detailColumn.addView(text(
                                activityStatusLabel(event.optString("newStatus")) + " · " + timestamp,
                                10, R.color.qp_muted, false
                            ).apply { setPadding(dp(8), dp(3), 0, dp(3)) })
                        }
                        if (index < detailResponses.lastIndex) {
                            detailColumn.addView(text(" ", 4, R.color.qp_muted, false).apply { setPadding(0, dp(8), 0, dp(8)) })
                        }
                    }
                    detailScroll.addView(detailColumn)
                    AlertDialog.Builder(this)
                        .setTitle(batch.first().batchName?.takeIf(String::isNotBlank) ?: "Print details")
                        .setView(detailScroll)
                        .setPositiveButton("Close", null)
                        .show()
                }
            } catch (error: Exception) {
                runOnUiThread {
                    if (isFinishing) return@runOnUiThread
                    AlertDialog.Builder(this)
                        .setTitle("Order details unavailable")
                        .setMessage(error.message ?: "Please reconnect and try again.")
                        .setPositiveButton("Close", null)
                        .show()
                }
            }
        }.start()
    }

    private fun showFiles() {
        idStudioScreenOpen = false
        privateIdViewerOpen = false
        applySensitiveWindowProtection()
        pickerMode = false
        editingPdf = null
        resetToolbar("Files")
        navigation.menu.findItem(FILES).isChecked = true
        updateBottomNavigationSelection(FILES)
        content.removeAllViews()
        content.addView(libraryView(false), frameParams())
    }

    private fun showPicker(resetPath: Boolean = true) {
        pickerMode = true
        if (resetPath) pickerPath = ""
        setToolbarPage(when {
            fileChooserSelectFolders -> "Select folders"
            fileChooserAllowsMultiple -> "Select files"
            else -> "Select file"
        })
        setToolbarBackAction("Back", ::cancelPicker)
        setBottomNavigationVisible(false)
        content.removeAllViews()
        content.addView(libraryView(true), frameParams())
    }

    private fun showPrint() {
        idStudioScreenOpen = false
        privateIdViewerOpen = false
        applySensitiveWindowProtection()
        clearLibrarySelection()
        pickerMode = false
        profileVisible = false
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = portalUrl != null
        setToolbarPage("Print")
        toolbar.navigationIcon = null
        toolbar.navigationContentDescription = null
        toolbar.setNavigationOnClickListener(null)
        accountAction.visibility = View.VISIBLE
        setBottomNavigationVisible(true)
        navigation.menu.findItem(SCAN_PRINT).isChecked = true
        updateBottomNavigationSelection(SCAN_PRINT)
        content.removeAllViews()
        val view = ensureWebView()
        (view.parent as? ViewGroup)?.removeView(view)
        content.addView(view, frameParams())
    }

    private fun refreshLibrary(preserveScroll: Boolean = true) {
        val previousScroll = if (preserveScroll) (content.getChildAt(0) as? ScrollView)?.scrollY ?: 0 else 0
        if (pickerMode) showPicker(resetPath = false) else showFiles()
        if (preserveScroll) {
            content.post {
                (content.getChildAt(0) as? ScrollView)?.scrollTo(0, previousScroll)
            }
        }
    }

    private fun folderActionTile(
        iconResource: Int,
        label: String,
        description: String,
        action: () -> Unit
    ): View {
        val tile = MaterialCardView(this).apply {
            radius = dp(12).toFloat()
            cardElevation = 0f
            strokeWidth = dp(1)
            strokeColor = getColor(R.color.qp_outline)
            setCardBackgroundColor(getColor(R.color.qp_card))
            rippleColor = ColorStateList.valueOf(getColor(R.color.qp_soft))
            contentDescription = description
            isClickable = true
            isFocusable = true
        }
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(11), 0, dp(8), 0)
        }
        val icon = ImageView(this).apply {
            setImageResource(iconResource)
            imageTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
            scaleType = ImageView.ScaleType.CENTER_INSIDE
            setPadding(dp(6), dp(6), dp(6), dp(6))
            background = roundedDrawable(getColor(R.color.qp_soft), dp(9))
        }
        row.addView(icon, LinearLayout.LayoutParams(dp(36), dp(36)))
        row.addView(
            text(label, 12, R.color.qp_text, true).apply {
                maxLines = 1
                ellipsize = android.text.TextUtils.TruncateAt.END
                setPadding(dp(9), 0, 0, 0)
            },
            LinearLayout.LayoutParams(0, -2, 1f)
        )
        tile.addView(row, ViewGroup.LayoutParams(-1, -1))
        tile.setOnClickListener { action() }
        return tile
    }

    private fun libraryView(isPicker: Boolean): View {
        val scroll = ScrollView(this).apply { isFillViewport = true; clipToPadding = false }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(12), dp(16), dp(22))
        }
        scroll.addView(column)
        val path = if (isPicker) pickerPath else folderPath
        val rootFolder = try { store.folder(path) } catch (_: Exception) { store.root }
        val allEntries = store.list(rootFolder)
        val visibleEntries = allEntries.filter {
            when {
                isPicker && fileChooserSelectFolders -> it.isDirectory
                isPicker -> it.isDirectory || DocumentNamePolicy.isPrintable(it.name, it.length())
                librarySearchQuery.isBlank() -> true
                else -> it.name.contains(librarySearchQuery, ignoreCase = true)
            }
        }
        val heading = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        val copy = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        copy.addView(text(
            if (!isPicker) "My Files" else if (fileChooserSelectFolders) "Choose folders" else "Choose files",
            21, R.color.qp_text, true
        ).apply {
            if (!isPicker) id = R.id.smartprint_files_heading
        })
        copy.addView(text(
            when {
                !isPicker -> "${allEntries.count { it.isDirectory }} folders · ${allEntries.count { !it.isDirectory }} files"
                fileChooserSelectFolders -> "Select one or more folders. Only files directly inside each folder are added."
                else -> "${visibleEntries.count { !it.isDirectory }} printable files available"
            },
            11, R.color.qp_muted, false
        ).apply { setPadding(0, dp(2), 0, 0) })
        heading.addView(copy, LinearLayout.LayoutParams(0, -2, 1f))
        if (!isPicker) {
            heading.addView(button(if (librarySelectionMode) "Done" else "Select", false).apply {
                minHeight = dp(36)
                contentDescription = if (librarySelectionMode) "Finish file selection" else "Enter file selection mode"
                setOnClickListener { librarySelectionMode = !librarySelectionMode; showFiles() }
            }, LinearLayout.LayoutParams(-2, dp(36)).apply { marginStart = dp(8) })
        }
        column.addView(heading, linearParams().apply { bottomMargin = dp(9) })
        if (isPicker && !fileChooserSelectFolders) {
            val sourceRow = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(2), 0, dp(2), dp(10))
            }
            val sourceCopy = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
            }
            sourceCopy.addView(text("SmartPrint My Files", 13, R.color.qp_text, true))
            sourceCopy.addView(text("Files already saved in SmartPrint", 11, R.color.qp_muted, false)
                .apply { setPadding(0, dp(2), 0, 0) })
            sourceRow.addView(sourceCopy, LinearLayout.LayoutParams(0, -2, 1f))
            sourceRow.addView(button("Browse phone", false).apply {
                minHeight = dp(40)
                setOnClickListener {
                    try {
                        browsePhoneDocuments.launch(arrayOf("application/pdf", "image/jpeg", "image/png"))
                    } catch (e: Exception) {
                        toast("Phone files could not be opened: " + message(e))
                    }
                }
            }, LinearLayout.LayoutParams(-2, dp(40)).apply { marginStart = dp(8) })
            column.addView(sourceRow, linearParams().apply { bottomMargin = dp(4) })
        }
        if (isPicker && !fileChooserSelectFolders) addIdStudioPickerSection(column)
        if (!isPicker) {
            val folderActions = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            folderActions.addView(
                folderActionTile(
                    R.drawable.ic_document_scan,
                    "Scan here",
                    "Scan document into this folder"
                ) { startDocumentScan(path) },
                LinearLayout.LayoutParams(0, dp(52), 1f)
            )
            folderActions.addView(
                folderActionTile(
                    R.drawable.ic_create_new_folder,
                    "New folder",
                    "Create a new folder"
                ) {
                    nameDialog("New folder", "") { name ->
                        try { store.createFolder(folderPath, name); showFiles() }
                        catch (e: Exception) { toast("Folder not created: ${message(e)}") }
                    }
                },
                LinearLayout.LayoutParams(0, dp(52), 1f).apply { marginStart = dp(8) }
            )
            column.addView(folderActions, linearParams().apply { bottomMargin = dp(12) })
        }

        if (!isPicker) {
            val searchRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
            val search = EditText(this).apply {
                hint = "Search this folder"
                maxLines = 1
                setSingleLine(true)
                textSize = 14f
                setTextColor(getColor(R.color.qp_text))
                setHintTextColor(getColor(R.color.qp_muted))
                background = roundedDrawable(getColor(R.color.qp_card), dp(11), getColor(R.color.qp_outline))
                setPadding(dp(12), 0, dp(12), 0)
                setText(librarySearchQuery)
            }
            searchRow.addView(search, LinearLayout.LayoutParams(0, dp(42), 1f))
            searchRow.addView(button("Go", false).apply {
                minHeight = dp(40)
                setOnClickListener { librarySearchQuery = search.text.toString().trim(); showFiles() }
            }, LinearLayout.LayoutParams(dp(54), dp(40)).apply { marginStart = dp(8) })
            column.addView(searchRow, linearParams().apply { bottomMargin = dp(10) })
        }

        val crumb = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(2), dp(3), dp(2), dp(8))
        }
        val breadcrumbText = if (path.isBlank()) "My Files" else "My Files  ›  " + path.replace("/", "  ›  ")
        crumb.addView(text(breadcrumbText, 12, R.color.qp_muted, true),
            LinearLayout.LayoutParams(0, -2, 1f))
        if (path.isNotEmpty()) {
            val up = ImageView(this).apply {
                setImageResource(R.drawable.ic_arrow_upward)
                imageTintList = ColorStateList.valueOf(getColor(R.color.qp_text))
                contentDescription = "Go up one folder"
                scaleType = ImageView.ScaleType.CENTER_INSIDE
                setPadding(dp(9), dp(9), dp(9), dp(9))
                background = roundedDrawable(getColor(R.color.qp_soft), dp(11))
                isClickable = true
                isFocusable = true
                setOnClickListener {
                    if (isPicker) pickerPath = parent(pickerPath) else folderPath = parent(folderPath)
                    refreshLibrary(preserveScroll = false)
                }
            }
            crumb.addView(up, LinearLayout.LayoutParams(dp(40), dp(40)))
        }
        column.addView(crumb, linearParams())

        if (!isPicker) {
            val actions = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            actions.addView(button("Import files", false).apply {
                minHeight = dp(38)
                setOnClickListener {
                    pendingImportDestinationPath = if (pickerMode) pickerPath else folderPath
                    importFiles.launch(arrayOf("application/pdf", "image/jpeg", "image/png"))
                }
            }, LinearLayout.LayoutParams(0, dp(38), 1f))
            actions.addView(button("Import folder", false).apply {
                minHeight = dp(38)
                setOnClickListener { importTree.launch(null) }
            }, LinearLayout.LayoutParams(0, dp(38), 1f).apply { marginStart = dp(7) })
            column.addView(actions, linearParams().apply { bottomMargin = dp(12) })
        }

        val selectedHere = selectedFilePaths.mapNotNull { runCatching { File(it).canonicalFile }.getOrNull() }.filter { it.isFile && it.exists() }
        val chooserSelection = if (isPicker && fileChooserAllowsMultiple && !fileChooserSelectFolders) selectedChooserFiles() else emptyList()
        val chooserFolderSelection = if (isPicker && fileChooserSelectFolders) selectedChooserFolders() else emptyList()
        if (isPicker && fileChooserSelectFolders) {
            val selectionRow = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(12), dp(8), dp(12), dp(8))
                background = roundedDrawable(getColor(R.color.qp_soft), dp(12))
            }
            selectionRow.addView(text("${chooserFolderSelection.size} folders selected", 12, R.color.qp_text, true),
                LinearLayout.LayoutParams(0, -2, 1f))
            val visibleFolders = visibleEntries.filter { it.isDirectory }
            val allVisibleFoldersSelected = visibleFolders.isNotEmpty() && visibleFolders.all { chooserSelectedFolderPaths.contains(it.canonicalPath) }
            selectionRow.addView(button(if (allVisibleFoldersSelected) "Deselect all" else "Select all", false).apply {
                minHeight = dp(34)
                setOnClickListener {
                    visibleFolders.forEach { folder ->
                        if (allVisibleFoldersSelected) chooserSelectedFolderPaths.remove(folder.canonicalPath)
                        else chooserSelectedFolderPaths.add(folder.canonicalPath)
                    }
                    refreshLibrary()
                }
            })
            selectionRow.addView(button("Clear", false).apply {
                minHeight = dp(34)
                isEnabled = chooserFolderSelection.isNotEmpty()
                setOnClickListener { chooserSelectedFolderPaths.clear(); refreshLibrary() }
            }, LinearLayout.LayoutParams(-2, dp(34)).apply { marginStart = dp(5) })
            selectionRow.addView(button("Add folders", true).apply {
                minHeight = dp(34)
                isEnabled = chooserFolderSelection.isNotEmpty()
                setOnClickListener { attachSelectedFolders() }
            }, LinearLayout.LayoutParams(-2, dp(34)).apply { marginStart = dp(6) })
            column.addView(selectionRow, linearParams().apply { bottomMargin = dp(10) })
        } else if (isPicker && fileChooserAllowsMultiple) {
            val selectionRow = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(12), dp(8), dp(12), dp(8))
                background = roundedDrawable(getColor(R.color.qp_soft), dp(12))
            }
            selectionRow.addView(text("${chooserSelection.size} selected", 12, R.color.qp_text, true),
                LinearLayout.LayoutParams(0, -2, 1f))
            val selectablePickerFiles = visibleEntries.filter { !it.isDirectory && DocumentNamePolicy.isPrintable(it.name, it.length()) }
            val allVisiblePickerFilesSelected = selectablePickerFiles.isNotEmpty() && selectablePickerFiles.all { chooserSelectedFilePaths.contains(it.canonicalPath) }
            selectionRow.addView(button(if (allVisiblePickerFilesSelected) "Deselect all" else "Select all", false).apply {
                minHeight = dp(34)
                setOnClickListener {
                    selectablePickerFiles.forEach { file ->
                        if (allVisiblePickerFilesSelected) chooserSelectedFilePaths.remove(file.canonicalPath)
                        else chooserSelectedFilePaths.add(file.canonicalPath)
                    }
                    refreshLibrary()
                }
            })
            selectionRow.addView(button("Clear", false).apply {
                minHeight = dp(34)
                isEnabled = chooserSelection.isNotEmpty()
                setOnClickListener { chooserSelectedFilePaths.clear(); refreshLibrary() }
            }, LinearLayout.LayoutParams(-2, dp(34)).apply { marginStart = dp(5) })
            selectionRow.addView(button("Attach", true).apply {
                minHeight = dp(34)
                isEnabled = chooserSelection.isNotEmpty() &&
                    PrintBatchSelectionPolicy.validationError(chooserSelection) == null
                setOnClickListener {
                    chooseFiles(chooserSelection, chooserSelection.map(::printBatchGroupName))
                }
            }, LinearLayout.LayoutParams(-2, dp(34)).apply { marginStart = dp(6) })
            column.addView(selectionRow, linearParams().apply { bottomMargin = dp(10) })
        }
        val selectableCurrentFiles = visibleEntries.filter { !it.isDirectory && it.isFile }
        val allCurrentFilesSelected = selectableCurrentFiles.isNotEmpty() && selectableCurrentFiles.all { selectedFilePaths.contains(it.canonicalPath) }
        if (!isPicker && (librarySelectionMode || selectedHere.isNotEmpty())) {
            val selectionControls = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(10), dp(7), dp(10), dp(7)); background = roundedDrawable(getColor(R.color.qp_soft), dp(12))
            }
            selectionControls.addView(text("${selectedHere.size} selected", 12, R.color.qp_text, true), LinearLayout.LayoutParams(0, -2, 1f))
            if (librarySelectionMode) {
                selectionControls.addView(button(if (allCurrentFilesSelected) "Deselect all" else "Select all", false).apply {
                    minHeight = dp(34)
                    setOnClickListener {
                        selectableCurrentFiles.forEach { file ->
                            if (allCurrentFilesSelected) selectedFilePaths.remove(file.canonicalPath) else selectedFilePaths.add(file.canonicalPath)
                        }
                        refreshLibrary()
                    }
                })
                selectionControls.addView(button("Clear", false).apply {
                    minHeight = dp(34); isEnabled = selectedHere.isNotEmpty()
                    setOnClickListener { selectedFilePaths.clear(); refreshLibrary() }
                }, LinearLayout.LayoutParams(-2, dp(34)).apply { marginStart = dp(5) })
            } else {
                selectionControls.addView(button("Edit", false).apply { minHeight = dp(34); setOnClickListener { librarySelectionMode = true; showFiles() } })
                selectionControls.addView(button("Clear", false).apply {
                    minHeight = dp(34); setOnClickListener { selectedFilePaths.clear(); showFiles() }
                }, LinearLayout.LayoutParams(-2, dp(34)).apply { marginStart = dp(5) })
            }
            column.addView(selectionControls, linearParams().apply { bottomMargin = dp(7) })
        }
        if (!isPicker && selectedHere.isNotEmpty()) {
            val selectionActions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
            selectionActions.addView(button("ZIP", false).apply {
                minHeight = dp(38); setOnClickListener { exportSelected(selectedHere); clearLibrarySelection(); showFiles() }
            }, LinearLayout.LayoutParams(0, dp(38), 1f))
            val supportedBatch = selectedHere.size in 2..20 && selectedHere.all {
                it.extension.lowercase(Locale.ROOT) in setOf("pdf", "jpg", "jpeg", "png") && it.length() in 1..(20L * 1024L * 1024L)
            } && selectedHere.sumOf { it.length() } <= DocumentBatchComposer.MAX_SOURCE_BYTES
            selectionActions.addView(button("Combine into PDF", true).apply {
                minHeight = dp(38); isEnabled = supportedBatch; alpha = if (supportedBatch) 1f else 0.45f
                setOnClickListener { preparePrintBatch(selectedHere); clearLibrarySelection(); showFiles() }
            }, LinearLayout.LayoutParams(0, dp(38), 1f).apply { marginStart = dp(7) })
            column.addView(selectionActions, linearParams().apply { bottomMargin = dp(5) })
            column.addView(text("Combining rasterizes PDF pages; originals stay unchanged.", 10, R.color.qp_muted, false)
                .apply { setPadding(dp(2), 0, 0, dp(6)) })
        }

        if (visibleEntries.isEmpty()) {
            column.addView(text(
                when {
                    librarySearchQuery.isNotBlank() && !isPicker -> "No matches"
                    isPicker && fileChooserSelectFolders -> "No folders yet"
                    isPicker -> "No printable files"
                    else -> "This folder is empty"
                },
                13, R.color.qp_muted, false
            ).apply { setPadding(dp(4), dp(24), dp(4), dp(24)) })
        } else {
            addLibraryEntries(column, visibleEntries, isPicker, 0)
        }
        if (!isPicker) {
            val used = android.text.format.Formatter.formatShortFileSize(this, store.storageUsageBytes())
            column.addView(text("$used used on this device", 10, R.color.qp_muted, false)
                .apply { setPadding(dp(2), dp(12), 0, 0) })
        }
        return scroll
    }

    private fun addLibraryEntries(parentView: LinearLayout, entries: List<File>, isPicker: Boolean, depth: Int) {
        entries.forEach { entry ->
            if (!isPicker || entry.isDirectory || (!fileChooserSelectFolders && DocumentNamePolicy.isPrintable(entry.name, entry.length()))) {
                parentView.addView(entryView(entry, isPicker, depth), linearParams())
                if (entry.isDirectory && expandedFolderPaths.contains(entry.canonicalPath) && depth < MAX_TREE_DEPTH) {
                    val children = runCatching { store.list(entry).toList() }.getOrDefault(emptyList())
                    if (children.isNotEmpty()) addLibraryEntries(parentView, children, isPicker, depth + 1)
                }
            }
        }
    }

    private fun entryView(file: File, isPicker: Boolean, depth: Int = 0, showActions: Boolean = true): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(8 + depth * 15), dp(7), dp(5), dp(7))
            minimumHeight = dp(52)
            background = roundedDrawable(
                if (file.isDirectory && expandedFolderPaths.contains(file.canonicalPath)) getColor(R.color.qp_soft) else getColor(R.color.qp_surface),
                dp(9)
            )
        }
        val icon = ImageView(this).apply {
            setImageResource(fileIconResource(file))
            imageTintList = ColorStateList.valueOf(
                getColor(if (file.isDirectory) R.color.qp_primary else R.color.qp_muted)
            )
            scaleType = ImageView.ScaleType.CENTER_INSIDE
            setPadding(dp(7), dp(7), dp(7), dp(7))
            background = roundedDrawable(
                getColor(if (file.isDirectory) R.color.qp_soft else R.color.qp_surface), dp(10)
            )
        }
        row.addView(icon, LinearLayout.LayoutParams(dp(38), dp(38)))
        val details = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(10), 0, dp(4), 0)
        }
        details.addView(text(file.name, 13, R.color.qp_text, true).apply {
            maxLines = 1
            ellipsize = android.text.TextUtils.TruncateAt.END
        })
        val info = if (file.isDirectory) {
            val count = runCatching { store.list(file).size }.getOrDefault(0)
            "$count items"
        } else android.text.format.Formatter.formatShortFileSize(this@MainActivity, file.length())
        details.addView(text(info, 10, R.color.qp_muted, false).apply { setPadding(0, dp(2), 0, 0) })
        row.addView(details, LinearLayout.LayoutParams(0, -2, 1f))

        if (file.isDirectory) {
            val expanded = expandedFolderPaths.contains(file.canonicalPath)
            val chevron = ImageView(this).apply {
                setImageResource(R.drawable.ic_chevron_right)
                imageTintList = ColorStateList.valueOf(getColor(R.color.qp_muted))
                rotation = if (expanded) 90f else 0f
                contentDescription = if (expanded) "Collapse ${file.name}" else "Expand ${file.name}"
                setPadding(dp(7), dp(7), dp(7), dp(7))
                isClickable = true
                isFocusable = true
                setOnClickListener {
                    if (!expandedFolderPaths.add(file.canonicalPath)) expandedFolderPaths.remove(file.canonicalPath)
                    refreshLibrary()
                }
            }
            row.addView(chevron, LinearLayout.LayoutParams(dp(38), dp(40)))
            if (isPicker && fileChooserSelectFolders) {
                val selected = CheckBox(this).apply {
                    isChecked = chooserSelectedFolderPaths.contains(file.canonicalPath)
                    contentDescription = "Select folder " + file.name
                    buttonTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
                    setOnCheckedChangeListener { _, checked -> setChooserFolderSelected(file, checked) }
                }
                row.addView(selected, LinearLayout.LayoutParams(dp(40), dp(40)))
            }
        } else if (isPicker && fileChooserAllowsMultiple) {
            val selected = CheckBox(this).apply {
                isChecked = chooserSelectedFilePaths.contains(file.canonicalPath)
                contentDescription = "Select " + file.name
                setOnCheckedChangeListener { _, checked -> setChooserFileSelected(file, checked) }
            }
            row.addView(selected, LinearLayout.LayoutParams(dp(40), dp(40)))
        } else if (!isPicker && showActions && librarySelectionMode) {
            val selected = CheckBox(this).apply {
                isChecked = selectedFilePaths.contains(file.canonicalPath)
                contentDescription = "Select " + file.name
                buttonTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
                setOnCheckedChangeListener { _, checked ->
                    if (checked) selectedFilePaths.add(file.canonicalPath) else selectedFilePaths.remove(file.canonicalPath)
                    refreshLibrary()
                }
            }
            row.addView(selected, LinearLayout.LayoutParams(dp(38), dp(40)))
        }

        if (!isPicker && showActions && !librarySelectionMode) {
            val more = ImageView(this).apply {
                setImageResource(R.drawable.ic_more_vertical)
                imageTintList = ColorStateList.valueOf(getColor(R.color.qp_muted))
                contentDescription = "More options for " + file.name
                setPadding(dp(8), dp(8), dp(8), dp(8))
                isClickable = true
                isFocusable = true
                setOnClickListener { showEntryMenu(this, file) }
            }
            row.addView(more, LinearLayout.LayoutParams(dp(36), dp(40)))
        }
        row.setOnClickListener {
            when {
                !isPicker && showActions && librarySelectionMode && file.isFile -> {
                    val path = file.canonicalPath
                    if (!selectedFilePaths.add(path)) selectedFilePaths.remove(path)
                    refreshLibrary()
                }
                file.isDirectory && isPicker && fileChooserSelectFolders ->
                    setChooserFolderSelected(file, !chooserSelectedFolderPaths.contains(file.canonicalPath))
                file.isDirectory -> {
                    if (isPicker) pickerPath = store.pathOf(file) else folderPath = store.pathOf(file)
                    refreshLibrary(preserveScroll = false)
                }
                isPicker && fileChooserAllowsMultiple -> setChooserFileSelected(
                    file, !chooserSelectedFilePaths.contains(file.canonicalPath)
                )
                isPicker -> chooseFile(file)
                else -> openDocument(file)
            }
        }
        if (!isPicker && showActions && file.isDirectory && !librarySelectionMode) {
            row.setOnLongClickListener { showEntryMenu(row, file); true }
        } else if (!isPicker && showActions && !librarySelectionMode) {
            row.setOnLongClickListener {
                val clip = ClipData.newPlainText("SmartPrint item order", file.name)
                row.startDragAndDrop(clip, View.DragShadowBuilder(row), file.canonicalPath, 0)
            }
            row.setOnDragListener { _, event ->
                when (event.action) {
                    android.view.DragEvent.ACTION_DRAG_STARTED ->
                        event.clipDescription?.hasMimeType(ClipDescription.MIMETYPE_TEXT_PLAIN) == true
                    android.view.DragEvent.ACTION_DRAG_ENTERED -> { row.alpha = 0.65f; true }
                    android.view.DragEvent.ACTION_DRAG_EXITED -> { row.alpha = 1f; true }
                    android.view.DragEvent.ACTION_DROP -> {
                        val sourcePath = event.localState as? String
                        val source = sourcePath?.let(::File)
                        if (source != null) {
                            try {
                                if (store.moveBefore(source, file)) {
                                    selectedFilePaths.remove(source.canonicalPath)
                                    showFiles()
                                }
                            } catch (e: Exception) { toast("Could not reorder: " + message(e)) }
                        }
                        true
                    }
                    android.view.DragEvent.ACTION_DRAG_ENDED -> { row.alpha = 1f; true }
                    else -> true
                }
            }
        }
        return row
    }

    private fun fileIconResource(file: File): Int {
        if (file.isDirectory) return R.drawable.ic_folder
        return when (file.extension.lowercase(Locale.ROOT)) {
            "pdf" -> R.drawable.ic_file_pdf
            "jpg", "jpeg", "png", "webp", "bmp", "gif" -> R.drawable.ic_file_image
            else -> R.drawable.ic_file
        }
    }

    private fun showEntryMenu(anchor: View, file: File) {
        val actions = mutableListOf<Pair<String, () -> Unit>>()
        if (!file.isDirectory) actions += "Open" to { openDocument(file) }
        if (!file.isDirectory && supportsInAppViewer(file)) actions += "Open with another app" to { openDocumentExternally(file) }
        if (!file.isDirectory && file.extension.equals("pdf", true)) {
            actions += "Edit PDF" to { showPdfEditor(file) }
        }
        actions += "Rename" to {
            nameDialog("Rename", file.name) { name ->
                try {
                    store.rename(file, name)
                    selectedFilePaths.remove(file.canonicalPath)
                    expandedFolderPaths.remove(file.canonicalPath)
                    showFiles()
                } catch (e: Exception) { toast("Rename failed: " + message(e)) }
            }
        }
        actions += (if (file.isDirectory) "Export folder" else "Export") to {
            if (file.isDirectory) { exportFolder = file; exportTree.launch(null) }
            else { exportFile = file; exportOne.launch(file.name) }
        }
        actions += "Move to folder" to { showMoveDestinationPicker(file) }
        val siblings = file.parentFile?.let { store.list(it) }.orEmpty()
        val position = siblings.indexOfFirst { it.canonicalPath == file.canonicalPath }
        if (position > 0) actions += "Move earlier" to {
            try { store.reorder(file, -1); showFiles() }
            catch (e: Exception) { toast("Reorder failed: " + message(e)) }
        }
        if (position >= 0 && position < siblings.lastIndex) actions += "Move later" to {
            try { store.reorder(file, 1); showFiles() }
            catch (e: Exception) { toast("Reorder failed: " + message(e)) }
        }
        if (!file.isDirectory) actions += "Share" to { shareDocument(file) }
        actions += "Delete" to {
            showConfirmSheet(
                if (file.isDirectory) "Delete folder?" else "Delete file?",
                "This removes the local copy.",
                "Delete",
                destructive = true
            ) {
                try {
                    store.delete(file)
                    selectedFilePaths.remove(file.canonicalPath)
                    expandedFolderPaths.remove(file.canonicalPath)
                    showFiles()
                } catch (e: Exception) { toast("Delete failed: " + message(e)) }
            }
        }
        showOptionSheet(if (file.isDirectory) "Folder actions" else "File actions", actions)
    }

    private fun showMoveDestinationPicker(file: File) {
        val sourcePath = runCatching { store.pathOf(file) }.getOrDefault("")
        val currentParent = file.parentFile?.let { store.pathOf(it) }.orEmpty()
        val destinations = mutableListOf("")
        store.root.walkTopDown()
            .filter { it.isDirectory && it.canonicalPath != store.root.canonicalPath }
            .take(MAX_TREE_FILES)
            .forEach { directory ->
                val path = runCatching { store.pathOf(directory) }.getOrNull() ?: return@forEach
                if (path != currentParent &&
                    !(file.isDirectory && (path == sourcePath || path.startsWith("$sourcePath/")))) {
                    destinations.add(path)
                }
            }
        val labels = destinations.map { if (it.isBlank()) "My Files" else it.replace("/", "  /  ") }
        val actions = labels.mapIndexed { index, label ->
            label to {
                try {
                    store.move(file, destinations[index])
                    selectedFilePaths.remove(file.canonicalPath)
                    expandedFolderPaths.remove(file.canonicalPath)
                    showFiles()
                } catch (e: Exception) { toast("Move failed: " + message(e)) }
            }
        }
        showOptionSheet("Move to folder", actions)
    }

    private fun exportSelected(files: List<File>) {
        if (files.isEmpty()) return
        archiveFiles = files.toList()
        selectedFilePaths.clear()
        exportArchive.launch("Smart Print files ${SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).format(Date())}.zip")
    }

    private fun preparePrintBatch(files: List<File>) {
        val orderedFiles = files.toList()
        val output = File(cacheDir, "SmartPrint-batch-${System.currentTimeMillis()}.pdf")
        val destinationPath = orderedFiles.firstOrNull()?.parentFile?.let { store.pathOf(it) }.orEmpty()
        toast("Preparing your ordered print batch…")
        Thread {
            try {
                val pages = DocumentBatchComposer.compose(orderedFiles, output)
                val name = "Smart Print print batch ${SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).format(Date())}.pdf"
                val saved = FileInputStream(output).use { store.importFile(it, name, destinationPath) }
                runOnUiThread {
                    folderPath = destinationPath
                    selectedFilePaths.clear()
                    if (portalUrl != null) showPrint() else showFiles()
                    toast("Prepared ${orderedFiles.size} documents as one $pages-page PDF: ${saved.name}. Attach this PDF to one Smart Print print order.")
                }
            } catch (e: Exception) {
                runOnUiThread { toast("Batch preparation failed: ${message(e)}") }
            } finally {
                output.delete()
            }
        }.start()
    }

    private fun showPdfEditor(file: File) {
        try {
            require(file.isFile && file.extension.equals("pdf", true)) {
                "Only saved PDFs can be edited here."
            }
            val pages = DocumentBatchComposer.pageEdits(file).toMutableList()
            editingPdf = file
            renderPdfEditor(file, pages)
        } catch (e: Exception) {
            toast("Could not open the PDF editor: ${message(e)}")
        }
    }

    private fun renderPdfEditor(file: File, pages: MutableList<PdfPageEdit>) {
        pickerMode = false
        clearLibrarySelection()
        setToolbarPage("Edit PDF")
        setToolbarBackAction("Back to My Files") { showFiles() }
        setBottomNavigationVisible(false)
        content.removeAllViews()

        val scroll = ScrollView(this).apply { isFillViewport = true; clipToPadding = false }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(14), dp(16), dp(24))
        }
        scroll.addView(column)

        column.addView(text(file.name, 15, R.color.qp_text, true))
        column.addView(text(
            "Rasterized copy · Original kept.",
            11, R.color.qp_muted, false
        ).apply { setPadding(0, dp(4), 0, dp(12)) })
        column.addView(button("Save copy", true).apply {
            setOnClickListener { savePdfEdits(file, pages.toList()) }
        }, linearParams().apply { bottomMargin = dp(12) })

        pages.forEachIndexed { position, page ->
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(9), dp(8), dp(7), dp(8))
                background = roundedDrawable(getColor(R.color.qp_card), dp(11), getColor(R.color.qp_outline))
            }
            val pageHeader = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            val pageMark = TextView(this).apply {
                text = (position + 1).toString()
                textSize = 13f
                gravity = Gravity.CENTER
                setTypeface(typeface, android.graphics.Typeface.BOLD)
                setTextColor(getColor(R.color.qp_primary))
                background = roundedDrawable(getColor(R.color.qp_soft), dp(9))
            }
            pageHeader.addView(pageMark, LinearLayout.LayoutParams(dp(36), dp(40)))
            val meta = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(10), 0, dp(6), 0)
            }
            meta.addView(text("Page ${position + 1}", 12, R.color.qp_text, true))
            meta.addView(text(
                if (page.rotationDegrees == 0) "Original" else "${page.rotationDegrees}°",
                10, R.color.qp_muted, false
            ).apply { setPadding(0, dp(2), 0, 0) })
            pageHeader.addView(meta, LinearLayout.LayoutParams(0, -2, 1f))
            row.addView(pageHeader, linearParams())

            // Keep every page action on its own full-width row. A horizontal
            // action strip below the metadata avoids clipping the final action
            // on narrow phones while preserving generous touch targets.
            val actions = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            actions.addView(button("View", false).apply {
                minHeight = dp(42)
                minWidth = dp(0)
                contentDescription = "Preview page ${position + 1}"
                setOnClickListener {
                    try {
                        showImageSheet("Page ${position + 1}", DocumentBatchComposer.renderPagePreview(file, page))
                    } catch (e: Exception) {
                        toast("Preview failed: ${message(e)}")
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f))
            actions.addView(button("↻", false).apply {
                minHeight = dp(42)
                minWidth = dp(0)
                contentDescription = "Rotate page ${position + 1}"
                setOnClickListener {
                    pages[position] = page.copy(rotationDegrees = (page.rotationDegrees + 90) % 360)
                    renderPdfEditor(file, pages)
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(4) })
            actions.addView(button("↑", false).apply {
                minHeight = dp(42)
                minWidth = dp(0)
                contentDescription = "Move page ${position + 1} up"
                isEnabled = position > 0
                alpha = if (position > 0) 1f else 0.4f
                setOnClickListener {
                    if (position > 0) {
                        val value = pages.removeAt(position)
                        pages.add(position - 1, value)
                        renderPdfEditor(file, pages)
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(4) })
            actions.addView(button("↓", false).apply {
                minHeight = dp(42)
                minWidth = dp(0)
                contentDescription = "Move page ${position + 1} down"
                isEnabled = position < pages.lastIndex
                alpha = if (position < pages.lastIndex) 1f else 0.4f
                setOnClickListener {
                    if (position < pages.lastIndex) {
                        val value = pages.removeAt(position)
                        pages.add(position + 1, value)
                        renderPdfEditor(file, pages)
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(4) })
            actions.addView(button("×", false).apply {
                minHeight = dp(42)
                minWidth = dp(0)
                contentDescription = "Remove page ${position + 1}"
                setOnClickListener {
                    if (pages.size <= 1) {
                        toast("A PDF needs one page.")
                    } else {
                        pages.removeAt(position)
                        renderPdfEditor(file, pages)
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(4) })
            row.addView(actions, LinearLayout.LayoutParams(-1, dp(42)).apply { topMargin = dp(8) })
            row.setOnLongClickListener {
                showConfirmSheet("Remove page?", "The original stays unchanged.", "Remove", destructive = true) {
                    if (pages.size <= 1) {
                        toast("A PDF needs one page.")
                    } else {
                        pages.removeAt(position)
                        renderPdfEditor(file, pages)
                    }
                }
                true
            }
            column.addView(row, linearParams().apply { bottomMargin = dp(5) })
        }
        column.addView(text("Swipe-free · Use arrows to reorder.", 10, R.color.qp_muted, false)
            .apply { setPadding(dp(2), dp(8), 0, 0) })
        content.addView(scroll, frameParams())
    }

    private fun savePdfEdits(source: File, edits: List<PdfPageEdit>) {
        val output = File(cacheDir, "SmartPrint-edited-${System.currentTimeMillis()}.pdf")
        val destinationPath = source.parentFile?.let { store.pathOf(it) }.orEmpty()
        toast("Saving a new edited PDF…")
        Thread {
            try {
                val pageCount = DocumentBatchComposer.editPdf(source, edits, output)
                val editedName = "${source.nameWithoutExtension}_edited.pdf"
                val saved = FileInputStream(output).use { store.importFile(it, editedName, destinationPath) }
                runOnUiThread {
                    folderPath = destinationPath
                    editingPdf = null
                    showFiles()
                    toast("Saved ${saved.name} with $pageCount pages. The original PDF is unchanged.")
                }
            } catch (e: Exception) {
                runOnUiThread { toast("Could not save edited PDF: ${message(e)}") }
            } finally {
                output.delete()
            }
        }.start()
    }

    private fun sheetRoot(title: String, subtitle: String? = null): Pair<BottomSheetDialog, LinearLayout> {
        val dialog = BottomSheetDialog(this)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(10), dp(20), dp(22))
            background = SmartPrintGlass.surface(this@MainActivity, 22)
        }
        val handle = View(this).apply {
            background = roundedDrawable(getColor(R.color.qp_outline), dp(2))
        }
        root.addView(handle, LinearLayout.LayoutParams(dp(36), dp(4)).apply {
            gravity = Gravity.CENTER_HORIZONTAL
            bottomMargin = dp(15)
        })
        root.addView(text(title, 18, R.color.qp_text, true))
        subtitle?.takeIf { it.isNotBlank() }?.let {
            root.addView(text(it, 12, R.color.qp_muted, false).apply {
                setPadding(0, dp(5), 0, dp(12))
            })
        } ?: run { root.addView(View(this), LinearLayout.LayoutParams(1, dp(10))) }
        dialog.setContentView(root)
        return dialog to root
    }

    private fun showOptionSheet(title: String, actions: List<Pair<String, () -> Unit>>) {
        val (dialog, root) = sheetRoot(title)
        val scroll = ScrollView(this).apply { isFillViewport = false }
        val list = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        actions.forEach { (label, action) ->
            val item = button(label, false).apply {
                minHeight = dp(46)
                gravity = Gravity.CENTER_VERTICAL
                setOnClickListener { dialog.dismiss(); action() }
            }
            list.addView(item, linearParams().apply { bottomMargin = dp(6) })
        }
        scroll.addView(list)
        root.addView(scroll, LinearLayout.LayoutParams(-1, -2))
        root.addView(button("Cancel", false).apply {
            minHeight = dp(42)
            setOnClickListener { dialog.dismiss() }
        }, linearParams().apply { topMargin = dp(6) })
        dialog.show()
    }

    private fun showConfirmSheet(
        title: String,
        detail: String,
        confirmLabel: String,
        destructive: Boolean = false,
        onConfirm: () -> Unit
    ) {
        val (dialog, root) = sheetRoot(title, detail)
        val actions = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        actions.addView(button("Cancel", false).apply {
            minHeight = dp(44)
            setOnClickListener { dialog.dismiss() }
        }, LinearLayout.LayoutParams(0, dp(44), 1f))
        actions.addView(button(confirmLabel, true).apply {
            minHeight = dp(44)
            if (destructive) {
                backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_danger))
                setTextColor(getColor(R.color.qp_danger_content))
            }
            setOnClickListener { dialog.dismiss(); onConfirm() }
        }, LinearLayout.LayoutParams(0, dp(44), 1f).apply { marginStart = dp(8) })
        root.addView(actions, linearParams().apply { topMargin = dp(12) })
        dialog.show()
    }

    private fun showImageSheet(title: String, bitmap: android.graphics.Bitmap) {
        val (dialog, root) = sheetRoot(title)
        val preview = ImageView(this).apply {
            setImageBitmap(bitmap)
            adjustViewBounds = true
            scaleType = ImageView.ScaleType.FIT_CENTER
            setPadding(dp(4), dp(6), dp(4), dp(6))
        }
        root.addView(preview, LinearLayout.LayoutParams(-1, -2))
        root.addView(button("Close", false).apply {
            minHeight = dp(42)
            setOnClickListener { dialog.dismiss() }
        }, linearParams().apply { topMargin = dp(8) })
        dialog.setOnDismissListener { if (!bitmap.isRecycled) bitmap.recycle() }
        dialog.show()
    }

    private fun nameDialog(title: String, initial: String, onSave: (String) -> Unit) {
        val (dialog, root) = sheetRoot(title)
        val input = EditText(this).apply {
            setSingleLine(true)
            setText(initial)
            setSelection(text.length)
            hint = "Name"
            textSize = 14f
            setTextColor(getColor(R.color.qp_text))
            setHintTextColor(getColor(R.color.qp_muted))
            background = roundedDrawable(getColor(R.color.qp_surface), dp(11), getColor(R.color.qp_outline))
            setPadding(dp(12), dp(11), dp(12), dp(11))
        }
        root.addView(input, linearParams())
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("Cancel", false).apply {
            minHeight = dp(44)
            setOnClickListener { dialog.dismiss() }
        }, LinearLayout.LayoutParams(0, dp(44), 1f))
        actions.addView(button("Save", true).apply {
            minHeight = dp(44)
            setOnClickListener {
                val value = input.text.toString().trim()
                if (value.isBlank()) {
                    input.error = "Enter a name"
                    return@setOnClickListener
                }
                dialog.dismiss()
                onSave(value)
            }
        }, LinearLayout.LayoutParams(0, dp(44), 1f).apply { marginStart = dp(8) })
        root.addView(actions, linearParams().apply { topMargin = dp(12) })
        dialog.show()
    }

    private fun roundedDrawable(fillColor: Int, radiusDp: Int, strokeColor: Int? = null): android.graphics.drawable.GradientDrawable =
        android.graphics.drawable.GradientDrawable().apply {
            setColor(fillColor)
            cornerRadius = dp(radiusDp).toFloat()
            strokeColor?.let { setStroke(dp(1), it) }
        }

    private fun ensureWebView(): WebView {
        webView?.let { return it }
        val view = WebView(this).apply {
            setBackgroundColor(getColor(R.color.qp_surface))
            isVerticalScrollBarEnabled = false
            isHorizontalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
        }
        view.settings.userAgentString = "${view.settings.userAgentString} SmartPrintAndroid/1"
        // Never trust the QR-selected route as a credential-sharing origin.
        // The bridge is pinned to the configured service origin for this build.
        val trustedPage = Uri.parse(BuildConfig.SERVICE_BASE_URL)
        val trustedOrigin = "${trustedPage.scheme}://${trustedPage.authority}"
        if (WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT)) {
            val theme = SmartPrintThemePreference.read(this).storedValue
            val bootstrap = """
                (() => {
                  const selected = '$theme';
                  try { localStorage.setItem('qrtoprint.theme', selected); } catch {}
                  document.documentElement.dataset.theme = selected;
                  document.documentElement.style.colorScheme = selected;
                })();
            """.trimIndent()
            WebViewCompat.addDocumentStartJavaScript(view, bootstrap, setOf(trustedOrigin))
            webViewThemeBootstrapInstalled = true
        }
        if (trustedPage.scheme in setOf("https", "http") && trustedPage.host != null &&
            WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) {
            WebViewCompat.addWebMessageListener(
                view,
                "SmartPrintNative",
                setOf(trustedOrigin)
            ) { _, message, sourceOrigin, isMainFrame, replyProxy ->
                // Only the fixed configured origin and the top-level document may use this bridge.
                if (isMainFrame &&
                    sourceOrigin.scheme == trustedPage.scheme &&
                    sourceOrigin.host.equals(trustedPage.host, true) &&
                    sourceOrigin.port == trustedPage.port) {
                    if (message.data == "requestLocalNetworkAccess") {
                        runOnUiThread {
                            if (!isFinishing && !isDestroyed) requestLocalNetworkAccess(replyProxy)
                            else runCatching { replyProxy.postMessage("localNetworkAccess:denied") }
                        }
                    } else {
                        val parts = message.data.orEmpty().split("|", limit = 3)
                        when {
                            parts.size == 2 && parts[0] == "setPhotoStudioOpen" &&
                                parts[1] in setOf("on", "off") -> {
                                runOnUiThread {
                                    if (!isFinishing && !isDestroyed) {
                                        photoStudioOpen = parts[1] == "on"
                                        replyProxy.postMessage("OK")
                                    }
                                }
                            }
                            parts.size == 2 && parts[0] == "setSensitivePreview" &&
                                parts[1] in setOf("on", "off") -> {
                                runOnUiThread {
                                    if (!isFinishing && !isDestroyed) {
                                        if (parts[1] == "on") {
                                            window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
                                        } else {
                                            window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)
                                        }
                                        replyProxy.postMessage("OK")
                                    }
                                }
                            }
                            parts.size == 2 && parts[0] == "previewCustomerPdf" &&
                                isCustomerUploadPdfPreviewToken(parts[1]) -> {
                                val token = parts[1]
                                runOnUiThread {
                                    if (!isFinishing && !isDestroyed) {
                                        showCustomerUploadPdfPreview(token)
                                        replyProxy.postMessage("OK")
                                    }
                                }
                            }
                            parts.size == 2 && parts[0] == "retainCustomerPdfPreviews" -> {
                                runOnUiThread {
                                    if (!isFinishing && !isDestroyed) {
                                        retainCustomerUploadPdfPreviews(parts[1])
                                        replyProxy.postMessage("OK")
                                    }
                                }
                            }
                            else -> Thread {
                        val response = try {
                            when {
                                message.data == "getCustomerAccessToken" -> {
                                    var auth = customerAccountStore.read()
                                    if (auth != null && CustomerAccountClient.accessTokenIsExpired(auth)) {
                                        auth = CustomerAccountClient.refresh(BuildConfig.SERVICE_BASE_URL, auth)
                                        customerAccountStore.save(auth)
                                    }
                                    auth?.accessToken.orEmpty()
                                }
                                parts.size == 2 && parts[0] == "getCustomerPrintSession" && isActiveStationCode(parts[1]) ->
                                    customerPrintSessionStore.read(parts[1]).orEmpty()
                                parts.size == 3 && parts[0] == "saveCustomerPrintSession" && isActiveStationCode(parts[1]) -> {
                                    customerPrintSessionStore.save(parts[1], parts[2]); "OK"
                                }
                                parts.size == 3 && parts[0] == "setCustomerShopProfile" && isActiveStationCode(parts[1]) ->
                                    saveCustomerShopProfile(parts[2])
                                parts.size == 2 && parts[0] == "clearCustomerPrintSession" && isActiveStationCode(parts[1]) -> {
                                    customerPrintSessionStore.clear(parts[1]); "OK"
                                }
                                parts.size == 2 && parts[0] == "setTheme" &&
                                    parts[1] in setOf("light", "dark") -> {
                                    val selected = if (parts[1] == "dark") SmartPrintTheme.DARK else SmartPrintTheme.LIGHT
                                    runOnUiThread { SmartPrintThemePreference.save(this@MainActivity, selected) }
                                    "OK"
                                }
                                else -> ""
                            }
                        } catch (_: Exception) { "" }
                        runOnUiThread {
                            if (!isFinishing && !isDestroyed) replyProxy.postMessage(response)
                        }
                        }.start()
                        }
                    }
                }
            }
        }
        view.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowContentAccess = true
            allowFileAccess = false
            javaScriptCanOpenWindowsAutomatically = false
            setSupportMultipleWindows(false)
            // The print page is HTTPS, but the paired Agent intentionally exposes
            // its private-LAN status/upload API over HTTP. WebView's compatibility
            // mixed-content mode blocks active fetch() calls to that endpoint.
            // Cleartext is enabled at the WebView engine only because every HTTP
            // request is subsequently denied by shouldInterceptRequest unless it
            // matches the strict private-IP / TCP 17842 / Agent-route allowlist.
            mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
            safeBrowsingEnabled = true
        }
        CookieManager.getInstance().setAcceptCookie(true)
        view.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val uri = request.url
                return when (uri.scheme?.lowercase(Locale.ROOT)) {
                    "https" -> if (uri.host.equals(portalHost, true)) false else { launchExternal(uri); true }
                    "upi", "tel", "mailto" -> { launchExternal(uri); true }
                    "intent" -> { launchIntent(uri.toString()); true }
                    "http" -> !(allowedAgentLocalEndpoint(uri, request.method, request.requestHeaders) || allowedLocalDevelopmentEndpoint(uri))
                    // Android's WebView does not render PDF iframe documents. The
                    // customer form opens selected PDFs in the existing native reader.
                    "about" -> false
                    else -> true
                }
            }
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val uri = request.url
                if (uri.scheme.equals("http", true) &&
                    !allowedAgentLocalEndpoint(uri, request.method, request.requestHeaders) &&
                    !allowedLocalDevelopmentEndpoint(uri)) {
                    return WebResourceResponse(
                        "text/plain", "UTF-8", 403, "Blocked by Smart Print", emptyMap(),
                        ByteArrayInputStream("Only the selected private-LAN development portal and paired-Agent status/upload endpoints are allowed over HTTP.".toByteArray())
                    )
                }
                return super.shouldInterceptRequest(view, request)
            }
            override fun onPageFinished(view: WebView, url: String) {
                super.onPageFinished(view, url)
                val loadedUrl = runCatching { Uri.parse(url) }.getOrNull() ?: return
                if (!loadedUrl.scheme.equals(trustedPage.scheme, true) ||
                    !loadedUrl.host.equals(trustedPage.host, true) ||
                    loadedUrl.port != trustedPage.port) return

                val selectedTheme = SmartPrintThemePreference.read(this@MainActivity).storedValue
                val script = if (webViewThemeBootstrapInstalled) """
                    (() => {
                      const selected = '$selectedTheme';
                      try { localStorage.setItem('qrtoprint.theme', selected); } catch {}
                      document.documentElement.dataset.theme = selected;
                      document.documentElement.style.colorScheme = selected;
                    })();
                """.trimIndent() else """
                    (() => {
                      const selected = '$selectedTheme';
                      try {
                        if (localStorage.getItem('qrtoprint.theme') !== selected) {
                          localStorage.setItem('qrtoprint.theme', selected);
                          window.location.reload();
                          return;
                        }
                      } catch {}
                      document.documentElement.dataset.theme = selected;
                      document.documentElement.style.colorScheme = selected;
                    })();
                """.trimIndent()
                view.evaluateJavascript(script, null)
            }
            override fun onReceivedSslError(view: WebView, handler: SslErrorHandler, error: android.net.http.SslError) {
                handler.cancel()
                toast("The shop connection could not be verified securely.")
            }
        }
        view.webChromeClient = object : WebChromeClient() {
            override fun onShowFileChooser(
                webView: WebView,
                callback: ValueCallback<Array<Uri>>,
                params: FileChooserParams
            ): Boolean {
                fileCallback?.onReceiveValue(null)
                fileCallback = callback
                fileChooserSelectFolders = params.acceptTypes.any { accepted ->
                    accepted.split(',').any { it.trim().equals(FOLDER_PICKER_ACCEPT, ignoreCase = true) }
                }
                fileChooserAllowsMultiple = fileChooserSelectFolders ||
                    params.mode == FileChooserParams.MODE_OPEN_MULTIPLE
                chooserSelectedFilePaths.clear()
                chooserSelectedFolderPaths.clear()
                showPicker()
                return true
            }
        }
        webView = view
        return view
    }

    private fun isActiveStationCode(candidate: String): Boolean {
        val activeCode = portalUrl?.let { Uri.parse(it).lastPathSegment } ?: return false
        return candidate.matches(Regex("^[A-Za-z0-9_-]{32}$")) && candidate == activeCode
    }

    private fun saveCustomerShopProfile(encodedProfile: String): String {
        val profile = JSONObject(Uri.decode(encodedProfile))
        val name = profile.optString("displayName").trim().take(100)
            .ifBlank { "Connected shop" }
        val logo = profile.optString("logoUrl").trim().takeIf {
            it.isNotBlank() && it != "null"
        }?.take(2048)
        val accent = profile.optString("accentColor").trim().takeIf {
            it.matches(Regex("^#[0-9A-Fa-f]{6}$"))
        }
        getSharedPreferences("smartprint.customer.last-shop", MODE_PRIVATE).edit()
            .putString("shopDisplayName", name)
            .putString("shopLogoUrl", logo)
            .putString("shopAccentColor", accent)
            .apply()
        runOnUiThread {
            shopDisplayName = name
            shopLogoUrl = logo
            shopAccentColor = accent
            if (!isFinishing && !isDestroyed && ::navigation.isInitialized &&
                navigation.selectedItemId == HOME) showHome()
        }
        return "OK"
    }

    private fun loadShopLogo(imageView: ImageView) {
        val raw = shopLogoUrl?.takeIf { it.isNotBlank() } ?: return
        val service = Uri.parse(BuildConfig.SERVICE_BASE_URL)
        val target = try { URL(URL(BuildConfig.SERVICE_BASE_URL), raw) } catch (_: Exception) { return }
        val targetUri = Uri.parse(target.toString())
        val targetHost = targetUri.host?.lowercase(Locale.ROOT) ?: return
        val servicePort = if (service.port == -1) 443 else service.port
        val targetPort = if (targetUri.port == -1) 443 else targetUri.port
        val sameServiceOrigin = targetHost.equals(service.host, true) && targetPort == servicePort
        if (!targetUri.scheme.equals("https", true) ||
            targetUri.userInfo != null || targetUri.fragment != null ||
            (!sameServiceOrigin && (targetPort != 443 || isPrivateOrLocalLogoHost(targetHost)))) return
        imageView.tag = raw
        Thread {
            val bitmap = try {
                val connection = target.openConnection() as? HttpURLConnection
                    ?: return@Thread
                try {
                    connection.instanceFollowRedirects = false
                    connection.connectTimeout = 5000
                    connection.readTimeout = 5000
                    connection.useCaches = false
                    connection.setRequestProperty("Accept", "image/png,image/jpeg,image/webp")
                    connection.connect()
                    val contentType = connection.contentType?.substringBefore(';')?.trim()?.lowercase(Locale.ROOT)
                    if (connection.responseCode != 200 ||
                        contentType !in setOf("image/png", "image/jpeg", "image/webp") ||
                        connection.contentLengthLong > 2L * 1024L * 1024L) {
                        null
                    } else {
                        val bytes = connection.inputStream.use { input ->
                            val output = ByteArrayOutputStream()
                            val buffer = ByteArray(8192)
                            var total = 0
                            while (true) {
                                val count = input.read(buffer)
                                if (count < 0) break
                                total += count
                                if (total > 2 * 1024 * 1024) return@use ByteArray(0)
                                output.write(buffer, 0, count)
                            }
                            output.toByteArray()
                        }
                        if (bytes.isEmpty()) null else {
                            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
                            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) null else {
                                val sample = maxOf(1, maxOf(bounds.outWidth, bounds.outHeight) / 512)
                                BitmapFactory.decodeByteArray(
                                    bytes, 0, bytes.size,
                                    BitmapFactory.Options().apply { inSampleSize = sample }
                                )
                            }
                        }
                    }
                } finally {
                    connection.disconnect()
                }
            } catch (_: Exception) { null }
            if (bitmap != null) runOnUiThread {
                if (!isFinishing && !isDestroyed && imageView.tag == raw) {
                    imageView.setPadding(0, 0, 0, 0)
                    imageView.contentDescription = "Connected shop logo"
                    imageView.setImageBitmap(bitmap)
                } else bitmap.recycle()
            }
        }.start()
    }

    private fun isPrivateOrLocalLogoHost(host: String): Boolean {
        if (host == "localhost" || host.endsWith(".localhost") ||
            host.endsWith(".local") || host.endsWith(".internal") ||
            host == "::1" || host.startsWith("fc", true) ||
            host.startsWith("fd", true) || host.startsWith("fe80:", true)) return true
        val octets = host.split('.').mapNotNull { it.toIntOrNull() }
        if (octets.size != 4 || octets.any { it !in 0..255 }) return false
        return octets[0] == 0 || octets[0] == 10 || octets[0] == 127 ||
            (octets[0] == 172 && octets[1] in 16..31) ||
            (octets[0] == 169 && octets[1] == 254) ||
            (octets[0] == 192 && octets[1] == 168)
    }

    private fun clearShopProfile() {
        shopDisplayName = null
        shopLogoUrl = null
        shopAccentColor = null
        getSharedPreferences("smartprint.customer.last-shop", MODE_PRIVATE).edit()
            .remove("shopDisplayName")
            .remove("shopLogoUrl")
            .remove("shopAccentColor")
            .apply()
    }

    private fun allowedAgentLocalEndpoint(uri: Uri, method: String, headers: Map<String, String>): Boolean {
        if (uri.query != null || uri.fragment != null || uri.userInfo != null) return false
        val preflightMethod = headers.entries.firstOrNull { it.key.equals("Access-Control-Request-Method", ignoreCase = true) }
            ?.value
        return LocalAgentUploadPolicy.allows(
            uri.scheme.orEmpty(), uri.host.orEmpty(), uri.port, uri.path.orEmpty(), method, preflightMethod
        )
    }

    private fun allowedLocalDevelopmentEndpoint(uri: Uri): Boolean {
        val isolatedPhotoStudioLoopback =
            BuildConfig.PHOTO_STUDIO_QA_ENABLED &&
                (packageName == "com.quantaedge.smartprint.localqa" ||
                    packageName.startsWith("com.quantaedge.smartprint.localqa.")) &&
                uri.host.equals("127.0.0.1", true) &&
                uri.port == 3000
        return BuildConfig.DEBUG &&
            uri.scheme.equals("http", true) &&
            uri.port in setOf(3000, 8080) &&
            uri.host.equals(portalHost, true) &&
            (LocalAgentUploadPolicy.isPrivateIpv4Host(uri.host.orEmpty()) || isolatedPhotoStudioLoopback)
    }

    private fun showShopManager() {
        if (portalUrl == null) {
            showOptionSheet(
                "Shop connection",
                listOf(
                    "Scan shop QR" to { scanQr() },
                    "Check shop Wi-Fi" to { scanQr(QrScanPurpose.WIFI_READINESS) }
                )
            )
            return
        }
        val shopName = shopDisplayName?.takeIf { it.isNotBlank() } ?: "this shop"
        showOptionSheet(
            "Connected to " + shopName,
            listOf(
                "Open print station" to { showPrint() },
                "Check shop Wi-Fi" to { scanQr(QrScanPurpose.WIFI_READINESS) },
                "Switch to another shop" to { switchShop() },
                "Disconnect this device" to {
                    showConfirmSheet(
                        "Disconnect from " + shopName + "?",
                        "This only removes the shop connection from this device. Shop orders, payments and your saved files are not deleted.",
                        "Disconnect",
                        destructive = true
                    ) { detachShop() }
                }
            )
        )
    }

    private fun detachShop() {
        val url = portalUrl
        val code = url?.let { Uri.parse(it).lastPathSegment }
            ?.takeIf { it.matches(Regex("^[A-Za-z0-9_-]{32}$")) }
        if (code != null) customerPrintSessionStore.clear(code)

        val page = webView
        if (page != null) {
            page.stopLoading()
            if (code != null) {
                val quotedCode = JSONObject.quote(code)
                page.evaluateJavascript(
                    "(function(){try{sessionStorage.removeItem('qrtoprint.customer-session');" +
                        "sessionStorage.removeItem('qrtoprint.customer-session:' + " + quotedCode +
                        ");}catch(e){}})();"
                ) {
                    page.loadUrl("about:blank")
                    page.clearHistory()
                }
            } else {
                page.loadUrl("about:blank")
                page.clearHistory()
            }
        }

        getSharedPreferences("smartprint.customer.last-shop", MODE_PRIVATE).edit()
            .remove("portalUrl")
            .remove("portalHost")
            .apply()
        portalUrl = null
        portalHost = null
        clearShopProfile()
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = false
        showHome()
        toast("Disconnected from this shop on this device. Shop orders and payments are unchanged.")
    }

    private fun switchShop() {
        if (portalUrl == null) { scanQr(); return }
        showConfirmSheet(
            "Switch shop?",
            "Your saved files and completed shop orders stay unchanged.",
            "Scan new shop"
        ) { scanQr() }
    }

    private fun scanQr(purpose: QrScanPurpose = QrScanPurpose.SHOP_CONNECTION) {
        val intent = Intent(this, QrScannerActivity::class.java)
            .putExtra(QrScannerActivity.EXTRA_SCAN_PURPOSE, purpose.wireValue)
        val localQaPackage = packageName == "com.quantaedge.smartprint.localqa" ||
            packageName.startsWith("com.quantaedge.smartprint.localqa.")
        val pauseCameraForQa = BuildConfig.DEBUG && localQaPackage &&
            getSharedPreferences("smartprint.qa.instrumentation", MODE_PRIVATE)
                .getBoolean("pauseQrCameraForPurposeTest", false)
        if (pauseCameraForQa) {
            intent.putExtra(QrScannerActivity.EXTRA_QA_PAUSE_CAMERA, true)
        }
        scannerLauncher.launch(intent)
    }

    private fun handleScannedQr(rawValue: String?, purpose: QrScanPurpose) {
        when (val route = QrScanRoutingPolicy.route(
            rawValue = rawValue,
            purpose = purpose,
            expectedServiceUrl = BuildConfig.SERVICE_BASE_URL,
            allowLocalDevelopment = BuildConfig.DEBUG
        )) {
            is QrScanRoute.ConnectToShop -> confirmShop(route.link)
            is QrScanRoute.VerifyShopWifi -> verifyShopPhoneConnection(route.link)
            is QrScanRoute.Unsupported -> toast(route.message)
        }
    }

    private fun showShopWifiVerificationResult(title: String, detail: String, verified: Boolean): BottomSheetDialog {
        val (dialog, root) = sheetRoot(title)
        val iconFrame = FrameLayout(this).apply { background = roundedDrawable(getColor(R.color.qp_soft), dp(22)) }
        val statusIcon = ImageView(this).apply {
            setImageResource(if (verified) R.drawable.ic_wifi_verified else R.drawable.ic_wifi)
            imageTintList = ColorStateList.valueOf(getColor(if (verified) R.color.qp_primary else R.color.qp_danger))
            contentDescription = if (verified) "Wi-Fi verified" else "Wi-Fi check not completed"
        }
        iconFrame.addView(statusIcon, FrameLayout.LayoutParams(dp(30), dp(30), Gravity.CENTER))
        root.addView(iconFrame, LinearLayout.LayoutParams(dp(62), dp(62)).apply {
            gravity = Gravity.CENTER_HORIZONTAL; topMargin = dp(2); bottomMargin = dp(13)
        })
        root.addView(text(detail, 13, R.color.qp_text, false).apply {
            gravity = Gravity.CENTER; textAlignment = View.TEXT_ALIGNMENT_CENTER
            setPadding(dp(4), 0, dp(4), dp(4))
        }, linearParams())
        root.addView(text(if (verified) "Your saved shop and files are unchanged." else "Your saved shop and files have not been changed.",
            11, R.color.qp_muted, false).apply {
            gravity = Gravity.CENTER; textAlignment = View.TEXT_ALIGNMENT_CENTER
            setPadding(dp(4), dp(9), dp(4), dp(2))
        }, linearParams())
        root.addView(button("Done", true).apply {
            minHeight = dp(46); setOnClickListener { dialog.dismiss() }
        }, linearParams().apply { topMargin = dp(15) })
        dialog.setCancelable(true)
        dialog.show()
        return dialog
    }

    private fun verifyShopPhoneConnection(link: LocalPhoneTestQrLink) {
        val (progress, progressRoot) = sheetRoot(
            "Checking shop Wi-Fi",
            "Confirming that this phone can reach the selected shop computer."
        )
        val progressRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(3), 0, dp(6))
        }
        val spinner = android.widget.ProgressBar(this).apply {
            isIndeterminate = true
            indeterminateTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
        }
        progressRow.addView(spinner, LinearLayout.LayoutParams(dp(28), dp(28)))
        progressRow.addView(text("Checking this network…", 12, R.color.qp_muted, false).apply {
            setPadding(dp(12), 0, 0, 0)
        }, LinearLayout.LayoutParams(0, -2, 1f))
        progressRoot.addView(progressRow, linearParams().apply { topMargin = dp(10) })
        progress.setCancelable(false)
        progress.show()

        Thread {
            val result = try {
                val connection = URL(link.url).openConnection() as HttpURLConnection
                try {
                    connection.instanceFollowRedirects = false
                    connection.connectTimeout = 5000
                    connection.readTimeout = 5000
                    connection.requestMethod = "GET"
                    connection.useCaches = false
                    connection.setRequestProperty("Accept", "text/html")
                    connection.setRequestProperty("Cache-Control", "no-cache")
                    when (connection.responseCode) {
                        HttpURLConnection.HTTP_OK -> Pair(
                            "Shop Wi-Fi verified",
                            "This phone reached the shop computer over the selected network. The shop's phone-readiness check is now marked as passed."
                        )
                        HttpURLConnection.HTTP_FORBIDDEN -> Pair(
                            "Wrong shop network",
                            "The shop computer was reached from a network other than the one selected by the owner. Connect to the shop's selected Wi-Fi or network and scan again."
                        )
                        HttpURLConnection.HTTP_GONE -> Pair(
                            "Readiness code expired",
                            "This one-time code has expired or has already been used. Ask the shop owner to start a new phone check."
                        )
                        else -> Pair(
                            "Connection not verified",
                            "The shop computer responded with HTTP ${connection.responseCode}. Check that the phone is on the selected shop network and try a fresh code."
                        )
                    }
                } finally {
                    connection.disconnect()
                }
            } catch (_: Exception) {
                Pair(
                    "Shop computer not reachable",
                    "Connect this phone to the same Wi-Fi or network selected by the shop owner, then ask the owner to create a fresh phone-check QR code."
                )
            }

            runOnUiThread {
                if (isFinishing || isDestroyed) return@runOnUiThread
                progress.dismiss()
                showShopWifiVerificationResult(result.first, result.second, result.first == "Shop Wi-Fi verified")
            }
        }.start()
    }

    private fun applySensitiveWindowProtection() {
        if (photoStudioOpen || idStudioScreenOpen || privateIdViewerOpen) {
            window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)
        }
    }

    private fun showIdStudioHome() {
        idStudioScreenOpen = true
        privateIdViewerOpen = false
        applySensitiveWindowProtection()
        pickerMode = false
        clearLibrarySelection()
        setToolbarPage("ID Studio")
        setToolbarBackAction("Back to Home", ::showHome)
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = portalUrl != null
        toolbar.menu.findItem(OPEN_WITH)?.isVisible = false
        accountAction.visibility = View.GONE
        setBottomNavigationVisible(false)
        content.removeAllViews()

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(14), dp(16), dp(24))
        }
        column.addView(text("SmartPrint ID Studio", 21, R.color.qp_text, true))
        column.addView(text(
            "Create your ID whenever you need it. Scanning, reviewing and saving work on this device without connecting to a shop.",
            12, R.color.qp_muted, false
        ).apply { setPadding(0, dp(5), 0, dp(14)) })
        column.addView(button("Create a new ID card", true).apply {
            setOnClickListener { startIdStudioCapture() }
        }, linearParams().apply { bottomMargin = dp(9) })

        val draftId = idStudioDraftId?.takeIf(idStudioStore::draftExists)
        if (draftId != null) {
            val nextSide = when {
                !idStudioStore.hasFront(draftId) -> "front"
                !idStudioStore.hasBack(draftId) -> "back"
                else -> "review"
            }
            column.addView(button(
                if (nextSide == "front") "Continue front-side scan"
                else if (nextSide == "back") "Continue back-side scan"
                else "Review scanned ID",
                false
            ).apply {
                setOnClickListener {
                    when (nextSide) {
                        "front" -> startIdStudioSideScan("front")
                        "back" -> showIdStudioBackStep()
                        else -> showIdStudioReview()
                    }
                }
            }, linearParams().apply { bottomMargin = dp(15) })
        }

        val prepared = idStudioStore.listPrepared()
        column.addView(text("Prepared ID cards", 15, R.color.qp_text, true)
            .apply { setPadding(0, dp(3), 0, dp(8)) })
        if (prepared.isEmpty()) {
            column.addView(text(
                "Prepared cards stay private here, separate from My Files and other apps.",
                12, R.color.qp_muted, false
            ).apply { setPadding(dp(2), dp(5), dp(2), dp(15)) })
        } else {
            prepared.forEach { file ->
                val item = card()
                val body = LinearLayout(this).apply {
                    orientation = LinearLayout.VERTICAL
                    setPadding(dp(12), dp(11), dp(12), dp(11))
                }
                body.addView(text(file.name, 13, R.color.qp_text, true).apply { maxLines = 2 })
                body.addView(text(
                    android.text.format.Formatter.formatShortFileSize(this@MainActivity, file.length()) +
                        " · Private to SmartPrint · A4 front/back sheet",
                    10, R.color.qp_muted, false
                ).apply { setPadding(0, dp(4), 0, dp(8)) })
                val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
                actions.addView(button("View", false).apply {
                    setOnClickListener { showDocumentViewer(file) }
                }, LinearLayout.LayoutParams(0, dp(38), 1f))
                actions.addView(button("Delete", false).apply {
                    setOnClickListener {
                        showConfirmSheet(
                            "Delete prepared ID?",
                            "This removes the private sheet from this device. It does not change a print request already sent to a shop.",
                            "Delete",
                            destructive = true
                        ) {
                            try {
                                idStudioStore.deletePrepared(file)
                                showIdStudioHome()
                                toast("Prepared ID deleted from this device.")
                            } catch (error: Exception) {
                                toast("Could not delete the prepared ID: " + message(error))
                            }
                        }
                    }
                }, LinearLayout.LayoutParams(0, dp(38), 1f).apply { marginStart = dp(8) })
                body.addView(actions)
                item.addView(body)
                column.addView(item, linearParams().apply { bottomMargin = dp(8) })
            }
        }
        val printHelp = if (portalUrl == null) {
            "When you are ready to print, scan a shop QR code. Your prepared ID will then appear in that shop's document picker."
        } else {
            "When ready, open the connected shop's Print page and select this ID from the private ID Studio section."
        }
        column.addView(text(printHelp, 11, R.color.qp_muted, false)
            .apply { setPadding(dp(2), dp(12), dp(2), dp(3)) })
        content.addView(ScrollView(this).apply {
            isFillViewport = true
            addView(column)
        }, frameParams())
    }

    private fun startIdStudioCapture() {
        val current = idStudioDraftId?.takeIf(idStudioStore::draftExists)
        if (current != null && (idStudioStore.hasFront(current) || idStudioStore.hasBack(current))) {
            showConfirmSheet(
                "Start a new ID scan?",
                "The unfinished scan currently saved in ID Studio will be discarded. Prepared ID cards are not affected.",
                "Discard and scan",
                destructive = true
            ) {
                try {
                    idStudioStore.deleteDraft(current)
                    idStudioDraftId = idStudioStore.createDraft()
                    idStudioFrontRotation = 0
                    idStudioBackRotation = 0
                    startIdStudioSideScan("front")
                } catch (error: Exception) {
                    toast("Could not start an ID scan: " + message(error))
                    showIdStudioHome()
                }
            }
            return
        }
        idStudioDraftId = try {
            current ?: idStudioStore.createDraft()
        } catch (error: Exception) {
            toast("Could not start an ID scan: " + message(error))
            return
        }
        idStudioFrontRotation = 0
        idStudioBackRotation = 0
        startIdStudioSideScan("front")
    }

    private fun startIdStudioSideScan(side: String) {
        val draftId = idStudioDraftId
        if (draftId == null || !idStudioStore.draftExists(draftId)) {
            toast("Start a new ID scan before capturing a side.")
            showIdStudioHome()
            return
        }
        if (side != "front" && side != "back") {
            toast("Choose the front or back side of the card.")
            showIdStudioHome()
            return
        }
        pendingIdStudioSide = side
        val options = GmsDocumentScannerOptions.Builder()
            .setGalleryImportAllowed(true)
            .setPageLimit(1)
            .setResultFormats(GmsDocumentScannerOptions.RESULT_FORMAT_JPEG)
            .setScannerMode(GmsDocumentScannerOptions.SCANNER_MODE_FULL)
            .build()
        GmsDocumentScanning.getClient(options)
            .getStartScanIntent(this)
            .addOnSuccessListener { sender ->
                documentScanLauncher.launch(IntentSenderRequest.Builder(sender).build())
            }
            .addOnFailureListener { error ->
                pendingIdStudioSide = null
                toast("The ID scanner is unavailable. Your saved scan is unchanged. " + message(error))
                if (idStudioStore.hasFront(draftId)) showIdStudioBackStep() else showIdStudioHome()
            }
    }

    private fun saveIdStudioScan(scan: GmsDocumentScanningResult, side: String) {
        val draftId = idStudioDraftId
        try {
            require(draftId != null && idStudioStore.draftExists(draftId)) {
                "The private ID scan session is no longer available. Start again."
            }
            val page = scan.pages?.firstOrNull()
                ?: error("The scanner did not return a corrected card image. Please scan again.")
            val uri = page.imageUri
                ?: error("The corrected card image is unavailable. Please scan again.")
            val input = contentResolver.openInputStream(uri)
                ?: error("The corrected card image could not be opened.")
            input.use { idStudioStore.saveScan(draftId, side, it) }
            toast(if (side == "front") "Front side saved privately." else "Back side saved privately.")
            if (side == "front") showIdStudioBackStep() else showIdStudioReview()
        } catch (error: Exception) {
            toast("Could not save the " + side + " scan: " + message(error))
            if (draftId != null && idStudioStore.draftExists(draftId) && idStudioStore.hasFront(draftId)) {
                showIdStudioBackStep()
            } else {
                showIdStudioHome()
            }
        }
    }

    private fun showIdStudioBackStep() {
        idStudioScreenOpen = true
        privateIdViewerOpen = false
        applySensitiveWindowProtection()
        setToolbarPage("ID Studio · Back side")
        setToolbarBackAction("Back to ID Studio", ::showIdStudioHome)
        toolbar.menu.findItem(OPEN_WITH)?.isVisible = false
        accountAction.visibility = View.GONE
        setBottomNavigationVisible(false)
        content.removeAllViews()
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(20), dp(22), dp(20), dp(24))
        }
        column.addView(text("Front side saved", 20, R.color.qp_text, true).apply {
            gravity = Gravity.CENTER
        })
        column.addView(text(
            "Turn the same card over and scan its reverse. Both corrected images stay together in this private preparation.",
            13, R.color.qp_muted, false
        ).apply {
            gravity = Gravity.CENTER
            textAlignment = View.TEXT_ALIGNMENT_CENTER
            setPadding(0, dp(7), 0, dp(18))
        })
        column.addView(button("Scan back side", true).apply {
            setOnClickListener { startIdStudioSideScan("back") }
        }, LinearLayout.LayoutParams(-1, dp(48)))
        column.addView(button("Retake front side", false).apply {
            setOnClickListener { startIdStudioSideScan("front") }
        }, LinearLayout.LayoutParams(-1, dp(44)).apply { topMargin = dp(9) })
        content.addView(column, frameParams())
    }

    private fun showIdStudioReview() {
        val draftId = idStudioDraftId
        if (draftId == null || !idStudioStore.draftExists(draftId) ||
            !idStudioStore.hasFront(draftId) || !idStudioStore.hasBack(draftId)) {
            toast("Scan both sides before preparing the ID sheet.")
            showIdStudioHome()
            return
        }
        idStudioScreenOpen = true
        privateIdViewerOpen = false
        applySensitiveWindowProtection()
        setToolbarPage("Review ID card")
        setToolbarBackAction("Back to ID Studio", ::showIdStudioHome)
        toolbar.menu.findItem(OPEN_WITH)?.isVisible = false
        accountAction.visibility = View.GONE
        setBottomNavigationVisible(false)
        content.removeAllViews()

        val front = idStudioStore.scanFile(draftId, "front")
        val back = idStudioStore.scanFile(draftId, "back")
        var cardWidthMm = IdCardLayoutPolicy.DEFAULT_CARD_WIDTH_MM
        var cardHeightMm = IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(12), dp(16), dp(22))
        }
        column.addView(text("Review the A4 print sheet", 20, R.color.qp_text, true))
        column.addView(text(
            "Check that both sides are upright, clear and complete. The preview shows their final positions and cut guides.",
            12, R.color.qp_muted, false
        ).apply { setPadding(0, dp(4), 0, dp(10)) })

        val previewCard = card()
        previewCard.setCardBackgroundColor(getColor(R.color.qp_surface))
        val preview = ImageView(this).apply {
            scaleType = ImageView.ScaleType.FIT_CENTER
            contentDescription = "A4 ID front and back print sheet preview"
            setPadding(dp(6), dp(6), dp(6), dp(6))
        }
        fun updatePreview(width: Float, height: Float) {
            val image = IdCardPdfComposer.renderSheetPreview(
                front, back, idStudioFrontRotation, idStudioBackRotation, width, height
            )
            preview.setImageBitmap(image)
        }
        try {
            updatePreview(cardWidthMm, cardHeightMm)
        } catch (error: Exception) {
            toast("Could not preview the ID sheet: " + message(error))
            showIdStudioBackStep()
            return
        }
        previewCard.addView(preview, LinearLayout.LayoutParams(-1, dp(360)))
        column.addView(previewCard, linearParams().apply { bottomMargin = dp(9) })

        val orientationRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        orientationRow.addView(button(
            if (idStudioFrontRotation == 0) "Rotate front 180°" else "Front upright",
            false
        ).apply {
            setOnClickListener {
                idStudioFrontRotation = (idStudioFrontRotation + 180) % 360
                showIdStudioReview()
            }
        }, LinearLayout.LayoutParams(0, dp(42), 1f))
        orientationRow.addView(button(
            if (idStudioBackRotation == 0) "Rotate back 180°" else "Back upright",
            false
        ).apply {
            setOnClickListener {
                idStudioBackRotation = (idStudioBackRotation + 180) % 360
                showIdStudioReview()
            }
        }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(7) })
        column.addView(orientationRow, linearParams().apply { bottomMargin = dp(12) })

        column.addView(text("Card dimensions · millimetres", 12, R.color.qp_text, true)
            .apply { setPadding(0, 0, 0, dp(5)) })
        fun dimensionInput(hintText: String, value: String): EditText = EditText(this).apply {
            setSingleLine(true)
            inputType = android.text.InputType.TYPE_CLASS_NUMBER or
                android.text.InputType.TYPE_NUMBER_FLAG_DECIMAL
            textSize = 14f
            setTextColor(getColor(R.color.qp_text))
            setHintTextColor(getColor(R.color.qp_muted))
            hint = hintText
            setText(value)
            background = roundedDrawable(getColor(R.color.qp_card), dp(9), getColor(R.color.qp_outline))
            setPadding(dp(10), dp(7), dp(10), dp(7))
        }
        val widthInput = dimensionInput("Width mm", cardWidthMm.toString())
        val heightInput = dimensionInput("Height mm", cardHeightMm.toString())
        val sizeRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        sizeRow.addView(widthInput, LinearLayout.LayoutParams(0, dp(46), 1f))
        sizeRow.addView(heightInput, LinearLayout.LayoutParams(0, dp(46), 1f).apply { marginStart = dp(8) })
        column.addView(sizeRow, linearParams().apply { bottomMargin = dp(6) })
        column.addView(text(
            "Default: 85.6 × 53.98 mm. Enter the physical size of your card. Both panels remain identical and fit side by side on A4.",
            10, R.color.qp_muted, false
        ).apply { setPadding(0, 0, 0, dp(8)) })
        column.addView(button("Update A4 preview", false).apply {
            setOnClickListener {
                val width = widthInput.text.toString().toFloatOrNull()
                val height = heightInput.text.toString().toFloatOrNull()
                if (width == null || height == null) {
                    toast("Enter valid card dimensions in millimetres.")
                    return@setOnClickListener
                }
                try {
                    IdCardLayoutPolicy.calculate(width, height)
                    cardWidthMm = width
                    cardHeightMm = height
                    updatePreview(width, height)
                } catch (error: Exception) {
                    toast(message(error))
                }
            }
        }, linearParams().apply { bottomMargin = dp(10) })

        column.addView(button("Retake front side", false).apply {
            setOnClickListener { startIdStudioSideScan("front") }
        }, linearParams().apply { bottomMargin = dp(7) })
        column.addView(button("Retake back side", false).apply {
            setOnClickListener { startIdStudioSideScan("back") }
        }, linearParams().apply { bottomMargin = dp(12) })
        column.addView(button("Prepare private ID print sheet", true).apply {
            setOnClickListener {
                val width = widthInput.text.toString().toFloatOrNull()
                val height = heightInput.text.toString().toFloatOrNull()
                if (width == null || height == null) {
                    toast("Enter valid card dimensions in millimetres.")
                    return@setOnClickListener
                }
                prepareIdStudioSheet(draftId, width, height)
            }
        }, linearParams())
        content.addView(ScrollView(this).apply {
            isFillViewport = true
            addView(column)
        }, frameParams())
    }

    private fun prepareIdStudioSheet(draftId: String, cardWidthMm: Float, cardHeightMm: Float) {
        val output = try {
            IdCardLayoutPolicy.calculate(cardWidthMm, cardHeightMm)
            idStudioStore.createPreparedOutput()
        } catch (error: Exception) {
            toast("Could not prepare the ID sheet: " + message(error))
            return
        }
        try {
            IdCardPdfComposer.compose(
                idStudioStore.scanFile(draftId, "front"),
                idStudioStore.scanFile(draftId, "back"),
                output,
                cardWidthMm,
                cardHeightMm,
                idStudioFrontRotation,
                idStudioBackRotation
            )
            require(idStudioStore.isPreparedDocument(output)) {
                "The private ID sheet could not be verified."
            }
            idStudioStore.deleteDraft(draftId)
            idStudioDraftId = null
            pendingIdStudioSide = null
            showIdStudioHome()
            toast("ID print sheet prepared privately. It will appear in the shop picker when you connect to a shop.")
        } catch (error: Exception) {
            output.delete()
            toast("ID sheet was not prepared: " + message(error) +
                ". Your scanned sides are still saved so you can retry.")
        }
    }

    private fun addIdStudioPickerSection(parent: LinearLayout) {
        val prepared = idStudioStore.listPrepared()
        if (prepared.isEmpty()) return
        val section = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(11), dp(10), dp(11), dp(10))
            background = roundedDrawable(getColor(R.color.qp_soft), dp(12))
        }
        section.addView(text("ID Studio · Private documents", 14, R.color.qp_text, true))
        section.addView(text(
            "These cards stay inside SmartPrint. Select one to attach it to this connected shop's print request.",
            11, R.color.qp_muted, false
        ).apply { setPadding(0, dp(4), 0, dp(8)) })
        prepared.forEach { file ->
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(0, dp(5), 0, dp(5))
            }
            val details = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            details.addView(text(file.name, 11, R.color.qp_text, true).apply { maxLines = 2 })
            details.addView(text(
                android.text.format.Formatter.formatShortFileSize(this@MainActivity, file.length()) +
                    " · A4 · Front + back",
                10, R.color.qp_muted, false
            ).apply { setPadding(0, dp(2), 0, 0) })
            row.addView(details, LinearLayout.LayoutParams(0, -2, 1f))
            row.addView(button("Select", true).apply {
                minHeight = dp(36)
                setOnClickListener { chooseFiles(listOf(file), listOf("ID Studio")) }
            }, LinearLayout.LayoutParams(dp(92), dp(36)).apply { marginStart = dp(8) })
            section.addView(row)
        }
        parent.addView(section, linearParams().apply { bottomMargin = dp(12) })
    }

    private fun startDocumentScan(destinationPath: String? = null) {
        pendingScanNeedsDestination = destinationPath == null
        pendingScanDestinationPath = destinationPath.orEmpty()
        // One ML Kit flow on phones and tablets. The SDK owns live capture, shutter/gallery
        // controls, crop, perspective correction, page review and PDF creation.
        val options = GmsDocumentScannerOptions.Builder()
            .setGalleryImportAllowed(true)
            .setPageLimit(50)
            .setResultFormats(
                GmsDocumentScannerOptions.RESULT_FORMAT_PDF,
                GmsDocumentScannerOptions.RESULT_FORMAT_JPEG
            )
            .setScannerMode(GmsDocumentScannerOptions.SCANNER_MODE_FULL)
            .build()
        GmsDocumentScanning.getClient(options)
            .getStartScanIntent(this)
            .addOnSuccessListener { sender ->
                documentScanLauncher.launch(IntentSenderRequest.Builder(sender).build())
            }
            .addOnFailureListener { error ->
                pendingScanNeedsDestination = false
                pendingScanDestinationPath = ""
                toast("Document scanner is unavailable on this device. You can still import photos or PDFs. ${message(error)}")
            }
    }

    private fun showScanDestinationPicker(scan: GmsDocumentScanningResult) {
        val (dialog, root) = sheetRoot("Save scanned document", "Choose where this scan should stay in My Files.")
        root.addView(button("My Files", true).apply {
            minHeight = dp(46)
            setOnClickListener { dialog.dismiss(); saveDocumentScan(scan, "") }
        }, linearParams().apply { bottomMargin = dp(7) })
        root.addView(button("Create a new folder", false).apply {
            minHeight = dp(42)
            setOnClickListener {
                nameDialog("New folder for this scan", "") { name ->
                    try {
                        val folder = store.createFolder("", name)
                        dialog.dismiss()
                        saveDocumentScan(scan, store.pathOf(folder))
                    } catch (error: Exception) {
                        toast("Folder not created: ${message(error)}")
                    }
                }
            }
        }, linearParams().apply { bottomMargin = dp(8) })
        val folders = store.root.walkTopDown()
            .filter { it.isDirectory && it.canonicalPath != store.root.canonicalPath }
            .sortedBy { runCatching { store.pathOf(it).lowercase(Locale.ROOT) }.getOrDefault(it.name.lowercase(Locale.ROOT)) }
            .toList()
        if (folders.isEmpty()) {
            root.addView(text("No saved folders yet. You can create one above.", 12, R.color.qp_muted, false)
                .apply { setPadding(dp(2), dp(8), 0, dp(8)) })
        } else {
            root.addView(text("Existing folders", 12, R.color.qp_muted, true)
                .apply { setPadding(dp(2), dp(4), 0, dp(7)) })
            val scroll = ScrollView(this).apply { isFillViewport = false }
            val list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            folders.forEach { folder ->
                val path = store.pathOf(folder)
                val label = "My Files  ›  " + path.replace("/", "  ›  ")
                list.addView(button(label, false).apply {
                    minHeight = dp(44)
                    gravity = Gravity.START or Gravity.CENTER_VERTICAL
                    setOnClickListener { dialog.dismiss(); saveDocumentScan(scan, path) }
                }, linearParams().apply { bottomMargin = dp(5) })
            }
            scroll.addView(list)
            root.addView(scroll, LinearLayout.LayoutParams(-1, dp(260)))
        }
        root.addView(button("Cancel scan", false).apply {
            minHeight = dp(40)
            setOnClickListener {
                dialog.dismiss()
                pendingScanNeedsDestination = false
                pendingScanDestinationPath = ""
                toast("Scan not saved.")
            }
        }, linearParams().apply { topMargin = dp(8) })
        dialog.show()
    }

    private fun saveDocumentScan(scan: GmsDocumentScanningResult, destinationPath: String) {
        try {
            val destination = store.folder(destinationPath)
            val pdf = scan.getPdf() ?: error("The scanner did not return a PDF.")
            val input = contentResolver.openInputStream(pdf.getUri())
                ?: error("Scanned PDF is unavailable.")
            val saved = input.use { store.importFile(it, "Scanned document.pdf", destinationPath) }
            pendingScanNeedsDestination = false
            pendingScanDestinationPath = ""
            folderPath = store.pathOf(destination)
            showFiles()
            val location = if (destinationPath.isBlank()) "My Files" else "My Files / " + destinationPath.replace("/", " / ")
            toast("Saved ${saved.name} in $location.")
        } catch (error: Exception) {
            toast("Could not save the scan: ${message(error)}")
            showScanDestinationPicker(scan)
        }
    }

    private fun confirmShop(link: StationQrLink) {
        clearShopProfile()
        portalHost = link.host
        portalUrl = link.url
        getSharedPreferences("smartprint.customer.last-shop", MODE_PRIVATE).edit()
            .putString("portalUrl", link.url)
            .putString("portalHost", link.host)
            .apply()
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = true
        val view = ensureWebView()
        showPrint()
        view.loadUrl(link.url)
    }

    private fun selectedChooserFiles(): List<File> = chooserSelectedFilePaths.mapNotNull { path ->
        runCatching { File(path).canonicalFile }.getOrNull()
    }.filter { it.isFile }

    private fun selectedChooserFolders(): List<File> = chooserSelectedFolderPaths.mapNotNull { path ->
        runCatching { File(path).canonicalFile }.getOrNull()
    }.filter { it.isDirectory }

    private fun setChooserFolderSelected(folder: File, checked: Boolean) {
        val path = folder.canonicalPath
        if (checked) chooserSelectedFolderPaths.add(path) else chooserSelectedFolderPaths.remove(path)
        refreshLibrary()
    }

    private fun attachSelectedFolders() {
        val selectedFolders = selectedChooserFolders()
        if (selectedFolders.isEmpty()) { toast("Select at least one folder."); return }
        val documents = selectedFolders.flatMap { folder ->
            val groupName = store.pathOf(folder).replace("/", " - ").take(80).ifBlank { "Loose files" }
            store.list(folder).filter { file ->
                file.isFile && DocumentNamePolicy.isPrintable(file.name, file.length())
            }.map { file -> file to groupName }
        }.distinctBy { it.first.canonicalPath }
        if (documents.isEmpty()) {
            toast("No printable PDF, JPG or PNG files were found directly inside these folders. Nested folders are not included.")
            return
        }
        val error = PrintBatchSelectionPolicy.validationError(documents.map { it.first })
        if (error != null) { toast(error); return }
        if (selectedFolders.any { folder -> store.list(folder).any { it.isDirectory } }) {
            toast("Only files directly inside each selected folder are included. Nested folders are skipped.")
        }
        chooseFiles(documents.map { it.first }, documents.map { it.second })
    }

    private fun setChooserFileSelected(file: File, checked: Boolean) {
        val path = file.canonicalPath
        if (!checked) {
            chooserSelectedFilePaths.remove(path)
            refreshLibrary()
            return
        }
        val candidate = selectedChooserFiles().filterNot { it.canonicalPath == path } + file
        val error = PrintBatchSelectionPolicy.validationError(candidate)
        if (error != null) {
            toast(error)
            return
        }
        chooserSelectedFilePaths.add(path)
        refreshLibrary()
    }

    private fun printBatchGroupName(file: File): String {
        if (idStudioStore.isPreparedDocument(file)) return "ID Studio"
        val parentPath = file.parentFile?.let { runCatching { store.pathOf(it) }.getOrDefault("") }.orEmpty()
        return parentPath.replace("/", " - ").take(80).ifBlank { "Loose files" }
    }

    private fun chooseFile(file: File) = chooseFiles(listOf(file), listOf(printBatchGroupName(file)))

    private fun chooseFiles(files: List<File>, groupNames: List<String>? = null) {
        val callback = fileCallback
        if (callback == null) { toast("Open a document field in the print page first."); return }
        val error = PrintBatchSelectionPolicy.validationError(files)
        if (error != null) { toast(error); return }
        if (!fileChooserAllowsMultiple && files.size != 1) {
            toast("This print field accepts one document at a time.")
            return
        }
        try {
            val uris = files.map { file ->
                FileProvider.getUriForFile(this, "$packageName.documents", file)
            }.toTypedArray()
            val safeGroupNames = groupNames?.takeIf { it.size == files.size }
                ?: files.map(::printBatchGroupName)
            val previewTokens = JSONArray().apply {
                files.forEach { put(createCustomerUploadPdfPreview(it) ?: JSONObject.NULL) }
            }
            val nativePrintKinds = JSONArray().apply {
                files.forEach { put(if (idStudioStore.isPreparedDocument(it)) "ID_DOCUMENT" else "DOCUMENT") }
            }
            val metadataScript = "window.__smartPrintNativePickerGroups = " +
                JSONArray().apply { safeGroupNames.forEach { put(it) } }.toString() + ";" +
                "window.__smartPrintNativePdfPreviews = " + previewTokens.toString() + ";" +
                "window.__smartPrintNativePrintKinds = " + nativePrintKinds.toString() + ";"
            val deliverSelection = {
                fileCallback = null
                chooserSelectedFilePaths.clear()
                chooserSelectedFolderPaths.clear()
                fileChooserAllowsMultiple = false
                fileChooserSelectFolders = false
                showPrint()
                val activeWebView = webView
                if (activeWebView != null) {
                    activeWebView.evaluateJavascript(metadataScript) { callback.onReceiveValue(uris) }
                } else {
                    callback.onReceiveValue(uris)
                }
            }
            deliverSelection()
        } catch (e: Exception) {
            cancelPicker()
            toast("Could not attach the selected documents: " + message(e))
        }
    }

    private fun deliverPhoneDocuments(uris: List<Uri>) {
        val callback = fileCallback ?: return
        val selected = uris.distinct()
        if (selected.isEmpty()) return
        if (selected.size > 20) {
            toast("Choose up to 20 documents at once.")
            return
        }
        if (!fileChooserAllowsMultiple && selected.size != 1) {
            toast("This print field accepts one document at a time.")
            return
        }
        fileCallback = null
        chooserSelectedFilePaths.clear()
        chooserSelectedFolderPaths.clear()
        fileChooserAllowsMultiple = false
        fileChooserSelectFolders = false
        showPrint()
        val previewTokens = JSONArray().apply {
            selected.forEach { put(createCustomerUploadPdfPreview(it) ?: JSONObject.NULL) }
        }
        val metadataScript = "window.__smartPrintNativePdfPreviews = $previewTokens;"
        val activeWebView = webView
        if (activeWebView != null) {
            activeWebView.evaluateJavascript(metadataScript) {
                runCatching { callback.onReceiveValue(selected.toTypedArray()) }
                    .onFailure { toast("Could not attach the selected phone documents.") }
            }
        } else {
            runCatching { callback.onReceiveValue(selected.toTypedArray()) }
                .onFailure { toast("Could not attach the selected phone documents.") }
        }
    }

    private fun createCustomerUploadPdfPreview(file: File): String? {
        val source = runCatching { file.canonicalFile }.getOrNull() ?: return null
        if (!source.isFile || !source.name.endsWith(".pdf", true) ||
            source.length() !in 1..MAX_CUSTOMER_UPLOAD_PREVIEW_BYTES) return null
        val token = UUID.randomUUID().toString()
        customerUploadPdfPreviews[token] = source
        return token
    }

    private fun createCustomerUploadPdfPreview(uri: Uri): String? {
        val name = queryName(uri)
        if (!name.endsWith(".pdf", true)) return null
        val directory = File(cacheDir, "customer-upload-pdf-preview")
        if (!directory.exists() && !directory.mkdirs()) return null
        val canonicalDirectory = directory.canonicalFile
        val existingPreviewBytes = customerUploadPdfPreviews.values.sumOf { preview ->
            val parent = runCatching { preview.canonicalFile.parentFile }.getOrNull()
            if (parent == canonicalDirectory) preview.length() else 0L
        }
        val target = runCatching { File.createTempFile("upload-preview-", ".pdf", directory) }.getOrNull()
            ?: return null
        return try {
            val input = contentResolver.openInputStream(uri) ?: error("Selected PDF is unavailable.")
            val copiedBytes = input.use { source ->
                target.outputStream().use { destination ->
                    val buffer = ByteArray(8192)
                    var total = 0L
                    while (true) {
                        val count = source.read(buffer)
                        if (count < 0) break
                        total += count
                        require(total <= MAX_CUSTOMER_UPLOAD_PREVIEW_BYTES) {
                            "This PDF is too large to preview."
                        }
                        require(existingPreviewBytes + total <= MAX_CUSTOMER_UPLOAD_PREVIEW_BATCH_BYTES) {
                            "The selected PDFs exceed the preview limit for one batch."
                        }
                        destination.write(buffer, 0, count)
                    }
                    total
                }
            }
            require(copiedBytes > 0L) { "Selected PDF is empty." }
            val token = UUID.randomUUID().toString()
            customerUploadPdfPreviews[token] = target
            token
        } catch (_: Exception) {
            target.delete()
            null
        }
    }

    private fun isCustomerUploadPdfPreviewToken(token: String): Boolean =
        runCatching { UUID.fromString(token).toString() == token }.getOrDefault(false)

    private fun retainCustomerUploadPdfPreviews(rawTokens: String) {
        val retained = rawTokens.split(',')
            .map(String::trim)
            .filter(::isCustomerUploadPdfPreviewToken)
            .toSet()
        val previewDirectory = File(cacheDir, "customer-upload-pdf-preview").canonicalFile
        customerUploadPdfPreviews.entries.toList().forEach { entry ->
            if (entry.key !in retained && customerUploadPdfPreviews.remove(entry.key, entry.value)) {
                val parent = runCatching { entry.value.canonicalFile.parentFile }.getOrNull()
                if (parent == previewDirectory) entry.value.delete()
            }
        }
    }

    private fun clearCustomerUploadPdfPreviews() {
        val previewDirectory = File(cacheDir, "customer-upload-pdf-preview").canonicalFile
        customerUploadPdfPreviews.values.forEach { file ->
            val parent = runCatching { file.canonicalFile.parentFile }.getOrNull()
            if (parent == previewDirectory) file.delete()
        }
        customerUploadPdfPreviews.clear()
        previewDirectory.deleteRecursively()
    }

    private fun cancelPicker() {
        fileCallback?.onReceiveValue(null)
        fileCallback = null
        chooserSelectedFilePaths.clear()
        chooserSelectedFolderPaths.clear()
        fileChooserAllowsMultiple = false
        fileChooserSelectFolders = false
        showPrint()
    }

    private fun importUri(uri: Uri, destination: String) {
        val name = queryName(uri)
        val input = contentResolver.openInputStream(uri) ?: error("Selected file cannot be opened.")
        input.use { store.importFile(it, name, destination) }
    }

    private fun queryName(uri: Uri): String {
        try {
            contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null).use { cursor: Cursor? ->
                if (cursor != null && cursor.moveToFirst()) {
                    val i = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (i >= 0) cursor.getString(i)?.takeIf { it.isNotBlank() }?.let { return it }
                }
            }
        } catch (_: Exception) { }
        return uri.lastPathSegment?.substringAfterLast('/')?.ifBlank { null } ?: "Imported document"
    }

    private fun importDirectory(
        source: DocumentFile,
        destinationPath: String,
        depth: Int,
        counts: IntArray,
        budget: FolderTransferBudget
    ): Boolean {
        for (child in source.listFiles()) {
            // Count folders as well as files so empty-directory trees cannot bypass the cap.
            if (!budget.tryTake(depth)) return false
            try {
                val name = child.name ?: "Imported item"
                if (child.isDirectory) {
                    if (depth >= MAX_TREE_DEPTH) return false
                    val folder = store.createFolder(destinationPath, name, allowExisting = true)
                    if (!importDirectory(child, store.pathOf(folder), depth + 1, counts, budget)) return false
                } else if (child.isFile) {
                    val input = contentResolver.openInputStream(child.uri) ?: error("File cannot be opened.")
                    input.use { store.importFile(it, name, destinationPath) }
                    counts[0]++
                }
            } catch (_: Exception) {
                counts[1]++
            }
        }
        return true
    }

    private fun copyFolder(
        source: File,
        targetParent: DocumentFile,
        depth: Int,
        count: IntArray,
        budget: FolderTransferBudget
    ): Boolean {
        if (depth > MAX_TREE_DEPTH) return false
        val target = targetParent.findFile(source.name)?.takeIf { it.isDirectory }
            ?: targetParent.createDirectory(source.name) ?: error("Could not create ${source.name}.")
        for (child in store.list(source)) {
            // Count folders as well as files so empty-folder trees stay bounded too.
            if (!budget.tryTake(depth)) return false
            if (child.isDirectory) {
                if (!copyFolder(child, target, depth + 1, count, budget)) return false
            } else {
                val outputFile = target.createFile(DocumentNamePolicy.mimeType(child.name), uniqueName(target, child.name))
                    ?: error("Could not create ${child.name}.")
                val output = contentResolver.openOutputStream(outputFile.uri, "w") ?: error("Cannot write ${child.name}.")
                output.use { out -> FileInputStream(child).use { it.copyTo(out) } }
                count[0]++
            }
        }
        return true
    }

    private fun uniqueName(folder: DocumentFile, name: String): String {
        if (folder.findFile(name) == null) return name
        val extAt = name.lastIndexOf('.')
        val base = if (extAt > 0) name.substring(0, extAt) else name
        val ext = if (extAt > 0) name.substring(extAt) else ""
        for (n in 2 until 10_000) {
            val candidate = "$base ($n)$ext"
            if (folder.findFile(candidate) == null) return candidate
        }
        return "$base-${System.currentTimeMillis()}$ext"
    }

    private fun supportsInAppViewer(file: File): Boolean {
        if (!file.isFile) return false
        return file.extension.lowercase(Locale.ROOT) in setOf(
            "pdf", "jpg", "jpeg", "png", "webp", "bmp", "gif"
        )
    }

    private fun showDocumentViewer(file: File, startPosition: Int? = null) {
        if (!supportsInAppViewer(file)) {
            openDocumentExternally(file)
            return
        }
        if (!file.exists() || !file.isFile) {
            toast("This document is no longer available.")
            return
        }

        documentViewer?.dispose()
        customerUploadPdfPreviewOpen = false
        viewingDocument = file
        documentViewerReturnToIdStudio = idStudioStore.isPreparedDocument(file)
        privateIdViewerOpen = documentViewerReturnToIdStudio
        if (documentViewerReturnToIdStudio) idStudioScreenOpen = false
        applySensitiveWindowProtection()
        pickerMode = false
        profileVisible = false
        clearLibrarySelection()
        setToolbarPage(file.name)
        setToolbarBackAction(
            if (documentViewerReturnToIdStudio) "Back to ID Studio" else "Back to My Files",
            ::closeDocumentViewer
        )
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = false
        toolbar.menu.findItem(OPEN_WITH)?.isVisible = !privateIdViewerOpen
        accountAction.visibility = View.GONE
        setBottomNavigationVisible(false)
        content.removeAllViews()

        val images = if (file.extension.equals("pdf", true)) {
            emptyList()
        } else {
            val siblings = file.parentFile?.let { folder ->
                runCatching {
                    store.list(folder).filter { candidate ->
                        candidate.isFile && candidate.extension.lowercase(Locale.ROOT) in
                            setOf("jpg", "jpeg", "png", "webp", "bmp", "gif")
                    }
                }.getOrDefault(emptyList())
            }.orEmpty()
            (if (siblings.any { sameDocument(it, file) }) siblings else siblings + file)
        }
        val reader = SmartPrintDocumentViewer(this, file, images, startPosition) { current ->
            viewingDocument = current
            setToolbarPage(current.name)
        }
        documentViewer = reader
        content.addView(reader, frameParams())
    }

    private fun showCustomerUploadPdfPreview(token: String) {
        val file = customerUploadPdfPreviews[token]
            ?.takeIf { it.isFile && it.name.endsWith(".pdf", true) }
        if (file == null) {
            toast("Please select this PDF again before previewing it.")
            return
        }

        documentViewer?.dispose()
        viewingDocument = file
        customerUploadPdfPreviewOpen = true
        pickerMode = false
        profileVisible = false
        setToolbarPage(file.name)
        setToolbarBackAction("Back to upload", ::closeCustomerUploadPdfPreview)
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = false
        toolbar.menu.findItem(OPEN_WITH)?.isVisible = false
        accountAction.visibility = View.GONE
        setBottomNavigationVisible(false)
        content.removeAllViews()

        val reader = SmartPrintDocumentViewer(this, file, emptyList(), null) {}
        documentViewer = reader
        content.addView(reader, frameParams())
    }

    private fun closeCustomerUploadPdfPreview() {
        documentViewer?.dispose()
        documentViewer = null
        viewingDocument = null
        customerUploadPdfPreviewOpen = false
        showPrint()
    }

    private fun sameDocument(left: File, right: File): Boolean =
        runCatching { left.canonicalPath == right.canonicalPath }.getOrDefault(false)

    private fun closeDocumentViewer() {
        documentViewer?.dispose()
        documentViewer = null
        viewingDocument = null
        customerUploadPdfPreviewOpen = false
        privateIdViewerOpen = false
        applySensitiveWindowProtection()
        if (documentViewerReturnToIdStudio) {
            documentViewerReturnToIdStudio = false
            showIdStudioHome()
        } else showFiles()
    }

    private fun openDocument(file: File) {
        if (supportsInAppViewer(file)) showDocumentViewer(file)
        else openDocumentExternally(file)
    }

    private fun openDocumentExternally(file: File) {
        if (idStudioStore.isPreparedDocument(file)) {
            toast("Prepared ID documents can only be viewed and printed inside SmartPrint.")
            return
        }
        try {
            val uri = FileProvider.getUriForFile(this, "$packageName.documents", file)
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, DocumentNamePolicy.mimeType(file.name))
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            startActivity(Intent.createChooser(intent, "Open document"))
        } catch (_: Exception) { toast("No app could open this document.") }
    }

    private fun shareDocument(file: File) {
        if (idStudioStore.isPreparedDocument(file)) {
            toast("Prepared ID documents cannot be shared outside SmartPrint.")
            return
        }
        try {
            val uri = FileProvider.getUriForFile(this, "$packageName.documents", file)
            val intent = Intent(Intent.ACTION_SEND).apply {
                type = DocumentNamePolicy.mimeType(file.name)
                putExtra(Intent.EXTRA_STREAM, uri)
                clipData = ClipData.newUri(contentResolver, file.name, uri)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            startActivity(Intent.createChooser(intent, "Share document"))
        } catch (_: Exception) { toast("Could not share this document.") }
    }

    private fun handleShareIntent(intent: Intent?) {
        if (intent == null) return
        val uris = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE -> {
                @Suppress("DEPRECATION")
                intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.toList().orEmpty()
            }
            else -> emptyList()
        }
        if (uris.isEmpty()) return
        var ok = 0
        var failed = 0
        uris.forEach { uri ->
            try { importUri(uri, folderPath); ok++ } catch (_: Exception) { failed++ }
        }
        reportImport(ok, failed)
        showFiles()
    }

    private fun launchExternal(uri: Uri) {
        try { startActivity(Intent(Intent.ACTION_VIEW, uri)) }
        catch (_: Exception) { toast("No app is available to open this link.") }
    }

    private fun launchIntent(raw: String) {
        try { startActivity(Intent.parseUri(raw, Intent.URI_INTENT_SCHEME).apply { component = null }) }
        catch (_: Exception) { toast("No app is available to open this payment link.") }
    }

    private fun reportImport(ok: Int, failed: Int) {
        when {
            ok == 0 && failed > 0 -> toast("No files imported. Check file access and free storage.")
            failed > 0 -> toast("Imported $ok file(s); $failed could not be imported.")
            ok > 0 -> toast("Imported $ok file(s).")
        }
    }

    private fun parent(path: String) = path.substringBeforeLast('/', "").trim('/')
    private fun card() = MaterialCardView(this).apply {
        radius = dp(21).toFloat()
        cardElevation = dp(2).toFloat()
        strokeWidth = dp(1)
        strokeColor = getColor(R.color.qp_glass_border)
        rippleColor = ColorStateList.valueOf(android.graphics.Color.TRANSPARENT)
        setCardBackgroundColor(getColor(R.color.qp_glass_card))
        useCompatPadding = false
    }
    private fun text(value: String, size: Int, color: Int, bold: Boolean) = TextView(this).apply {
        text = value
        textSize = maxOf(size, 11).toFloat()
        setTextColor(getColor(color))
        if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD)
        includeFontPadding = false
    }
    private fun button(value: String, primary: Boolean) = MaterialButton(this).apply {
        text = value
        isAllCaps = false
        minHeight = dp(42)
        minWidth = dp(0)
        insetTop = 0
        insetBottom = 0
        cornerRadius = dp(16)
        setPadding(dp(12), dp(7), dp(12), dp(7))
        textSize = 13f
        if (primary) {
            backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
            setTextColor(getColor(R.color.qp_primary_content))
        } else {
            strokeWidth = dp(1)
            strokeColor = ColorStateList.valueOf(getColor(R.color.qp_glass_border))
            backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_glass_inset))
            setTextColor(getColor(R.color.qp_text))
        }
    }
    private fun linearParams() = LinearLayout.LayoutParams(-1, -2)
    private fun frameParams(): FrameLayout.LayoutParams {
        val width = if (resources.configuration.screenWidthDp >= 1200) dp(1120) else -1
        return FrameLayout.LayoutParams(width, -1, Gravity.TOP or Gravity.CENTER_HORIZONTAL)
    }
    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()
    private fun toast(value: String) = Toast.makeText(this, value, Toast.LENGTH_LONG).show()
    private fun message(e: Exception) = e.message?.takeIf { it.isNotBlank() } ?: "Unexpected error"
}
