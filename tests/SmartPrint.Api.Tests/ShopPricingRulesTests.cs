using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.ShopPricing.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopPricingRulesTests
{
    private static ShopPricingRequest ValidRequest() => new(
        MonoPerSideMinor: 200,
        ColourPerSideMinor: 500,
        MonoDuplexPerSheetMinor: 300,
        ColourDuplexPerSheetMinor: 700,
        MinimumChargeMinor: 1000,
        Photo35x45Minor: 200,
        Photo25x35Minor: 200,
        Photo2x2Minor: 300,
        Photo4x6Minor: 400,
        IdDocumentPriceMinor: 500);

    [Fact]
    public void AcceptsACompleteValidMinorUnitPriceProfile()
    {
        Assert.Empty(ShopPricingRules.Validate(ValidRequest()));
    }

    [Fact]
    public void RequiresPositiveMonoAndColourPerSidePrices()
    {
        Assert.Contains("pricing", ShopPricingRules.Validate(ValidRequest() with { MonoPerSideMinor = 0 }).Keys);
        Assert.Contains("pricing", ShopPricingRules.Validate(ValidRequest() with { ColourPerSideMinor = 0 }).Keys);
    }

    [Fact]
    public void AllowsZeroDuplexAndPhotoPricesButRejectsNegativeRates()
    {
        var request = ValidRequest() with
        {
            MonoDuplexPerSheetMinor = 0,
            ColourDuplexPerSheetMinor = 0,
            Photo35x45Minor = 0,
            Photo25x35Minor = 0,
            Photo2x2Minor = 0,
            Photo4x6Minor = 0
        };
        Assert.Empty(ShopPricingRules.Validate(request));
        Assert.Contains("pricing", ShopPricingRules.Validate(request with { Photo2x2Minor = -1 }).Keys);
    }

    [Fact]
    public void AppliesTheExistingPerRateAndMinimumChargeCaps()
    {
        Assert.Contains("pricing", ShopPricingRules.Validate(
            ValidRequest() with { ColourPerSideMinor = ShopPricingRules.MaximumRateMinor + 1 }).Keys);
        Assert.Contains("pricing", ShopPricingRules.Validate(
            ValidRequest() with { MinimumChargeMinor = ShopPricingRules.MaximumMinimumChargeMinor + 1 }).Keys);
        Assert.Empty(ShopPricingRules.Validate(
            ValidRequest() with { MinimumChargeMinor = ShopPricingRules.MaximumMinimumChargeMinor }));
    }

    [Fact]
    public void OptionalIdDocumentPriceMayBeOmittedOrMustStayWithinRateCap()
    {
        Assert.Empty(ShopPricingRules.Validate(ValidRequest() with { IdDocumentPriceMinor = null }));
        Assert.Contains("pricing", ShopPricingRules.Validate(
            ValidRequest() with { IdDocumentPriceMinor = ShopPricingRules.MaximumRateMinor + 1 }).Keys);
    }
}
