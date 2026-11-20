using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintDispatchReassignmentRulesTests
{
    [Fact]
    public void AllowsReassignmentOnlyBeforeAgentClaimAndWindowsSubmission()
    {
        Assert.True(PrintDispatchReassignmentRules.CanReassignBeforeSpoolerSubmission(
            "PRINT_AUTHORISED",
            "AWAITING_AGENT",
            spoolerJobId: null,
            printDispatchLeaseExpiresUtc: null,
            spoolerSubmittedUtc: null,
            spoolerObservedUtc: null,
            outputConfirmedUtc: null));
    }

    [Theory]
    [InlineData("PAID_AUTHORISED", "AWAITING_AGENT")]
    [InlineData("PRINT_SUBMITTING", "SUBMISSION_STARTED")]
    [InlineData("SPOOLER_ACCEPTED", "QUEUED")]
    [InlineData("PRINTING", "PRINTING")]
    [InlineData("OUTPUT_UNCONFIRMED", "UNKNOWN")]
    [InlineData("PRINT_AUTHORISED", "SUBMISSION_STARTED")]
    [InlineData("PRINT_AUTHORISED", "QUEUED")]
    public void RejectsEveryStateOutsideTheAwaitingAgentBoundary(string status, string spoolerStatus)
    {
        Assert.False(PrintDispatchReassignmentRules.CanReassignBeforeSpoolerSubmission(
            status, spoolerStatus, null, null, null, null, null));
    }

    [Fact]
    public void RejectsAuthorisedJobWhenAnySpoolerEvidenceOrLeaseExists()
    {
        var lease = DateTimeOffset.UtcNow.AddMinutes(5);
        var submitted = DateTimeOffset.UtcNow;
        var observed = DateTimeOffset.UtcNow;
        var confirmed = DateTimeOffset.UtcNow;

        Assert.False(PrintDispatchReassignmentRules.CanReassignBeforeSpoolerSubmission(
            "PRINT_AUTHORISED", "AWAITING_AGENT", 12, null, null, null, null));
        Assert.False(PrintDispatchReassignmentRules.CanReassignBeforeSpoolerSubmission(
            "PRINT_AUTHORISED", "AWAITING_AGENT", null, lease, null, null, null));
        Assert.False(PrintDispatchReassignmentRules.CanReassignBeforeSpoolerSubmission(
            "PRINT_AUTHORISED", "AWAITING_AGENT", null, null, submitted, null, null));
        Assert.False(PrintDispatchReassignmentRules.CanReassignBeforeSpoolerSubmission(
            "PRINT_AUTHORISED", "AWAITING_AGENT", null, null, null, observed, null));
        Assert.False(PrintDispatchReassignmentRules.CanReassignBeforeSpoolerSubmission(
            "PRINT_AUTHORISED", "AWAITING_AGENT", null, null, null, null, confirmed));
    }
}
