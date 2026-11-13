package com.quantaedge.smartprint

import android.content.Context
import androidx.core.content.FileProvider
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class PrivateStudioFileProviderInstrumentedTest {
    @Test
    fun photoStudioTransientPdfCanBeOpenedThroughTheDocumentsProvider() {
        assertTransientFileCanBeOpened("photo-studio-transient", "photo-studio-provider-test.pdf")
    }

    @Test
    fun idStudioTransientPdfRemainsAvailableThroughTheDocumentsProvider() {
        assertTransientFileCanBeOpened("id-studio-transient", "id-studio-provider-test.pdf")
    }

    private fun assertTransientFileCanBeOpened(directoryName: String, fileName: String) {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val directory = File(context.cacheDir, directoryName).apply { mkdirs() }
        val file = File(directory, fileName)
        try {
            file.writeBytes("%PDF-1.4\nSmartPrint provider test".toByteArray(Charsets.US_ASCII))
            val uri = FileProvider.getUriForFile(
                context,
                "${context.packageName}.documents",
                file
            )

            assertEquals("content", uri.scheme)
            context.contentResolver.openInputStream(uri).use { input ->
                assertNotNull("The provider must resolve its configured transient cache root.", input)
                val header = ByteArray(5)
                assertEquals(5, input!!.read(header))
                assertEquals("%PDF-".toByteArray(Charsets.US_ASCII).toList(), header.toList())
            }
        } finally {
            file.delete()
        }
    }
}
