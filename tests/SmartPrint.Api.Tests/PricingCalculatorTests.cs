using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;

namespace SmartPrint.Api.Tests;

public sealed class PricingCalculatorTests
{
    [Fact]
    public void CalculatesSingleSidedMonoByImpressions()
    {
        Assert.Equal(2_000, PricingCalculator.Calculate(5, 2, "Mono", false, ConfiguredPricing()));
    }

    [Fact]
    public void CalculatesDuplexByPhysicalSheetsAndCopies()
    {
        Assert.Equal(4_200, PricingCalculator.Calculate(5, 2, "Colour", true, ConfiguredPricing()));
    }

    [Fact]
    public void AppliesMinimumJobCharge()
    {
        Assert.Equal(500, PricingCalculator.Calculate(1, 1, "Mono", false, ConfiguredPricing(minimum: 500)));
    }

    [Fact]
    public void RejectsUnpricedDuplexMode()
    {
        var pricing = ConfiguredPricing();
        pricing.MonoDuplexPerSheetMinor = 0;
        Assert.Throws<DuplexPricingNotConfiguredException>(() => PricingCalculator.Calculate(2, 1, "Mono", true, pricing));
    }

    [Theory]
    [InlineData(0, 1, "Mono")]
    [InlineData(501, 1, "Mono")]
    [InlineData(1, 0, "Mono")]
    [InlineData(1, 101, "Mono")]
    [InlineData(1, 1, "Automatic")]
    public void RejectsUnsupportedQuoteInputs(int pages, int copies, string mode)
    {
        Assert.ThrowsAny<ArgumentException>(() => PricingCalculator.Calculate(pages, copies, mode, false, ConfiguredPricing()));
    }

    [Fact]
    public void RejectsUnconfiguredRateCard()
    {
        var pricing = ConfiguredPricing();
        pricing.IsConfigured = false;
        Assert.Throws<InvalidOperationException>(() => PricingCalculator.Calculate(1, 1, "Mono", false, pricing));
    }

    [Fact]
    public void PhotoQuoteUsesConfiguredPerPhotoRateWithoutDocumentMinimumCharge()
    {
        var pricing = ConfiguredPricing(minimum: 5_000);
        pricing.Photo35x45Minor = 175;

        Assert.Equal(700, PricingCalculator.CalculatePhoto(4, "35x45", pricing));
        Assert.Equal(175, PricingCalculator.PhotoUnitRate("35x45", pricing));
    }

    [Fact]
    public void PhotoQuoteUsesTheSelectedSizeRate()
    {
        var pricing = ConfiguredPricing();
        pricing.Photo25x35Minor = 125;
        pricing.Photo2x2Minor = 250;
        pricing.Photo4x6Minor = 450;

        Assert.Equal(375, PricingCalculator.CalculatePhoto(3, "25x35", pricing));
        Assert.Equal(500, PricingCalculator.CalculatePhoto(2, "2x2", pricing));
        Assert.Equal(900, PricingCalculator.CalculatePhoto(2, "4x6", pricing));
    }

    [Fact]
    public void PhotoQuoteRejectsAnUnpricedSize()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PricingCalculator.CalculatePhoto(1, "35x45", ConfiguredPricing()));
    }

    [Fact]
    public void PhotoQuoteAndPageCountUseTheGeneratedPhotoQuantityLimit()
    {
        var maximum = UploadLimits.MaxPhotoCount;
        var pricing = ConfiguredPricingWithPhotoRate(175);

        Assert.Equal((long)maximum * 175, PricingCalculator.CalculatePhoto(maximum, "35x45", pricing));
        Assert.Equal(4, PricingCalculator.PhotoPageCount(maximum, "35x45", 25));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PricingCalculator.CalculatePhoto(maximum + 1, "35x45", pricing));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PricingCalculator.PhotoPageCount(maximum + 1, "35x45", 25));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PricingCalculator.PhotoPageCount(maximum + 1, "35x45"));
    }

    [Fact]
    public void PhotoSheetCapacitiesMatchTheSupportedA4Layouts()
    {
        Assert.Equal(25, PricingCalculator.MaximumPhotosPerA4Sheet("35x45"));
        Assert.Equal(49, PricingCalculator.MaximumPhotosPerA4Sheet("25x35"));
        Assert.Equal(15, PricingCalculator.MaximumPhotosPerA4Sheet("2x2"));
        Assert.Equal(1, PricingCalculator.MaximumPhotosPerA4Sheet("4x6"));
    }

    [Fact]
    public void MultiPagePhotoOrderUsesTheDeclaredLayoutCapacity()
    {
        Assert.Equal(2, PricingCalculator.PhotoPageCount(50, "35x45", 25));
        Assert.Equal(3, PricingCalculator.PhotoPageCount(50, "35x45", 20));
    }

    [Fact]
    public void MultiPagePhotoRefundsFollowThePhotosOnEachPage()
    {
        var allocations = PricingCalculator.CalculatePhotoPageRefundAllocations(
            50, "35x45", 20, 175);

        Assert.Equal(new long[] { 3_500, 3_500, 1_750 }, allocations);
        Assert.Equal(PricingCalculator.CalculatePhoto(50, "35x45", ConfiguredPricingWithPhotoRate(175)), allocations.Sum());
    }


    [Fact]
    public void FiftyPhotosAtNinePerPageProduceSixA4PagesAndCorrectLastPageRefund()
    {
        Assert.Equal(6, PricingCalculator.PhotoPageCount(50, "35x45", 9));

        var allocations = PricingCalculator.CalculatePhotoPageRefundAllocations(
            50, "35x45", 9, 175);

        Assert.Equal(new long[] { 1_575, 1_575, 1_575, 1_575, 1_575, 875 }, allocations);
        Assert.Equal(PricingCalculator.CalculatePhoto(50, "35x45", ConfiguredPricingWithPhotoRate(175)), allocations.Sum());
    }

    [Fact]
    public void RejectsPageCapacityThatExceedsTheSelectedPhotoSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.PhotoPageCount(50, "35x45", 26));
    }

    private static ShopPricingProfile ConfiguredPricingWithPhotoRate(long photoRate) {
        var pricing = ConfiguredPricing();
        pricing.Photo35x45Minor = photoRate;
        return pricing;
    }

    private static ShopPricingProfile ConfiguredPricing(long minimum = 0) => new()
    {
        TenantId = Guid.NewGuid(), IsConfigured = true,
        MonoPerSideMinor = 200, ColourPerSideMinor = 500,
        MonoDuplexPerSheetMinor = 300, ColourDuplexPerSheetMinor = 700,
        MinimumChargeMinor = minimum, Version = 3
    };
}
