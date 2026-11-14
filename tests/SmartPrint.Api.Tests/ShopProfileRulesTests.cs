using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopProfileRulesTests
{
    [Theory]
    [InlineData("#7662E8", true)]
    [InlineData("#000000", true)]
    [InlineData("#FFFFFF", true)]
    [InlineData("7662E8", false)]
    [InlineData("#12345", false)]
    [InlineData("#12345678", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("not-a-colour", false)]
    public void AcceptsOnlySixDigitHexColours(string value, bool expected) =>
        Assert.Equal(expected, ShopProfileRules.IsHexColor(value));
}
