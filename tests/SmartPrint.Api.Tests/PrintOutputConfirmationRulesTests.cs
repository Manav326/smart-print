using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintOutputConfirmationRulesTests
{
    [Theory]
    [InlineData("RETAINED")]
    [InlineData("retained")]
    [InlineData(" Retained ")]
    public void RetainedSpoolerStatusBlocksPhysicalOutputConfirmation(string spoolerStatus)
    {
        Assert.True(PrintOutputConfirmationRules.IsSpoolerJobRetained(spoolerStatus));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PRINTING")]
    [InlineData("PRINTED")]
    [InlineData("OUTPUT_UNCONFIRMED")]
    [InlineData("PRINTER_UNAVAILABLE")]
    public void ClearedOrUncertainSpoolerStatesDoNotRemainBlocked(string? spoolerStatus)
    {
        Assert.False(PrintOutputConfirmationRules.IsSpoolerJobRetained(spoolerStatus));
    }
}
