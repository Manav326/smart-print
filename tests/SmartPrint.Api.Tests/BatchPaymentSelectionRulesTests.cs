using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class BatchPaymentSelectionRulesTests
{
    [Theory]
    [InlineData("AWAITING_CONFIGURATION", false)]
    [InlineData("AWAITING_SHOP_APPROVAL", false)]
    [InlineData("AWAITING_CUSTOMER_APPROVAL", false)]
    [InlineData("AWAITING_PAYMENT", true)]
    [InlineData("PAYMENT_REJECTED", true)]
    [InlineData("AWAITING_CUSTOMER_PAYMENT", true)]
    [InlineData("PAYMENT_REPORTED", true)]
    [InlineData("AWAITING_COUNTER_PAYMENT", true)]
    [InlineData("PAID_AUTHORISED", true)]
    [InlineData("LOCAL_QUEUE_CLAIMED", true)]
    [InlineData("LOCAL_QUEUE_READY", true)]
    [InlineData("PRINT_AUTHORISED", true)]
    [InlineData("PRINT_SUBMITTING", true)]
    [InlineData("SPOOLER_ACCEPTED", true)]
    [InlineData("PRINTING", true)]
    [InlineData("OUTPUT_UNCONFIRMED", true)]
    [InlineData("COMPLETED", true)]
    [InlineData("REJECTED", true)]
    [InlineData("REFUNDED", true)]
    public void Review_completion_is_based_only_on_existing_job_status(
        string status, bool expected) =>
        Assert.Equal(expected, BatchPaymentSelectionRules.IsReviewComplete(status));

    [Theory]
    [InlineData("AWAITING_PAYMENT", true)]
    [InlineData("PAYMENT_REJECTED", true)]
    [InlineData("AWAITING_CONFIGURATION", false)]
    [InlineData("AWAITING_SHOP_APPROVAL", false)]
    [InlineData("AWAITING_CUSTOMER_PAYMENT", false)]
    [InlineData("PAID_AUTHORISED", false)]
    [InlineData("PRINTING", false)]
    [InlineData("COMPLETED", false)]
    [InlineData("REJECTED", false)]
    [InlineData("REFUNDED", false)]
    public void Only_accepted_and_unpaid_jobs_are_added_to_a_payment_manifest(
        string status, bool expected) =>
        Assert.Equal(expected, BatchPaymentSelectionRules.IsPayable(status));
}
