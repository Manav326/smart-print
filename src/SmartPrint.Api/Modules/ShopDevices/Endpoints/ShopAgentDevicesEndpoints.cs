using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.ShopDevices.Services;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.ShopDevices.Endpoints;

/// <summary>
/// HTTP adapters for shop-side Agent enrollment, listing and revocation.
/// </summary>
public static class ShopAgentDevicesEndpoints
{
    /// <summary>
    /// Maps the paired-Agent management routes while preserving their original tenant and role gates.
    /// </summary>
    public static RouteGroupBuilder MapShopAgentDevicesEndpoints(this RouteGroupBuilder shopAgents)
    {
        shopAgents.MapPost("/agent-enrollment-codes", async (
            ClaimsPrincipal principal,
            ShopAgentDevicesService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) ||
                !TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var result = await service.CreateEnrollmentCodeAsync(tenantId, GetActorId(principal), ct);
            return Results.Created("/api/v1/shop/agent-devices", result);
        });

        shopAgents.MapGet("/agent-devices", async (
            ClaimsPrincipal principal,
            ShopAgentDevicesService service,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var devices = await service.GetDevicesAsync(tenantId, ct);
            return Results.Ok(devices);
        });

        shopAgents.MapPatch("/agent-devices/{agentId:guid}/revoke", async (
            Guid agentId,
            ClaimsPrincipal principal,
            ShopAgentDevicesService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) ||
                !TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var result = await service.RevokeAsync(tenantId, agentId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return shopAgents;
    }

    private static Guid? GetActorId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out var actorId) ? actorId : null;
}
