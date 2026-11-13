package com.quantaedge.smartprint

import java.util.Locale

object DocumentNamePolicy {
    const val MAX_PRINT_BYTES = UploadLimits.MAX_FILE_BYTES
    const val MAX_IMPORT_BYTES = 100L * 1024L * 1024L

    fun isPrintable(name: String, bytes: Long): Boolean =
        bytes in 1..MAX_PRINT_BYTES && extension(name) in setOf("pdf", "jpg", "jpeg", "png")

    fun mimeType(name: String): String = when (extension(name)) {
        "pdf" -> "application/pdf"
        "jpg", "jpeg" -> "image/jpeg"
        "png" -> "image/png"
        "txt" -> "text/plain"
        "doc" -> "application/msword"
        "docx" -> "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        "xls" -> "application/vnd.ms-excel"
        "xlsx" -> "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        "ppt" -> "application/vnd.ms-powerpoint"
        "pptx" -> "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        else -> "application/octet-stream"
    }

    fun safeName(input: String): String {
        val value = input.map { c ->
            if (c.isISOControl() || c in charArrayOf('/', '\\', ':', '*', '?', '"', '<', '>', '|')) '_' else c
        }.joinToString("").trim().trim('.')
        return value.take(180).ifBlank { "Untitled document" }
    }

    private fun extension(name: String) = name.substringAfterLast('.', "").lowercase(Locale.ROOT)
}
