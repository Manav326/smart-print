using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartPrint.Api.Domain;

public sealed record RazorpayWebhookPayment(
    string EventType,
    string PaymentId,
    string OrderId,
    long? AmountMinor,
    string? Currency,
    string? Status);

public static class RazorpaySubscriptionPaymentRules
{
    public const int MaximumWebhookBodyBytes = 256 * 1024;

    public static bool VerifyCheckoutSignature(string orderId, string paymentId, string? signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(orderId) ||
            string.IsNullOrWhiteSpace(paymentId) ||
            string.IsNullOrWhiteSpace(signature) ||
            string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var message = Encoding.UTF8.GetBytes(orderId + "|" + paymentId);
        var key = Encoding.UTF8.GetBytes(secret);
        return VerifyHmacSha256(message, signature, key);
    }

    public static bool VerifyWebhookSignature(ReadOnlySpan<byte> rawBody, string? signature, string secret)
    {
        if (rawBody.IsEmpty || string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(secret))
            return false;

        return VerifyHmacSha256(rawBody, signature, Encoding.UTF8.GetBytes(secret));
    }

    public static bool IsCapturedPaymentMatch(
        string expectedOrderId,
        long expectedAmountMinor,
        string expectedCurrency,
        string? paymentId,
        string? actualOrderId,
        long? actualAmountMinor,
        string? actualCurrency,
        string? actualStatus)
    {
        return !string.IsNullOrWhiteSpace(paymentId) &&
               string.Equals(expectedOrderId, actualOrderId, StringComparison.Ordinal) &&
               actualAmountMinor == expectedAmountMinor &&
               string.Equals(expectedCurrency, actualCurrency, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(actualStatus, "captured", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldMarkPaymentFailed(string currentStatus) =>
        !string.Equals(currentStatus, "CAPTURED", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(currentStatus, "CAPTURED_REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseWebhook(
        ReadOnlyMemory<byte> rawBody,
        out RazorpayWebhookPayment? payment,
        out string error)
    {
        payment = null;
        error = string.Empty;
        if (rawBody.IsEmpty || rawBody.Length > MaximumWebhookBodyBytes)
        {
            error = "Webhook body is empty or exceeds the allowed size.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            var eventType = ReadString(root, "event");
            var paymentEntity = root
                .GetProperty("payload")
                .GetProperty("payment")
                .GetProperty("entity");

            var paymentId = ReadString(paymentEntity, "id");
            var orderId = ReadString(paymentEntity, "order_id");
            if (string.IsNullOrWhiteSpace(eventType) ||
                string.IsNullOrWhiteSpace(paymentId) ||
                string.IsNullOrWhiteSpace(orderId))
            {
                error = "Webhook does not contain an event, payment ID and order ID.";
                return false;
            }

            long? amount = paymentEntity.TryGetProperty("amount", out var amountElement) &&
                           amountElement.TryGetInt64(out var parsedAmount)
                ? parsedAmount
                : null;

            payment = new RazorpayWebhookPayment(
                eventType,
                paymentId,
                orderId,
                amount,
                ReadString(paymentEntity, "currency"),
                ReadString(paymentEntity, "status"));
            return true;
        }
        catch (JsonException)
        {
            error = "Webhook body is not valid JSON.";
            return false;
        }
        catch (KeyNotFoundException)
        {
            error = "Webhook payment entity is missing.";
            return false;
        }
        catch (InvalidOperationException)
        {
            error = "Webhook payment entity has an invalid shape.";
            return false;
        }
    }

    public static bool IsSupportedEvent(string eventType) =>
        eventType is "payment.captured" or "order.paid" or "payment.failed";

    public static string Sha256Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool VerifyHmacSha256(ReadOnlySpan<byte> message, string receivedSignature, ReadOnlySpan<byte> key)
    {
        try
        {
            var expected = HMACSHA256.HashData(key, message);
            var received = Convert.FromHexString(receivedSignature.Trim());
            return received.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(expected, received);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
