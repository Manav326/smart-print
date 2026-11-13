package com.quantaedge.smartprint

import java.util.Locale

internal enum class CustomerTrackingStepState { DONE, CURRENT, UPCOMING, SKIPPED }

internal data class CustomerTrackingStep(
    val title: String,
    val detail: String,
    val state: CustomerTrackingStepState
)

internal object CustomerOrderTrackingPresentation {
    private val ready = setOf("COMPLETED", "PDF_EXPORTED")
    private val excluded = setOf("REJECTED", "REFUNDED", "CANCELLED", "FAILED")
    private val early = setOf(
        "UPLOADED", "VALIDATING", "ANALYSING", "AWAITING_CONFIGURATION",
        "AWAITING_CUSTOMER_APPROVAL", "AWAITING_SHOP_APPROVAL"
    )
    private val paymentPending = setOf(
        "AWAITING_PAYMENT", "AWAITING_CUSTOMER_PAYMENT", "AWAITING_COUNTER_PAYMENT",
        "CASH_AWAITING_PAYMENT", "CUSTOMER_REPORTED", "PAYMENT_REPORTED",
        "PAYMENT_REJECTED", "QUOTE_EXPIRED", "REJECTED", "CANCELLED"
    )
    private val printing = setOf(
        "PAID_AUTHORISED", "LOCAL_QUEUE_CLAIMED", "LOCAL_QUEUE_READY",
        "PRINT_AUTHORISED", "PRINT_SUBMITTING", "SPOOLER_ACCEPTED", "PRINTING",
        "OUTPUT_UNCONFIRMED", "PDF_EXPORTING", "QUEUED", "SUBMITTING_TO_SPOOLER"
    )
    private val paymentVerified = setOf("VERIFIED", "PARTIALLY_REFUNDED", "REFUNDED")

