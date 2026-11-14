using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public static class ShopPricingRules
{
    public const long MaximumRateMinor = 100_000;
    public const long MaximumMinimumChargeMinor = 10_000_000;

    public static bool IsValid(ShopPricingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rates = new[]
        {
            request.MonoPerSideMinor,
            request.ColourPerSideMinor,
            request.MonoDuplexPerSheetMinor,
            request.ColourDuplexPerSheetMinor,
            request.Photo35x45Minor,
            request.Photo25x35Minor,
            request.Photo2x2Minor,
            request.Photo4x6Minor
        };

        return rates.All(amount => amount >= 0 && amount <= MaximumRateMinor) &&
               request.MinimumChargeMinor >= 0 &&
               request.MinimumChargeMinor <= MaximumMinimumChargeMinor &&
               request.MonoPerSideMinor > 0 &&
               request.ColourPerSideMinor > 0;
    }
}
