using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.AgentConnectivity.Services;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.AgentConnectivity.Endpoints;

/// <summary>
/// HTTP adapters for Agent heartbeat and installed-Windows-queue inventory.
/// Authentication and per-device rate limiting remain on the parent Agent route group.
/// </summary>
public static class AgentConnectivityEndpoints
{
    public static RouteGroupBuilder MapAgentConnectivityEndpoints(
        this RouteGroupBuilder agentApi,
        IReadOnlyList<string> allowedPortalOrigins)
    {
        agentApi.MapPost("/heartbeat", async (
            HttpContext context,
            IConfiguration configuration,
            AgentHeartbeatRequest request,
            AgentConnectivityService service,
            CancellationToken ct) =>
        {
            var device = GetAuthenticatedDevice(context);
            if (device is null)
                return Results.Unauthorized();

            var publicBaseUrl = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            var result = await service.HeartbeatAsync(
                device, request, publicBaseUrl, allowedPortalOrigins, ct);

            if (!result.Succeeded)
                return Results.BadRequest(new { code = result.FailureCode, message = result.FailureMessage });

            return Results.Ok(new
            {
                result.Id,
                result.Status,
                result.LastSeenUtc,
                result.AgentVersion,
                result.LocalEndpoints,
                result.PublicBaseUrl,
                result.AllowedPortalOrigins,
                result.AgentCompletedDocumentRetentionDays,
                result.AgentRetentionSweepHours
            });
        });

        agentApi.MapPost("/printers/sync", async (
            HttpContext context,
            AgentPrinterSyncRequest request,
            AgentConnectivityService service,
            CancellationToken ct) =>
        {
            var device = GetAuthenticatedDevice(context);
            if (device is null)
                return Results.Unauthorized();

            var result = await service.SyncPrintersAsync(device, request, ct);
            if (!result.Succeeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printers"] = ["Windows can report at most 100 installed printer queues in one inventory update."]
                });
            }

            return Results.Ok(new
            {
                Id = result.AgentDeviceId!.Value,
                result.Discovered,
                result.Created,
                result.Updated,
                result.Unavailable,
                result.Rejected,
                result.CapabilitiesUnknown,
                result.Partial,
                result.AutoSelected,
                result.Selected,
                result.MaximumSelected,
                result.SyncedUtc
            });
        });

        return agentApi;
    }

    private static AgentDevice? GetAuthenticatedDevice(HttpContext context) =>
        context.Items.TryGetValue(AgentRateLimitService.AuthenticatedAgentItemKey, out var value)
            ? value as AgentDevice
            : null;
}
