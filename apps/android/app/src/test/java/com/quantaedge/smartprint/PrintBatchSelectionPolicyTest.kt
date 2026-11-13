package com.quantaedge.smartprint

import java.io.File
import java.io.RandomAccessFile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
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
    fun uploadPolicyUsesFiftyMegabytesPerFileAndFiveHundredMegabytesPerBatch() {
        assertEquals(50L * 1024L * 1024L, DocumentNamePolicy.MAX_PRINT_BYTES)
        assertEquals(500L * 1024L * 1024L, PrintBatchSelectionPolicy.MAX_TOTAL_BYTES)
    }

    @Test
    fun acceptsAnIndividualFileExactlyAtFiftyMegabytes() {
        val file = createFile("exact-limit.pdf", DocumentNamePolicy.MAX_PRINT_BYTES)
        assertNull(PrintBatchSelectionPolicy.validationError(listOf(file)))
    }

    @Test
    fun rejectsAnIndividualFileAboveFiftyMegabytes() {
        val file = createFile("large.pdf", DocumentNamePolicy.MAX_PRINT_BYTES + 1L)

        assertNotNull(PrintBatchSelectionPolicy.validationError(listOf(file)))
    }

    @Test
    fun acceptsCombinedSizeExactlyAtFiveHundredMegabytes() {
        val files = (1..10).map {
            createFile("document-$it.pdf", DocumentNamePolicy.MAX_PRINT_BYTES)
        }

        assertEquals(500L * 1024L * 1024L, files.sumOf { it.length() })
        assertNull(PrintBatchSelectionPolicy.validationError(files))
    }

    @Test
    fun rejectsCombinedSizeAboveFiveHundredMegabytes() {
        val fileBytes = PrintBatchSelectionPolicy.MAX_TOTAL_BYTES / 11 + 1L
        val files = (1..11).map { createFile("document-$it.pdf", fileBytes) }

        assertEquals(11, files.size)
        assertEquals(true, files.all { it.length() <= DocumentNamePolicy.MAX_PRINT_BYTES })
        assertEquals(true, files.sumOf { it.length() } > PrintBatchSelectionPolicy.MAX_TOTAL_BYTES)

        assertNotNull(PrintBatchSelectionPolicy.validationError(files))
    }

    @Test
    fun rejectsTheSameFileSelectedMoreThanOnce() {
        val file = createFile("same.pdf", 100L)

        assertNotNull(PrintBatchSelectionPolicy.validationError(listOf(file, file)))
    }

    private fun createFile(name: String, size: Long): File {
        val file = File(temporaryFolder.root, name)
        RandomAccessFile(file, "rw").use { it.setLength(size) }
        return file
    }
}
