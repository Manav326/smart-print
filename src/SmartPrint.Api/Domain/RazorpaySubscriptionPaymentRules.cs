using System.Security.Cryptography;
using System.Text;

namespace SmartPrint.Api.Domain;

public static class RazorpaySubscriptionPaymentRules
{
    public const long MaximumOrderAmountMinor = 10_000_000; // ₹100,000, matching the mPay Razorpay order ceiling.

    public static bool TryResolvePrice(
        SubscriptionPlan plan,
        SubscriptionPlanVersion version,
        string? requestedInterval,
        out string billingInterval,
        out long amountMinor,
        out string errorCode)
    {
        billingInterval = string.Empty;
        amountMinor = 0;
        errorCode = "plan_version_not_available";

        if (version.PlanId != plan.Id || !plan.IsActive || plan.IsSystemPlan ||
            string.Equals(plan.Code, "TRIAL", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(version.Status, "Published", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(version.Currency, "INR", StringComparison.Ordinal))
        {
            errorCode = "unsupported_currency";
            return false;
        }

        var interval = requestedInterval?.Trim().ToUpperInvariant();
        var configuredAmount = interval switch
        {
            "MONTHLY" => version.MonthlyPriceMinor,
            "ANNUAL" => version.AnnualPriceMinor,
            _ => null
        };

        if (interval is not ("MONTHLY" or "ANNUAL"))
        {
            errorCode = "billing_interval_not_available";
            return false;
        }

        if (configuredAmount is null or <= 0)
        {
            errorCode = configuredAmount == 0 ? "free_plan_does_not_require_checkout" : "billing_interval_not_available";
            return false;
        }

        if (configuredAmount > MaximumOrderAmountMinor)
        {
            errorCode = "amount_exceeds_gateway_limit";
            return false;
        }

        billingInterval = interval == "MONTHLY" ? "Monthly" : "Annual";
        amountMinor = configuredAmount.Value;
        errorCode = string.Empty;
        return true;
    }

    public static bool VerifyCheckoutSignature(string orderId, string paymentId, string signature, string secret) =>
        VerifyHmac($"{orderId}|{paymentId}", signature, secret);

    public static bool VerifyWebhookSignature(string rawBody, string signature, string secret) =>
        VerifyHmac(rawBody, signature, secret);

    private static bool VerifyHmac(string message, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature) ||
            signature.Length != 64 || signature.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
        var supplied = signature.ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(supplied));
    }

    public static bool IsCapturedPayment(
        string? actualOrderId,
        string? expectedOrderId,
        long? actualAmountMinor,
        long expectedAmountMinor,
        string? actualCurrency,
        string expectedCurrency,
        string? actualStatus) =>
        string.Equals(actualOrderId, expectedOrderId, StringComparison.Ordinal) &&
        actualAmountMinor == expectedAmountMinor &&
        string.Equals(actualCurrency, expectedCurrency, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actualStatus, "captured", StringComparison.OrdinalIgnoreCase);
}
