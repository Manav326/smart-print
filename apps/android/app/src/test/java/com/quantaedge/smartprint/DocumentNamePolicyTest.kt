package com.quantaedge.smartprint

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class DocumentNamePolicyTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    @Test fun validatesPrintExtensionsAndSize() {
        assertTrue(DocumentNamePolicy.isPrintable("a.PDF", 10))
        assertTrue(DocumentNamePolicy.isPrintable("b.jpeg", 10))
        assertFalse(DocumentNamePolicy.isPrintable("a.docx", 10))
        assertFalse(DocumentNamePolicy.isPrintable("a.pdf", DocumentNamePolicy.MAX_PRINT_BYTES + 1))
        assertFalse(DocumentNamePolicy.isPrintable("a.pdf", 0))
    }

    @Test fun detectsPdfContentAfterItsExtensionWasRemoved() {
        val file = File(temporaryFolder.root, "renamed-document")
        val prefix = ByteArray(1023) { ' '.code.toByte() }
        file.writeBytes(prefix + "%PDF-1.7\n".toByteArray() + byteArrayOf(0x25, 0x25, 0x45, 0x4f, 0x46))

        assertEquals("application/pdf", DocumentNamePolicy.mimeType(file))
        assertTrue(DocumentNamePolicy.isPdf(file))
        assertTrue(DocumentNamePolicy.isPrintable(file))
        assertTrue(DocumentNamePolicy.isBatchSource(file))
        assertTrue(DocumentNamePolicy.supportsInAppViewer(file))
    }

    @Test fun pdfHeaderDetectionUsesTheSameBoundedWindowAsUploadAnalysis() {
        val accepted = ByteArray(DocumentNamePolicy.PDF_HEADER_SCAN_BYTES)
        "%PDF-".toByteArray().copyInto(accepted, 1023)
        assertTrue(DocumentNamePolicy.isPdfHeader(accepted, accepted.size))

        val rejected = ByteArray(DocumentNamePolicy.PDF_HEADER_SCAN_BYTES + 1)
        "%PDF-".toByteArray().copyInto(rejected, 1024)
        assertFalse(DocumentNamePolicy.isPdfHeader(rejected, rejected.size))
    }

    @Test fun contentTypeWinsWhenTheFilenameExtensionIsWrong() {
        val file = File(temporaryFolder.root, "really-a-pdf.jpg")
        file.writeBytes("%PDF-1.4\n".toByteArray())

        assertEquals("application/pdf", DocumentNamePolicy.mimeType(file))
        assertTrue(DocumentNamePolicy.isPdf(file))
        assertFalse(DocumentNamePolicy.isImage(file))
    }

    @Test fun detectsSupportedImageContentWithoutAnExtension() {
        val jpeg = File(temporaryFolder.root, "camera-export")
        jpeg.writeBytes(byteArrayOf(0xff.toByte(), 0xd8.toByte(), 0xff.toByte(), 0xe0.toByte()))
        assertEquals("image/jpeg", DocumentNamePolicy.mimeType(jpeg))
        assertTrue(DocumentNamePolicy.isPrintable(jpeg))
        assertTrue(DocumentNamePolicy.isImage(jpeg))

        val png = File(temporaryFolder.root, "screenshot")
        png.writeBytes(byteArrayOf(
            0x89.toByte(), 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a
        ))
        assertEquals("image/png", DocumentNamePolicy.mimeType(png))
        assertTrue(DocumentNamePolicy.isPrintable(png))
    }

    @Test fun detectedTypeIsRecomputedWhenTheSamePathIsReplaced() {
        val file = File(temporaryFolder.root, "renamed-document")
        file.writeBytes("%PDF-1.7\\n".toByteArray())
        assertEquals("application/pdf", DocumentNamePolicy.mimeType(file))

        // Size is part of the cache identity, so replacing the file cannot reuse its
        // previous PDF classification even when the path is unchanged.
        file.writeBytes(byteArrayOf(0xff.toByte(), 0xd8.toByte(), 0xff.toByte(), 0xe0.toByte(), 0x00))
        assertEquals("image/jpeg", DocumentNamePolicy.mimeType(file))
        assertTrue(DocumentNamePolicy.isImage(file))
        assertFalse(DocumentNamePolicy.isPdf(file))
    }

    @Test fun unknownExtensionlessFilesAreNotMisclassifiedAsPrintable() {
        val file = File(temporaryFolder.root, "renamed-file")
        file.writeText("This is not a PDF or image.")
        assertEquals("application/octet-stream", DocumentNamePolicy.mimeType(file))
        assertFalse(DocumentNamePolicy.isPrintable(file))
        assertFalse(DocumentNamePolicy.supportsInAppViewer(file))
    }

    @Test fun printableEligibilityRequiresSupportedFileContent() {
        val invalidPdf=File(temporaryFolder.root,"fake.pdf")
        invalidPdf.writeText("This is not a PDF")
        assertEquals("application/octet-stream",DocumentNamePolicy.mimeType(invalidPdf))
        assertFalse(DocumentNamePolicy.isPrintable(invalidPdf))
        assertFalse(DocumentNamePolicy.isBatchSource(invalidPdf))

        val pngNamedJpeg=File(temporaryFolder.root,"actually-png.jpg")
        pngNamedJpeg.writeBytes(byteArrayOf(0x89.toByte(),0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a))
        assertEquals("image/png",DocumentNamePolicy.mimeType(pngNamedJpeg))
        assertTrue(DocumentNamePolicy.isPrintable(pngNamedJpeg))
        assertTrue(DocumentNamePolicy.isBatchSource(pngNamedJpeg))
    }

    @Test fun sanitisesNames() {
        assertEquals("_safe_.pdf", DocumentNamePolicy.safeName("../safe?.pdf"))
        assertEquals("Untitled document", DocumentNamePolicy.safeName("   "))
    }
}
