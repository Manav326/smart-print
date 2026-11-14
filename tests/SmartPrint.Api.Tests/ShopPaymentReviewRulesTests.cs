using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopPaymentReviewRulesTests
{
    [Theory]
    [InlineData("UPI", "AWAITING_CUSTOMER_PAYMENT", true)]
    [InlineData("UPI", "CUSTOMER_REPORTED", true)]
    [InlineData("UPI", "CASH_AWAITING_PAYMENT", false)]
    [InlineData("UPI", "VERIFIED", false)]
    [InlineData("CASH", "CASH_AWAITING_PAYMENT", true)]
    [InlineData("CASH", "AWAITING_CUSTOMER_PAYMENT", false)]
    [InlineData("CASH", "CUSTOMER_REPORTED", false)]
    [InlineData("CASH", "VERIFIED", false)]
    [InlineData("CARD", "AWAITING_CUSTOMER_PAYMENT", false)]
    [InlineData(null, "AWAITING_CUSTOMER_PAYMENT", false)]
    [InlineData("UPI", null, false)]
    public void AllowsVerificationOnlyForActiveMethodSpecificAttempts(
        string? method,
        string? status,
        bool expected)
    {
        Assert.Equal(expected, ShopPaymentReviewRules.CanVerify(method, status));
    }

    [Theory]
    [InlineData("AWAITING_CUSTOMER_PAYMENT", true)]
    [InlineData("CUSTOMER_REPORTED", true)]
    [InlineData("CASH_AWAITING_PAYMENT", true)]
    [InlineData("VERIFIED", false)]
    [InlineData("REJECTED", false)]
    [InlineData("PARTIALLY_REFUNDED", false)]
    [InlineData(null, false)]
    public void AllowsRejectionOnlyBeforePaymentIsVerified(string? status, bool expected)
    {
        Assert.Equal(expected, ShopPaymentReviewRules.CanReject(status));
    }

    [Theory]
    [InlineData("REJECTED", true)]
    [InlineData("REFUNDED", true)]
    [InlineData("PAYMENT_REJECTED", false)]
    [InlineData("PAID_AUTHORISED", false)]
    [InlineData("COMPLETED", false)]
    [InlineData(null, false)]
    public void PreservesJobsAlreadyRejectedOrRefundedWhenVerifyingPayment(
        string? jobStatus,
        bool expected)
    {
        Assert.Equal(expected, ShopPaymentReviewRules.MustPreserveRejectedJobStatus(jobStatus));
    }
}
