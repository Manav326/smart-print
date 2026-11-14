using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobEventsEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobEventsEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/print-jobs/{jobId:guid}/events", async (
            Guid jobId,
            ClaimsPrincipal principal,
            ShopPrintJobEventsService eventsService,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var events = await eventsService.GetEventsAsync(tenantId, jobId, ct);
            return events is null ? Results.NotFound() : Results.Ok(events);
        });

        return shop;
    }
}
