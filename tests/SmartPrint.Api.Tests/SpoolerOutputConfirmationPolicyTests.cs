using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class SpoolerOutputConfirmationPolicyTests
{
    [Theory]
    [InlineData("RETAINED")]
    [InlineData("retained")]
    [InlineData(" Retained ")]
    public void RetainedWindowsJobCannotBeConfirmed(string spoolerStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(spoolerStatus));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OUTPUT_UNCONFIRMED")]
    [InlineData("PRINTED")]
    [InlineData("QUEUED")]
    public void NonRetainedOrClearedSpoolerStateCanBeConfirmedAfterPhysicalInspection(string? spoolerStatus)
    {
        Assert.True(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(spoolerStatus));
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
    public void FreshNonTransientObservationReplacesThePreviousState(string? currentStatus, string? incomingStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.ShouldPreserveLastKnownRetainedStatus(
            currentStatus, incomingStatus));
    }
}
