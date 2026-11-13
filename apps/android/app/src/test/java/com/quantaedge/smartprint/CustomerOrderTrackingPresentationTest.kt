package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class CustomerOrderTrackingPresentationTest {
    @Test
    fun shopReviewIsTheCurrentMilestoneBeforeApproval() {
        val steps = CustomerOrderTrackingPresentation.steps(
            listOf("AWAITING_SHOP_APPROVAL"), null
        )
        assertEquals(CustomerTrackingStepState.DONE, steps[0].state)
        assertEquals(CustomerTrackingStepState.CURRENT, steps[1].state)
        assertEquals(CustomerTrackingStepState.UPCOMING, steps[2].state)
    }

    @Test
    fun paymentMovesToCompletedWhenThePrinterHasStarted() {
        val steps = CustomerOrderTrackingPresentation.steps(
            listOf("PRINTING"), "VERIFIED"
        )
        assertEquals(CustomerTrackingStepState.DONE, steps[1].state)
        assertEquals(CustomerTrackingStepState.DONE, steps[2].state)
        assertEquals(CustomerTrackingStepState.CURRENT, steps[3].state)
        assertEquals(CustomerTrackingStepState.UPCOMING, steps[4].state)
    }

    @Test
    fun oneUnpaidFileKeepsBatchPaymentAsTheCurrentMilestone() {
        val steps = CustomerOrderTrackingPresentation.steps(
            listOf("PRINTING", "AWAITING_PAYMENT"),
            null,
            listOf("VERIFIED", "AWAITING_CUSTOMER_PAYMENT")
        )
        assertEquals(CustomerTrackingStepState.DONE, steps[1].state)
        assertEquals(CustomerTrackingStepState.CURRENT, steps[2].state)
        assertEquals(CustomerTrackingStepState.UPCOMING, steps[3].state)
    }

    @Test
    fun allPerFilePaymentsVerifiedCanCompletePaymentWithoutBatchPaymentRecord() {
        val steps = CustomerOrderTrackingPresentation.steps(
            listOf("PAID_AUTHORISED", "LOCAL_QUEUE_READY"),
            null,
            listOf("VERIFIED", "VERIFIED")
        )
        assertEquals(CustomerTrackingStepState.DONE, steps[2].state)
        assertEquals(CustomerTrackingStepState.CURRENT, steps[3].state)
    }

    @Test
    fun completePhysicalAndPdfOrdersReachTheirCorrectFinalMilestone() {
        val physical = CustomerOrderTrackingPresentation.steps(listOf("COMPLETED"), "VERIFIED")
        val pdf = CustomerOrderTrackingPresentation.steps(listOf("PDF_EXPORTED"), "VERIFIED")
        assertEquals(CustomerTrackingStepState.DONE, physical.last().state)
        assertEquals("Ready for collection", physical.last().title)
        assertEquals(CustomerTrackingStepState.DONE, pdf.last().state)
        assertEquals("PDF ready", pdf.last().title)
    }

    @Test
    fun rejectedOnlyOrdersSkipPaymentAndPrinting() {
        val steps = CustomerOrderTrackingPresentation.steps(listOf("REJECTED"), null)
        assertEquals(CustomerTrackingStepState.SKIPPED, steps[2].state)
        assertEquals(CustomerTrackingStepState.SKIPPED, steps[3].state)
        assertEquals(CustomerTrackingStepState.SKIPPED, steps[4].state)
    }

    @Test
    fun refundedOnlyOrdersShowTheRefundAndDoNotClaimPrintingCompleted() {
        val steps = CustomerOrderTrackingPresentation.steps(
            listOf("REFUNDED"), "REFUNDED", listOf("REFUNDED")
        )
        assertEquals(CustomerTrackingStepState.DONE, steps[2].state)
        assertTrue(steps[2].detail.contains("refunded", ignoreCase = true))
        assertEquals(CustomerTrackingStepState.SKIPPED, steps[3].state)
        assertEquals(CustomerTrackingStepState.SKIPPED, steps[4].state)
    }

    @Test
    fun partiallyCompletedOrdersDoNotClaimEveryFileIsReadyForCollection() {
        val steps = CustomerOrderTrackingPresentation.steps(
            listOf("COMPLETED", "REFUNDED", "CANCELLED"), "PARTIALLY_REFUNDED"
        )
        assertTrue(steps.last().detail.contains("1 of 3"))
        assertTrue(steps.last().detail.contains("remaining files closed"))
    }

    @Test
    fun currentHeadlineExplainsTheHighestRelevantMilestone() {
        assertEquals(
            "Your order is being printed",
            CustomerOrderTrackingPresentation.currentHeadline(
                listOf("COMPLETED", "PRINTING"), "VERIFIED"
            )
        )
        assertEquals(
            "Your order has a partial result",
            CustomerOrderTrackingPresentation.currentHeadline(
                listOf("COMPLETED", "REFUNDED"), "PARTIALLY_REFUNDED"
            )
        )
        assertEquals(
            "The print quote needs to be refreshed",
            CustomerOrderTrackingPresentation.currentHeadline(
                listOf("QUOTE_EXPIRED"), "QUOTE_EXPIRED"
            )
        )
        assertEquals(
            "Payment reported; waiting for shop verification",
            CustomerOrderTrackingPresentation.currentHeadline(
                listOf("AWAITING_PAYMENT"), "CUSTOMER_REPORTED"
            )
        )
    }
}
