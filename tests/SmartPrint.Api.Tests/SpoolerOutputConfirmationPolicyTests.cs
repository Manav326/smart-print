using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class SpoolerOutputConfirmationPolicyTests
{
    [Theory]
    [InlineData("SPOOLER_ACCEPTED", "RETAINED")]
    [InlineData("PRINTING", "retained")]
    [InlineData("OUTPUT_UNCONFIRMED", " Retained ")]
    public void RetainedWindowsJobCannotBeConfirmedFromAnyPhysicalOutputState(string jobStatus, string spoolerStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus));
    }

    [Theory]
    [InlineData("SPOOLER_ACCEPTED", "PRINTED")]
    [InlineData("PRINTING", "QUEUED")]
    [InlineData("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED")]
    [InlineData("OUTPUT_UNCONFIRMED", "PRINTED")]
    public void FreshNonRetainedAgentObservationAllowsPhysicalOutputConfirmation(string jobStatus, string spoolerStatus)
    {
        Assert.True(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus));
    }

    [Theory]
    [InlineData("AWAITING_PAYMENT", "PRINTED")]
    [InlineData("COMPLETED", "PRINTED")]
    [InlineData(null, "QUEUED")]
    public void NonOutputStatesCannotBeConfirmed(string? jobStatus, string? spoolerStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus));
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
