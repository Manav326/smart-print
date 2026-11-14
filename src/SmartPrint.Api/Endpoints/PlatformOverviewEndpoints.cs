using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class PlatformOverviewEndpoints
{
    public static RouteGroupBuilder MapPlatformOverviewEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/overview", async (
            PlatformOverviewService overviewService,
            CancellationToken ct) =>
        {
            var overview = await overviewService.GetOverviewAsync(ct);
            return Results.Ok(overview);
        });

        platform.MapGet("/tenants", async (
            string? search,
            string? status,
            int? page,
            int? pageSize,
            PlatformOverviewService overviewService,
            CancellationToken ct) =>
        {
            if (search is not null && search.Trim().Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["search"] = ["Search must be 100 characters or fewer."]
                });
            }

            if (status is not null && status.Trim().Length > 24)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Status must be 24 characters or fewer."]
                });
            }

            var pageNumber = Math.Clamp(page ?? 1, 1, 10_000);
            var boundedPageSize = Math.Clamp(pageSize ?? 25, 1, 50);
            var result = await overviewService.ListTenantsAsync(
                search, status, pageNumber, boundedPageSize, ct);
            return Results.Ok(result);
        });

        platform.MapGet("/tenants/{tenantId:guid}/status-history", async (
            Guid tenantId,
            PlatformOverviewService overviewService,
            CancellationToken ct) =>
        {
            var history = await overviewService.GetStatusHistoryAsync(tenantId, ct);
            return history is null ? Results.NotFound() : Results.Ok(history);
        });

        return platform;
    }
}