    fun steps(
        statuses: List<String>,
        paymentStatus: String?,
        filePaymentStatuses: List<String> = emptyList()
    ): List<CustomerTrackingStep> {
        val normalized = statuses.map { it.trim().uppercase(Locale.ROOT) }.filter(String::isNotBlank)
        if (normalized.isEmpty()) return emptyList()

        val active = normalized.filterNot { it in ready || it in excluded }
        val allReviewed = normalized.all { it !in early }
        val allResolved = active.isEmpty()
        val payment = paymentStatus.orEmpty().trim().uppercase(Locale.ROOT)
        val hasBillableFiles = normalized.any { it !in excluded }
        val billableIndexes = normalized.indices.filter { normalized[it] !in excluded }
        val perFilePayments = normalized.indices.map { index ->
            filePaymentStatuses.getOrNull(index).orEmpty().trim().uppercase(Locale.ROOT)
        }
        val batchPaymentKnown = payment.isNotBlank()
        val paymentVerifiedForBatch = if (batchPaymentKnown) {
            payment in paymentVerified
        } else {
            billableIndexes.all { index ->
                normalized[index] in printing || normalized[index] in ready ||
                    perFilePayments[index] in paymentVerified
            }
        }
        val paymentRequired = if (batchPaymentKnown) {
            payment in paymentPending
        } else {
            billableIndexes.any { index ->
                normalized[index] in paymentPending || perFilePayments[index] in paymentPending
            }
        }
        val anyPrinting = normalized.any { it in printing }
        val allOutputResolved = allResolved
        val allReady = normalized.all { it in ready }

        val reviewState = when {
            allReviewed -> CustomerTrackingStepState.DONE
            normalized.any { it in early } -> CustomerTrackingStepState.CURRENT
            else -> CustomerTrackingStepState.UPCOMING
        }
        val paymentState = when {
            !hasBillableFiles -> CustomerTrackingStepState.SKIPPED
            !allReviewed -> CustomerTrackingStepState.UPCOMING
            paymentRequired -> CustomerTrackingStepState.CURRENT
            paymentVerifiedForBatch -> CustomerTrackingStepState.DONE
            else -> CustomerTrackingStepState.CURRENT
        }
        val printingState = when {
            !hasBillableFiles -> CustomerTrackingStepState.SKIPPED
            allReady || allOutputResolved -> CustomerTrackingStepState.DONE
            paymentState != CustomerTrackingStepState.DONE -> CustomerTrackingStepState.UPCOMING
            anyPrinting -> CustomerTrackingStepState.CURRENT
            else -> CustomerTrackingStepState.UPCOMING
        }
        val readyState = when {
            allReady -> CustomerTrackingStepState.DONE
            allOutputResolved && normalized.any { it in ready } -> CustomerTrackingStepState.DONE
            allOutputResolved && normalized.none { it in ready } -> CustomerTrackingStepState.SKIPPED
            normalized.any { it in ready } -> CustomerTrackingStepState.CURRENT
            else -> CustomerTrackingStepState.UPCOMING
        }

        val readyCount = normalized.count { it in ready }
        val readyDetail = when {
            readyCount == normalized.size -> if (normalized.all { it == "PDF_EXPORTED" }) "PDF output saved" else "All files completed"
            readyCount > 0 && allOutputResolved -> "$readyCount of ${normalized.size} files completed; remaining files closed"
            readyCount > 0 -> "$readyCount of ${normalized.size} files completed"
            allOutputResolved -> "No files produced output"
            anyPrinting -> "The shop is processing your files"
            else -> "Waiting for printing to begin"
        }
        val anyFileVerified = billableIndexes.any { index ->
            normalized[index] in printing || normalized[index] in ready ||
                perFilePayments[index] in paymentVerified
        }
        val paymentDetail = when {
            !hasBillableFiles -> "No payment required for excluded files"
            payment in paymentVerified -> "Payment verified"
            payment in paymentPending -> when (payment) {
                "CUSTOMER_REPORTED", "PAYMENT_REPORTED" -> "Payment reported · shop verification pending"
                "PAYMENT_REJECTED", "REJECTED" -> "Payment was declined · review the payment details"
                "QUOTE_EXPIRED" -> "The quote needs to be refreshed"
                else -> "Waiting for payment"
            }
            paymentRequired -> "Payment is pending for one or more files"
            anyFileVerified -> "Payment verified for processed files"
            else -> "Payment stage will appear after shop review"
        }
        val reviewDetail = when {
            reviewState == CustomerTrackingStepState.DONE -> "Shop review resolved for all files"
            normalized.any { it == "AWAITING_SHOP_APPROVAL" } -> "The shop is reviewing your files"
            else -> "Your files are being checked"
        }
        val printDetail = when {
            printingState == CustomerTrackingStepState.DONE && readyCount > 0 -> "Output completed or remaining files were closed"
            anyPrinting -> "Printer processing is underway"
            else -> "Starts after review and payment"
        }

        return listOf(
            CustomerTrackingStep("Order received", "Your files were submitted to the shop", CustomerTrackingStepState.DONE),
            CustomerTrackingStep("Shop review", reviewDetail, reviewState),
            CustomerTrackingStep("Payment", paymentDetail, paymentState),
            CustomerTrackingStep("Printing", printDetail, printingState),
            CustomerTrackingStep(
                when {
                    allOutputResolved && !allReady && readyCount > 0 -> "Order outcome"
                    allOutputResolved && readyCount == 0 -> "Order closed"
                    readyCount > 0 && !allReady -> "Partial output ready"
                    normalized.any { it == "PDF_EXPORTED" } && normalized.none { it == "COMPLETED" } -> "PDF ready"
                    else -> "Ready for collection"
                },
                readyDetail,
                readyState
            )
        )
    }

    fun currentHeadline(
        statuses: List<String>,
        paymentStatus: String?,
        filePaymentStatuses: List<String> = emptyList()
    ): String {
        val payment = paymentStatus.orEmpty().trim().uppercase(Locale.ROOT)
        val steps = steps(statuses, paymentStatus, filePaymentStatuses)
        val current = steps.lastOrNull { it.state == CustomerTrackingStepState.CURRENT }
        if (current != null) {
            return when (current.title) {
                "Shop review" -> "The shop is reviewing your order"
                "Payment" -> if (
                    payment in setOf("PAYMENT_REJECTED", "REJECTED") ||
                    statuses.any { it.equals("PAYMENT_REJECTED", true) } ||
                    filePaymentStatuses.any { it.equals("PAYMENT_REJECTED", true) }
                ) "Payment needs attention" else "Waiting for payment"
                "Printing" -> "Your order is being printed"
                "Partial output ready" -> "Some files are ready; the rest are still in progress"
                else -> current.title
            }
        }
        val outcome = CustomerOrderStatusPresentation.batchSummaryStatus(statuses)
        return when (outcome) {
            "COMPLETED" -> "Your print order is complete"
            "PDF_EXPORTED" -> "Your PDF is ready"
            "PARTIALLY_COMPLETED" -> "Your order has a partial result"
            "REFUNDED" -> "Your order was refunded"
            "CANCELLED" -> "Your order was cancelled"
            "REJECTED" -> "Your files were not accepted"
            "FAILED" -> "Your print order could not be completed"
            "MIXED_OUTCOME" -> "Your order has a mixed outcome"
            else -> "Your order has been received"
        }
    }
}
