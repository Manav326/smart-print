package com.quantaedge.smartprint

import android.graphics.Bitmap
import android.graphics.pdf.PdfRenderer
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Encrypted, device-local storage for Photo Studio documents.
 *
 * The printable PDF, original source photo and JSON metadata are encrypted
 * independently with AES-GCM. None of these files is placed in shared storage.
 */
class PhotoStudioStore(filesDir: File, cacheDir: File) {
    val root = File(filesDir, "photo-studio")
    private val transientRoot = File(cacheDir, "photo-studio-transient")
    private val vaultKey: SecretKey by lazy(LazyThreadSafetyMode.SYNCHRONIZED) { loadOrCreateKey() }

    init {
        check(root.exists() || root.mkdirs()) { "Private Photo Studio storage could not be created." }
        check(root.isDirectory) { "Private Photo Studio storage is unavailable." }
        check(transientRoot.exists() || transientRoot.mkdirs()) { "Private Photo Studio working storage could not be created." }
        cleanupTransientFiles()
        vaultKey
    }

    fun savePreparedOutput(pdfBytes: ByteArray, sourceBytes: ByteArray, suppliedMetadata: JSONObject): File {
        require(pdfBytes.size.toLong() in 1L..MAX_PREPARED_BYTES) {
            "The prepared photo sheet exceeds SmartPrint's private document limit."
        }
        require(sourceBytes.size.toLong() in 1L..MAX_SOURCE_BYTES) {
            "The original photo exceeds SmartPrint's private source limit."
        }
        require(hasPdfSignature(pdfBytes)) { "The prepared photo sheet could not be verified." }

        val sizeCode = suppliedMetadata.optString("sizeCode")
        require(sizeCode in PHOTO_SIZE_LIMITS.keys) { "Choose a supported photo size before saving." }
        val photoCount = suppliedMetadata.optInt("photoCount", 0)
        val photosPerPage = suppliedMetadata.optInt("photosPerPage", 0)
        require(photoCount in 1..100) { "The photo count must be between 1 and 100." }
        require(photosPerPage in 1..(PHOTO_SIZE_LIMITS[sizeCode] ?: 0)) {
            "The photos-per-A4-page setting is invalid."
        }
        val pageCount = (photoCount + photosPerPage - 1) / photosPerPage
        require(suppliedMetadata.optInt("pageCount", pageCount) == pageCount) {
            "The prepared A4 page count does not match the selected photo quantity."
        }
        require(suppliedMetadata.optInt("resolutionDpi", 0) == OUTPUT_DPI) {
            "The prepared photo resolution could not be verified."
        }

        val id = UUID.randomUUID().toString()
        val output = documentFile(id)
        val source = sourceFile(id)
        val metadataFile = metadataFile(id)
        val metadata = JSONObject(suppliedMetadata.toString()).apply {
            put("id", id)
            put("title", safeTitle(optString("title"), sizeCode))
            put("sizeCode", sizeCode)
            put("photoCount", photoCount)
            put("photosPerPage", photosPerPage)
            put("pageCount", pageCount)
            put("resolutionDpi", OUTPUT_DPI)
            put("sourceName", optString("sourceName").take(180))
            put("sourceMime", optString("sourceMime").take(100).ifBlank { "image/jpeg" })
            put("createdUtc", System.currentTimeMillis())
            put("updatedUtc", System.currentTimeMillis())
        }

        try {
            writeEncryptedAtomically(output, pdfBytes)
            writeEncryptedAtomically(source, sourceBytes)
            writeEncryptedAtomically(metadataFile, metadata.toString().toByteArray(Charsets.UTF_8))
        } catch (error: Exception) {
            output.delete()
            source.delete()
            metadataFile.delete()
            throw error
        }
        return output
    }

