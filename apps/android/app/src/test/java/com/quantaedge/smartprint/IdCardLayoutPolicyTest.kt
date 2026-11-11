package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class IdCardLayoutPolicyTest {
    @Test
    fun defaultCardFacesAreStackedVerticallyAndCentredOnA4Portrait() {
        val layout = IdCardLayoutPolicy.calculate()

        assertEquals(IdCardLayoutPolicy.PAGE_WIDTH_POINTS, layout.pageWidth, 0.01f)
        assertEquals(IdCardLayoutPolicy.PAGE_HEIGHT_POINTS, layout.pageHeight, 0.01f)
        assertEquals(layout.front.left, layout.back.left, 0.01f)
        assertEquals(layout.front.width, layout.back.width, 0.01f)
        assertEquals(layout.front.height, layout.back.height, 0.01f)
        assertTrue("The back face must be below the front face.",
            layout.back.top > layout.front.top + layout.front.height)
        assertEquals(
            IdCardLayoutPolicy.mmToPoints(IdCardLayoutPolicy.GAP_MM),
            layout.back.top - layout.front.top - layout.front.height,
            0.02f
        )
        assertEquals(
            (layout.pageWidth - layout.front.width) / 2f,
            layout.front.left,
            0.01f
        )
        assertTrue(layout.front.top >= IdCardLayoutPolicy.mmToPoints(IdCardLayoutPolicy.MIN_VERTICAL_MARGIN_MM))
        assertTrue(layout.back.top + layout.back.height <=
            layout.pageHeight - IdCardLayoutPolicy.mmToPoints(IdCardLayoutPolicy.MIN_VERTICAL_MARGIN_MM))
    }

    @Test
    fun defaultCardDimensionsRemainExactPhysicalMillimetres() {
        val layout = IdCardLayoutPolicy.calculate()

        assertEquals(
            IdCardLayoutPolicy.DEFAULT_CARD_WIDTH_MM,
            layout.front.width / 72f * 25.4f,
            0.01f
        )
        assertEquals(
            IdCardLayoutPolicy.DEFAULT_CARD_HEIGHT_MM,
            layout.front.height / 72f * 25.4f,
            0.01f
        )
    }

    @Test
    fun maximumSupportedCustomDimensionsStillFitOnA4Portrait() {
        val layout = IdCardLayoutPolicy.calculate(86f, 70f)

        assertTrue(layout.back.top + layout.back.height < layout.pageHeight)
        assertEquals(layout.front.left, layout.back.left, 0.01f)
    }
}
