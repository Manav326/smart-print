namespace SmartPrint.Api.Modules.ShopPricing.Contracts;

/// <summary>
/// Public shop pricing response preserving the existing JSON fields.
/// Monetary values remain in minor units (paise for INR).
/// </summary>
public sealed record ShopPricingResponse(
    bool IsConfigured,
    long MonoPerSideMinor,
    long ColourPerSideMinor,
    long MonoDuplexPerSheetMinor,
    long ColourDuplexPerSheetMinor,
    long MinimumChargeMinor,
    long Photo35x45Minor,
    long Photo25x35Minor,
    long Photo2x2Minor,
    long Photo4x6Minor,
    long IdDocumentPriceMinor,
    int Version,
    DateTimeOffset UpdatedUtc);
