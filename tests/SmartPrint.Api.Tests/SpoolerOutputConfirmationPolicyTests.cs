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
    [InlineData("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED")]
    [InlineData("OUTPUT_UNCONFIRMED", "PRINTED")]
    public void FreshNonRetainedAgentObservationAllowsPhysicalOutputConfirmation(string jobStatus, string spoolerStatus)
    {
        Assert.True(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus, ObservedUtc, "Ready"));
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
            "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", ObservedUtc, printerStatus));
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
