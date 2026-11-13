package com.quantaedge.smartprint

import java.io.File
import java.io.FileInputStream
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

    private const val MAX_HEADER_BYTES = 1024

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
        // Normal filenames need no file I/O; inspect content only for renamed or uncommon extensions.
        return extension(file.name) in setOf("pdf", "jpg", "jpeg", "png") ||
            mimeType(file) in printableMimeTypes
    }

    fun isBatchSource(file: File): Boolean {
        if (!file.isFile) return false
        return extension(file.name) in setOf("pdf", "jpg", "jpeg", "png") ||
            mimeType(file) in printableMimeTypes
    }

    fun isPdf(file: File): Boolean = mimeType(file) == "application/pdf"

    fun isImage(file: File): Boolean = mimeType(file).startsWith("image/")

    fun supportsInAppViewer(file: File): Boolean {
        if (!file.isFile) return false
        return mimeType(file.name) in inAppViewerMimeTypes || mimeType(file) in inAppViewerMimeTypes
    }

    fun supportsInAppViewer(mimeType: String): Boolean = mimeType in inAppViewerMimeTypes

    /** Returns the detected content type, using the filename only when bytes are unknown. */
    fun mimeType(file: File): String =
        detectMimeType(file) ?: mimeType(file.name)

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
        fun byte(index: Int) = header[index].toInt() and 0xff
        fun matches(offset: Int, signature: IntArray): Boolean =
            offset >= 0 && offset + signature.size <= bytesRead &&
                signature.indices.all { byte(offset + it) == signature[it] }

        // PDF permits its header to appear within the first 1024 bytes.
        for (offset in 0..(bytesRead - 5).coerceAtLeast(-1)) {
            if (matches(offset, intArrayOf(0x25, 0x50, 0x44, 0x46, 0x2d))) return "application/pdf"
        }
        if (matches(0, intArrayOf(0xff, 0xd8, 0xff))) return "image/jpeg"
        if (matches(0, intArrayOf(0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a))) return "image/png"
        if (matches(0, intArrayOf(0x42, 0x4d))) return "image/bmp"
        if (matches(0, intArrayOf(0x47, 0x49, 0x46, 0x38, 0x37, 0x61)) ||
            matches(0, intArrayOf(0x47, 0x49, 0x46, 0x38, 0x39, 0x61))) return "image/gif"
        if (bytesRead >= 12 &&
            matches(0, intArrayOf(0x52, 0x49, 0x46, 0x46)) &&
            matches(8, intArrayOf(0x57, 0x45, 0x42, 0x50))) return "image/webp"

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
