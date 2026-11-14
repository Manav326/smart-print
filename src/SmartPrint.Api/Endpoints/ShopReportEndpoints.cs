using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopReportEndpoints
{
    public static RouteGroupBuilder MapShopReportEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/reports/summary", async (
            ClaimsPrincipal principal,
            ShopReportService reportService,
            int? days,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var periodDays = days ?? 30;
            var result = await reportService.GetSummaryAsync(tenantId, periodDays, ct);
            if (result.Failure == ShopReportFailure.AccessInactive)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "This shop's trial or subscription period is no longer active.");
            }
            if (result.Failure == ShopReportFailure.AdvancedReportsNotIncluded)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Advanced reporting is not included in this shop's current plan.");
            }
            if (result.Failure == ShopReportFailure.InvalidPeriod)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["days"] = ["Choose a reporting period of 7, 30 or 90 days."]
                });
            }

            return Results.Ok(result.Summary);
        });

        return shop;
    }
}