package com.quantaedge.smartprint

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import org.junit.runner.RunWith
import org.json.JSONObject

@RunWith(AndroidJUnit4::class)
class CustomerGoogleAuthTest {
    @Test
    fun acceptsOnlyAValidServerIssuedNonce() {
        val expected = "a".repeat(64)
        assertEquals(expected, CustomerAccountClient.parseGoogleAuthChallenge(JSONObject().put("nonce", expected)))
    }

    @Test
    fun rejectsMissingOrMalformedServerNonce() {
        assertThrows(IllegalArgumentException::class.java) {
            CustomerAccountClient.parseGoogleAuthChallenge(JSONObject())
        }
        assertThrows(IllegalArgumentException::class.java) {
            CustomerAccountClient.parseGoogleAuthChallenge(JSONObject().put("nonce", "short"))
        }
    }
}
