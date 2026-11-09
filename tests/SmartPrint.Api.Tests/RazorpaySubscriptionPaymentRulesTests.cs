using System.Security.Cryptography;
using System.Text;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class RazorpaySubscriptionPaymentRulesTests
{
    private const string Secret = "test-webhook-secret";

    [Fact]
    public void CheckoutSignatureAcceptsTheExactOrderAndPaymentPair()
    {
        const string orderId = "order_123";
        const string paymentId = "pay_456";
        var signature = HmacHex($"{orderId}|{paymentId}", "api-secret");

        Assert.True(RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            orderId, paymentId, signature, "api-secret"));
    }

    [Theory]
    [InlineData("", "pay_456", "signature", "secret")]
    [InlineData("order_123", "", "signature", "secret")]
    [InlineData("order_123", "pay_456", "", "secret")]
    [InlineData("order_123", "pay_456", "signature", "")]
    public void CheckoutSignatureRejectsMissingInputs(string orderId, string paymentId, string signature, string secret)
    {
        Assert.False(RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            orderId, paymentId, signature, secret));
    }

    [Fact]
    public void CheckoutSignatureRejectsAValidSignatureForAnotherOrder()
    {
        var signature = HmacHex("order_other|pay_456", "api-secret");

        Assert.False(RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            "order_123", "pay_456", signature, "api-secret"));
    }

    [Fact]
    public void CheckoutSignatureRejectsMalformedHexWithoutThrowing()
    {
        Assert.False(RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            "order_123", "pay_456", "not-hex", "api-secret"));
    }

    [Fact]
    public void WebhookSignatureUsesTheOriginalRawRequestBytes()
    {
        var rawBody = Encoding.UTF8.GetBytes("{\"event\":\"payment.captured\", \"id\":1}");
        var signature = HmacHex(rawBody, Secret);

        Assert.True(RazorpaySubscriptionPaymentRules.VerifyWebhookSignature(rawBody, signature, Secret));
        Assert.False(RazorpaySubscriptionPaymentRules.VerifyWebhookSignature(
            Encoding.UTF8.GetBytes("{\"event\":\"payment.captured\",\"id\":1}"),
            signature,
            Secret));
    }

    [Fact]
    public void WebhookSignatureRejectsWrongSecretAndMalformedSignature()
    {
        var rawBody = Encoding.UTF8.GetBytes("{\"event\":\"payment.captured\"}");
        var signature = HmacHex(rawBody, Secret);

        Assert.False(RazorpaySubscriptionPaymentRules.VerifyWebhookSignature(rawBody, signature, "wrong-secret"));
        Assert.False(RazorpaySubscriptionPaymentRules.VerifyWebhookSignature(rawBody, "xyz", Secret));
    }

    [Fact]
    public void CapturedWebhookParsesOrderPaymentAmountAndCurrency()
    {
        const string json = """
            {
              "event": "payment.captured",
              "payload": {
                "payment": {
                  "entity": {
                    "id": "pay_123",
                    "order_id": "order_123",
                    "amount": 49900,
                    "currency": "INR",
                    "status": "captured"
                  }
                }
              }
            }
            """;

        var parsed = RazorpaySubscriptionPaymentRules.TryParseWebhook(
            Encoding.UTF8.GetBytes(json), out var payment, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(payment);
        Assert.Equal("payment.captured", payment.EventType);
        Assert.Equal("pay_123", payment.PaymentId);
        Assert.Equal("order_123", payment.OrderId);
        Assert.Equal(49900, payment.AmountMinor);
        Assert.Equal("INR", payment.Currency);
        Assert.Equal("captured", payment.Status);
    }

    [Fact]
    public void OrderPaidEventUsesItsPaymentEntityForReconciliation()
    {
        const string json = """
            {
              "event": "order.paid",
              "payload": {
                "payment": {
                  "entity": {
                    "id": "pay_456",
                    "order_id": "order_456",
                    "amount": 125000,
                    "currency": "INR",
                    "status": "captured"
                  }
                }
              }
            }
            """;

        Assert.True(RazorpaySubscriptionPaymentRules.TryParseWebhook(
            Encoding.UTF8.GetBytes(json), out var payment, out var error), error);
        Assert.NotNull(payment);
        Assert.Equal("order.paid", payment.EventType);
        Assert.Equal("pay_456", payment.PaymentId);
        Assert.Equal("order_456", payment.OrderId);
        Assert.Equal(125000, payment.AmountMinor);
    }

    [Fact]
    public void FailedPaymentEventParsesWithoutTreatingItAsCaptured()
    {
        const string json = """
            {
              "event": "payment.failed",
              "payload": {
                "payment": {
                  "entity": {
                    "id": "pay_failed",
                    "order_id": "order_123",
                    "amount": 49900,
                    "currency": "INR",
                    "status": "failed"
                  }
                }
              }
            }
            """;

        Assert.True(RazorpaySubscriptionPaymentRules.TryParseWebhook(
            Encoding.UTF8.GetBytes(json), out var payment, out var error), error);
        Assert.NotNull(payment);
        Assert.Equal("payment.failed", payment.EventType);
        Assert.Equal("failed", payment.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"event\":\"payment.captured\"}")]
    [InlineData("{\"event\":\"payment.captured\",\"payload\":{\"payment\":{\"entity\":{\"id\":\"pay_only\"}}}}")]
    public void WebhookParserRejectsMalformedOrIncompletePayloads(string json)
    {
        Assert.False(RazorpaySubscriptionPaymentRules.TryParseWebhook(
            Encoding.UTF8.GetBytes(json), out var payment, out var error));
        Assert.Null(payment);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void WebhookParserRejectsAnOversizedPayload()
    {
        var body = new byte[RazorpaySubscriptionPaymentRules.MaximumWebhookBodyBytes + 1];

        Assert.False(RazorpaySubscriptionPaymentRules.TryParseWebhook(body, out var payment, out var error));
        Assert.Null(payment);
        Assert.Contains("size", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapturedPaymentMustMatchOrderAmountCurrencyAndCapturedStatus()
    {
        Assert.True(RazorpaySubscriptionPaymentRules.IsCapturedPaymentMatch(
            "order_123", 49900, "INR", "pay_123", "order_123", 49900, "INR", "captured"));
    }

    [Theory]
    [InlineData("order_other", 49900L, "INR", "captured")]
    [InlineData("order_123", 49899L, "INR", "captured")]
    [InlineData("order_123", 49900L, "USD", "captured")]
    [InlineData("order_123", 49900L, "INR", "authorized")]
    [InlineData("order_123", 49900L, "INR", "failed")]
    public void CapturedPaymentRejectsMismatchedProviderDetails(
        string actualOrderId, long actualAmount, string actualCurrency, string actualStatus)
    {
        Assert.False(RazorpaySubscriptionPaymentRules.IsCapturedPaymentMatch(
            "order_123", 49900, "INR", "pay_123",
            actualOrderId, actualAmount, actualCurrency, actualStatus));
    }

    [Fact]
    public void FailureWebhookCannotDowngradeAnAlreadyCapturedPayment()
    {
        Assert.False(RazorpaySubscriptionPaymentRules.ShouldMarkPaymentFailed("CAPTURED"));
        Assert.False(RazorpaySubscriptionPaymentRules.ShouldMarkPaymentFailed("CAPTURED_REVIEW_REQUIRED"));
        Assert.True(RazorpaySubscriptionPaymentRules.ShouldMarkPaymentFailed("CREATED"));
        Assert.True(RazorpaySubscriptionPaymentRules.ShouldMarkPaymentFailed("FAILED"));
    }

    [Theory]
    [InlineData("payment.captured", true)]
    [InlineData("order.paid", true)]
    [InlineData("payment.failed", true)]
    [InlineData("payment.authorized", false)]
    [InlineData("payout.processed", false)]
    public void OnlyFinalSubscriptionPaymentEventsAreHandled(string eventType, bool supported)
    {
        Assert.Equal(supported, RazorpaySubscriptionPaymentRules.IsSupportedEvent(eventType));
    }

    private static string HmacHex(string value, string key) =>
        HmacHex(Encoding.UTF8.GetBytes(value), key);

    private static string HmacHex(byte[] value, string key) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), value)).ToLowerInvariant();
}
