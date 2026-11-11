package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class QrScanRoutingPolicyTest {
    private val serviceUrl = "https://service.smartprint.example"
    private val stationUrl = "$serviceUrl/s/${"A".repeat(32)}"
    private val readinessUrl =
        "http://192.168.1.25:17842/api/v1/local/connection-check?token=${"B".repeat(43)}"

    @Test
    fun scannerCopyClearlySeparatesShopQrFromWifiCheck() {
        assertEquals("Scan Shop QR", QrScanPurpose.SHOP_CONNECTION.title)
        assertEquals("Scan the Shop QR to send documents to this shop.", QrScanPurpose.SHOP_CONNECTION.instruction)
        assertEquals("Check Wi-Fi", QrScanPurpose.WIFI_READINESS.title)
        assertEquals(
            "Scan the Wi-Fi QR shown on the shop computer.",
            QrScanPurpose.WIFI_READINESS.instruction
        )
    }

    @Test
    fun shopConnectionEntryAcceptsOnlyTheConfiguredStationQr() {
        val route = QrScanRoutingPolicy.route(
            rawValue = stationUrl,
            purpose = QrScanPurpose.SHOP_CONNECTION,
            expectedServiceUrl = serviceUrl,
            allowLocalDevelopment = false
        )
        assertTrue(route is QrScanRoute.ConnectToShop)
    }

    @Test
    fun wifiCheckEntryAcceptsOnlyThePrivateAgentReadinessQr() {
        val route = QrScanRoutingPolicy.route(
            rawValue = readinessUrl,
            purpose = QrScanPurpose.WIFI_READINESS,
            expectedServiceUrl = serviceUrl,
            allowLocalDevelopment = false
        )
        assertTrue(route is QrScanRoute.VerifyShopWifi)
    }

    @Test
    fun readinessQrCannotBeMistakenForShopConnectionOrSwitch() {
        val route = QrScanRoutingPolicy.route(
            rawValue = readinessUrl,
            purpose = QrScanPurpose.SHOP_CONNECTION,
            expectedServiceUrl = serviceUrl,
            allowLocalDevelopment = false
        )
        assertTrue(route is QrScanRoute.Unsupported)
    }

    @Test
    fun stationQrCannotChangeShopFromWifiCheckEntry() {
        val route = QrScanRoutingPolicy.route(
            rawValue = stationUrl,
            purpose = QrScanPurpose.WIFI_READINESS,
            expectedServiceUrl = serviceUrl,
            allowLocalDevelopment = false
        )
        assertTrue(route is QrScanRoute.Unsupported)
        assertEquals(
            "This isn't a Wi-Fi QR. Choose the Wi-Fi QR shown on the shop computer. Your saved shop is unchanged.",
            (route as QrScanRoute.Unsupported).message
        )
    }

    @Test
    fun unknownScannerPurposeSafelyDefaultsToShopConnection() {
        assertEquals(QrScanPurpose.SHOP_CONNECTION, QrScanPurpose.fromWireValue(null))
        assertEquals(QrScanPurpose.SHOP_CONNECTION, QrScanPurpose.fromWireValue("unknown"))
        assertEquals(QrScanPurpose.WIFI_READINESS, QrScanPurpose.fromWireValue("wifi_readiness"))
    }
}
