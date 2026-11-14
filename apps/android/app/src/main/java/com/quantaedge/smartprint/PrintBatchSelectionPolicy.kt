package com.quantaedge.smartprint

import java.io.File

/** Validation rules shared by the native Android picker and the customer batch upload. */
object PrintBatchSelectionPolicy {
    const val MAX_FILES = UploadLimits.MAX_BATCH_FILES
    const val MAX_TOTAL_BYTES = UploadLimits.MAX_BATCH_BYTES

    /**
     * Fast validation for the native picker. Private Studio entries are encrypted vault files
     * with non-printable extensions, so validate their bounded encrypted size here and validate
     * the actual decrypted PDF again immediately before the WebView receives it.
     */
    fun pickerValidationError(
        files: List<File>,
        allowMultiple: Boolean,
        isPrivatePreparedDocument: (File) -> Boolean
    ): String? {
        if (files.isEmpty()) return "Select at least one document."
        if (!allowMultiple && files.size > 1) return "This print field accepts one document at a time."
        if (files.size > MAX_FILES) return "A print batch can contain up to $MAX_FILES files."

        val paths = files.map { file -> runCatching { file.canonicalPath }.getOrDefault(file.absolutePath) }
        if (paths.distinct().size != paths.size) return "A document can only be selected once in a print batch."

        // AES-GCM vault files add a small fixed envelope around the printable PDF.
        // Account for that envelope so a valid PDF exactly at the upload limit remains selectable.
        val encryptedFileLimit = DocumentNamePolicy.MAX_PRINT_BYTES + 64L
        val encryptedBatchLimit = MAX_TOTAL_BYTES + (MAX_FILES * 64L)
        var totalBytes = 0L
        for (file in files) {
            if (!file.isFile) return "A selected document is no longer available."
            val size = file.length()
            val isPrivate = isPrivatePreparedDocument(file)
            if (isPrivate) {
                if (size !in 1..encryptedFileLimit) {
                    return file.name + " exceeds the per-file upload limit."
                }
            } else if (!DocumentNamePolicy.isPrintable(file)) {
                return file.name + " is not a supported PDF/JPG/PNG file or exceeds the per-file upload limit."
            }
            if (size > encryptedBatchLimit - totalBytes) {
                return "A print batch can contain up to ${MAX_TOTAL_BYTES / (1024L * 1024L)} MB in total. Remove some files and try again."
            }
            totalBytes += size
        }
        return null
    }

    /** Returns a customer-safe reason when the selected files cannot be uploaded together. */
    fun validationError(files: List<File>): String? {
        if (files.isEmpty()) return "Select at least one document."
        if (files.size > MAX_FILES) return "A print batch can contain up to $MAX_FILES files."

        val paths = files.map { file -> runCatching { file.canonicalPath }.getOrDefault(file.absolutePath) }
        if (paths.distinct().size != paths.size) return "A document can only be selected once in a print batch."

        var totalBytes = 0L
        for (file in files) {
            if (!file.isFile) return "A selected document is no longer available."
            val size = file.length()
            if (!DocumentNamePolicy.isPrintable(file)) {
                return file.name + " is not a supported PDF/JPG/PNG file or exceeds the ${DocumentNamePolicy.MAX_PRINT_BYTES / (1024L * 1024L)} MB per-file limit."
            }
            if (size > MAX_TOTAL_BYTES - totalBytes) {
                return "A print batch can contain up to ${MAX_TOTAL_BYTES / (1024L * 1024L)} MB in total. Remove some files and try again."
            }
            totalBytes += size
        }
        return null
    }
}
