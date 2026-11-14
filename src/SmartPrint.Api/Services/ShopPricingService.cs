using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopPricingUpdateFailure
{
    None,
    InvalidPricing,
    NotFound
}

public sealed record ShopPricingUpdateResult(
    ShopPricingUpdateFailure Failure,
    ShopPricingProfile? Pricing = null)
{
    public bool Succeeded => Failure == ShopPricingUpdateFailure.None;
}

public sealed class ShopPricingService(SmartPrintDbContext db)
{
    public async Task<ShopPricingUpdateResult> UpdateAsync(
        Guid tenantId,
        ShopPricingRequest request,
        CancellationToken cancellationToken)
    {
        if (!ShopPricingRules.IsValid(request))
            return new ShopPricingUpdateResult(ShopPricingUpdateFailure.InvalidPricing);

        var pricing = await db.ShopPricingProfiles.SingleOrDefaultAsync(
            item => item.TenantId == tenantId,
            cancellationToken);
        if (pricing is null)
            return new ShopPricingUpdateResult(ShopPricingUpdateFailure.NotFound);

        pricing.MonoPerSideMinor = request.MonoPerSideMinor;
        pricing.ColourPerSideMinor = request.ColourPerSideMinor;
        pricing.MonoDuplexPerSheetMinor = request.MonoDuplexPerSheetMinor;
        pricing.ColourDuplexPerSheetMinor = request.ColourDuplexPerSheetMinor;
        pricing.MinimumChargeMinor = request.MinimumChargeMinor;
        pricing.Photo35x45Minor = request.Photo35x45Minor;
        pricing.Photo25x35Minor = request.Photo25x35Minor;
        pricing.Photo2x2Minor = request.Photo2x2Minor;
        pricing.Photo4x6Minor = request.Photo4x6Minor;
        pricing.IsConfigured = true;
        pricing.Version++;
        pricing.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new ShopPricingUpdateResult(ShopPricingUpdateFailure.None, pricing);
    }



}
