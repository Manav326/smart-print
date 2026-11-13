package com.quantaedge.smartprint

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import android.net.Uri
import android.webkit.ValueCallback
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.action.ViewActions.scrollTo
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withText
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

@RunWith(AndroidJUnit4::class)
class NativeFilePickerInstrumentedTest {
    @Test
    fun studioItemsAreSelectedWithoutLeavingThePickerAndWaitForOneFinalAction() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        context.getSharedPreferences("smartprint.customer.last-shop", Context.MODE_PRIVATE)
            .edit().clear().commit()

        val photoStore = PhotoStudioStore(context.filesDir, context.cacheDir)
        val idStore = IdStudioStore(context.filesDir, context.cacheDir)
        val jpeg = testJpeg()
        val pdf = IdCardPdfComposer.compose(jpeg, jpeg)
        val photo = photoStore.savePreparedOutput(
            pdf,
            jpeg,
            JSONObject()
                .put("title", "Picker test photo")
                .put("sourceName", "picker-test.jpg")
                .put("sourceMime", "image/jpeg")
                .put("sizeCode", "35x45")
                .put("photoCount", 1)
                .put("photosPerPage", 1)
                .put("pageCount", 1)
                .put("resolutionDpi", 300)
        )
        val id = idStore.savePreparedOutput(pdf, jpeg, jpeg)
        val callbackLatch = CountDownLatch(1)
        val callbackUris = AtomicReference<Array<Uri>?>(null)

        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    MainActivity::class.java.getDeclaredField("portalUrl").apply {
                        isAccessible = true
                    }.set(activity, "https://shop.example.test/station/test")

                    MainActivity::class.java.getDeclaredField("fileChooserAllowsMultiple").apply {
                        isAccessible = true
                    }.setBoolean(activity, true)

                    MainActivity::class.java.getDeclaredField("fileCallback").apply {
                        isAccessible = true
                    }.set(activity, ValueCallback<Array<Uri>> { uris ->
                        callbackUris.set(uris)
                        callbackLatch.countDown()
                    })

                    MainActivity::class.java.getDeclaredMethod(
                        "showPicker",
                        Boolean::class.javaPrimitiveType
                    ).apply { isAccessible = true }.invoke(activity, true)
                }

                onView(withText("Photo Studio · Private documents")).check(matches(isDisplayed()))

                onView(withContentDescription("Select Photo Studio document"))
                    .perform(scrollTo(), click())
                onView(withText("Add selected files (1)")).check(matches(isDisplayed()))
                onView(withText("Photo Studio · Private documents")).check(matches(isDisplayed()))

                onView(withContentDescription("Select ID Studio document"))
                    .perform(scrollTo(), click())
                onView(withText("Add selected files (2)")).check(matches(isDisplayed()))
                onView(withText("ID Studio · Private documents")).check(matches(isDisplayed()))

                assertTrue(
                    "Selecting Studio items must wait for the single final Add action.",
                    callbackLatch.count == 1L
                )
                onView(withText("Add selected files (2)")).perform(click())
                assertTrue("The final Add action should return the selected documents.", callbackLatch.await(15, TimeUnit.SECONDS))

                val attached = callbackUris.get()
                assertNotNull(attached)
                assertEquals(2, attached!!.size)
                attached.forEach { uri ->
                    context.contentResolver.openInputStream(uri).use { input ->
                        assertNotNull("The returned private content URI must be readable.", input)
                        val header = ByteArray(5)
                        assertEquals(5, input!!.read(header))
                        assertEquals("%PDF-".toByteArray(Charsets.US_ASCII).toList(), header.toList())
                    }
                }
                assertTrue("The original encrypted Photo Studio document must remain saved.", photo.isFile)
                assertTrue("The original encrypted ID Studio document must remain saved.", id.isFile)
            }
        } finally {
            photoStore.deletePrepared(photo)
            idStore.deletePrepared(id)
            jpeg.fill(0)
        }
    }

    private fun testJpeg(): ByteArray {
        val bitmap = Bitmap.createBitmap(640, 400, Bitmap.Config.ARGB_8888)
        try {
            bitmap.eraseColor(Color.rgb(235, 232, 224))
            return ByteArrayOutputStream().use { output ->
                check(bitmap.compress(Bitmap.CompressFormat.JPEG, 92, output))
                output.toByteArray()
            }
        } finally {
            bitmap.recycle()
        }
    }
}
