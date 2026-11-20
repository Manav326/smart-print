using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.ShopPayments.Domain;
using SmartPrint.Api.Modules.ShopPayments.Services;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.ShopPayments.Endpoints;

/// <summary>
/// HTTP adapter for shop-owned payment-method configuration.
/// Validation and persistence are delegated to the module's rule and application service.
/// </summary>
public static class ShopPaymentSettingsEndpoints
{
    public static RouteGroupBuilder MapShopPaymentSettingsWrites(this RouteGroupBuilder shop)
    {
        shop.MapPut("/payment-settings", async (
            ShopPaymentSettingsRequest request,
            ClaimsPrincipal principal,
            ShopPaymentSettingsService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) ||
                !TenantIdentity.TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var upiId = request.UpiId?.Trim();
            var payeeName = request.PayeeName?.Trim();
            var errors = ShopPaymentSettingsRules.Validate(
                request.UpiEnabled, upiId, payeeName, request.CashEnabled);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            var normalizedRequest = request with { UpiId = upiId, PayeeName = payeeName };
            var response = await service.UpdateAsync(tenantId, normalizedRequest, ct);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });

        return shop;
    }
}
