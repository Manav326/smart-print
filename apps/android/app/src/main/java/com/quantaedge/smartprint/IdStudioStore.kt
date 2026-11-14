package com.quantaedge.smartprint

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import android.util.Base64
import org.json.JSONObject
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * App-private AES-GCM vault for identity-card scans and prepared sheets.
 * Files in id-studio are ciphertext only. Scan composition uses byte arrays
 * in memory. A prepared PDF is materialised only into the app cache while it
 * is being viewed or attached to the connected-shop picker.
 */
class IdStudioStore(filesDir: File, cacheDir: File) {
    val root = File(filesDir, "id-studio")
    private val drafts = File(root, ".drafts")
    private val transientRoot = File(cacheDir, "id-studio-transient")
    private val vaultKey: SecretKey by lazy(LazyThreadSafetyMode.SYNCHRONIZED) { loadOrCreateKey() }

    init {
        check(root.exists() || root.mkdirs()) { "Private ID storage could not be created." }
        check(root.isDirectory) { "Private ID storage is unavailable." }
        check(drafts.exists() || drafts.mkdirs()) { "Private ID scan storage could not be created." }
        check(drafts.isDirectory) { "Private ID scan storage is unavailable." }
        check(transientRoot.exists() || transientRoot.mkdirs()) { "Private ID working storage could not be created." }
        cleanupTransientFiles()
        vaultKey
        migrateLegacyFiles()
        cleanupAbandonedDrafts()
    }

    fun createDraft(): String {
        val id = UUID.randomUUID().toString()
        val directory = File(drafts, id).canonicalFile
        require(directory.parentFile?.canonicalPath == drafts.canonicalPath) {
            "ID scan storage path is invalid."
        }
        check(directory.mkdir()) { "A private ID scan session could not be started." }
        return id
    }

    fun draftDirectory(draftId: String): File {
        require(isValidId(draftId)) { "This ID scan session is invalid." }
        val directory = File(drafts, draftId).canonicalFile
        require(directory.parentFile?.canonicalPath == drafts.canonicalPath) {
            "ID scan storage path is invalid."
        }
        require(directory.isDirectory) { "This private ID scan session is no longer available." }
        return directory
    }

