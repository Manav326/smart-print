package com.quantaedge.smartprint

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CustomerHistoryStoreInstrumentedTest {
    @Test
    fun historyCacheIsEncryptedAccountScopedAndPersistsCompleteRecords() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val store = CustomerHistoryStore(context)
        val email = "history-cache@example.test"
        val otherEmail = "another-account@example.test"
        store.clear(email)
        store.clear(otherEmail)
        val expected = CustomerPrintHistoryItem(
            id = "00000000-0000-0000-0000-000000000001",
            batchId = "batch-1",
            batchName = "School documents",
            batchGroupName = "School",
            jobNumber = "SP-1001",
            status = "COMPLETED",
            pageCount = 2,
            copies = 1,
            currency = "INR",
            quotedAmountMinor = 2500,
            createdUtc = "2026-11-12T10:00:00Z",
            shopName = "Test Print Shop",
            originalFileName = "admission-form.pdf",
            paidAmountMinor = 2500,
            refundedAmountMinor = 0,
            paymentStatus = "VERIFIED",
            paymentMethod = "UPI",
            rejectedPageNumbersJson = "[]",
            outputCompletedUtc = "2026-11-12T10:05:00Z",
            printKind = "DOCUMENT",
            photoSizeCode = null,
            photoCount = 0,
            photosPerPage = null
        )
        try {
            assertNull("A new account starts without a local snapshot.", store.read(email))
            store.replace(email, listOf(expected))
            assertEquals(listOf(expected), store.read(email))
            assertNull("History must not leak between customer accounts.", store.read(otherEmail))
            val storedValues = context.getSharedPreferences(
                "smartprint.customer.history",
                Context.MODE_PRIVATE
            ).all.values.filterIsInstance<String>().joinToString("")
            assertTrue("The persisted history text must not be readable as plain text.",
                !storedValues.contains("admission-form.pdf"))
        } finally {
            store.clear(email)
            store.clear(otherEmail)
        }
    }
}
