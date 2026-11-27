using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintJobQuoteRulesTests
{
    [Theory]
    [InlineData("AWAITING_CONFIGURATION")]
    [InlineData("QUOTE_EXPIRED")]
    [InlineData("Uploaded")]
    [InlineData("UPLOADED")]
    public void AllowsQuoteForNewOrExpiredJobs(string status)
    {
        Assert.True(PrintJobQuoteRules.CanCreateQuote(status));
    }

    [Theory]
    [InlineData("AWAITING_CUSTOMER_APPROVAL")]
    [InlineData("AWAITING_SHOP_APPROVAL")]
    [InlineData("AWAITING_PAYMENT")]
    [InlineData("PAID_AUTHORISED")]
    [InlineData("LOCAL_QUEUE_READY")]
    [InlineData("PRINT_SUBMITTING")]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    public void RejectsQuoteForJobsAlreadyInProgress(string status)
    {
        Assert.False(PrintJobQuoteRules.CanCreateQuote(status));
    }

    [Fact]
    public void RejectsMissingStatus()
    {
        Assert.False(PrintJobQuoteRules.CanCreateQuote(null));
    }

    [Theory]
    [InlineData("AWAITING_CUSTOMER_APPROVAL", "AWAITING_CUSTOMER_APPROVAL")]
    [InlineData("AWAITING_SHOP_APPROVAL", "AWAITING_SHOP_APPROVAL")]
    [InlineData("AWAITING_PAYMENT", "AWAITING_PAYMENT")]
    public void SamePriceQuoteRenewalPreservesTheExistingApprovalStage(string previous, string expected)
    {
        Assert.Equal(expected, PrintJobQuoteRules.ResolveStatusAfterQuote(previous, true, false, true));
    }

    [Theory]
    [InlineData("AWAITING_PAYMENT", true)]
    [InlineData("AWAITING_CUSTOMER_APPROVAL", true)]
    [InlineData("AWAITING_SHOP_APPROVAL", true)]
    [InlineData("PAYMENT_REJECTED", true)]
    public void ChangedPriceAlwaysRequiresCustomerAcceptance(string previous, bool instantPaymentEnabled)
    {
        Assert.Equal("AWAITING_CUSTOMER_APPROVAL",
            PrintJobQuoteRules.ResolveStatusAfterQuote(previous, true, true, instantPaymentEnabled));
    }

    [Fact]
    public void NewQuoteStillHonoursTheShopInstantPaymentPreference()
    {
        Assert.Equal("AWAITING_PAYMENT", PrintJobQuoteRules.ResolveStatusAfterQuote(null, false, false, true));
        Assert.Equal("AWAITING_CUSTOMER_APPROVAL", PrintJobQuoteRules.ResolveStatusAfterQuote(null, false, false, false));
    }
}
