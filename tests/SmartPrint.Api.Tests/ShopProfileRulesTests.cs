using SmartPrint.Api.Modules.ShopProfile.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopProfileRulesTests
{
    [Fact]
    public void AcceptsDefaultBrandingWithoutLogo()
    {
        var errors = ShopProfileRules.Validate("My Print Shop", "#7662E8", null);

        Assert.Empty(errors);
    }

    [Fact]
    public void AcceptsSixDigitHexColourAndAbsoluteHttpsLogo()
    {
        var errors = ShopProfileRules.Validate(
            "My Print Shop",
            "#A1b2C3",
            "https://cdn.example.test/shop-logo.png");

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("", "#7662E8", null)]
    [InlineData("   ", "#7662E8", null)]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890", "#7662E8", null)]
    [InlineData("Shop", "7662E8", null)]
    [InlineData("Shop", "#12345G", null)]
    [InlineData("Shop", "#12345678", null)]
    [InlineData("Shop", "#7662E8", "http://cdn.example.test/logo.png")]
    [InlineData("Shop", "#7662E8", "/shop-logo.png")]
    public void RejectsInvalidProfileValues(string displayName, string accentColor, string? logoUrl)
    {
        var errors = ShopProfileRules.Validate(displayName, accentColor, logoUrl);

        Assert.Contains("profile", errors.Keys);
        Assert.Equal(
            "Provide a display name, a six-digit hex accent colour, and an optional HTTPS logo URL.",
            errors["profile"][0]);
    }

    [Fact]
    public void AcceptsMaximumLengthDisplayName()
    {
        var errors = ShopProfileRules.Validate(new string('S', 180), "#7662E8", null);

        Assert.Empty(errors);
    }
}
