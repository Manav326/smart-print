using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobQueryEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobQueryEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/print-jobs", async (
            ClaimsPrincipal principal,
            ShopPrintJobQueryService jobQueries,
            int? offset,
            int? take,
            string? search,
            Guid? batchId,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var boundedOffset = Math.Clamp(offset ?? 0, 0, 1_000_000);
            var boundedTake = Math.Clamp(take ?? 100, 1, 100);
            var searchTerm = search?.Trim();
            if (searchTerm is { Length: > 120 })
            {
                searchTerm = searchTerm[..120];
            }

            var jobs = await jobQueries.GetJobsAsync(
                tenantId, boundedOffset, boundedTake, searchTerm, batchId, ct);
            return Results.Ok(jobs);
        });

        shop.MapShopReportEndpoints();
        return shop;
    }
}