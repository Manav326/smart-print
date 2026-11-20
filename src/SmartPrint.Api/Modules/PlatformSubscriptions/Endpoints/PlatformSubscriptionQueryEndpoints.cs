using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Modules.PlatformSubscriptions.Endpoints;

/// <summary>
/// Read-only platform subscription queries. Assignment and lifecycle writes remain separate.
/// </summary>
public static class PlatformSubscriptionQueryEndpoints
{
    public static RouteGroupBuilder MapPlatformSubscriptionQueries(this RouteGroupBuilder platform)
    {
        platform.MapGet("/subscriptions", async (
            string? search,
            int? page,
            int? pageSize,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (search is not null && search.Trim().Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["search"] = ["Search must be 100 characters or fewer."]
                });
            }

            var pageNumber = Math.Clamp(page ?? 1, 1, 10_000);
            var boundedPageSize = Math.Clamp(pageSize ?? 25, 1, 50);
            var query = db.Tenants.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim()}%";
                query = query.Where(tenant =>
                    EF.Functions.ILike(tenant.BusinessName, pattern) ||
                    EF.Functions.ILike(tenant.Slug, pattern));
            }

            var totalCount = await query.CountAsync(ct);
            var tenants = await query
                .OrderByDescending(tenant => tenant.CreatedUtc)
                .Skip((pageNumber - 1) * boundedPageSize)
                .Take(boundedPageSize)
                .Select(tenant => new
                {
                    tenant.Id,
                    tenant.Slug,
                    tenant.BusinessName,
                    tenant.Status,
                    tenant.CreatedUtc
                })
                .ToListAsync(ct);

            var tenantIds = tenants.Select(tenant => tenant.Id).ToArray();
            var now = DateTimeOffset.UtcNow;
            var subscriptions = await db.TenantSubscriptions.AsNoTracking()
                .Where(item => tenantIds.Contains(item.TenantId))
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id)
                .ToListAsync(ct);
            var latestSubscriptions = subscriptions
                .GroupBy(item => item.TenantId)
                .ToDictionary(
                    group => group.Key,
                    group =>
                        group.Where(item => PlanEntitlementRules.IsSubscriptionAccessActive(
                                item.Status, item.PeriodStartUtc, item.PeriodEndUtc, now))
                            .OrderByDescending(item => item.PeriodStartUtc)
                            .ThenByDescending(item => item.CreatedUtc)
                            .FirstOrDefault()
                        ?? group.Where(item =>
                                (item.Status == "Active" || item.Status == "Trial" || item.Status == "Scheduled") &&
                                item.PeriodStartUtc > now && item.PeriodEndUtc > now)
                            .OrderBy(item => item.PeriodStartUtc)
                            .ThenBy(item => item.CreatedUtc)
                            .FirstOrDefault()
                        ?? group.First());

            var versionIds = latestSubscriptions.Values
                .Where(item => item.PlanVersionId != null)
                .Select(item => item.PlanVersionId!.Value)
                .Distinct()
                .ToArray();
            var planVersions = await (
                from version in db.SubscriptionPlanVersions.AsNoTracking()
                join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
                where versionIds.Contains(version.Id)
                select new
                {
                    version.Id,
                    plan.Code,
                    plan.Name,
                    version.Version,
                    version.Status,
                    version.Currency,
                    version.MonthlyPriceMinor,
                    version.AnnualPriceMinor,
                    plan.IsActive
                })
                .ToListAsync(ct);
            var versionsById = planVersions.ToDictionary(item => item.Id);

            var items = tenants.Select(tenant =>
            {
                latestSubscriptions.TryGetValue(tenant.Id, out var subscription);
                var plan = subscription?.PlanVersionId is Guid versionId &&
                           versionsById.TryGetValue(versionId, out var resolvedPlan)
                    ? resolvedPlan
                    : null;
                return new
                {
                    tenant.Id,
                    tenant.Slug,
                    tenant.BusinessName,
                    tenant.Status,
                    tenant.CreatedUtc,
                    Subscription = subscription is null ? null : new
                    {
                        subscription.Id,
                        subscription.PlanCode,
                        status = PlanEntitlementRules.GetEffectiveSubscriptionStatus(
                            subscription.Status, subscription.PeriodStartUtc, subscription.PeriodEndUtc, now),
                        subscription.BillingInterval,
                        subscription.PeriodStartUtc,
                        subscription.PeriodEndUtc,
                        subscription.CreatedUtc,
                        subscription.PlanVersionId
                    },
                    Plan = plan
                };
            }).ToArray();

            return Results.Ok(new
            {
                page = pageNumber,
                pageSize = boundedPageSize,
                totalCount,
                items
            });
        });
        
        platform.MapGet("/tenants/{tenantId:guid}/subscription-history", async (
            Guid tenantId,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!await db.Tenants.AnyAsync(tenant => tenant.Id == tenantId, ct))
            {
                return Results.NotFound();
            }

            var history = await db.PlatformSubscriptionAudits.AsNoTracking()
                .Where(item => item.TenantId == tenantId)
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.PreviousSubscriptionId,
                    item.PreviousStatus,
                    item.AssignedSubscriptionId,
                    item.PlanVersionId,
                    item.ActorUserId,
                    item.EventType,
                    item.PlanCode,
                    item.BillingInterval,
                    item.Status,
                    item.AmountMinor,
                    item.Currency,
                    item.PeriodStartUtc,
                    item.PeriodEndUtc,
                    item.Reason,
                    item.PaymentReference,
                    item.IdempotencyKey,
                    item.CreatedUtc
                })
                .ToListAsync(ct);
            return Results.Ok(history);
        });
        return platform;
    }
}
