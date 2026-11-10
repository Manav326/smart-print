package com.quantaedge.smartprint

import android.graphics.BitmapFactory
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.util.UUID

/**
 * Private storage boundary for prepared identity-card print assets.
 *
 * Unlike LocalDocumentStore, this directory is never traversed by My Files,
 * folder export, ZIP export, share or Open With actions. The only read path
 * exposed to the WebView is the app's non-exported FileProvider during a
 * connected-shop file-selection action.
 */
class IdStudioStore(filesDir: File) {
    val root = File(filesDir, "id-studio")
    private val drafts = File(root, ".drafts")

    init {
        check(root.exists() || root.mkdirs()) { "Private ID storage could not be created." }
        check(root.isDirectory) { "Private ID storage is unavailable." }
        check(drafts.exists() || drafts.mkdirs()) { "Private ID scan storage could not be created." }
        check(drafts.isDirectory) { "Private ID scan storage is unavailable." }
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
        val directory = draftDirectory(draftId)
        val target = File(directory, side + ".jpg")
        val temporary = File(directory, "." + side + "-" + UUID.randomUUID().toString() + ".tmp")
        var copied = 0L
        try {
            FileOutputStream(temporary).use { output ->
                val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    copied += count
                    require(copied <= MAX_SCAN_BYTES) {
                        "This scan is too large. Please scan the card again with a closer crop."
                    }
                    output.write(buffer, 0, count)
                }
                output.fd.sync()
            }
            require(copied >= MIN_SCAN_BYTES) { "The scanner returned an empty or incomplete image." }
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeFile(temporary.absolutePath, bounds)
            require(bounds.outWidth >= MIN_IMAGE_EDGE && bounds.outHeight >= MIN_IMAGE_EDGE) {
                "The scanned card image is too small to print clearly. Move closer and scan again."
            }
            require(bounds.outWidth.toLong() * bounds.outHeight.toLong() <= MAX_IMAGE_PIXELS) {
                "The scanned image is too large to prepare safely. Please scan again."
            }
            val decoded = BitmapFactory.decodeFile(
                temporary.absolutePath,
                BitmapFactory.Options().apply { inSampleSize = 4 }
            ) ?: error("The scanned image is incomplete or unreadable. Please scan again.")
            decoded.recycle()
            try {
                java.nio.file.Files.move(
                    temporary.toPath(),
                    target.toPath(),
                    java.nio.file.StandardCopyOption.REPLACE_EXISTING,
                    java.nio.file.StandardCopyOption.ATOMIC_MOVE
                )
            } catch (_: java.nio.file.AtomicMoveNotSupportedException) {
                java.nio.file.Files.move(
                    temporary.toPath(),
                    target.toPath(),
                    java.nio.file.StandardCopyOption.REPLACE_EXISTING
                )
            }
            return target
        } catch (error: Exception) {
            temporary.delete()
            throw error
        }
    }

    fun scanFile(draftId: String, side: String): File {
        require(side == "front" || side == "back") { "Choose the front or back side of the card." }
        val directory = draftDirectory(draftId)
        val file = File(directory, side + ".jpg").canonicalFile
        require(file.parentFile?.canonicalPath == directory.canonicalPath && file.isFile) {
            "The requested scan is no longer available. Scan that side again."
        }
        return file
    }

    fun hasFront(draftId: String): Boolean = runCatching { scanFile(draftId, "front").isFile }.getOrDefault(false)
    fun hasBack(draftId: String): Boolean = runCatching { scanFile(draftId, "back").isFile }.getOrDefault(false)

    fun createPreparedOutput(): File =
        File(root, FILE_PREFIX + " " + UUID.randomUUID().toString() + ".pdf").canonicalFile.also {
            require(it.parentFile?.canonicalPath == root.canonicalPath && !it.exists()) {
                "A private ID document with this name already exists."
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
            canonical.extension.equals("pdf", ignoreCase = true)
    }.getOrDefault(false)

    fun deletePrepared(file: File) {
        require(isPreparedDocument(file)) { "Only a prepared ID document can be deleted here." }
        check(file.delete()) { "The prepared ID document could not be deleted." }
    }

    fun deleteDraft(draftId: String) {
        val directory = draftDirectory(draftId)
        check(directory.deleteRecursively()) { "Temporary ID scans could not be cleared." }
    }

    fun draftExists(draftId: String): Boolean =
        isValidId(draftId) && File(drafts, draftId).isDirectory

    private fun isValidId(value: String): Boolean =
        runCatching { UUID.fromString(value).toString() == value }.getOrDefault(false)

    companion object {
        const val FILE_PREFIX = "SmartPrint ID -"
        const val MAX_SCAN_BYTES = 30L * 1024L * 1024L
        private const val MIN_SCAN_BYTES = 512L
        private const val MIN_IMAGE_EDGE = 480
        private const val MAX_IMAGE_PIXELS = 30_000_000L
    }
}