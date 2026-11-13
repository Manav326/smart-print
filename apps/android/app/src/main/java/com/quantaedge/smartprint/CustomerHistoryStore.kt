package com.quantaedge.smartprint

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import java.security.MessageDigest
import java.util.Locale
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Encrypted, account-scoped customer print-history snapshot.
 * It is read locally when the screen opens and is written only after a
 * complete, user-requested server sync.
 */
internal class CustomerHistoryStore(context: Context) {
    private val preferences = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)

    fun read(email: String): List<CustomerPrintHistoryItem>? {
        val key = preferenceKey(email)
        val encoded = preferences.getString(key, null) ?: return null
        return try {
            val payload = Base64.decode(encoded, Base64.NO_WRAP)
            val plain = try {
                decrypt(payload)
            } finally {
                payload.fill(0)
            }
            try {
                val array = JSONArray(String(plain, StandardCharsets.UTF_8))
                (0 until array.length()).mapNotNull { index ->
                    array.optJSONObject(index)?.toHistoryItem()
                }
            } finally {
                plain.fill(0)
            }
        } catch (_: Exception) {
            preferences.edit().remove(key).commit()
            null
        }
    }

    fun replace(email: String, history: List<CustomerPrintHistoryItem>) {
        val array = JSONArray()
        history.distinctBy { it.id }.forEach { item -> array.put(item.toJson()) }
        val plain = array.toString().toByteArray(StandardCharsets.UTF_8)
        val encrypted = try {
            encrypt(plain)
        } finally {
            plain.fill(0)
        }
        val encoded = try {
            Base64.encodeToString(encrypted, Base64.NO_WRAP)
        } finally {
            encrypted.fill(0)
        }
        check(preferences.edit().putString(preferenceKey(email), encoded).commit()) {
            "Your print history could not be saved securely."
        }
    }

    fun clear(email: String) {
        preferences.edit().remove(preferenceKey(email)).apply()
    }

    private fun preferenceKey(email: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest(email.trim().lowercase(Locale.ROOT).toByteArray(StandardCharsets.UTF_8))
        return "history_" + digest.joinToString("") { "%02x".format(Locale.ROOT, it) }
    }

    private fun CustomerPrintHistoryItem.toJson() = JSONObject()
        .put("id", id)
        .put("batchId", batchId ?: JSONObject.NULL)
        .put("batchName", batchName ?: JSONObject.NULL)
        .put("batchGroupName", batchGroupName ?: JSONObject.NULL)
        .put("jobNumber", jobNumber)
        .put("status", status)
        .put("pageCount", pageCount)
        .put("copies", copies)
        .put("currency", currency)
        .put("quotedAmountMinor", quotedAmountMinor)
        .put("createdUtc", createdUtc)
        .put("shopName", shopName)
        .put("originalFileName", originalFileName)
        .put("paidAmountMinor", paidAmountMinor)
        .put("refundedAmountMinor", refundedAmountMinor)
        .put("paymentStatus", paymentStatus)
        .put("paymentMethod", paymentMethod)
        .put("rejectedPageNumbersJson", rejectedPageNumbersJson)
        .put("outputCompletedUtc", outputCompletedUtc ?: JSONObject.NULL)
        .put("printKind", printKind ?: JSONObject.NULL)
        .put("photoSizeCode", photoSizeCode ?: JSONObject.NULL)
        .put("photoCount", photoCount)
        .put("photosPerPage", photosPerPage ?: JSONObject.NULL)
        .put("privatePrinting", privatePrinting)
        .put("updatedUtc", updatedUtc)

    private fun JSONObject.toHistoryItem(): CustomerPrintHistoryItem {
        fun optional(name: String): String? =
            optString(name).takeUnless { it.isBlank() || it == "null" }
        return CustomerPrintHistoryItem(
            id = getString("id"),
            batchId = optional("batchId"),
            batchName = optional("batchName"),
            batchGroupName = optional("batchGroupName"),
            jobNumber = optString("jobNumber"),
            status = optString("status"),
            pageCount = optInt("pageCount"),
            copies = optInt("copies"),
            currency = optString("currency", "INR"),
            quotedAmountMinor = optLong("quotedAmountMinor"),
            createdUtc = optString("createdUtc"),
            shopName = optString("shopName"),
            originalFileName = optString("originalFileName", "Print document"),
            paidAmountMinor = optLong("paidAmountMinor"),
            refundedAmountMinor = optLong("refundedAmountMinor"),
            paymentStatus = optString("paymentStatus"),
            paymentMethod = optString("paymentMethod"),
            rejectedPageNumbersJson = optString("rejectedPageNumbersJson", "[]"),
            outputCompletedUtc = optional("outputCompletedUtc"),
            printKind = optional("printKind"),
            photoSizeCode = optional("photoSizeCode"),
            photoCount = optInt("photoCount"),
            photosPerPage = if (!has("photosPerPage") || isNull("photosPerPage")) null else optInt("photosPerPage"),
            privatePrinting = optBoolean("privatePrinting", false),
            updatedUtc = optional("updatedUtc") ?: optString("createdUtc")
        )
    }

    private fun encrypt(plain: ByteArray): ByteArray {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        return cipher.iv + cipher.doFinal(plain)
    }

    private fun decrypt(payload: ByteArray): ByteArray {
        require(payload.size > IV_BYTES) { "Stored history is invalid." }
        val iv = payload.copyOfRange(0, IV_BYTES)
        val encrypted = payload.copyOfRange(IV_BYTES, payload.size)
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
        const val PREFERENCES = "smartprint.customer.history"
        const val KEY_ALIAS = "smartprint.customer.history.v1"
        const val IV_BYTES = 12
    }
}
