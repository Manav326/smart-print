package com.quantaedge.smartprint

import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.InputStream
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.StandardCopyOption
import java.util.Locale
import java.util.UUID

class LocalDocumentStore(filesDir: File) {
    val root = File(filesDir, "documents")

    init {
        check(root.exists() || root.mkdirs()) { "Document storage could not be created." }
        check(root.isDirectory) { "Document storage is not a directory." }
    }

    fun folder(path: String): File {
        if (path.isBlank()) return root
        val parts = path.replace('\\', '/').split('/').filter { it.isNotBlank() }
        require(parts.none { it == "." || it == ".." }) { "Invalid folder path." }
        val result = File(root, parts.joinToString(File.separator)).canonicalFile
        val base = root.canonicalFile
        require(result.path == base.path || result.path.startsWith(base.path + File.separator)) {
            "Folder is outside QRtoPrint storage."
        }
        require(result.isDirectory) { "Folder is no longer available." }
        return result
    }

    fun createFolder(parentPath: String, name: String, allowExisting: Boolean = false): File {
        val parent = folder(parentPath)
        val target = File(parent, cleanName(name))
        if (allowExisting && target.isDirectory) return target
        require(!target.exists()) { "An item with that name already exists." }
        check(target.mkdir()) { "Folder could not be created." }
        return target
    }

    fun importFile(input: InputStream, name: String, destinationPath: String): File =
        importFileInternal(input, name, destinationPath, DocumentNamePolicy.MAX_IMPORT_BYTES, requirePdf = false)

    /**
     * Imports a derived, losslessly edited PDF. This is deliberately not subject
     * to the 50 MB batch-combine limit or the 100 MB original-import limit;
     * available disk space and the device safety reserve bound the write instead.
     */
    fun importDerivedPdf(input: InputStream, name: String, destinationPath: String): File =
        importFileInternal(input, name, destinationPath, Long.MAX_VALUE, requirePdf = true)

    private fun importFileInternal(
        input: InputStream,
        name: String,
        destinationPath: String,
        maxBytes: Long,
        requirePdf: Boolean
    ): File {
        val parent = folder(destinationPath)
        val target = uniqueFile(parent, DocumentNamePolicy.safeName(name))
        var total = 0L
        try {
            require(parent.usableSpace > MIN_FREE_SPACE_RESERVE) {
                "There is not enough free storage to save this document. Free up space and try again."
            }
            FileOutputStream(target).use { output ->
                val buffer = ByteArray(64 * 1024)
                while (true) {
                    val n = input.read(buffer)
                    if (n < 0) break
                    total += n
                    require(total <= maxBytes) {
                        "The document exceeds the supported size limit."
                    }
                    require(parent.usableSpace > MIN_FREE_SPACE_RESERVE + n) {
                        "Device storage is almost full. The save was stopped before the phone ran out of space."
                    }
                    output.write(buffer, 0, n)
                }
                output.fd.sync()
            }
            require(total > 0) { "The selected file is empty." }
            if (requirePdf) {
                require(DocumentNamePolicy.isPdf(target)) {
                    "The edited document does not contain valid PDF content."
                }
            }
            return target
        } catch (e: Exception) {
            target.delete()
            throw e
        }
    }

    /** Total bytes in the app-private customer document library. */
    fun storageUsageBytes(): Long =
        root.walkTopDown().filter { it.isFile }.sumOf { it.length() }

    private companion object {
        const val MIN_FREE_SPACE_RESERVE = 5L * 1024L * 1024L
    }

    fun list(directory: File): Array<File> {
        val base = root.canonicalPath
        val path = directory.canonicalPath
        require(path == base || path.startsWith(base + File.separator)) { "Folder is outside local storage." }
        return (directory.listFiles() ?: emptyArray()).sortedWith(
            compareBy<File>({ !it.isDirectory }, { it.lastModified() }, { it.name.lowercase(Locale.ROOT) })
        ).toTypedArray()
    }

    /** Places an item immediately before another sibling in the same folder. */
    fun moveBefore(item: File, target: File): Boolean {
        val base = root.canonicalFile
        val source = item.canonicalFile
        val destination = target.canonicalFile
        require(source.path.startsWith(base.path + File.separator) &&
            destination.path.startsWith(base.path + File.separator)) {
            "Items must be inside local storage."
        }
        require(source.parentFile?.canonicalPath == destination.parentFile?.canonicalPath) {
            "Items can only be reordered within the same folder."
        }
        if (source.path == destination.path) return false
        val parent = source.parentFile ?: return false
        val items = list(parent).toMutableList()
        val from = items.indexOfFirst { it.canonicalPath == source.path }
        val to = items.indexOfFirst { it.canonicalPath == destination.path }
        if (from < 0 || to < 0) return false
        val selected = items.removeAt(from)
        val targetIndex = if (from < to) to - 1 else to
        items.add(targetIndex, selected)
        val firstTimestamp = System.currentTimeMillis() + 60_000L
        items.forEachIndexed { index, file ->
            check(file.setLastModified(firstTimestamp + index * 1_000L)) {
                "The display order could not be saved."
            }
        }
        return true
    }

