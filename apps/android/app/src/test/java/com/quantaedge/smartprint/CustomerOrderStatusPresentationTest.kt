package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class CustomerOrderStatusPresentationTest {
    @Test
    fun terminalCustomerStatusesHaveExplicitLabels() {
        val expected = mapOf(
            "PDF_EXPORTED" to "PDF ready",
            "REFUNDED" to "Refunded",
            "CANCELLED" to "Cancelled",
            "FAILED" to "Failed"
        )
        expected.forEach { (status, label) ->
            assertEquals(label, CustomerOrderStatusPresentation.activityStatusLabel(status))
            assertNotEquals("In progress", CustomerOrderStatusPresentation.activityStatusLabel(status))
            assertTrue(CustomerOrderStatusPresentation.isTerminalStatus(status))
        }
    }

    @Test
    fun mixedTerminalBatchIsNotMisrepresentedAsFullyCompleted() {
        assertEquals("PARTIALLY_COMPLETED", CustomerOrderStatusPresentation.batchSummaryStatus(
            listOf("REFUNDED", "COMPLETED", "CANCELLED")
        ))
        assertEquals("PARTIALLY_COMPLETED", CustomerOrderStatusPresentation.batchSummaryStatus(
            listOf("PDF_EXPORTED", "REJECTED")
        ))
        assertEquals("COMPLETED", CustomerOrderStatusPresentation.batchSummaryStatus(
            listOf("COMPLETED", "COMPLETED")
        ))
        assertEquals("PDF_EXPORTED", CustomerOrderStatusPresentation.batchSummaryStatus(
            listOf("PDF_EXPORTED", "PDF_EXPORTED")
        ))
    }

    @Test
    fun activeBatchStageDoesNotDependOnFileOrdering() {
        assertEquals("PRINTING", CustomerOrderStatusPresentation.batchSummaryStatus(
            listOf("REFUNDED", "AWAITING_PAYMENT", "PRINTING", "COMPLETED")
        ))
        assertEquals("PRINTING", CustomerOrderStatusPresentation.batchSummaryStatus(
            listOf("COMPLETED", "PRINTING", "AWAITING_PAYMENT", "REFUNDED")
        ))
        assertFalse(CustomerOrderStatusPresentation.isTerminalStatus("PRINTING"))
    }

    @Test
    fun noOutputMixedTerminalBatchHasAnExplicitMixedOutcome() {
        assertEquals("MIXED_OUTCOME", CustomerOrderStatusPresentation.batchSummaryStatus(
            listOf("REFUNDED", "CANCELLED", "FAILED")
        ))
    }

    @Test
    fun batchOutcomeExposesFileCountsAndCompletionProgress() {
        val outcome = CustomerOrderStatusPresentation.batchOutcome(
            listOf("COMPLETED", "PRINTING", "REFUNDED", "REJECTED")
        )
        assertEquals(4, outcome.fileCount)
        assertEquals(1, outcome.readyCount)
        assertEquals(1, outcome.activeCount)
        assertEquals(2, outcome.excludedCount)
        assertEquals(25, outcome.progressPercent)
        assertEquals("PRINTING", outcome.status)
    }

    @Test
    fun eachFileShowsItsOwnAllocationAndRefundNotTheBatchTotal() {
        val summary = CustomerOrderStatusPresentation.filePaymentSummary(
            status = "PARTIALLY_REFUNDED", allocatedAmountMinor = 1_250L,
            refundedAmountMinor = 250L, currency = "INR"
        )
        assertEquals(
            "File allocation: 12.50 INR · Returned: 2.50 INR · Net: 10.00 INR · Partly refunded",
            summary
        )
    }

    @Test
    fun batchTotalHasOneDedicatedSummaryLine() {
        assertEquals("Batch total: 40.00 INR · Payment verified · 2 files",
            CustomerOrderStatusPresentation.batchPaymentSummary("VERIFIED", 4_000L, "INR", 2))
    }
}
