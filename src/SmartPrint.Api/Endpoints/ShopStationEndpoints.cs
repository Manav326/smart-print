using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopStationEndpoints
{
    public static RouteGroupBuilder MapShopStationEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/qr-stations", async (
            ClaimsPrincipal principal,
            IConfiguration configuration,
            ShopStationService stations,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var result = await stations.ListAsync(
                tenantId, configuration["PublicPortal:BaseUrl"], ct);
            return Results.Ok(result);
        });

        shop.MapPost("/qr-stations", async (
            CreateQrStationRequest request,
            ClaimsPrincipal principal,
            IConfiguration configuration,
            ShopStationService stations,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            var result = await stations.CreateAsync(tenantId, request, portalBase, ct);
            if (result.Failure == ShopStationMutationFailure.InvalidPublicUrl)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The canonical public HTTPS URL is not configured.");

            if (result.Failure == ShopStationMutationFailure.InvalidDisplayName)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["displayName"] = ["A station name of up to 120 characters is required."]
                });
            }

            if (result.Failure == ShopStationMutationFailure.PrinterUnavailable)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printerDeviceId"] = ["Select an active printer that belongs to this shop."]
                });
            }

            if (result.Failure == ShopStationMutationFailure.PrinterOutputUnsupported)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printerDeviceId"] = ["This virtual/document driver is not supported for SmartPrint output. Select a physical printer or Microsoft Print to PDF for non-production testing."]
                });
            }

            if (result.Failure == ShopStationMutationFailure.QuotaExceeded)
            {
                return Results.Conflict(new
                {
                    code = result.QuotaCode,
                    message = result.QuotaMessage,
                    current = result.QuotaCurrent,
                    maximum = result.QuotaMaximum
                });
            }

            var station = result.Station!;
            return Results.Created($"/api/v1/shop/qr-stations/{station.Id}", new
            {
                station.Id,
                station.DisplayName,
                station.PrinterDeviceId,
                station.PublicCodeHint,
                station.IsEnabled,
                PublicUrl = result.PublicUrl
            });
        });

        shop.MapPost("/qr-stations/{stationId:guid}/rotate-code", async (
            Guid stationId,
            ClaimsPrincipal principal,
            IConfiguration configuration,
            ShopStationService stations,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            var result = await stations.RotateCodeAsync(tenantId, stationId, portalBase, ct);
            if (result.Failure == ShopStationMutationFailure.InvalidPublicUrl)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The canonical public HTTPS URL is not configured.");

            if (result.Failure == ShopStationMutationFailure.NotFound)
                return Results.NotFound();

            var station = result.Station!;
            return Results.Ok(new
            {
                station.Id,
                station.PublicCodeHint,
                PublicUrl = result.PublicUrl
            });
        });

        shop.MapPatch("/qr-stations/{stationId:guid}/enabled", async (
            Guid stationId,
            bool isEnabled,
            ClaimsPrincipal principal,
            ShopStationService stations,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await stations.SetEnabledAsync(tenantId, stationId, isEnabled, ct);
            if (result.Failure == ShopStationMutationFailure.NotFound)
                return Results.NotFound();

            var station = result.Station!;
            return Results.Ok(new { station.Id, station.IsEnabled });
        });

        return shop;
    }
}