    fun move(item: File, destinationPath: String) {
        val base = root.canonicalFile
        val source = item.canonicalFile
        require(source.path.startsWith(base.path + File.separator)) {
            "Item is outside local storage."
        }
        val destination = folder(destinationPath).canonicalFile
        require(source.path != destination.path) { "Choose a different destination folder." }
        if (item.isDirectory) {
            require(!destination.path.startsWith(source.path + File.separator)) {
                "A folder cannot be moved inside itself."
            }
        }
        val target = File(destination, item.name)
        require(!target.exists()) { "An item with that name already exists in the destination." }
        check(item.renameTo(target)) { "Item could not be moved." }
    }

    /**
     * Changes the explicit order of sibling files/folders without changing their names or bytes.
     * Order is stored using filesystem metadata inside app-private storage, so exports preserve
     * the visible sequence without adding numeric prefixes to customer filenames.
     */
    fun reorder(item: File, offset: Int): Boolean {
        require(offset == -1 || offset == 1) { "Order offset must be -1 or 1." }
        val base = root.canonicalFile
        val canonical = item.canonicalFile
        require(canonical.path.startsWith(base.path + File.separator)) {
            "Item is outside local storage."
        }
        val parent = canonical.parentFile ?: error("Item has no parent folder.")
        val items = list(parent).toMutableList()
        val from = items.indexOfFirst { it.canonicalPath == canonical.path }
        if (from < 0) return false
        val to = from + offset
        if (to !in items.indices) return false
        val selected = items.removeAt(from)
        items.add(to, selected)
        val firstTimestamp = System.currentTimeMillis() + 60_000L
        items.forEachIndexed { index, file ->
            check(file.setLastModified(firstTimestamp + index * 1_000L)) {
                "The display order could not be saved."
            }
        }
        return true
    }

    fun pathOf(file: File): String {
        val base = root.canonicalPath
        val path = file.canonicalPath
        require(path == base || path.startsWith(base + File.separator)) { "Item is outside local storage." }
        if (path == base) return ""
        return path.removePrefix(base + File.separator).replace(File.separatorChar, '/')
    }

    /**
     * Replaces an existing app-private PDF only after a complete replacement has
     * been staged and validated in the same directory. The final move is atomic
     * when supported by the filesystem; the fallback keeps a recoverable backup.
     */
    fun replacePdfContents(file: File, replacement: File): File {
        val base = root.canonicalFile
        val target = file.canonicalFile
        require(target.path.startsWith(base.path + File.separator)) {
            "The original PDF is outside SmartPrint storage."
        }
        require(target.isFile && DocumentNamePolicy.isPdf(target)) {
            "The original PDF is no longer available."
        }

        val source = replacement.canonicalFile
        require(source.isFile && source.path != target.path && DocumentNamePolicy.isPdf(source)) {
            "The replacement must be a valid PDF file."
        }
        require(source.length() > 0L) {
            "The replacement PDF is empty."
        }

        val parent = target.parentFile ?: error("The original PDF has no parent folder.")
        val staging = File(parent, ".\${target.name}.\${UUID.randomUUID()}.replacement")
        val backup = File(parent, ".\${target.name}.\${UUID.randomUUID()}.backup")
        try {
            FileInputStream(source).use { input ->
                FileOutputStream(staging).use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var total = 0L
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        total += count
                        require(parent.usableSpace > MIN_FREE_SPACE_RESERVE + count) {
                            "There is not enough free space to safely replace the original PDF."
                        }
                        output.write(buffer, 0, count)
                    }
                    output.fd.sync()
                }
            }
            require(staging.length() > 0L && DocumentNamePolicy.isPdf(staging)) {
                "The replacement PDF could not be validated."
            }

            try {
                java.nio.file.Files.move(
                    staging.toPath(),
                    target.toPath(),
                    StandardCopyOption.ATOMIC_MOVE,
                    StandardCopyOption.REPLACE_EXISTING
                )
            } catch (_: AtomicMoveNotSupportedException) {
                check(target.renameTo(backup)) {
                    "The original PDF could not be safely backed up. It was not changed."
                }
                try {
                    check(staging.renameTo(target)) {
                        "The new PDF could not be installed. The original is retained as \${backup.name}."
                    }
                    backup.delete()
                } catch (error: Exception) {
                    if (!target.exists()) backup.renameTo(target)
                    throw error
                }
            }
            return target
        } catch (error: Exception) {
            staging.delete()
            throw error
        } finally {
            // Never remove a recovery backup when the replacement failed.
            if (target.isFile) backup.delete()
        }
    }

    fun rename(file: File, name: String) {
        require(file.canonicalPath.startsWith(root.canonicalPath + File.separator)) { "Item is outside local storage." }
        val target = File(file.parentFile, cleanName(name))
        require(!target.exists()) { "An item with that name already exists." }
        check(file.renameTo(target)) { "Item could not be renamed." }
    }

    fun delete(file: File) {
        require(file.canonicalPath.startsWith(root.canonicalPath + File.separator)) { "Cannot delete the library root." }
        check(file.deleteRecursively()) { "Item could not be deleted." }
    }

    private fun cleanName(name: String): String {
        val safe = DocumentNamePolicy.safeName(name)
        require(safe != "." && safe != "..") { "Name is invalid." }
        return safe
    }

    private fun uniqueFile(parent: File, name: String): File {
        val dot = name.lastIndexOf('.')
        val base = if (dot > 0) name.substring(0, dot) else name
        val ext = if (dot > 0) name.substring(dot) else ""
        var file = File(parent, name)
        var i = 2
        while (file.exists()) {
            file = File(parent, "$base ($i)$ext")
            i++
            require(i < 10_000) { "Too many duplicate file names." }
        }
        return file
    }
}
