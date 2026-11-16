using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class SpoolerOutputConfirmationPolicyTests
{
    private static readonly DateTimeOffset ObservedUtc = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("SPOOLER_ACCEPTED", "RETAINED")]
    [InlineData("PRINTING", "retained")]
    [InlineData("OUTPUT_UNCONFIRMED", " Retained ")]
    public void RetainedWindowsJobCannotBeConfirmedFromAnyPhysicalOutputState(string jobStatus, string spoolerStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus, ObservedUtc, "Ready"));
    }

    [Theory]
    [InlineData("SPOOLER_ACCEPTED", "PRINTED")]
    [InlineData("PRINTING", "QUEUED")]
    [InlineData("OUTPUT_UNCONFIRMED", "PRINTED")]
    public void FreshNonRetainedAgentObservationAllowsPhysicalOutputConfirmation(string jobStatus, string spoolerStatus)
    {
        Assert.True(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus, ObservedUtc, "Ready"));
    }

    [Theory]
    [InlineData("SPOOLER_JOB_NO_LONGER_VISIBLE", 17)]
    [InlineData("AGENT_RESTART_DURING_PRINT_SUBMISSION", null)]
    public void VerifiedSpoolerAbsenceAllowsOutputUnconfirmedManualResolution(string reasonCode, int? spoolerJobId)
    {
        Assert.True(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", ObservedUtc, "Ready", reasonCode, spoolerJobId));
    }

    [Fact]
    public void LegacyOutputUnconfirmedObservationWithoutVerifiedAbsenceReasonCannotBeConfirmed()
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", ObservedUtc, "Ready",
            "UNEXPECTED_PRINT_EXECUTION_ERROR", 17));
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", ObservedUtc, "Ready", null, 17));
    }

    [Theory]
    [InlineData("AWAITING_PAYMENT", "PRINTED")]
    [InlineData("COMPLETED", "PRINTED")]
    [InlineData(null, "QUEUED")]
    public void NonOutputStatesCannotBeConfirmed(string? jobStatus, string? spoolerStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus, ObservedUtc, "Ready"));
    }

    [Fact]
    public void MissingOrUnverifiedAgentObservationCannotBeConfirmed()
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", null, "Ready"));
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "PRINTER_UNAVAILABLE", ObservedUtc, "Ready"));
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "UNKNOWN", ObservedUtc, "Ready"));
    }

    [Theory]
    [InlineData("OFFLINE")]
    [InlineData("PAPEROUT")]
    [InlineData("PAUSED")]
    [InlineData("ERROR")]
    [InlineData("USERINTERVENTION")]
    public void QueueWarningsBlockRoutineConfirmationButAllowManualOutputUnconfirmedResolution(string spoolerStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "SPOOLER_ACCEPTED", spoolerStatus, ObservedUtc, "Ready"));
        Assert.True(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", spoolerStatus, ObservedUtc, "Ready"));
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Paused")]
    public void OfflineOrPausedPrinterBlocksRoutineConfirmationButNotManualResolution(string printerStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "PRINTING", "QUEUED", ObservedUtc, printerStatus));
        Assert.True(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "PRINTED", ObservedUtc, printerStatus));
    }

    [Theory]
    [InlineData("SPOOLER_JOB_NO_LONGER_VISIBLE", 17, true)]
    [InlineData(" spooler_job_no_longer_visible ", 17, true)]
    [InlineData("SPOOLER_JOB_NO_LONGER_VISIBLE", null, false)]
    [InlineData("SPOOLER_JOB_NO_LONGER_VISIBLE", 0, false)]
    [InlineData("AGENT_RESTART_DURING_PRINT_SUBMISSION", null, true)]
    [InlineData("AGENT_RESTART_DURING_PRINT_SUBMISSION", 17, false)]
    [InlineData("LOCAL_QUEUE_PROOF_MISMATCH", 17, false)]
    [InlineData("AGENT_STOPPED_DURING_OUTPUT", 17, false)]
    public void OnlyVerifiedAgentAbsenceReportsMayClearRetainedSpoolerState(
        string reasonCode, int? spoolerJobId, bool expected)
    {
        Assert.Equal(expected, SpoolerOutputConfirmationPolicy.IsVerifiedSpoolerAbsenceReport(reasonCode, spoolerJobId));
    }

    [Theory]
    [InlineData("RETAINED")]
    [InlineData("retained")]
    [InlineData(" Retained ")]
    public void IdentifiesRetainedWindowsJobsRegardlessOfCasingOrWhitespace(string spoolerStatus)
    {
        Assert.True(SpoolerOutputConfirmationPolicy.IsRetained(spoolerStatus));
    }

    [Fact]
    public void TemporaryPrinterUnavailableObservationDoesNotClearLastKnownRetainedState()
    {
        Assert.True(SpoolerOutputConfirmationPolicy.ShouldPreserveLastKnownRetainedStatus(
            "RETAINED", "PRINTER_UNAVAILABLE"));
        Assert.True(SpoolerOutputConfirmationPolicy.ShouldPreserveLastKnownRetainedStatus(
            " retained ", "printer_unavailable"));
    }

    [Theory]
    [InlineData("RETAINED", "PRINTED")]
    [InlineData("RETAINED", "OUTPUT_UNCONFIRMED")]
    [InlineData("RETAINED", "QUEUED")]
    [InlineData("PRINTING", "PRINTER_UNAVAILABLE")]
    [InlineData(null, "PRINTER_UNAVAILABLE")]
    public void FreshNonTransientAgentObservationReplacesThePreviousState(string? currentStatus, string? incomingStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.ShouldPreserveLastKnownRetainedStatus(
            currentStatus, incomingStatus));
    }
}