    fun saveScan(draftId: String, side: String, input: InputStream): File {
        require(side == "front" || side == "back") { "Choose the front or back side of the card." }
        val bytes = input.use { readBounded(it, MAX_SCAN_BYTES) }
        require(bytes.size >= MIN_SCAN_BYTES) { "The selected photo is empty or incomplete." }
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        require(bounds.outWidth > 0 && bounds.outHeight > 0) {
            "This photo format is not supported or the image is damaged. Choose a clear JPG, PNG or WebP photo."
        }
        val shortEdge = minOf(bounds.outWidth, bounds.outHeight)
        val longEdge = maxOf(bounds.outWidth, bounds.outHeight)
        require(shortEdge >= MIN_IMAGE_SHORT_EDGE && longEdge >= MIN_IMAGE_LONG_EDGE) {
            "The scan is too small for clear ID printing. Keep the whole card in frame and scan it closer."
        }
        val ratio = longEdge.toFloat() / shortEdge.toFloat()
        require(ratio in MIN_CARD_ASPECT_RATIO..MAX_CARD_ASPECT_RATIO) {
            "This does not look like a card-shaped scan. Retake it with the card edges inside the frame."
        }
        require(bounds.outWidth.toLong() * bounds.outHeight.toLong() <= MAX_IMAGE_PIXELS) {
            "The scanned image is too large to prepare safely. Please scan again."
        }
        val decoded = BitmapFactory.decodeByteArray(
            bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = 4 }
        ) ?: error("This photo is incomplete or unreadable. Choose another photo.")
        decoded.recycle()
        val directory = draftDirectory(draftId)
        val target = File(directory, "$side.jpg")
        writeEncryptedAtomically(target, bytes)
        return target
    }

    fun readScanBytes(draftId: String, side: String): ByteArray {
        require(side == "front" || side == "back") { "Choose the front or back side of the card." }
        val directory = draftDirectory(draftId)
        val file = File(directory, "$side.jpg").canonicalFile
        require(file.parentFile?.canonicalPath == directory.canonicalPath && file.isFile) {
            "The requested scan is no longer available. Scan that side again."
        }
        return readVaultOrMigrate(file, MAX_SCAN_BYTES)
    }

    // Draft-list state only needs to know whether a protected scan exists.
    // Avoid decrypting full-resolution images on the UI thread just to label Resume.
    fun hasFront(draftId: String): Boolean = hasScan(draftId, "front")

    fun hasBack(draftId: String): Boolean = hasScan(draftId, "back")

    private fun hasScan(draftId: String, side: String): Boolean = runCatching {
        require(side == "front" || side == "back")
        val directory = draftDirectory(draftId)
        val scan = File(directory, "$side.jpg").canonicalFile
        scan.parentFile?.canonicalPath == directory.canonicalPath &&
            scan.isFile && scan.length() > 0L
    }.getOrDefault(false)

    fun savePreparedOutput(
        pdfBytes: ByteArray,
        frontBytes: ByteArray? = null,
        backBytes: ByteArray? = null,
        suppliedMetadata: JSONObject? = null
    ): File {
        require(pdfBytes.size.toLong() in 1L..MAX_PREPARED_BYTES) {
            "The prepared ID sheet is too large. Please scan the card again with a closer crop."
        }
        require(hasPdfSignature(pdfBytes)) { "The private ID sheet could not be verified." }
        require((frontBytes == null) == (backBytes == null)) {
            "Both original ID sides are required to enable editing."
        }
        val id = UUID.randomUUID().toString()
        val output = preparedFile(id).canonicalFile
        require(output.parentFile?.canonicalPath == root.canonicalPath && !output.exists()) {
            "A private ID document with this name already exists."
        }
        val metadata = JSONObject(suppliedMetadata?.toString() ?: "{}").apply {
            put("id", id)
            put("title", safeTitle(optString("title").ifBlank { "ID card" }))
            put("createdUtc", System.currentTimeMillis())
            put("updatedUtc", System.currentTimeMillis())
        }
        try {
            writeEncryptedAtomically(output, pdfBytes)
            if (frontBytes != null && backBytes != null) {
                writeEncryptedAtomically(preparedSidecar(id, "front"), frontBytes)
                writeEncryptedAtomically(preparedSidecar(id, "back"), backBytes)
            }
            writeEncryptedAtomically(preparedSidecar(id, "meta"), metadata.toString().toByteArray(Charsets.UTF_8))
        } catch (error: Exception) {
            output.delete()
            preparedSidecar(id, "front").delete()
            preparedSidecar(id, "back").delete()
            preparedSidecar(id, "meta").delete()
            throw error
        }
        return output
    }

    fun updatePreparedOutput(
        file: File,
        pdfBytes: ByteArray,
        frontBytes: ByteArray,
        backBytes: ByteArray,
        suppliedMetadata: JSONObject
    ): File {
        require(isPreparedDocument(file)) { "Only a prepared ID document can be edited." }
        require(pdfBytes.size.toLong() in 1L..MAX_PREPARED_BYTES && hasPdfSignature(pdfBytes)) {
            "The edited ID sheet could not be verified."
        }
        require(frontBytes.isNotEmpty() && backBytes.isNotEmpty()) {
            "Both original ID sides are required to save edits."
        }
        val id = idFromPreparedFile(file)
        val existing = readMetadata(file)
        val metadata = JSONObject(suppliedMetadata.toString()).apply {
            put("id", id)
            put("title", safeTitle(existing.optString("title", optString("title"))))
            put("createdUtc", existing.optLong("createdUtc", System.currentTimeMillis()))
            put("updatedUtc", System.currentTimeMillis())
        }
        writeEncryptedAtomically(file, pdfBytes)
        writeEncryptedAtomically(preparedSidecar(id, "front"), frontBytes)
        writeEncryptedAtomically(preparedSidecar(id, "back"), backBytes)
        writeEncryptedAtomically(preparedSidecar(id, "meta"), metadata.toString().toByteArray(Charsets.UTF_8))
        return file
    }

    fun readMetadata(file: File): JSONObject {
        require(isPreparedDocument(file)) { "Only a prepared ID document can be opened here." }
        val sidecar = preparedSidecar(idFromPreparedFile(file), "meta")
        if (!sidecar.isFile) return JSONObject()
            .put("id", idFromPreparedFile(file))
            .put("title", "ID card")
        val bytes = readVaultOrMigrate(sidecar, MAX_METADATA_BYTES)
        return JSONObject(String(bytes, Charsets.UTF_8))
    }

    fun renamePrepared(file: File, title: String): JSONObject {
        val metadata = readMetadata(file)
        metadata.put("title", safeTitle(title))
        metadata.put("updatedUtc", System.currentTimeMillis())
        writeEncryptedAtomically(
            preparedSidecar(idFromPreparedFile(file), "meta"),
            metadata.toString().toByteArray(Charsets.UTF_8)
        )
        return metadata
    }

    fun canEditPreparedDocument(file: File): Boolean = runCatching {
        isPreparedDocument(file) &&
            preparedSidecar(idFromPreparedFile(file), "front").isFile &&
            preparedSidecar(idFromPreparedFile(file), "back").isFile
    }.getOrDefault(false)

    fun readPreparedSideBytes(file: File, side: String): ByteArray {
        require(side == "front" || side == "back") { "Choose the front or back side of the card." }
        require(canEditPreparedDocument(file)) {
            "The original scans for this older ID document are not available for editing."
        }
        return readVaultOrMigrate(preparedSidecar(idFromPreparedFile(file), side), MAX_SCAN_BYTES)
    }

    fun createCardFaceThumbnailBase64(file: File, side: String = "front"): String? = runCatching {
        require(side == "front" || side == "back") { "Choose the front or back side of the card." }
        val metadata = readMetadata(file)
        val sourceBytes = readPreparedSideBytes(file, side)
        try {
            val rotation = metadata.optInt(if (side == "front") "frontRotation" else "backRotation", 0)
                .takeIf { it == 0 || it == 180 } ?: 0
            val source = IdCardPdfComposer.renderSidePreview(sourceBytes, rotation)
            try {
                val width = minOf(CARD_THUMBNAIL_WIDTH, source.width).coerceAtLeast(1)
                val height = maxOf(1, (source.height.toFloat() * width / source.width).toInt())
                val thumbnail = if (width == source.width) source
                    else Bitmap.createScaledBitmap(source, width, height, true)
                try {
                    ByteArrayOutputStream().use { output ->
                        thumbnail.compress(Bitmap.CompressFormat.JPEG, 86, output)
                        Base64.encodeToString(output.toByteArray(), Base64.NO_WRAP)
                    }
                } finally {
                    if (thumbnail !== source) thumbnail.recycle()
                }
            } finally {
                source.recycle()
            }
        } finally {
            sourceBytes.fill(0)
        }
    }.getOrNull()

    fun createThumbnailBase64(file: File): String? {
        val materialized = runCatching { materializePreparedFile(file) }.getOrNull() ?: return null
        return try {
            val descriptor = ParcelFileDescriptor.open(materialized, ParcelFileDescriptor.MODE_READ_ONLY)
            descriptor.use { parcel ->
                PdfRenderer(parcel).use { renderer ->
                    if (renderer.pageCount < 1) return null
                    renderer.openPage(0).use { page ->
                        val width = THUMBNAIL_WIDTH
                        val height = maxOf(1, (width.toFloat() * page.height / page.width).toInt())
                        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
                        try {
                            page.render(bitmap, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                            ByteArrayOutputStream().use { output ->
                                bitmap.compress(Bitmap.CompressFormat.JPEG, 78, output)
                                Base64.encodeToString(output.toByteArray(), Base64.NO_WRAP)
                            }
                        } finally {
                            bitmap.recycle()
                        }
                    }
                }
            }
        } catch (_: Exception) {
            null
        } finally {
            releaseTransientFile(materialized)
        }
    }

    fun listPrepared(): List<File> =
        (root.listFiles() ?: emptyArray())
            .filter { isPreparedDocument(it) }
            .sortedByDescending { it.lastModified() }

    fun isPreparedDocument(file: File): Boolean = runCatching {
        val canonical = file.canonicalFile
        canonical.isFile &&
            canonical.parentFile?.canonicalPath == root.canonicalPath &&
            canonical.name.startsWith(FILE_PREFIX) &&
            canonical.extension.equals("spid", ignoreCase = true)
    }.getOrDefault(false)

    /** A short-lived private-cache PDF for PdfRenderer or Android's WebView picker. */
    fun materializePreparedFile(file: File): File {
        require(isPreparedDocument(file)) { "Only a prepared ID document can be opened here." }
        val bytes = readVaultOrMigrate(file, MAX_PREPARED_BYTES)
        val target = File(transientRoot, "id-studio-" + UUID.randomUUID().toString() + ".pdf")
        try {
            require(hasPdfSignature(bytes)) { "The prepared ID sheet is damaged. Please prepare it again." }
            FileOutputStream(target).use { output ->
                output.write(bytes)
                output.fd.sync()
            }
            return target
        } catch (error: Exception) {
            target.delete()
            throw error
        } finally {
            bytes.fill(0)
        }
    }

    fun releaseTransientFile(file: File) {
        runCatching {
            val canonical = file.canonicalFile
            if (canonical.parentFile?.canonicalPath == transientRoot.canonicalPath) canonical.delete()
        }
    }

    fun cleanupTransientFiles() {
        val cutoff = System.currentTimeMillis() - TRANSIENT_RETENTION_MILLIS
        (transientRoot.listFiles() ?: emptyArray()).forEach { file ->
            if (file.lastModified() < cutoff) file.deleteRecursively()
        }
    }

    fun deletePrepared(file: File) {
        require(isPreparedDocument(file)) { "Only a prepared ID document can be deleted here." }
        val id = idFromPreparedFile(file)
        check(file.delete()) { "The prepared ID document could not be deleted." }
        preparedSidecar(id, "front").delete()
        preparedSidecar(id, "back").delete()
        preparedSidecar(id, "meta").delete()
    }

    fun deleteDraft(draftId: String) {
        val directory = draftDirectory(draftId)
        check(directory.deleteRecursively()) { "Temporary ID scans could not be cleared." }
    }

    fun draftExists(draftId: String): Boolean =
        isValidId(draftId) && File(drafts, draftId).isDirectory

    private fun preparedFile(id: String): File = File(root, FILE_PREFIX + id + ".spid")

    private fun preparedSidecar(id: String, kind: String): File = File(root, id + "." + kind)

    private fun idFromPreparedFile(file: File): String =
        file.name.removePrefix(FILE_PREFIX).removeSuffix(".spid")

    private fun safeTitle(raw: String): String =
        raw.replace(Regex("[\\p{Cntrl}]+"), " ")
            .replace(Regex("\\s+"), " ").trim().take(80).ifBlank { "ID card" }

    private fun cleanupAbandonedDrafts() {
        val cutoff = System.currentTimeMillis() - DRAFT_RETENTION_MILLIS
        (drafts.listFiles() ?: emptyArray()).forEach { directory ->
            if (directory.isDirectory && directory.lastModified() < cutoff) directory.deleteRecursively()
        }
    }

    private fun migrateLegacyFiles() {
        (root.listFiles() ?: emptyArray())
            .filter { it.isFile && it.name.startsWith(FILE_PREFIX) && it.extension.equals("pdf", true) }
            .forEach { legacy ->
                val bytes = legacy.readBytes()
                require(bytes.size.toLong() in 1L..MAX_PREPARED_BYTES && hasPdfSignature(bytes)) {
                    "A saved ID sheet could not be migrated safely. Its original file has been preserved."
                }
                val encrypted = File(root, legacy.nameWithoutExtension + ".spid")
                writeEncryptedAtomically(encrypted, bytes)
                check(legacy.delete()) { "The migrated plaintext ID sheet could not be removed." }
            }
        drafts.walkTopDown().filter { it.isFile && it.extension.equals("jpg", true) }.forEach { legacy ->
            readVaultOrMigrate(legacy, MAX_SCAN_BYTES)
        }
    }

    private fun readVaultOrMigrate(file: File, maximumBytes: Long): ByteArray {
        val bytes = file.inputStream().use { readBounded(it, maximumBytes + VAULT_OVERHEAD_BYTES) }
        if (hasVaultHeader(bytes)) return decrypt(bytes, maximumBytes)
        writeEncryptedAtomically(file, bytes)
        return bytes
    }

    private fun writeEncryptedAtomically(target: File, plaintext: ByteArray) {
        val encrypted = encrypt(plaintext)
        val temporary = File(target.parentFile, "." + target.name + "-" + UUID.randomUUID() + ".tmp")
        try {
            FileOutputStream(temporary).use { output ->
                output.write(encrypted)
                output.fd.sync()
            }
            try {
                java.nio.file.Files.move(
                    temporary.toPath(), target.toPath(),
                    java.nio.file.StandardCopyOption.REPLACE_EXISTING,
                    java.nio.file.StandardCopyOption.ATOMIC_MOVE
                )
            } catch (_: java.nio.file.AtomicMoveNotSupportedException) {
                java.nio.file.Files.move(
                    temporary.toPath(), target.toPath(),
                    java.nio.file.StandardCopyOption.REPLACE_EXISTING
                )
            }
        } catch (error: Exception) {
            temporary.delete()
            throw error
        }
    }

    private fun encrypt(plaintext: ByteArray): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, vaultKey)
        val encrypted = cipher.doFinal(plaintext)
        return ByteArrayOutputStream(VAULT_HEADER.size + cipher.iv.size + encrypted.size).use { output ->
            output.write(VAULT_HEADER)
            output.write(cipher.iv)
            output.write(encrypted)
            output.toByteArray()
        }
    }

    private fun decrypt(envelope: ByteArray, maximumBytes: Long): ByteArray {
        require(envelope.size >= VAULT_HEADER.size + IV_BYTES + GCM_TAG_BYTES) {
            "This private ID file is incomplete."
        }
        require(hasVaultHeader(envelope)) { "This private ID file has an unsupported format." }
        val ivStart = VAULT_HEADER.size
        val iv = envelope.copyOfRange(ivStart, ivStart + IV_BYTES)
        val encrypted = envelope.copyOfRange(ivStart + IV_BYTES, envelope.size)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, vaultKey, GCMParameterSpec(GCM_TAG_BITS, iv))
        val plaintext = cipher.doFinal(encrypted)
        require(plaintext.size.toLong() <= maximumBytes) { "This private ID file exceeds the allowed size." }
        return plaintext
    }

    private fun loadOrCreateKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        val existing = keyStore.getKey(KEY_ALIAS, null) as? SecretKey
        if (existing != null) return existing
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .setRandomizedEncryptionRequired(true)
                .setUserAuthenticationRequired(false)
                .build()
        )
        return generator.generateKey()
    }

    private fun readBounded(input: InputStream, maximumBytes: Long): ByteArray {
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
        var total = 0L
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            total += count
            require(total <= maximumBytes) { "This private ID file is too large to store safely." }
            output.write(buffer, 0, count)
        }
        return output.toByteArray()
    }

    private fun hasVaultHeader(bytes: ByteArray): Boolean =
        bytes.size >= VAULT_HEADER.size && VAULT_HEADER.indices.all { bytes[it] == VAULT_HEADER[it] }

    private fun hasPdfSignature(bytes: ByteArray): Boolean =
        bytes.size >= PDF_SIGNATURE.size && PDF_SIGNATURE.indices.all { bytes[it] == PDF_SIGNATURE[it] }

    private fun isValidId(value: String): Boolean =
        runCatching { UUID.fromString(value).toString() == value }.getOrDefault(false)

    companion object {
        const val FILE_PREFIX = "SmartPrint ID -"
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val KEY_ALIAS = "smartprint.id-studio.v1.aes-gcm"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val GCM_TAG_BITS = 128
        private const val GCM_TAG_BYTES = 16
        private const val IV_BYTES = 12
        private const val MIN_SCAN_BYTES = 512
        private const val MIN_IMAGE_SHORT_EDGE = 530 // approximately 250 dpi at the default card height
        private const val MIN_IMAGE_LONG_EDGE = 842 // approximately 250 dpi at the default card width
        private const val MAX_IMAGE_PIXELS = 30_000_000L
        private const val MIN_CARD_ASPECT_RATIO = 1.15f
        private const val MAX_CARD_ASPECT_RATIO = 2.1f
        private const val MAX_SCAN_BYTES = 30L * 1024L * 1024L
        private const val MAX_PREPARED_BYTES = UploadLimits.MAX_FILE_BYTES
        private const val MAX_METADATA_BYTES = 64L * 1024L
        private const val THUMBNAIL_WIDTH = 320
        private const val CARD_THUMBNAIL_WIDTH = 600
        private const val VAULT_OVERHEAD_BYTES = 64L
        private const val DRAFT_RETENTION_MILLIS = 48L * 60L * 60L * 1000L
        private const val TRANSIENT_RETENTION_MILLIS = 30L * 60L * 1000L
        private val VAULT_HEADER = byteArrayOf('S'.code.toByte(), 'P'.code.toByte(), 'I'.code.toByte(), 'D'.code.toByte(), 1)
        private val PDF_SIGNATURE = "%PDF-".toByteArray(Charsets.US_ASCII)
    }
}
