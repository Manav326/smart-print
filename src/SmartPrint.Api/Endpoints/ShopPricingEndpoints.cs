using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPricingEndpoints
{
    public static RouteGroupBuilder MapShopPricingEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/pricing", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var pricing = await db.ShopPricingProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
            return pricing is null
                ? Results.NotFound()
                : Results.Ok(new
                {
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
                    pricing.Version,
                    pricing.UpdatedUtc
                });
        });

        shop.MapPut("/pricing", async (
            ShopPricingRequest request,
            ClaimsPrincipal principal,
            ShopPricingService pricingService,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await pricingService.UpdateAsync(tenantId, request, ct);
            if (result.Failure == ShopPricingUpdateFailure.InvalidPricing)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pricing"] = ["Set positive mono and colour per-side prices, non-negative duplex prices and a valid minimum charge. Prices use paise for INR."]
                });
            }
            if (result.Failure == ShopPricingUpdateFailure.NotFound)
            {
                return Results.NotFound();
            }

            var pricing = result.Pricing!;
            return Results.Ok(new
            {
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
                pricing.Version,
                pricing.UpdatedUtc
            });
        });

        return shop;
    }
}