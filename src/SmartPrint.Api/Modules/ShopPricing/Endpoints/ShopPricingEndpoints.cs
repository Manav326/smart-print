using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.ShopPricing.Domain;
using SmartPrint.Api.Modules.ShopPricing.Services;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.ShopPricing.Endpoints;

/// <summary>
/// HTTP adapter for a shop's authoritative printing/photo pricing profile.
/// Validation and the versioned persistence update are owned by the module.
/// </summary>
public static class ShopPricingEndpoints
{
    public static RouteGroupBuilder MapShopPricingEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPut("/pricing", async (
            ShopPricingRequest request,
            ClaimsPrincipal principal,
            ShopPricingService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) ||
                !TenantIdentity.TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var errors = ShopPricingRules.Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            var response = await service.UpdateAsync(tenantId, request, ct);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });

        return shop;
    }
}