    fun updatePreparedOutput(id: String, pdfBytes: ByteArray, sourceBytes: ByteArray, suppliedMetadata: JSONObject): File {
        val output = findById(id) ?: error("This private photo sheet is no longer available.")
        require(pdfBytes.size.toLong() in 1L..MAX_PREPARED_BYTES) {
            "The prepared photo sheet exceeds SmartPrint's private document limit."
        }
        require(sourceBytes.size.toLong() in 1L..MAX_SOURCE_BYTES) {
            "The original photo exceeds SmartPrint's private source limit."
        }
        require(hasPdfSignature(pdfBytes)) { "The prepared photo sheet could not be verified." }
        val sizeCode = suppliedMetadata.optString("sizeCode")
        require(sizeCode in PHOTO_SIZE_LIMITS.keys) { "Choose a supported photo size before saving." }
        val photoCount = suppliedMetadata.optInt("photoCount", 0)
        val photosPerPage = suppliedMetadata.optInt("photosPerPage", 0)
        require(photoCount in 1..100) { "The photo count must be between 1 and 100." }
        require(photosPerPage in 1..(PHOTO_SIZE_LIMITS[sizeCode] ?: 0)) {
            "The photos-per-A4-page setting is invalid."
        }
        val pageCount = (photoCount + photosPerPage - 1) / photosPerPage
        require(suppliedMetadata.optInt("pageCount", pageCount) == pageCount) {
            "The prepared A4 page count does not match the selected photo quantity."
        }
        require(suppliedMetadata.optInt("resolutionDpi", 0) == OUTPUT_DPI) {
            "The prepared photo resolution could not be verified."
        }
        val existing = readMetadata(output)
        val metadata = JSONObject(suppliedMetadata.toString()).apply {
            put("id", id)
            put("title", safeTitle(existing.optString("title", optString("title")), sizeCode))
            put("sizeCode", sizeCode)
            put("photoCount", photoCount)
            put("photosPerPage", photosPerPage)
            put("pageCount", pageCount)
            put("resolutionDpi", OUTPUT_DPI)
            put("createdUtc", existing.optLong("createdUtc", System.currentTimeMillis()))
            put("updatedUtc", System.currentTimeMillis())
        }
        writeEncryptedAtomically(output, pdfBytes)
        writeEncryptedAtomically(sourceFile(id), sourceBytes)
        writeEncryptedAtomically(metadataFile(id), metadata.toString().toByteArray(Charsets.UTF_8))
        return output
    }

    fun listPrepared(): List<File> =
        (root.listFiles() ?: emptyArray())
            .filter { isPreparedDocument(it) }
            .sortedByDescending { it.lastModified() }

    fun findById(id: String): File? {
        if (!isValidId(id)) return null
        return documentFile(id).takeIf(::isPreparedDocument)
    }

    fun isPreparedDocument(file: File): Boolean = runCatching {
        val canonical = file.canonicalFile
        canonical.isFile &&
            canonical.parentFile?.canonicalPath == root.canonicalPath &&
            canonical.name.startsWith(FILE_PREFIX) &&
            canonical.extension.equals("spph", ignoreCase = true) &&
            isValidId(idFromFile(file))
    }.getOrDefault(false)

    fun readMetadata(file: File): JSONObject {
        require(isPreparedDocument(file)) { "Only a private Photo Studio document can be opened here." }
        val bytes = readEncrypted(metadataFile(idFromFile(file)), MAX_METADATA_BYTES)
        return JSONObject(String(bytes, Charsets.UTF_8))
    }

    fun renamePrepared(file: File, title: String): JSONObject {
        val metadata = readMetadata(file)
        val safe = safeTitle(title, metadata.optString("sizeCode"))
        metadata.put("title", safe)
        metadata.put("updatedUtc", System.currentTimeMillis())
        writeEncryptedAtomically(metadataFile(idFromFile(file)), metadata.toString().toByteArray(Charsets.UTF_8))
        return metadata
    }

    fun materializePdf(file: File): File {
        require(isPreparedDocument(file)) { "Only a private Photo Studio PDF can be opened or printed." }
        val bytes = readEncrypted(file, MAX_PREPARED_BYTES)
        require(hasPdfSignature(bytes)) { "The private photo sheet is damaged. Prepare it again from the original photo." }
        return writeTransient(bytes, ".pdf")
    }

    fun readOriginalPhoto(file: File): ByteArray {
        require(isPreparedDocument(file)) { "Only a private Photo Studio document can be edited here." }
        return readEncrypted(sourceFile(idFromFile(file)), MAX_SOURCE_BYTES)
    }

    fun materializeOriginalPhoto(file: File): File {
        val bytes = readOriginalPhoto(file)
        val metadata = readMetadata(file)
        val extension = metadata.optString("sourceName").substringAfterLast('.', "jpg")
            .lowercase().takeIf { it in setOf("jpg", "jpeg", "png") } ?: "jpg"
        return writeTransient(bytes, ".$extension")
    }

    fun createThumbnailBase64(file: File): String? = renderPageBase64(file, 0, THUMBNAIL_WIDTH)

