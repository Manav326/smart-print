package com.quantaedge.smartprint

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.json.JSONArray
import org.json.JSONObject

@RunWith(AndroidJUnit4::class)
class CustomerAccountClientInstrumentedTest {
    @Test
    fun parsesLegacyUnpaginatedHistoryEnvelopeForOlderApiBuilds() {
        val records = JSONArray().put(historyRecord("legacy-job-1")).put(historyRecord("legacy-job-2"))
        val response = JSONObject().put("jobs", records)

        val history = CustomerAccountClient.parseLegacyHistorySnapshot(response)

        assertEquals(2, history.size)
        assertEquals("legacy-job-1", history[0].id)
        assertEquals("admission-form.pdf", history[0].originalFileName)
    }

    @Test
    fun acceptsPascalCaseLegacyHistoryEnvelope() {
        val response = JSONObject().put("Jobs", JSONArray().put(historyRecord("legacy-job-1")))
        assertEquals("legacy-job-1", CustomerAccountClient.parseLegacyHistorySnapshot(response).single().id)
    }

    @Test
    fun parsesMpayZeroBasedHistoryPaginationEnvelope() {
        val records = JSONArray()
        repeat(10) { index -> records.put(historyRecord("job-$index")) }
        val response = JSONObject()
            .put("items", records)
            .put("page", 0)
            .put("size", 10)
            .put("totalItems", 21)
            .put("totalPages", 3)
            .put("hasNext", true)

        val page = CustomerAccountClient.parseHistoryPage(response, requestedPage = 0, requestedSize = 10)

        assertEquals(0, page.page)
        assertEquals(10, page.size)
        assertEquals(21L, page.totalItems)
        assertEquals(3, page.totalPages)
        assertTrue(page.hasNext)
        assertEquals(10, page.items.size)
        assertEquals("job-0", page.items.first().id)
    }

    @Test
    fun rejectsAnIncompleteServerPageBeforeHistoryCanBeStored() {
        val records = JSONArray()
        repeat(9) { index -> records.put(historyRecord("job-$index")) }
        val response = JSONObject()
            .put("items", records)
            .put("page", 0)
            .put("size", 10)
            .put("totalItems", 21)
            .put("totalPages", 3)
            .put("hasNext", true)

        var rejected = false
        try {
            CustomerAccountClient.parseHistoryPage(response, requestedPage = 0, requestedSize = 10)
        } catch (_: IllegalArgumentException) {
            rejected = true
        }
        assertTrue("An incomplete page must not be treated as a complete history result.", rejected)
    }

    private fun historyRecord(id: String) = JSONObject()
        .put("id", id)
        .put("batchId", "batch-1")
        .put("batchName", "College documents")
        .put("batchGroupName", "Documents")
        .put("jobNumber", id)
        .put("status", "COMPLETED")
        .put("pageCount", 2)
        .put("copies", 1)
        .put("currency", "INR")
        .put("quotedAmountMinor", 2500)
        .put("createdUtc", "2026-11-12T10:00:00Z")
        .put("shopName", "Test Print Shop")
        .put("originalFileName", "admission-form.pdf")
        .put("paidAmountMinor", 2500)
        .put("refundedAmountMinor", 0)
        .put("paymentStatus", "VERIFIED")
        .put("paymentMethod", "UPI")
        .put("rejectedPageNumbersJson", "[]")
        .put("outputCompletedUtc", JSONObject.NULL)
        .put("printKind", "DOCUMENT")
        .put("photoSizeCode", JSONObject.NULL)
        .put("photoCount", 0)
        .put("photosPerPage", JSONObject.NULL)
}
