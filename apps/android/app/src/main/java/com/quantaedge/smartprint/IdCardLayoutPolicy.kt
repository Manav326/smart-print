package com.quantaedge.smartprint

/**
 * Physical layout contract for one front/back ID sheet.
 * Coordinates are PDF points (1/72 inch), not screen pixels.
 */
object IdCardLayoutPolicy {
    const val PAGE_WIDTH_POINTS = 595f
    const val PAGE_HEIGHT_POINTS = 842f
    const val DEFAULT_CARD_WIDTH_MM = 85.6f
    const val DEFAULT_CARD_HEIGHT_MM = 53.98f
    const val GAP_MM = 8f
    const val SIDE_MARGIN_MM = 14f

    data class CardRect(val left: Float, val top: Float, val width: Float, val height: Float)
    data class Layout(
        val pageWidth: Float,
        val pageHeight: Float,
        val front: CardRect,
        val back: CardRect,
        val cropOffset: Float,
        val cropLength: Float,
        val labelTop: Float
    )

    fun mmToPoints(value: Float): Float = value * 72f / 25.4f

    fun calculate(
        cardWidthMm: Float = DEFAULT_CARD_WIDTH_MM,
        cardHeightMm: Float = DEFAULT_CARD_HEIGHT_MM
    ): Layout {
        require(cardWidthMm.isFinite() && cardHeightMm.isFinite())
        require(cardWidthMm in 55f..86f && cardHeightMm in 30f..70f) {
            "Card size must fit two panels side by side on one A4 sheet."
        }
        require(cardWidthMm > cardHeightMm) {
            "Enter the card dimensions in landscape orientation."
        }
        val cardWidth = mmToPoints(cardWidthMm)
        val cardHeight = mmToPoints(cardHeightMm)
        val margin = mmToPoints(SIDE_MARGIN_MM)
        val gap = mmToPoints(GAP_MM)
        val totalWidth = cardWidth * 2f + gap + margin * 2f
        require(totalWidth <= PAGE_WIDTH_POINTS) {
            "This card is too wide for the two-side A4 layout. Use a smaller custom size."
        }
        val top = (PAGE_HEIGHT_POINTS - cardHeight) / 2f
        val front = CardRect(margin, top, cardWidth, cardHeight)
        val back = CardRect(margin + cardWidth + gap, top, cardWidth, cardHeight)
        val cropOffset = mmToPoints(2f)
        val cropLength = mmToPoints(3.5f)
        return Layout(
            PAGE_WIDTH_POINTS, PAGE_HEIGHT_POINTS,
            front, back, cropOffset, cropLength,
            top + cardHeight + mmToPoints(6.5f)
        )
    }
}