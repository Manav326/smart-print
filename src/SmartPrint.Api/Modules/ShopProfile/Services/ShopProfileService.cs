using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.ShopProfile.Contracts;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Modules.ShopProfile.Services;

/// <summary>
/// Owns shop profile updates and the server-authoritative white-label entitlement check.
/// </summary>
public sealed class ShopProfileService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<ShopProfileUpdateResult> UpdateAsync(
        Guid tenantId,
        ShopProfileRequest request,
        CancellationToken ct)
    {
        var plan = await entitlements.GetForTenantAsync(tenantId, ct);
        var requestsCustomBranding = request.LogoUrl is not null ||
            !string.Equals(request.AccentColor, "#7662E8", StringComparison.OrdinalIgnoreCase);
        if (requestsCustomBranding && !plan.WhiteLabelEnabled)
            return ShopProfileUpdateResult.WhiteLabelNotIncluded();

        var profile = await db.ShopProfiles.SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
        if (profile is null) return ShopProfileUpdateResult.NotFound();

        profile.DisplayName = request.DisplayName.Trim();
        profile.LogoUrl = request.LogoUrl;
        profile.AccentColor = request.AccentColor.ToUpperInvariant();
        profile.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return ShopProfileUpdateResult.Updated(new ShopProfileUpdateResponse(
            profile.DisplayName,
            profile.LogoUrl,
            profile.AccentColor,
            profile.UpdatedUtc));
    }
}

public enum ShopProfileUpdateKind
{
    Updated,
    WhiteLabelNotIncluded,
    NotFound
}

public sealed record ShopProfileUpdateResult(
    ShopProfileUpdateKind Kind,
    ShopProfileUpdateResponse? Response = null)
{
    public static ShopProfileUpdateResult Updated(ShopProfileUpdateResponse response) =>
        new(ShopProfileUpdateKind.Updated, response);

    public static ShopProfileUpdateResult WhiteLabelNotIncluded() =>
        new(ShopProfileUpdateKind.WhiteLabelNotIncluded);

    public static ShopProfileUpdateResult NotFound() =>
        new(ShopProfileUpdateKind.NotFound);
}
