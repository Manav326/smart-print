using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;
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
        agentApi.MapPost("/private-relays/{ticketId:guid}/stage", async (
            Guid ticketId,
            AgentPrivateRelayStageUpdateRequest request,
            HttpContext context,
            AgentPrivateRelayService service,
            CancellationToken ct) =>
        {
            if (!context.Items.TryGetValue(AgentRateLimitService.AuthenticatedAgentItemKey, out var value) ||
                value is not AgentDevice device)
            {
                return Results.Unauthorized();
            }

            var result = await service.UpdateStageAsync(
                device.TenantId, device.Id, ticketId, request.Stage, ct);

            return result.Kind switch
            {
                AgentPrivateRelayStageResultKind.InvalidStage => Results.BadRequest(new { code = result.Code }),
                AgentPrivateRelayStageResultKind.NotFound => Results.NotFound(),
                AgentPrivateRelayStageResultKind.Conflict when result.Code == "private_relay_stage_out_of_order" =>
                    Results.Conflict(new { code = result.Code, stage = result.Stage }),
                AgentPrivateRelayStageResultKind.Conflict => Results.Conflict(new { code = result.Code }),
                AgentPrivateRelayStageResultKind.Duplicate => Results.Ok(new
                {
                    result.Id,
                    PrivateRelayStage = result.Stage,
                    DuplicateRequest = true
                }),
                _ => Results.Ok(new
                {
                    result.Id,
                    PrivateRelayStage = result.Stage,
                    PrivateRelayStageUpdatedUtc = result.StageUpdatedUtc,
                    DuplicateRequest = false
                })
            };
        });

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
