using System.Security.Cryptography;
using System.Text;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class RazorpaySubscriptionPaymentRulesTests
{
    private static SubscriptionPlan Plan(bool active = true, bool system = false, string code = "PRO") => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        Name = "Professional",
        IsActive = active,
        IsSystemPlan = system
    };

    private static SubscriptionPlanVersion Version(
        Guid planId,
        string status = "Published",
        string currency = "INR",
        long? monthly = 49900,
        long? annual = 499900) => new()
    {
        Id = Guid.NewGuid(),
        PlanId = planId,
        Status = status,
        Currency = currency,
        MonthlyPriceMinor = monthly,
        AnnualPriceMinor = annual
    };

    [Fact]
    public void ResolvesPublishedMonthlyPriceFromServerPlanVersion()
    {
        var plan = Plan();
        var version = Version(plan.Id);

        var allowed = RazorpaySubscriptionPaymentRules.TryResolvePrice(
            plan, version, "monthly", out var interval, out var amount, out var error);

        Assert.True(allowed);
        Assert.Empty(error);
        Assert.Equal("Monthly", interval);
        Assert.Equal(49900, amount);
    }

    [Fact]
    public void ResolvesPublishedAnnualPriceFromServerPlanVersion()
    {
        var plan = Plan();
        var version = Version(plan.Id);

        var allowed = RazorpaySubscriptionPaymentRules.TryResolvePrice(
            plan, version, "ANNUAL", out var interval, out var amount, out var error);

        Assert.True(allowed);
        Assert.Empty(error);
        Assert.Equal("Annual", interval);
        Assert.Equal(499900, amount);
    }

    [Theory]
    [InlineData(false, false, "PRO", "Published", "plan_version_not_available")]
    [InlineData(true, true, "PRO", "Published", "plan_version_not_available")]
    [InlineData(true, false, "TRIAL", "Published", "plan_version_not_available")]
    [InlineData(true, false, "PRO", "Draft", "plan_version_not_available")]
    [InlineData(true, false, "PRO", "Retired", "plan_version_not_available")]
    public void RejectsInactiveSystemTrialOrUnpublishedPlanVersions(
        bool active,
        bool system,
        string code,
        string versionStatus,
        string expectedCode)
    {
        var plan = Plan(active, system, code);
        var version = Version(plan.Id, status: versionStatus);

        Assert.False(RazorpaySubscriptionPaymentRules.TryResolvePrice(
            plan, version, "Monthly", out _, out _, out var error));
        Assert.Equal(expectedCode, error);
    }

    [Fact]
    public void RejectsVersionBelongingToAnotherPlan()
    {
        var plan = Plan();
        var version = Version(Guid.NewGuid());

        Assert.False(RazorpaySubscriptionPaymentRules.TryResolvePrice(
            plan, version, "Monthly", out _, out _, out var error));
        Assert.Equal("plan_version_not_available", error);
    }

    [Fact]
    public void RejectsUnsupportedCurrency()
    {
        var plan = Plan();
        var version = Version(plan.Id, currency: "USD");

        Assert.False(RazorpaySubscriptionPaymentRules.TryResolvePrice(
            plan, version, "Monthly", out _, out _, out var error));
        Assert.Equal("unsupported_currency", error);
    }

    [Theory]
    [InlineData(null, "billing_interval_not_available")]
    [InlineData(0, "free_plan_does_not_require_checkout")]
    [InlineData(-1, "billing_interval_not_available")]
    public void RejectsMissingFreeOrInvalidMonthlyAmount(long? price, string expectedCode)
    {
        var plan = Plan();
        var version = Version(plan.Id, monthly: price);

        Assert.False(RazorpaySubscriptionPaymentRules.TryResolvePrice(
            plan, version, "Monthly", out _, out _, out var error));
        Assert.Equal(expectedCode, error);
    }

    [Fact]
    public void RejectsBillingIntervalNotConfigured()
    {
        var plan = Plan();
        var version = Version(plan.Id, annual: null);

        Assert.False(RazorpaySubscriptionPaymentRules.TryResolvePrice(
            plan, version, "Annual", out _, out _, out var error));
        Assert.Equal("billing_interval_not_available", error);
    }

    [Fact]
    public void RejectsPriceAboveMpayRazorpayCeiling()
    {
        var plan = Plan();
        var version = Version(plan.Id, monthly: RazorpaySubscriptionPaymentRules.MaximumOrderAmountMinor + 1);

        Assert.False(RazorpaySubscriptionPaymentRules.TryResolvePrice(
            plan, version, "Monthly", out _, out _, out var error));
        Assert.Equal("amount_exceeds_gateway_limit", error);
    }

    [Fact]
    public void CheckoutSignatureMatchesMpayOrderPaymentHmacFormat()
    {
        const string secret = "server-only-test-secret";
        const string orderId = "order_12345";
        const string paymentId = "pay_67890";
        var signature = Hmac($"{orderId}|{paymentId}", secret);

        Assert.True(RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            orderId, paymentId, signature, secret));
    }

    [Theory]
    [InlineData("order_other", "pay_67890")]
    [InlineData("order_12345", "pay_other")]
    public void CheckoutSignatureCannotBeReusedForDifferentOrderOrPayment(string orderId, string paymentId)
    {
        const string secret = "server-only-test-secret";
        var signature = Hmac("order_12345|pay_67890", secret);

        Assert.False(RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            orderId, paymentId, signature, secret));
    }

    [Fact]
    public void CheckoutSignatureRejectsWrongSecretAndMalformedHex()
    {
        var signature = Hmac("order_12345|pay_67890", "correct-secret");

        Assert.False(RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            "order_12345", "pay_67890", signature, "wrong-secret"));
        Assert.False(RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            "order_12345", "pay_67890", "not-a-signature", "correct-secret"));
        Assert.False(RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            "order_12345", "pay_67890", signature[..63], "correct-secret"));
    }

    [Fact]
    public void WebhookSignatureUsesExactRawRequestBody()
    {
        const string body = "{\"event\":\"payment.captured\",\"payload\":{\"payment\":{}}}";
        const string secret = "webhook-only-secret";
        var signature = Hmac(body, secret);

        Assert.True(RazorpaySubscriptionPaymentRules.VerifyWebhookSignature(body, signature, secret));
        Assert.False(RazorpaySubscriptionPaymentRules.VerifyWebhookSignature(body + " ", signature, secret));
    }

    [Fact]
    public void WebhookSignatureRejectsMissingSecretOrSignature()
    {
        var signature = Hmac("{}", "secret");

        Assert.False(RazorpaySubscriptionPaymentRules.VerifyWebhookSignature("{}", signature, ""));
        Assert.False(RazorpaySubscriptionPaymentRules.VerifyWebhookSignature("{}", "", "secret"));
    }

    [Fact]
    public void CapturedPaymentRequiresExactOrderAmountCurrencyAndCapturedState()
    {
        Assert.True(RazorpaySubscriptionPaymentRules.IsCapturedPayment(
            "order_1", "order_1", 49900, 49900, "INR", "INR", "captured"));
    }

    [Theory]
    [InlineData("order_other", 49900L, "INR", "captured")]
    [InlineData("order_1", 49901L, "INR", "captured")]
    [InlineData("order_1", 49900L, "USD", "captured")]
    [InlineData("order_1", 49900L, "INR", "authorized")]
    [InlineData("order_1", 49900L, "INR", "failed")]
    public void CapturedPaymentRejectsOrderAmountCurrencyOrStatusMismatch(
        string orderId,
        long amount,
        string currency,
        string status)
    {
        Assert.False(RazorpaySubscriptionPaymentRules.IsCapturedPayment(
            orderId, "order_1", amount, 49900, currency, "INR", status));
    }

    private static string Hmac(string message, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }
}
