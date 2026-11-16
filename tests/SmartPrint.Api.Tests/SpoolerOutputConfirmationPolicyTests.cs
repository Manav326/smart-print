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
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus, 0, 4));
    }

    [Theory]
    [InlineData("SPOOLER_ACCEPTED", "PRINTED", 4, 4, true)]
    [InlineData("OUTPUT_UNCONFIRMED", "PRINTED", 4, 4, true)]
    [InlineData("PRINTING", "QUEUED", 0, 4, false)]
    [InlineData("PRINTING", "PRINTING", 3, 4, false)]
    [InlineData("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", 0, 4, false)]
    [InlineData("OUTPUT_UNCONFIRMED", "PRINTED", 3, 4, false)]
    [InlineData("OUTPUT_UNCONFIRMED", "PRINTED", 4, 0, false)]
    public void PhysicalOutputRequiresPrintedStatusAndAllPages(string jobStatus, string spoolerStatus, int pagesPrinted, int totalPages, bool expected)
    {
        Assert.Equal(expected, SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus, pagesPrinted, totalPages));
    }

    [Theory]
    [InlineData("AWAITING_PAYMENT", "PRINTED")]
    [InlineData("COMPLETED", "PRINTED")]
    [InlineData(null, "QUEUED")]
    public void NonOutputStatesCannotBeConfirmed(string? jobStatus, string? spoolerStatus)
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(jobStatus, spoolerStatus, 4, 4));
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
