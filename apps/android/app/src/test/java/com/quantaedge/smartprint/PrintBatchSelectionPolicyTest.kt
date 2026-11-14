package com.quantaedge.smartprint

import java.io.File
import java.io.RandomAccessFile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PrintBatchSelectionPolicyTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    @Test
    fun acceptsSeparatePdfAndImageFilesWithinBatchLimits() {
        val files = listOf(
            createFile("assignment.pdf", 120_000L),
            createFile("identity-card.jpg", 80_000L),
            createFile("photo.png", 90_000L)
        )

        assertNull(PrintBatchSelectionPolicy.validationError(files))
    }

    @Test
    fun rejectsEmptySelectionsAndUnsupportedExtensions() {
        assertNotNull(PrintBatchSelectionPolicy.validationError(emptyList()))
        assertNotNull(PrintBatchSelectionPolicy.validationError(listOf(createFile("notes.docx", 200L))))
    }

    @Test
    fun rejectsMoreThanTwentyFiles() {
        val files = (1..(PrintBatchSelectionPolicy.MAX_FILES + 1)).map { createFile("document-$it.pdf", 100L) }

        assertEquals(
            "A print batch can contain up to ${PrintBatchSelectionPolicy.MAX_FILES} files.",
            PrintBatchSelectionPolicy.validationError(files)
        )
    }

    @Test
    fun uploadLimitsUseTheSharedGeneratedPolicy() {
        assertEquals(UploadLimits.MAX_FILE_BYTES, DocumentNamePolicy.MAX_PRINT_BYTES)
        assertEquals(UploadLimits.MAX_BATCH_BYTES, PrintBatchSelectionPolicy.MAX_TOTAL_BYTES)
        assertEquals(UploadLimits.MAX_BATCH_FILES, PrintBatchSelectionPolicy.MAX_FILES)
    }

    @Test
    fun acceptsAnIndividualFileExactlyAtTheSharedLimit() {
        val file = createFile("exact-limit.pdf", DocumentNamePolicy.MAX_PRINT_BYTES)
        assertNull(PrintBatchSelectionPolicy.validationError(listOf(file)))
    }

    @Test
    fun rejectsAnIndividualFileAboveTheSharedLimit() {
        val file = createFile("large.pdf", DocumentNamePolicy.MAX_PRINT_BYTES + 1L)

        assertNotNull(PrintBatchSelectionPolicy.validationError(listOf(file)))
    }

    @Test
    fun acceptsCombinedSizeExactlyAtTheSharedBatchLimit() {
        val files = createBatchWithTotalBytes("exact-batch", PrintBatchSelectionPolicy.MAX_TOTAL_BYTES)

        assertEquals(PrintBatchSelectionPolicy.MAX_TOTAL_BYTES, files.sumOf { it.length() })
        assertTrue(files.all { it.length() in 1..DocumentNamePolicy.MAX_PRINT_BYTES })
        assertNull(PrintBatchSelectionPolicy.validationError(files))
    }

    @Test
    fun rejectsCombinedSizeAboveTheSharedBatchLimit() {
        val files = createBatchWithTotalBytes("over-batch", PrintBatchSelectionPolicy.MAX_TOTAL_BYTES + 1L)

        assertTrue(files.all { it.length() in 1..DocumentNamePolicy.MAX_PRINT_BYTES })
        assertTrue(files.size <= PrintBatchSelectionPolicy.MAX_FILES)
        assertTrue(files.sumOf { it.length() } > PrintBatchSelectionPolicy.MAX_TOTAL_BYTES)
        assertNotNull(PrintBatchSelectionPolicy.validationError(files))
    }

    @Test
    fun rejectsTheSameFileSelectedMoreThanOnce() {
        val file = createFile("same.pdf", 100L)

        assertNotNull(PrintBatchSelectionPolicy.validationError(listOf(file, file)))
    }

    @Test
    fun nativePickerAcceptsPrivateStudioDocumentsAlongsideLooseFilesWithoutDecryption() {
        val loose = createFile("assignment.pdf", 120_000L)
        val photo = createFile("SmartPrint Photo - prepared.spph", 240_000L)
        val id = createFile("SmartPrint ID - prepared.spid", 320_000L)

        val error = PrintBatchSelectionPolicy.pickerValidationError(
            listOf(loose, photo, id),
            allowMultiple = true
        ) { file ->
            file.extension.equals("spph", ignoreCase = true) ||
                file.extension.equals("spid", ignoreCase = true)
        }

        assertNull(error)
    }

    @Test
    fun nativePickerAllowsOnlyOneFileForSingleFileInputs() {
        val first = createFile("first.pdf", 100L)
        val second = createFile("second.jpg", 200L)

        assertEquals(
            "This print field accepts one document at a time.",
            PrintBatchSelectionPolicy.pickerValidationError(
                listOf(first, second),
                allowMultiple = false
            ) { false }
        )
    }

    @Test
    fun nativePickerRejectsOversizedEncryptedStudioDocumentsBeforeMaterializingThem() {
        val oversized = createFile(
            "SmartPrint Photo - oversized.spph",
            DocumentNamePolicy.MAX_PRINT_BYTES + 65L
        )

        assertNotNull(
            PrintBatchSelectionPolicy.pickerValidationError(
                listOf(oversized),
                allowMultiple = true
            ) { true }
        )
    }

    private fun createBatchWithTotalBytes(prefix: String, totalBytes: Long): List<File> {
        require(totalBytes > 0L)
        val fileCount = ((totalBytes - 1L) / DocumentNamePolicy.MAX_PRINT_BYTES + 1L).toInt()
        require(fileCount <= PrintBatchSelectionPolicy.MAX_FILES) {
            "The configured batch byte limit cannot be represented within the configured file-count limit."
        }
        val baseBytes = totalBytes / fileCount
        val extraBytes = (totalBytes % fileCount).toInt()
        return (0 until fileCount).map { index ->
            val size = baseBytes + if (index < extraBytes) 1L else 0L
            createFile("$prefix-$index.pdf", size)
        }
    }

    private fun createFile(name: String, size: Long): File {
        val file = File(temporaryFolder.root, name)
        RandomAccessFile(file, "rw").use { output ->
            output.setLength(size)
            val signature = when (file.extension.lowercase()) {
                "pdf" -> "%PDF-".toByteArray(Charsets.US_ASCII)
                "jpg", "jpeg" -> byteArrayOf(0xff.toByte(), 0xd8.toByte(), 0xff.toByte())
                "png" -> byteArrayOf(0x89.toByte(), 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)
                else -> byteArrayOf()
            }
            if (size >= signature.size && signature.isNotEmpty()) {
                output.seek(0)
                output.write(signature)
            }
        }
        return file
    }
}
