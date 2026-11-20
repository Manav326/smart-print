using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Modules.ShopPricing.Domain;

/// <summary>
/// Pure limits and validation for shop pricing values, all expressed in minor currency units.
/// </summary>
public static class ShopPricingRules
{
    public const long MaximumRateMinor = 100_000;
    public const long MaximumMinimumChargeMinor = 10_000_000;

    public static Dictionary<string, string[]> Validate(ShopPricingRequest request)
    {
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
        if (rates.Any(amount => amount < 0 || amount > MaximumRateMinor) ||
            request.IdDocumentPriceMinor is < 0 or > MaximumRateMinor ||
            request.MinimumChargeMinor < 0 || request.MinimumChargeMinor > MaximumMinimumChargeMinor ||
            request.MonoPerSideMinor == 0 || request.ColourPerSideMinor == 0)
        {
            return new Dictionary<string, string[]>
            {
                ["pricing"] = ["Set positive mono and colour per-side prices, non-negative duplex prices and a valid minimum charge. Prices use paise for INR."]
            };
        }

        return new Dictionary<string, string[]>();
    }
}