    fun renderPageBase64(file: File, pageIndex: Int, requestedWidth: Int = 960): String? {
        val materialized = runCatching { materializePdf(file) }.getOrNull() ?: return null
        return try {
            val descriptor = android.os.ParcelFileDescriptor.open(materialized, android.os.ParcelFileDescriptor.MODE_READ_ONLY)
            descriptor.use { parcel ->
                PdfRenderer(parcel).use { renderer ->
                    if (pageIndex !in 0 until renderer.pageCount) return null
                    renderer.openPage(pageIndex).use { page ->
                        val width = requestedWidth.coerceIn(240, 1600)
                        val height = maxOf(1, (width.toFloat() * page.height / page.width).toInt())
                        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
                        try {
                            page.render(bitmap, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                            ByteArrayOutputStream().use { output ->
                                bitmap.compress(Bitmap.CompressFormat.JPEG, if (width <= THUMBNAIL_WIDTH) 78 else 86, output)
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

    fun deletePrepared(file: File) {
        require(isPreparedDocument(file)) { "Only a private Photo Studio document can be deleted here." }
        val id = idFromFile(file)
        check(file.delete()) { "The private photo sheet could not be deleted." }
        sourceFile(id).delete()
        metadataFile(id).delete()
    }

    fun releaseTransientFile(file: File) {
        runCatching {
            val canonical = file.canonicalFile
            if (canonical.parentFile?.canonicalPath == transientRoot.canonicalPath) canonical.delete()
        }
    }

    private fun writeTransient(bytes: ByteArray, extension: String): File {
        val target = File(transientRoot, "photo-studio-" + UUID.randomUUID().toString() + extension)
        FileOutputStream(target).use { output ->
            output.write(bytes)
            output.fd.sync()
        }
        return target
    }

    private fun cleanupTransientFiles() {
        val cutoff = System.currentTimeMillis() - TRANSIENT_RETENTION_MILLIS
        (transientRoot.listFiles() ?: emptyArray()).forEach { file ->
            if (file.lastModified() < cutoff) file.deleteRecursively()
        }
    }

    private fun documentFile(id: String) = File(root, FILE_PREFIX + id + ".spph")
    private fun sourceFile(id: String) = File(root, id + ".source")
    private fun metadataFile(id: String) = File(root, id + ".meta")
    private fun idFromFile(file: File): String = file.name.removePrefix(FILE_PREFIX).removeSuffix(".spph")

    private fun readEncrypted(file: File, maximumBytes: Long): ByteArray {
        val canonical = file.canonicalFile
        require(canonical.parentFile?.canonicalPath == root.canonicalPath && canonical.isFile) {
            "The private Photo Studio data is no longer available."
        }
        val encrypted = canonical.readBytes()
        require(encrypted.size.toLong() <= maximumBytes + VAULT_OVERHEAD_BYTES) {
            "The private Photo Studio data exceeds its allowed size."
        }
        return decrypt(encrypted, maximumBytes)
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
        val ciphertext = cipher.doFinal(plaintext)
        return ByteArrayOutputStream(VAULT_HEADER.size + cipher.iv.size + ciphertext.size).use { output ->
            output.write(VAULT_HEADER)
            output.write(cipher.iv)
            output.write(ciphertext)
            output.toByteArray()
        }
    }

    private fun decrypt(envelope: ByteArray, maximumBytes: Long): ByteArray {
        require(envelope.size >= VAULT_HEADER.size + IV_BYTES + GCM_TAG_BYTES &&
            VAULT_HEADER.indices.all { envelope[it] == VAULT_HEADER[it] }) {
            "This private Photo Studio file is incomplete or unsupported."
        }
        val iv = envelope.copyOfRange(VAULT_HEADER.size, VAULT_HEADER.size + IV_BYTES)
        val ciphertext = envelope.copyOfRange(VAULT_HEADER.size + IV_BYTES, envelope.size)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, vaultKey, GCMParameterSpec(GCM_TAG_BITS, iv))
        val plaintext = cipher.doFinal(ciphertext)
        require(plaintext.size.toLong() <= maximumBytes) { "The private Photo Studio data is too large." }
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
                .build()
        )
        return generator.generateKey()
    }

    private fun safeTitle(raw: String, sizeCode: String): String {
        val cleaned = raw.replace(Regex("[\\p{Cntrl}]+"), " ")
            .replace(Regex("\\s+"), " ").trim().take(80)
        return cleaned.ifBlank { "Photo sheet · $sizeCode" }
    }

    private fun hasPdfSignature(bytes: ByteArray): Boolean =
        bytes.size >= PDF_SIGNATURE.size && PDF_SIGNATURE.indices.all { bytes[it] == PDF_SIGNATURE[it] }

    private fun isValidId(value: String): Boolean =
        runCatching { UUID.fromString(value).toString() == value }.getOrDefault(false)

    companion object {
        const val FILE_PREFIX = "SmartPrint Photo - "
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val KEY_ALIAS = "smartprint.photo-studio.v1.aes-gcm"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val GCM_TAG_BITS = 128
        private const val GCM_TAG_BYTES = 16
        private const val IV_BYTES = 12
        private const val MAX_PREPARED_BYTES = 20L * 1024L * 1024L
        private const val MAX_SOURCE_BYTES = 20L * 1024L * 1024L
        private const val MAX_METADATA_BYTES = 64L * 1024L
        private const val VAULT_OVERHEAD_BYTES = 64L
        private const val TRANSIENT_RETENTION_MILLIS = 30L * 60L * 1000L
        private const val THUMBNAIL_WIDTH = 320
        private val VAULT_HEADER = byteArrayOf('S'.code.toByte(), 'P'.code.toByte(), 'P'.code.toByte(), 'H'.code.toByte(), 1)
        private val PDF_SIGNATURE = "%PDF-".toByteArray(Charsets.US_ASCII)
        private val PHOTO_SIZE_LIMITS = mapOf("35x45" to 25, "25x35" to 49, "2x2" to 15, "4x6" to 1)
        private const val OUTPUT_DPI = 300
    }
}
