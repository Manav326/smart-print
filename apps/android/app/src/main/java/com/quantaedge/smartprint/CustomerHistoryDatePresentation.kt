package com.quantaedge.smartprint

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/** Readable customer-facing dates for the print Activity screen. */
internal object CustomerHistoryDatePresentation {
    private val monthFormatter = DateTimeFormatter.ofPattern("MMMM yyyy", Locale.ENGLISH)
    private val dateTimeFormatter = DateTimeFormatter.ofPattern("dd MMMM yyyy · hh:mm a", Locale.ENGLISH)
    private val timelineFormatter = DateTimeFormatter.ofPattern("dd MMMM · hh:mm a", Locale.ENGLISH)

    private fun parsed(value: String) = runCatching {
        Instant.parse(value).atZone(ZoneId.systemDefault())
    }.getOrNull()

    fun monthHeading(createdUtc: String): String =
        parsed(createdUtc)?.format(monthFormatter) ?: "Date unavailable"

    fun dateTime(createdUtc: String): String =
        parsed(createdUtc)?.format(dateTimeFormatter) ?: "Date unavailable"

    fun timelineDateTime(createdUtc: String): String =
        parsed(createdUtc)?.format(timelineFormatter) ?: ""
}
