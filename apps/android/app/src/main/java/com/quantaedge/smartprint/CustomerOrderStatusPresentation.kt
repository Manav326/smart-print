package com.quantaedge.smartprint

import java.util.Locale

internal data class CustomerBatchOutcome(
    val status: String,
    val fileCount: Int,
    val readyCount: Int,
    val activeCount: Int,
    val excludedCount: Int,
    val progressPercent: Int
)

internal object CustomerOrderStatusPresentation {
    private val terminalStatuses = setOf(
        "COMPLETED", "PDF_EXPORTED", "REJECTED", "REFUNDED", "CANCELLED", "FAILED",
        "PARTIALLY_COMPLETED", "MIXED_OUTCOME"
    )

    fun activityStatusLabel(status: String): String = when (status.trim().uppercase(Locale.ROOT)) {
        "UPLOADED" -> "Files received"
        "VALIDATING" -> "Checking files"
        "ANALYSING" -> "Reading files"
        "AWAITING_CONFIGURATION" -> "Print options needed"
        "AWAITING_CUSTOMER_APPROVAL" -> "Your approval needed"
        "AWAITING_SHOP_APPROVAL" -> "Shop review"
        "AWAITING_PAYMENT", "AWAITING_CUSTOMER_PAYMENT", "CASH_AWAITING_PAYMENT" -> "Payment due"
        "AWAITING_COUNTER_PAYMENT" -> "Pay at counter"
        "PAYMENT_REPORTED", "CUSTOMER_REPORTED" -> "Verify pending"
        "PAYMENT_REJECTED" -> "Payment declined"
        "PAID_AUTHORISED", "VERIFIED" -> "Payment verified"
        "LOCAL_QUEUE_CLAIMED" -> "Transferring"
        "LOCAL_QUEUE_READY", "QUEUED" -> "Queued"
        "PRINT_AUTHORISED" -> "Print approved"
        "PRINT_SUBMITTING", "SUBMITTING_TO_SPOOLER" -> "Sending"
        "SPOOLER_ACCEPTED" -> "Queue accepted"
        "PRINTING" -> "Printing"
        "OUTPUT_UNCONFIRMED" -> "Output check"
        "PDF_EXPORTING" -> "Saving PDF"
        "PDF_EXPORTED" -> "PDF ready"
        "COMPLETED" -> "Completed"
        "REJECTED" -> "Rejected"
        "REFUNDED" -> "Refunded"
        "PARTIALLY_REFUNDED" -> "Partly refunded"
        "CANCELLED" -> "Cancelled"
        "FAILED" -> "Failed"
        "QUOTE_EXPIRED" -> "Quote expired"
        "PARTIALLY_COMPLETED" -> "Partially completed"
        "MIXED_OUTCOME" -> "Mixed outcome"
        else -> "In progress"
    }

    fun isTerminalStatus(status: String): Boolean =
        terminalStatuses.contains(status.trim().uppercase(Locale.ROOT))

    /** A deterministic batch result; never let file ordering decide the customer-facing status. */
    fun batchSummaryStatus(statuses: List<String>): String {
        val normalized = statuses.map { it.trim().uppercase(Locale.ROOT) }.filter(String::isNotBlank)
        if (normalized.isEmpty()) return ""

        val active = normalized.filterNot(::isTerminalStatus)
        if (active.isNotEmpty()) {
            val stageRank = mapOf(
                "UPLOADED" to 0, "VALIDATING" to 0, "ANALYSING" to 0,
                "AWAITING_CONFIGURATION" to 0, "AWAITING_CUSTOMER_APPROVAL" to 0,
                "AWAITING_SHOP_APPROVAL" to 1,
                "AWAITING_PAYMENT" to 2, "AWAITING_CUSTOMER_PAYMENT" to 2,
                "AWAITING_COUNTER_PAYMENT" to 2, "CASH_AWAITING_PAYMENT" to 2,
                "CUSTOMER_REPORTED" to 2, "PAYMENT_REPORTED" to 2,
                "PAYMENT_REJECTED" to 2, "QUOTE_EXPIRED" to 2,
                "PAID_AUTHORISED" to 3, "VERIFIED" to 3,
                "LOCAL_QUEUE_CLAIMED" to 4, "LOCAL_QUEUE_READY" to 4,
                "PRINT_AUTHORISED" to 4, "PRINT_SUBMITTING" to 4,
                "SPOOLER_ACCEPTED" to 4, "PRINTING" to 4,
                "OUTPUT_UNCONFIRMED" to 4, "PDF_EXPORTING" to 4,
                "QUEUED" to 4, "SUBMITTING_TO_SPOOLER" to 4
            )
            return active.maxWithOrNull(
                compareBy<String> { stageRank[it] ?: 0 }.thenBy { it }
            ).orEmpty()
        }

        val readyCount = normalized.count { it == "COMPLETED" || it == "PDF_EXPORTED" }
        if (readyCount == normalized.size) {
            return if (normalized.all { it == "PDF_EXPORTED" }) "PDF_EXPORTED" else "COMPLETED"
        }
        if (readyCount > 0) return "PARTIALLY_COMPLETED"

        return when {
            normalized.all { it == "REFUNDED" } -> "REFUNDED"
            normalized.all { it == "CANCELLED" } -> "CANCELLED"
            normalized.all { it == "REJECTED" } -> "REJECTED"
            normalized.all { it == "FAILED" } -> "FAILED"
            else -> "MIXED_OUTCOME"
        }
    }

    fun batchOutcome(statuses: List<String>): CustomerBatchOutcome {
        val normalized = statuses.map { it.trim().uppercase(Locale.ROOT) }.filter(String::isNotBlank)
        val ready = normalized.count { it == "COMPLETED" || it == "PDF_EXPORTED" }
        val excluded = normalized.count { it in setOf("REJECTED", "REFUNDED", "CANCELLED", "FAILED") }
        val active = (normalized.size - ready - excluded).coerceAtLeast(0)
        return CustomerBatchOutcome(
            status = batchSummaryStatus(normalized),
            fileCount = normalized.size,
            readyCount = ready,
            activeCount = active,
            excludedCount = excluded,
            progressPercent = if (normalized.isEmpty()) 0 else (ready * 100 / normalized.size)
        )
    }

    fun filePaymentSummary(
        status: String?, allocatedAmountMinor: Long, refundedAmountMinor: Long, currency: String
    ): String? {
        if (allocatedAmountMinor <= 0L && refundedAmountMinor <= 0L && status.isNullOrBlank()) return null
        val allocated = maxOf(0L, allocatedAmountMinor)
        val refunded = maxOf(0L, refundedAmountMinor)
        val net = maxOf(0L, allocated - refunded)
        val statusLabel = status?.takeIf { it.isNotBlank() }
            ?.let { " · " + activityStatusLabel(it) }.orEmpty()
        return "File allocation: ${formatMoney(allocated, currency)} · " +
            "Returned: ${formatMoney(refunded, currency)} · Net: ${formatMoney(net, currency)}$statusLabel"
    }

    fun batchPaymentSummary(status: String, amountMinor: Long, currency: String, fileCount: Int): String =
        "Batch total: ${formatMoney(maxOf(0L, amountMinor), currency)} · " +
            "${activityStatusLabel(status)} · $fileCount ${if (fileCount == 1) "file" else "files"}"

    private fun formatMoney(amountMinor: Long, currency: String): String =
        "%.2f %s".format(Locale.ROOT, amountMinor / 100.0, currency)
}
