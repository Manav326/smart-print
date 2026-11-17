package com.quantaedge.smartprint

import java.io.ByteArrayInputStream
import java.io.File
import java.io.FileInputStream
import java.io.RandomAccessFile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class LocalDocumentStoreTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    @Test
    fun importsDuplicateFilesWithoutOverwriting() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))
        store.createFolder("", "School")

        val first = store.importFile(ByteArrayInputStream("first".toByteArray()), "homework.pdf", "School")
        val second = store.importFile(ByteArrayInputStream("second".toByteArray()), "homework.pdf", "School")

        assertEquals("homework.pdf", first.name)
        assertEquals("homework (2).pdf", second.name)
        assertEquals("first", first.readText())
        assertEquals("second", second.readText())
    }

    @Test
    fun rejectsPathTraversalAndCleansEmptyImports() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))

        assertThrows(IllegalArgumentException::class.java) { store.folder("../outside") }
        assertThrows(IllegalArgumentException::class.java) {
            store.importFile(ByteArrayInputStream(byteArrayOf()), "empty.pdf", "")
        }
        assertFalse(store.list(store.root).isNotEmpty())
    }

    @Test
    fun listsFoldersBeforeFilesAndKeepsNestedStructure() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))
        store.importFile(ByteArrayInputStream("file".toByteArray()), "a.pdf", "")
        val folder = store.createFolder("", "Documents")
        store.createFolder("Documents", "School")

        val entries = store.list(store.root)
        assertTrue(entries.first().isDirectory)
        assertEquals("Documents", entries.first().name)
        assertEquals("Documents/School", store.pathOf(store.folder("Documents/School")))
    }

    @Test
    fun folderTransferBudgetCountsDirectoriesAndEnforcesDepth() {
        val budget = FolderTransferBudget(maxItems = 3, maxDepth = 2)
        assertTrue(budget.tryTake(depth = 0))
        assertTrue(budget.tryTake(depth = 1))
        assertFalse(budget.tryTake(depth = 3))
        assertEquals(2, budget.itemsVisited)
        assertTrue(budget.tryTake(depth = 2))
        assertFalse(budget.tryTake(depth = 0))
        assertEquals(3, budget.itemsVisited)
    }
    @Test
    fun reordersSiblingItemsWithoutRenamingOrChangingContents() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))
        val first = store.importFile(ByteArrayInputStream("first".toByteArray()), "first.pdf", "")
        val second = store.importFile(ByteArrayInputStream("second".toByteArray()), "second.pdf", "")

        assertEquals("first.pdf", store.list(store.root)[0].name)
        assertTrue(store.reorder(second, -1))
        assertEquals("second.pdf", store.list(store.root)[0].name)
        assertEquals("first", first.readText())
        assertEquals("second", second.readText())
        assertEquals("first.pdf", first.name)
        assertEquals("second.pdf", second.name)
        assertFalse(store.reorder(second, -1))
    }

    @Test
    fun replacesOriginalPdfOnlyAfterValidatingTheCompleteReplacement() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))
        val original = store.importFile(
            ByteArrayInputStream("%PDF-1.4 original".toByteArray()),
            "report.pdf",
            ""
        )
        val replacement = File(temporaryFolder.root, "replacement.pdf")
        replacement.writeBytes("%PDF-1.7 rotated".toByteArray())

        val returned = store.replacePdfContents(original, replacement)

        assertEquals(original.canonicalPath, returned.canonicalPath)
        assertEquals("%PDF-1.7 rotated", original.readText())
        replacement.writeText("not a PDF")
        assertThrows(IllegalArgumentException::class.java) {
            store.replacePdfContents(original, replacement)
        }
        assertEquals("A rejected replacement must not touch the saved original.", "%PDF-1.7 rotated", original.readText())
    }

    @Test
    fun savesDerivedPdfLargerThanThePrintBatchLimit() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))
        val source = File(temporaryFolder.root, "derived-source.pdf")
        val targetSize = 52L * 1024L * 1024L
        source.writeBytes("%PDF-1.7\n".toByteArray())
        RandomAccessFile(source, "rw").use { random ->
            val originalBytes = random.length()
            random.setLength(targetSize)
            random.seek(originalBytes)
            val padding = ByteArray(64 * 1024) { ' '.code.toByte() }
            var remaining = targetSize - originalBytes
            while (remaining > 0) {
                val count = minOf(remaining, padding.size.toLong()).toInt()
                random.write(padding, 0, count)
                remaining -= count
            }
        }

        val saved = FileInputStream(source).use {
            store.importDerivedPdf(it, "rotated-output.pdf", "")
        }

        assertTrue("Derived PDF save must not inherit the 50 MB combine limit.", saved.length() > 50L * 1024L * 1024L)
        assertTrue("The derived document must still be detected by PDF content.", DocumentNamePolicy.isPdf(saved))
        assertEquals(targetSize, saved.length())
    }

    @Test
    fun supportsMovingItemsBetweenNestedFoldersAndUsesRootAsEmptyPath() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))
        val source = store.createFolder("", "Incoming")
        val target = store.createFolder("", "School")
        val file = store.importFile(ByteArrayInputStream("certificate".toByteArray()), "certificate.pdf", "Incoming")

        assertEquals("", store.pathOf(store.root))
        store.move(file, "School")
        assertFalse(file.exists())
        val moved = store.folder("School").resolve("certificate.pdf")
        assertTrue(moved.isFile)
        assertEquals("certificate", moved.readText())
        assertEquals("School", store.pathOf(target))
        assertEquals("Incoming", store.pathOf(source))
    }

}
