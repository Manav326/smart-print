using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.AgentPrivateDocuments.Services;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.AgentPrivateDocuments.Endpoints;

/// <summary>
/// HTTP adapter for the authenticated Agent private-relay work queue.
/// </summary>
public static class AgentPrivateRelayEndpoints
{
    public static RouteGroupBuilder MapAgentPrivateRelayEndpoints(this RouteGroupBuilder agentApi)
    {
        agentApi.MapGet("/private-relays/pending", async (
            HttpContext context,
            AgentPrivateRelayService service,
            IHostEnvironment environment,
            int? waitSeconds,
            CancellationToken ct) =>
        {
            if (!context.Items.TryGetValue(AgentRateLimitService.AuthenticatedAgentItemKey, out var value) ||
                value is not AgentDevice device)
            {
                return Results.Unauthorized();
            }

            var result = await service.GetPendingAsync(device, environment, waitSeconds, ct);
            return Results.Ok(result);
        });

        return agentApi;
    }
}
