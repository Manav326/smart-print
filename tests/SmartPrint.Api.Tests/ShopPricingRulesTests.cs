using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopPricingRulesTests
{
    private static ShopPricingRequest Request(
        long mono = 100,
        long colour = 200,
        long monoDuplex = 50,
        long colourDuplex = 100,
        long minimum = 500,
        long photo35x45 = 0,
        long photo25x35 = 0,
        long photo2x2 = 0,
        long photo4x6 = 0) =>
        new(mono, colour, monoDuplex, colourDuplex, minimum, photo35x45, photo25x35, photo2x2, photo4x6);

    [Fact]
    public void AcceptsValidPositiveSimpleRatesAndOptionalZeroRates()
    {
        Assert.True(ShopPricingRules.IsValid(Request()));
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(-1, 200)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public void RequiresPositiveMonoAndColourRates(long mono, long colour)
    {
        Assert.False(ShopPricingRules.IsValid(Request(mono: mono, colour: colour)));
    }

    [Fact]
    public void RejectsAnyRateAboveTheMaximum()
    {
        Assert.False(ShopPricingRules.IsValid(Request(mono: ShopPricingRules.MaximumRateMinor + 1)));
        Assert.False(ShopPricingRules.IsValid(Request(photo4x6: ShopPricingRules.MaximumRateMinor + 1)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000001)]
    public void RejectsInvalidMinimumCharge(long minimum)
    {
        Assert.False(ShopPricingRules.IsValid(Request(minimum: minimum)));
    }

    [Fact]
    public void AcceptsMaximumConfiguredRatesAndMinimumCharge()
    {
        Assert.True(ShopPricingRules.IsValid(Request(
            mono: ShopPricingRules.MaximumRateMinor,
            colour: ShopPricingRules.MaximumRateMinor,
            monoDuplex: ShopPricingRules.MaximumRateMinor,
            colourDuplex: ShopPricingRules.MaximumRateMinor,
            minimum: ShopPricingRules.MaximumMinimumChargeMinor,
            photo35x45: ShopPricingRules.MaximumRateMinor,
            photo25x35: ShopPricingRules.MaximumRateMinor,
            photo2x2: ShopPricingRules.MaximumRateMinor,
            photo4x6: ShopPricingRules.MaximumRateMinor)));
    }
}
