package com.quantaedge.smartprint

/**
 * Physical layout contract for one front/back ID sheet.
 * Coordinates are PDF points (1/72 inch), not screen pixels.
 *
 * Both card faces are centred horizontally and stacked vertically on one A4
 * portrait page. The same policy drives the live preview and final PDF.
 */
object IdCardLayoutPolicy {
    const val PAGE_WIDTH_POINTS = 595f
    const val PAGE_HEIGHT_POINTS = 842f
    const val DEFAULT_CARD_WIDTH_MM = 85.6f
    const val DEFAULT_CARD_HEIGHT_MM = 53.98f
    const val GAP_MM = 12f
    const val SIDE_MARGIN_MM = 14f
    const val MIN_VERTICAL_MARGIN_MM = 20f

    data class CardRect(val left: Float, val top: Float, val width: Float, val height: Float)
    data class Layout(
        val pageWidth: Float,
        val pageHeight: Float,
        val front: CardRect,
        val back: CardRect,
        val cropOffset: Float,
        val cropLength: Float,
        val frontLabelTop: Float,
        val backLabelTop: Float
    )

    fun mmToPoints(value: Float): Float = value * 72f / 25.4f

    fun calculate(
        cardWidthMm: Float = DEFAULT_CARD_WIDTH_MM,
        cardHeightMm: Float = DEFAULT_CARD_HEIGHT_MM
    ): Layout {
        require(cardWidthMm.isFinite() && cardHeightMm.isFinite())
        require(cardWidthMm in 55f..86f && cardHeightMm in 30f..70f) {
            "Card size must fit vertically on one A4 portrait sheet."
        }
        require(cardWidthMm > cardHeightMm) {
            "Enter the card dimensions in landscape orientation."
        }

        val cardWidth = mmToPoints(cardWidthMm)
        val cardHeight = mmToPoints(cardHeightMm)
        val gap = mmToPoints(GAP_MM)
        val sideMargin = mmToPoints(SIDE_MARGIN_MM)
        val verticalMargin = mmToPoints(MIN_VERTICAL_MARGIN_MM)
        val totalHeight = cardHeight * 2f + gap

        require(cardWidth + sideMargin * 2f <= PAGE_WIDTH_POINTS) {
            "This card is too wide for the A4 portrait sheet. Use a smaller custom size."
        }
        require(totalHeight + verticalMargin * 2f <= PAGE_HEIGHT_POINTS) {
            "Both card faces do not fit vertically on one A4 portrait sheet."
        }

        val left = (PAGE_WIDTH_POINTS - cardWidth) / 2f
        val top = (PAGE_HEIGHT_POINTS - totalHeight) / 2f
        val front = CardRect(left, top, cardWidth, cardHeight)
        val back = CardRect(left, top + cardHeight + gap, cardWidth, cardHeight)
        val cropOffset = mmToPoints(2f)
        val cropLength = mmToPoints(3.5f)

        return Layout(
            PAGE_WIDTH_POINTS,
            PAGE_HEIGHT_POINTS,
            front,
            back,
            cropOffset,
            cropLength,
            front.top + cardHeight + mmToPoints(6.5f),
            back.top + cardHeight + mmToPoints(6.5f)
        )
    }
}
