using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Modules.PlatformPlans.Endpoints;

/// <summary>
/// Read-only subscription-plan catalog query.
/// Plan creation, draft editing and publishing remain separate write workflows.
/// </summary>
public static class PlatformPlanQueryEndpoints
{
    public static RouteGroupBuilder MapPlatformPlanQueries(this RouteGroupBuilder platform)
    {
        platform.MapGet("/plans", async (SmartPrintDbContext db, CancellationToken ct) =>
        {
            var plans = await db.SubscriptionPlans.AsNoTracking()
                .OrderBy(plan => plan.Code)
                .ToListAsync(ct);
            var planIds = plans.Select(plan => plan.Id).ToArray();
            var versions = await db.SubscriptionPlanVersions.AsNoTracking()
                .Where(version => planIds.Contains(version.PlanId))
                .OrderByDescending(version => version.Version)
                .ToListAsync(ct);

            var response = plans.Select(plan => new
            {
                plan.Id,
                plan.Code,
                plan.Name,
                plan.Description,
                plan.IsActive,
                plan.IsSystemPlan,
                plan.CreatedUtc,
                plan.UpdatedUtc,
                Versions = versions.Where(version => version.PlanId == plan.Id)
                    .Select(version => new
                    {
                        version.Id,
                        version.Version,
                        version.Status,
                        version.Currency,
                        version.MonthlyPriceMinor,
                        version.AnnualPriceMinor,
                        version.MaxPrinters,
                        version.MaxAgentDevices,
                        version.MaxQrStations,
                        version.MaxMonthlyPrintJobs,
                        version.MaxDocumentBytes,
                        version.CloudAssistedPrintingEnabled,
                        version.WhiteLabelEnabled,
                        version.AdvancedReportsEnabled,
                        version.CreatedUtc,
                        version.UpdatedUtc,
                        version.PublishedUtc
                    })
                    .ToArray()
            }).ToArray();

            return Results.Ok(response);
        });
        return platform;
    }
}
