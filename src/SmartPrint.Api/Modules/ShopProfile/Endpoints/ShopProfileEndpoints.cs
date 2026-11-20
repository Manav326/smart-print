using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.ShopProfile.Contracts;
using SmartPrint.Api.Modules.ShopProfile.Domain;
using SmartPrint.Api.Modules.ShopProfile.Services;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.ShopProfile.Endpoints;

/// <summary>
/// Owns the shop profile HTTP surface. Reads preserve the owner-only profile view;
/// updates retain the owner/manager permission and delegate persistence to the service.
/// </summary>
public static class ShopProfileEndpoints
{
    public static RouteGroupBuilder MapShopProfileEndpoints(this RouteGroupBuilder shop)
    {
                shop.MapGet("/profile", async (
                    ClaimsPrincipal principal,
                    SmartPrintDbContext db,
                    UserManager<AppUser> userManager,
                    CancellationToken ct) =>
                {
                    if (!principal.FindAll("shop_role").Any(claim => claim.Value == "ShopOwner") ||
                        !TenantIdentity.TryGetTenantId(principal, out var tenantId) ||
                        !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
                    {
                        return Results.Forbid();
                    }
                
                    var owner = await userManager.FindByIdAsync(userId.ToString());
                    if (owner is null || owner.TenantId != tenantId)
                    {
                        return Results.Forbid();
                    }
                
                    var profile = await db.ShopProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
                    var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId && x.Status == "Active", ct);
                    return profile is null || tenant is null
                        ? Results.NotFound()
                        : Results.Ok(new
                        {
                            tenant.Id,
                            tenant.Slug,
                            tenant.BusinessName,
                            tenant.Status,
                            OwnerName = owner.DisplayName,
                            OwnerEmail = owner.Email ?? string.Empty,
                            profile.DisplayName,
                            profile.LogoUrl,
                            profile.AccentColor,
                            profile.Currency,
                            profile.TimeZone,
                            profile.WhiteLabelEnabled
                        });
                });

        shop.MapPut("/profile", async (
            ShopProfileRequest request,
            ClaimsPrincipal principal,
            ShopProfileService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) ||
                !TenantIdentity.TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var errors = ShopProfileRules.Validate(request.DisplayName, request.AccentColor, request.LogoUrl);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            var result = await service.UpdateAsync(tenantId, request, ct);
            if (result.Kind == ShopProfileUpdateKind.WhiteLabelNotIncluded)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Custom shop branding is not included in this shop's current plan.");
            if (result.Kind == ShopProfileUpdateKind.NotFound)
                return Results.NotFound();

            return Results.Ok(result.Response);
        });

        return shop;
    }
}
