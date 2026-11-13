package com.quantaedge.smartprint

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import java.time.Instant
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class CustomerApiException(val statusCode: Int, message: String) : IllegalStateException(message)

data class CustomerAuth(
    val accessToken: String,
    val refreshToken: String,
    val accessTokenExpiresUtc: String,
    val displayName: String,
    val email: String
)

data class CustomerPrintHistoryItem(
    val id: String,
    val batchId: String?,
    val batchName: String?,
    val batchGroupName: String?,
    val jobNumber: String,
    val status: String,
    val pageCount: Int,
    val copies: Int,
    val currency: String,
    val quotedAmountMinor: Long,
    val createdUtc: String,
    val shopName: String,
    val originalFileName: String,
    val paidAmountMinor: Long,
    val refundedAmountMinor: Long,
    val paymentStatus: String,
    val paymentMethod: String,
    val rejectedPageNumbersJson: String,
    val outputCompletedUtc: String?,
    val printKind: String?,
    val photoSizeCode: String?,
    val photoCount: Int,
    val photosPerPage: Int?
)

data class CustomerHistoryPage(
    val items: List<CustomerPrintHistoryItem>,
    val page: Int,
    val size: Int,
    val totalItems: Long,
    val totalPages: Int,
    val hasNext: Boolean
)

/**
 * Stores the customer session encrypted with an Android Keystore AES-GCM key.
 * Guest document files remain available independently of this account state.
 */
class CustomerAccountStore(context: Context) {
    private val preferences = context.getSharedPreferences("smartprint.customer.account", Context.MODE_PRIVATE)

    fun read(): CustomerAuth? {
        val value = preferences.getString("payload", null) ?: return null
        return try {
            val plain = decrypt(Base64.decode(value, Base64.NO_WRAP))
            val json = JSONObject(String(plain, StandardCharsets.UTF_8))
            CustomerAuth(
                accessToken = json.getString("accessToken"),
                refreshToken = json.getString("refreshToken"),
                accessTokenExpiresUtc = json.getString("accessTokenExpiresUtc"),
                displayName = json.optString("displayName"),
                email = json.optString("email")
            )
        } catch (_: Exception) {
            clear()
            null
        }
    }

    fun save(auth: CustomerAuth) {
        val json = JSONObject()
            .put("accessToken", auth.accessToken)
            .put("refreshToken", auth.refreshToken)
            .put("accessTokenExpiresUtc", auth.accessTokenExpiresUtc)
            .put("displayName", auth.displayName)
            .put("email", auth.email)
        val encrypted = encrypt(json.toString().toByteArray(StandardCharsets.UTF_8))
        preferences.edit().putString("payload", Base64.encodeToString(encrypted, Base64.NO_WRAP)).apply()
    }

    fun clear() {
        preferences.edit().remove("payload").apply()
    }

    private fun encrypt(plain: ByteArray): ByteArray {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        val encrypted = cipher.doFinal(plain)
        return cipher.iv + encrypted
    }

    private fun decrypt(payload: ByteArray): ByteArray {
        require(payload.size > 12) { "Stored account data is invalid." }
        val iv = payload.copyOfRange(0, 12)
        val encrypted = payload.copyOfRange(12, payload.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(), GCMParameterSpec(128, iv))
        return cipher.doFinal(encrypted)
    }

    private fun getOrCreateKey(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val existing = keyStore.getKey(KEY_ALIAS, null) as? SecretKey
        if (existing != null) return existing

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true)
                .build()
        )
        return generator.generateKey()
    }

    private companion object {
        const val KEY_ALIAS = "smartprint.customer.account.v1"
    }
}

/** Small HTTPS client for the separate customer identity API. */
object CustomerAccountClient {
    fun signIn(baseUrl: String, email: String, password: String): CustomerAuth {
        val response = request(
            baseUrl,
            "/api/v1/auth/customer-login",
            JSONObject().put("email", email).put("password", password)
        )
        val tokens = response.toAuth(email, "")
        val profile = profile(baseUrl, tokens.accessToken)
        return tokens.copy(displayName = profile.optString("displayName", email.substringBefore('@')))
    }

    fun register(baseUrl: String, displayName: String, email: String, password: String): CustomerAuth {
        val response = request(
            baseUrl,
            "/api/v1/auth/register-customer",
            JSONObject().put("displayName", displayName).put("email", email).put("password", password)
        )
        return response.toAuth(email, displayName)
    }

    fun accessTokenIsExpired(auth: CustomerAuth, leewaySeconds: Long = 90): Boolean {
        val expires = runCatching { Instant.parse(auth.accessTokenExpiresUtc) }.getOrNull()
            ?: return true
        return !expires.isAfter(Instant.now().plusSeconds(leewaySeconds))
    }

