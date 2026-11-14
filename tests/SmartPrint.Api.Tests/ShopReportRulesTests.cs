using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopReportRulesTests
{
    [Theory]
    [InlineData(7, true)]
    [InlineData(30, true)]
    [InlineData(90, true)]
    [InlineData(1, false)]
    [InlineData(14, false)]
    [InlineData(365, false)]
    [InlineData(0, false)]
    [InlineData(-7, false)]
    public void AllowsOnlySupportedReportPeriods(int days, bool expected) =>
        Assert.Equal(expected, ShopReportRules.IsValidPeriod(days));
}
