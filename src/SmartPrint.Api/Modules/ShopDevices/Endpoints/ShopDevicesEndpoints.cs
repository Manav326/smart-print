using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.ShopDevices.Contracts;
using SmartPrint.Api.Modules.ShopDevices.Services;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.ShopDevices.Endpoints;

/// <summary>
/// HTTP adapters for shop printer and QR-station lifecycle operations.
/// Quota locks, transactions, ownership-scoped database writes and active-output safety
/// checks are owned by `ShopDevicesService`.
/// </summary>
public static class ShopDevicesEndpoints
{
    public static RouteGroupBuilder MapShopDevicesEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPost("/printers", async (
            CreatePrinterRequest request,
            ClaimsPrincipal principal,
            ShopDevicesService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) || !TenantIdentity.TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await service.CreatePrinterAsync(tenantId, request, ct);
            return result.Kind switch
            {
                ShopDeviceOperationKind.InvalidRequest => Results.ValidationProblem(result.ValidationErrors!),
                ShopDeviceOperationKind.Conflict => Results.Conflict(new
                {
                    code = result.Code, message = result.Message,
                    current = result.Current, maximum = result.Maximum
                }),
                _ => Results.Created($"/api/v1/shop/printers/{result.Value!.Id}", result.Value)
            };
        });

        shop.MapPatch("/printers/{printerId:guid}/active", async (
            Guid printerId,
            bool isActive,
            Guid? replacePrinterId,
            ClaimsPrincipal principal,
            ShopDevicesService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) || !TenantIdentity.TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await service.SetPrinterActiveAsync(tenantId, printerId, isActive, replacePrinterId, ct);
            return result.Kind switch
            {
                ShopDeviceOperationKind.NotFound => Results.NotFound(),
                ShopDeviceOperationKind.BadRequest => Results.BadRequest(new { code = result.Code, message = result.Message }),
                ShopDeviceOperationKind.Conflict => Results.Conflict(new
                {
                    code = result.Code, message = result.Message
                }),
                _ => Results.Ok(result.Value)
            };
        });

        shop.MapPost("/qr-stations", async (
            CreateQrStationRequest request,
            ClaimsPrincipal principal,
            ShopDevicesService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) || !TenantIdentity.TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await service.CreateQrStationAsync(tenantId, request, ct);
            return result.Kind switch
            {
                ShopDeviceOperationKind.PortalNotConfigured =>
                    Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "The canonical public HTTPS URL is not configured."),
                ShopDeviceOperationKind.InvalidRequest or ShopDeviceOperationKind.InvalidPrinterSelection =>
                    Results.ValidationProblem(result.ValidationErrors!),
                ShopDeviceOperationKind.Conflict => Results.Conflict(new
                {
                    code = result.Code, message = result.Message,
                    current = result.Current, maximum = result.Maximum
                }),
                _ => Results.Created($"/api/v1/shop/qr-stations/{result.Value!.Id}", result.Value)
            };
        });

        shop.MapPost("/qr-stations/{stationId:guid}/rotate-code", async (
            Guid stationId,
            ClaimsPrincipal principal,
            ShopDevicesService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) || !TenantIdentity.TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await service.RotateQrCodeAsync(tenantId, stationId, ct);
            return result.Kind switch
            {
                ShopDeviceOperationKind.PortalNotConfigured =>
                    Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "The canonical public HTTPS URL is not configured."),
                ShopDeviceOperationKind.NotFound => Results.NotFound(),
                _ => Results.Ok(result.Value)
            };
        });

        shop.MapPatch("/qr-stations/{stationId:guid}/enabled", async (
            Guid stationId,
            bool isEnabled,
            ClaimsPrincipal principal,
            ShopDevicesService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) || !TenantIdentity.TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await service.SetQrStationEnabledAsync(tenantId, stationId, isEnabled, ct);
            return result.Kind == ShopDeviceOperationKind.NotFound
                ? Results.NotFound()
                : Results.Ok(result.Value);
        });

        return shop;
    }
}
