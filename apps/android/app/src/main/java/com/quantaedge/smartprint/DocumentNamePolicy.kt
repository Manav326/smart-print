package com.quantaedge.smartprint

import java.io.File
import java.io.FileInputStream
import java.util.LinkedHashMap
import java.util.Locale

/**
 * The single source of truth for document identity.
 *
 * File names are display labels and may be changed by the customer. For local files,
 * inspect only the small format header before falling back to the extension. This
 * avoids loading a whole document just to decide which viewer or MIME type to use.
 */
object DocumentNamePolicy {
    const val MAX_PRINT_BYTES = UploadLimits.MAX_FILE_BYTES
    const val MAX_IMPORT_BYTES = 100L * 1024L * 1024L

    private const val MAX_HEADER_BYTES = 1028
    private const val MAX_TYPE_CACHE_ENTRIES = 256

    private data class FileIdentity(
        val canonicalPath: String,
        val sizeBytes: Long,
        val modifiedAtMillis: Long
    )

    private val detectedTypeCache = object : LinkedHashMap<FileIdentity, String>(32, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<FileIdentity, String>?): Boolean =
            size > MAX_TYPE_CACHE_ENTRIES
    }

    private val PDF_HEADER = byteArrayOf(0x25, 0x50, 0x44, 0x46, 0x2d)
    private val JPEG_HEADER = byteArrayOf(0xff.toByte(), 0xd8.toByte(), 0xff.toByte())
    private val PNG_HEADER = byteArrayOf(0x89.toByte(), 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)
    private val BMP_HEADER = byteArrayOf(0x42, 0x4d)
    private val GIF87_HEADER = "GIF87a".toByteArray(Charsets.US_ASCII)
    private val GIF89_HEADER = "GIF89a".toByteArray(Charsets.US_ASCII)
    private val RIFF_HEADER = "RIFF".toByteArray(Charsets.US_ASCII)
    private val WEBP_HEADER = "WEBP".toByteArray(Charsets.US_ASCII)

    private val printableMimeTypes = setOf(
        "application/pdf",
        "image/jpeg",
        "image/png"
    )
    private val inAppViewerMimeTypes = printableMimeTypes + setOf(
        "image/webp",
        "image/bmp",
        "image/gif"
    )

    fun isPrintable(name: String, bytes: Long): Boolean =
        bytes in 1..MAX_PRINT_BYTES && extension(name) in setOf("pdf", "jpg", "jpeg", "png")

    fun isPrintable(file: File): Boolean {
        if (!file.isFile || file.length() !in 1..MAX_PRINT_BYTES) return false
        return detectMimeType(file) in printableMimeTypes
    }

    fun isBatchSource(file: File): Boolean {
        if (!file.isFile) return false
        return detectMimeType(file) in printableMimeTypes
    }

    fun isPdf(file: File): Boolean = mimeType(file) == "application/pdf"

    fun isImage(file: File): Boolean = mimeType(file).startsWith("image/")

    fun supportsInAppViewer(file: File): Boolean =
        file.isFile && supportsInAppViewer(mimeType(file))

    fun supportsInAppViewer(mimeType: String): Boolean = mimeType in inAppViewerMimeTypes

    /**
     * Returns the detected content type. The small signature result is cached per file
     * identity, so opening, editing and sharing the same unchanged file does not reread it.
     * The bounded LRU avoids retaining an unbounded list of customer file paths.
     */
    fun mimeType(file: File): String {
        if (!file.isFile) return mimeType(file.name)
        val identity = runCatching {
            FileIdentity(file.canonicalPath, file.length(), file.lastModified())
        }.getOrNull() ?: return mimeType(file.name)

        synchronized(detectedTypeCache) {
            detectedTypeCache[identity]?.let { return it }
        }

        val detected = detectMimeType(file) ?: run {
            // A printable extension must not turn corrupt or unreadable content
            // into a PDF/JPEG/PNG viewer document. Other viewer formats retain
            // their filename fallback.
            val fallback = mimeType(file.name)
            if (fallback in printableMimeTypes) "application/octet-stream" else fallback
        }
        synchronized(detectedTypeCache) {
            detectedTypeCache[identity] = detected
        }
        return detected
    }

    fun mimeType(name: String): String = when (extension(name)) {
        "pdf" -> "application/pdf"
        "jpg", "jpeg" -> "image/jpeg"
        "png" -> "image/png"
        "webp" -> "image/webp"
        "bmp" -> "image/bmp"
        "gif" -> "image/gif"
        "txt" -> "text/plain"
        "doc" -> "application/msword"
        "docx" -> "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        "xls" -> "application/vnd.ms-excel"
        "xlsx" -> "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        "ppt" -> "application/vnd.ms-powerpoint"
        "pptx" -> "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        else -> "application/octet-stream"
    }

    private fun detectMimeType(file: File): String? {
        if (!file.isFile || file.length() <= 0L) return null
        val header = ByteArray(MAX_HEADER_BYTES)
        val bytesRead = try {
            FileInputStream(file).use { input ->
                var offset = 0
                while (offset < header.size) {
                    val count = input.read(header, offset, header.size - offset)
                    if (count <= 0) break
                    offset += count
                }
                offset
            }
        } catch (_: Exception) {
            return null
        }

        if (bytesRead == 0) return null
        fun matches(offset: Int, signature: ByteArray): Boolean =
            offset >= 0 && offset + signature.size <= bytesRead &&
                signature.indices.all { header[offset + it] == signature[it] }

        if (matches(0, JPEG_HEADER)) return "image/jpeg"
        if (matches(0, PNG_HEADER)) return "image/png"

        // PDF permits its header to appear within the first 1024 bytes.
        for (offset in 0..(bytesRead - PDF_HEADER.size).coerceAtLeast(-1)) {
            if (matches(offset, PDF_HEADER)) return "application/pdf"
        }
        if (matches(0, BMP_HEADER)) return "image/bmp"
        if (matches(0, GIF87_HEADER) || matches(0, GIF89_HEADER)) return "image/gif"
        if (bytesRead >= 12 && matches(0, RIFF_HEADER) && matches(8, WEBP_HEADER)) return "image/webp"

        return null
    }

    fun safeName(input: String): String {
        val value = input.map { c ->
            if (c.isISOControl() || c == '/' || c == 92.toChar() ||
                c in charArrayOf(':', '*', '?', '"', '<', '>', '|')) '_' else c
        }.joinToString("").trim().trim('.')
        return value.take(180).ifBlank { "Untitled document" }
    }

    private fun extension(name: String) = name.substringAfterLast('.', "").lowercase(Locale.ROOT)
}
