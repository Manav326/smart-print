using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class DocumentRetentionRulesTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PurgesCloudDocumentsAfterConfirmedCompletionAndRetention()
    {
        Assert.True(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", "COMPLETED", Cutoff.AddTicks(-1), null, Cutoff.AddDays(-40),
            false, null, Cutoff, Cutoff));
        Assert.True(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", "COMPLETED", null, Cutoff, Cutoff.AddDays(-40),
            false, null, Cutoff, Cutoff));
    }

    [Fact]
    public void PurgesVerifiedVirtualTestOutputAfterRetentionWithoutPhysicalConfirmation()
    {
        Assert.True(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", "TEST_OUTPUT_READY", null, Cutoff.AddTicks(-1), Cutoff.AddDays(-40),
            false, null, Cutoff, Cutoff));
        Assert.False(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", "TEST_OUTPUT_READY", null, Cutoff.AddTicks(1), Cutoff.AddDays(-40),
            false, null, Cutoff, Cutoff));
        // A physical confirmation timestamp alone must not complete a test-output record.
        Assert.False(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", "TEST_OUTPUT_READY", Cutoff.AddDays(-40), null, Cutoff.AddDays(-40),
            false, null, Cutoff, Cutoff));
    }

    [Theory]
    [InlineData("LOCAL_AGENT", "COMPLETED", true, false)]
    [InlineData("CLOUD", "OUTPUT_UNCONFIRMED", true, false)]
    [InlineData("CLOUD", "COMPLETED", false, false)]
    [InlineData("CLOUD", "COMPLETED", true, true)]
    public void RetainsLocalUnconfirmedAndNotYetExpiredDocuments(
        string storageMode, string status, bool beforeCutoff, bool expected)
    {
        var completion = beforeCutoff ? Cutoff.AddTicks(-1) : Cutoff.AddTicks(1);
        Assert.Equal(expected, DocumentRetentionRules.ShouldPurgeCloudDocument(
            storageMode, status, completion, null, Cutoff.AddDays(-40), false,
            null, Cutoff, Cutoff));
    }

    [Theory]
    [InlineData("REJECTED", false, true)]
    [InlineData("PAYMENT_REJECTED", false, true)]
    [InlineData("QUOTE_EXPIRED", false, true)]
    [InlineData("REFUNDED", false, true)]
    [InlineData("REJECTED", true, false)]
    [InlineData("OUTPUT_UNCONFIRMED", false, false)]
    [InlineData("AWAITING_PAYMENT", false, false)]
    [InlineData("COMPLETED", true, false)]
    public void PurgesTerminalUnpaidOnlyAfterRetentionAndWithoutFinancialHold(
        string status, bool hasFinancialHold, bool expected)
        => Assert.Equal(expected, DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", status, null, null, Cutoff.AddDays(-40), hasFinancialHold,
            null, Cutoff, Cutoff));

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("PAYMENT_REJECTED")]
    [InlineData("QUOTE_EXPIRED")]
    [InlineData("REFUNDED")]
    public void RetainsTerminalJobsWithACompletedPdfExport(string status)
        => Assert.False(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", status, null, Cutoff.AddDays(-40), Cutoff.AddDays(-40),
            false, null, Cutoff, Cutoff));

    [Theory]
    [InlineData("DIAGNOSTIC_VERIFIED")]
    [InlineData("DIAGNOSTIC_RECEIVED")]
    [InlineData("DIAGNOSTIC_ISSUE")]
    [InlineData("DIAGNOSTIC_NO_OUTPUT")]
    public void PurgesConfirmedDiagnosticDocumentsAfterCompletedRetention(string status)
    {
        Assert.True(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", status, null, Cutoff.AddTicks(-1), Cutoff.AddDays(-40),
            false, null, Cutoff, Cutoff));
        Assert.False(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", status, null, Cutoff.AddTicks(1), Cutoff.AddDays(-40),
            false, null, Cutoff, Cutoff));
        Assert.False(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", status, null, null, Cutoff.AddDays(-40),
            false, null, Cutoff, Cutoff));
    }

    [Fact]
    public void DoesNotPurgeDocumentTwice()
        => Assert.False(DocumentRetentionRules.ShouldPurgeCloudDocument(
            "CLOUD", "COMPLETED", Cutoff.AddDays(-31), null, Cutoff.AddDays(-40),
            false, Cutoff, Cutoff, Cutoff));
}
