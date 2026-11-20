using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Modules.ShopPricing.Contracts;

namespace SmartPrint.Api.Modules.ShopPricing.Services;

/// <summary>
/// Owns the tenant-scoped pricing profile update and monotonic version increment.
/// </summary>
public sealed class ShopPricingService(SmartPrintDbContext db)
{
    public async Task<ShopPricingResponse?> UpdateAsync(
        Guid tenantId,
        ShopPricingRequest request,
        CancellationToken ct)
    {
        var pricing = await db.ShopPricingProfiles.SingleOrDefaultAsync(
            item => item.TenantId == tenantId, ct);
        if (pricing is null) return null;

        pricing.MonoPerSideMinor = request.MonoPerSideMinor;
        pricing.ColourPerSideMinor = request.ColourPerSideMinor;
        pricing.MonoDuplexPerSheetMinor = request.MonoDuplexPerSheetMinor;
        pricing.ColourDuplexPerSheetMinor = request.ColourDuplexPerSheetMinor;
        pricing.MinimumChargeMinor = request.MinimumChargeMinor;
        pricing.Photo35x45Minor = request.Photo35x45Minor;
        pricing.Photo25x35Minor = request.Photo25x35Minor;
        pricing.Photo2x2Minor = request.Photo2x2Minor;
        pricing.Photo4x6Minor = request.Photo4x6Minor;
        if (request.IdDocumentPriceMinor is long idDocumentPriceMinor)
            pricing.IdDocumentPriceMinor = idDocumentPriceMinor;
        pricing.IsConfigured = true;
        pricing.Version++;
        pricing.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ShopPricingResponse(
            pricing.IsConfigured,
            pricing.MonoPerSideMinor,
            pricing.ColourPerSideMinor,
            pricing.MonoDuplexPerSheetMinor,
            pricing.ColourDuplexPerSheetMinor,
            pricing.MinimumChargeMinor,
            pricing.Photo35x45Minor,
            pricing.Photo25x35Minor,
            pricing.Photo2x2Minor,
            pricing.Photo4x6Minor,
            pricing.IdDocumentPriceMinor,
            pricing.Version,
            pricing.UpdatedUtc);
    }
}