    fun refresh(baseUrl: String, auth: CustomerAuth): CustomerAuth {
        val response = request(
            baseUrl,
            "/api/v1/auth/refresh",
            JSONObject().put("refreshToken", auth.refreshToken)
        )
        return response.toAuth(auth.email, auth.displayName)
    }

    fun logout(baseUrl: String, auth: CustomerAuth) {
        request(
            baseUrl,
            "/api/v1/auth/logout",
            JSONObject().put("refreshToken", auth.refreshToken)
        )
    }

    fun profile(baseUrl: String, accessToken: String): JSONObject =
        request(baseUrl, "/api/v1/customer/me", null, accessToken)

    fun historyCount(baseUrl: String, accessToken: String): Long {
        val response = request(baseUrl, "/api/v1/customer/jobs/count", null, accessToken)
        val totalCount = response.getLong("totalCount")
        require(totalCount >= 0L) { "The server returned an invalid history count." }
        return totalCount
    }

    private fun parseHistoryItems(itemsJson: JSONArray): List<CustomerPrintHistoryItem> =
        (0 until itemsJson.length()).map { index ->
            val job = itemsJson.optJSONObject(index)
                ?: throw IllegalStateException("The server returned an invalid history record.")
            CustomerPrintHistoryItem(
                id = job.optString("id"),
                batchId = job.optString("batchId").takeUnless { it.isBlank() || it == "null" },
                batchName = job.optString("batchName").takeUnless { it.isBlank() || it == "null" },
                batchGroupName = job.optString("batchGroupName").takeUnless { it.isBlank() || it == "null" },
                jobNumber = job.optString("jobNumber"),
                status = job.optString("status"),
                pageCount = job.optInt("pageCount"),
                copies = job.optInt("copies"),
                currency = job.optString("currency", "INR"),
                quotedAmountMinor = job.optLong("quotedAmountMinor"),
                createdUtc = job.optString("createdUtc"),
                shopName = job.optString("shopName"),
                originalFileName = job.optString("originalFileName").takeUnless { it.isBlank() || it == "null" } ?: "Print document",
                paidAmountMinor = job.optLong("paidAmountMinor"),
                refundedAmountMinor = job.optLong("refundedAmountMinor"),
                paymentStatus = job.optString("paymentStatus").takeUnless { it.isBlank() || it == "null" }.orEmpty(),
                paymentMethod = job.optString("paymentMethod").takeUnless { it.isBlank() || it == "null" }.orEmpty(),
                rejectedPageNumbersJson = job.optString("rejectedPageNumbersJson", "[]"),
                outputCompletedUtc = job.optString("outputCompletedUtc").takeUnless { it.isBlank() || it == "null" },
                printKind = job.optString("printKind").takeUnless { it.isBlank() || it == "null" },
                photoSizeCode = job.optString("photoSizeCode").takeUnless { it.isBlank() || it == "null" },
                photoCount = job.optInt("photoCount"),
                photosPerPage = job.takeIf { it.has("photosPerPage") && !it.isNull("photosPerPage") }?.optInt("photosPerPage")
            )
        }

    internal fun parseLegacyHistorySnapshot(response: JSONObject): List<CustomerPrintHistoryItem> {
        val jobs = response.optJSONArray("jobs")
            ?: response.optJSONArray("Jobs")
            ?: throw IllegalStateException("The older SmartPrint service returned an unsupported history response.")
        return parseHistoryItems(jobs)
    }

    fun legacyHistorySnapshot(baseUrl: String, accessToken: String): List<CustomerPrintHistoryItem> =
        parseLegacyHistorySnapshot(request(baseUrl, "/api/v1/customer/jobs", null, accessToken))

    fun historyPage(
        baseUrl: String,
        accessToken: String,
        page: Int,
        size: Int = 10
    ): CustomerHistoryPage {
        require(page >= 0) { "History page must be zero or greater." }
        require(size in 1..100) { "History page size must be between 1 and 100." }
        val response = request(
            baseUrl,
            "/api/v1/customer/jobs?page=$page&size=$size",
            null,
            accessToken
        )
        return parseHistoryPage(response, page, size)
    }

