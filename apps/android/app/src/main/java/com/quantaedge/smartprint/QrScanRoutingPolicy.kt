package com.quantaedge.smartprint

/**
 * The scanner UI is shared, but its entry point tells the customer what they are
 * trying to do. Keeping the purpose explicit prevents a Wi-Fi check from ever
 * being interpreted as consent to change the connected shop.
 */
internal enum class QrScanPurpose(
    val wireValue: String,
    val eyebrow: String,
    val title: String,
    val instruction: String,
    val alignmentHint: String
) {
    SHOP_CONNECTION(
        wireValue = "shop_connection",
        eyebrow = "SHOP QR",
        title = "Scan Shop QR",
        instruction = "Scan the Shop QR to send your documents.",
        alignmentHint = "Fit the Shop QR inside the frame."
    ),
    WIFI_READINESS(
        wireValue = "wifi_readiness",
        eyebrow = "WI-FI CHECK",
        title = "Check Wi-Fi",
        instruction = "Scan the Wi-Fi QR shown on the shop computer.",
        alignmentHint = "Fit the Wi-Fi QR inside the frame."
    );

    companion object {
        fun fromWireValue(value: String?): QrScanPurpose =
            values().firstOrNull { it.wireValue == value } ?: SHOP_CONNECTION
    }
}

internal sealed class QrScanRoute {
    data class ConnectToShop(val link: StationQrLink) : QrScanRoute()
    data class VerifyShopWifi(val link: LocalPhoneTestQrLink) : QrScanRoute()
    data class Unsupported(val message: String) : QrScanRoute()
}

internal object QrScanRoutingPolicy {
    fun route(
        rawValue: String?,
        purpose: QrScanPurpose,
        expectedServiceUrl: String,
        allowLocalDevelopment: Boolean
    ): QrScanRoute = when (purpose) {
        QrScanPurpose.SHOP_CONNECTION -> {
            val link = runCatching {
                StationQrLink.parse(
                    rawValue,
                    expectedServiceUrl = expectedServiceUrl,
                    allowLocalDevelopment = allowLocalDevelopment
                )
            }.getOrNull()
            if (link != null) {
                QrScanRoute.ConnectToShop(link)
            } else {
                QrScanRoute.Unsupported(
                    "This isn't a Shop QR. Choose the QR provided by the shop to send your documents."
                )
            }
        }
        QrScanPurpose.WIFI_READINESS -> {
            val link = runCatching { LocalPhoneTestQrLink.parse(rawValue) }.getOrNull()
            if (link != null) {
                QrScanRoute.VerifyShopWifi(link)
            } else {
                QrScanRoute.Unsupported(
                    "This isn't a Wi-Fi QR. Choose the Wi-Fi QR shown on the shop computer. Your saved shop is unchanged."
                )
            }
        }
    }
}
