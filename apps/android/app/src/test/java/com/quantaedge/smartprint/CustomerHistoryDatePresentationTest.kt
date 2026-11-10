package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class CustomerHistoryDatePresentationTest {
    @Test
    fun monthHeadingUsesFullHumanReadableMonthName() {
        assertEquals("November 2026", CustomerHistoryDatePresentation.monthHeading("2026-11-10T12:00:00Z"))
        assertEquals("October 2026", CustomerHistoryDatePresentation.monthHeading("2026-10-10T12:00:00Z"))
    }

    @Test
    fun orderDateUsesFullMonthName() {
        assertTrue(CustomerHistoryDatePresentation.dateTime("2026-11-10T12:00:00Z").contains("November"))
    }

    @Test
    fun invalidDatesHaveSafeFallbacks() {
        assertEquals("Date unavailable", CustomerHistoryDatePresentation.monthHeading("not-a-date"))
        assertEquals("Date unavailable", CustomerHistoryDatePresentation.dateTime("not-a-date"))
    }
}
