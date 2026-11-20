using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.AgentPrivateDocuments.Services;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.AgentPrivateDocuments.Endpoints;

/// <summary>
/// HTTP adapters for querying and acknowledging Agent-local private-source purges.
/// Agent authentication and per-device rate limiting are inherited from the parent route group.
/// </summary>
public static class AgentPrivateDocumentPurgeEndpoints
{
    public static RouteGroupBuilder MapAgentPrivateDocumentPurgeEndpoints(this RouteGroupBuilder agentApi)
    {
        agentApi.MapGet("/jobs/private-purge", async (
            HttpContext context,
            AgentPrivateDocumentPurgeService service,
            CancellationToken ct) =>
        {
            var device = GetAuthenticatedDevice(context);
            if (device is null)
                return Results.Unauthorized();

            var jobIds = await service.GetPendingJobIdsAsync(device.TenantId, device.Id, ct);
            return Results.Ok(new { JobIds = jobIds });
        });

        agentApi.MapPost("/jobs/{jobId:guid}/private-document-purged", async (
            Guid jobId,
            HttpContext context,
            AgentPrivateDocumentPurgeService service,
            CancellationToken ct) =>
        {
            var device = GetAuthenticatedDevice(context);
            if (device is null)
                return Results.Unauthorized();

            var result = await service.AcknowledgeAsync(device.TenantId, device.Id, jobId, ct);
            return result.Kind switch
            {
                AgentPrivateDocumentPurgeResultKind.NotFound => Results.NotFound(),
                AgentPrivateDocumentPurgeResultKind.Conflict => Results.Conflict(new
                {
                    code = result.Code,
                    message = result.Message
                }),
                AgentPrivateDocumentPurgeResultKind.Duplicate => Results.Ok(new
                {
                    result.Id,
                    PrivateDocumentPurgedUtc = result.PurgedUtc,
                    DuplicateRequest = true
                }),
                _ => Results.Ok(new
                {
                    result.Id,
                    PrivateDocumentPurgedUtc = result.PurgedUtc,
                    DuplicateRequest = false
                })
            };
        });

        return agentApi;
    }

    private static AgentDevice? GetAuthenticatedDevice(HttpContext context) =>
        context.Items.TryGetValue(AgentRateLimitService.AuthenticatedAgentItemKey, out var value)
            ? value as AgentDevice
            : null;
}