    internal fun parseHistoryPage(
        response: JSONObject,
        requestedPage: Int,
        requestedSize: Int
    ): CustomerHistoryPage {
        require(requestedPage >= 0) { "History page must be zero or greater." }
        require(requestedSize in 1..100) { "History page size must be between 1 and 100." }
        val itemsJson = response.optJSONArray("items")
            ?: throw IllegalStateException("The server returned a history page without its items.")
        val items = parseHistoryItems(itemsJson)
        val responsePage = response.getInt("page")
        val responseSize = response.getInt("size")
        val totalItems = response.getLong("totalItems")
        val totalPages = response.getInt("totalPages")
        val hasNext = response.getBoolean("hasNext")
        require(responsePage == requestedPage && responseSize == requestedSize) {
            "The server returned a different history page than requested."
        }
        require(totalItems >= 0L && totalPages >= 0) {
            "The server returned an invalid history total."
        }
        val expectedTotalPages = if (totalItems == 0L) 0 else
            ((totalItems + responseSize - 1L) / responseSize).toInt()
        require(totalPages == expectedTotalPages && hasNext == (responsePage + 1 < totalPages)) {
            "The server returned inconsistent history pagination."
        }
        val offset = responsePage.toLong() * responseSize
        val expectedItemCount = if (offset >= totalItems) 0 else
            minOf(responseSize.toLong(), totalItems - offset).toInt()
        require(items.size == expectedItemCount) {
            "The server returned an incomplete history page."
        }
        return CustomerHistoryPage(
            items = items,
            page = responsePage,
            size = responseSize,
            totalItems = totalItems,
            totalPages = totalPages,
            hasNext = hasNext
        )
    }

    /** Reads the current state and audit timeline of a job owned by this signed-in customer. */
    fun jobStatus(baseUrl: String, accessToken: String, jobId: String): JSONObject {
        val safeId = jobId.trim()
        require(safeId.matches(Regex("^[0-9a-fA-F-]{36}$"))) { "The print job identifier is invalid." }
        return request(baseUrl, "/api/v1/customer/jobs/$safeId/status", null, accessToken)
    }

    private fun JSONObject.toAuth(email: String, displayName: String) = CustomerAuth(
        accessToken = getString("accessToken"),
        refreshToken = getString("refreshToken"),
        accessTokenExpiresUtc = getString("accessTokenExpiresUtc"),
        displayName = displayName,
        email = email
    )

    private fun request(
        baseUrl: String,
        path: String,
        body: JSONObject?,
        accessToken: String? = null
    ): JSONObject {
        val origin = baseUrl.trim().trimEnd('/')
        val parsed = URL(origin)
        require(parsed.protocol.equals("https", true) ||
            (BuildConfig.DEBUG && parsed.protocol.equals("http", true) &&
                parsed.host.matches(Regex("^(10\\.|192\\.168\\.|172\\.(1[6-9]|2[0-9]|3[01])\\.).*")))) {
            "SmartPrint account connections require HTTPS."
        }
        val connection = (URL(origin + path).openConnection() as HttpURLConnection).apply {
            requestMethod = if (body == null) "GET" else "POST"
            connectTimeout = 12_000
            readTimeout = 20_000
            instanceFollowRedirects = false
            setRequestProperty("Accept", "application/json")
            if (body != null) {
                doOutput = true
                setRequestProperty("Content-Type", "application/json; charset=utf-8")
            }
            if (!accessToken.isNullOrBlank()) setRequestProperty("Authorization", "Bearer $accessToken")
        }
        try {
            if (body != null) {
                connection.outputStream.use { output ->
                    output.write(body.toString().toByteArray(StandardCharsets.UTF_8))
                }
            }
            val status = connection.responseCode
            val stream = if (status in 200..299) connection.inputStream else connection.errorStream
            val payload = stream?.use { input ->
                BufferedReader(InputStreamReader(input, StandardCharsets.UTF_8)).readText()
            }.orEmpty()
            // Reverse proxies and older API hosts may return an empty or HTML 404.
            // Preserve the HTTP status so callers can select the documented legacy route.
            val json = if (status in 200..299) {
                if (payload.isBlank()) JSONObject() else JSONObject(payload)
            } else {
                runCatching { if (payload.isBlank()) JSONObject() else JSONObject(payload) }
                    .getOrElse { JSONObject() }
            }
            if (status !in 200..299) {
                val fieldErrors = json.optJSONObject("errors")
                val firstFieldError = fieldErrors?.keys()?.asSequence()
                    ?.mapNotNull { fieldErrors.optJSONArray(it)?.optString(0) }
                    ?.firstOrNull()
                val message = firstFieldError
                    ?: json.optString("message").takeIf { it.isNotBlank() }
                    ?: json.optString("detail").takeIf { it.isNotBlank() }
                    ?: json.optString("title").takeIf { it.isNotBlank() }
                    ?: when (status) {
                        401 -> "Email or password is incorrect."
                        409 -> "An account already exists for this email address."
                        else -> "SmartPrint account request failed (HTTP $status)."
                    }
                throw CustomerApiException(status, message)
            }
            return json
        } finally {
            connection.disconnect()
        }
    }
}
