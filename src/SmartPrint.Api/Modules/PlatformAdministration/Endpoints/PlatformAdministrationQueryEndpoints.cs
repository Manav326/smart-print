using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Modules.PlatformAdministration.Endpoints;

/// <summary>
/// Read-only platform overview, tenant directory and tenant-status history queries.
/// State-changing tenant operations remain separate from these query adapters.
/// </summary>
public static class PlatformAdministrationQueryEndpoints
{
    public static RouteGroupBuilder MapPlatformAdministrationQueries(this RouteGroupBuilder platform)
    {
        platform.MapGet("/overview", async (SmartPrintDbContext db, CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    var recentStart = now.AddDays(-30);
    var onlineAfter = now.AddMinutes(-2);

    var tenantCount = await db.Tenants.AsNoTracking().CountAsync(ct);
    var activeTenantCount = await db.Tenants.AsNoTracking()
        .CountAsync(tenant => tenant.Status == "Active", ct);
    var trialSubscriptionCount = await db.TenantSubscriptions.AsNoTracking()
        .CountAsync(subscription => subscription.Status == "Trial" &&
                                    subscription.PeriodStartUtc <= now &&
                                    subscription.PeriodEndUtc > now, ct);
    var activeSubscriptionCount = await db.TenantSubscriptions.AsNoTracking()
        .CountAsync(subscription => (subscription.Status == "Active" || subscription.Status == "Scheduled") &&
                                    subscription.PeriodStartUtc <= now &&
                                    subscription.PeriodEndUtc > now, ct);
    var printerCount = await db.PrinterDevices.AsNoTracking().CountAsync(ct);
    var activePrinterCount = await db.PrinterDevices.AsNoTracking()
        .CountAsync(printer => printer.IsActive, ct);
    var agentCount = await db.AgentDevices.AsNoTracking()
        .CountAsync(agent => agent.RevokedUtc == null && agent.Status != "Revoked", ct);
    var onlineAgentCount = await db.AgentDevices.AsNoTracking()
        .CountAsync(agent => agent.RevokedUtc == null && agent.Status != "Revoked" &&
                             agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter, ct);
    var jobsLast30Days = await db.PrintJobs.AsNoTracking()
        .CountAsync(job => job.CreatedUtc >= recentStart && job.CreatedUtc < now, ct);

    var subscriptionsByStatus = await db.TenantSubscriptions.AsNoTracking()
        .GroupBy(subscription =>
            (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
            subscription.PeriodEndUtc <= now
                ? "Expired"
                : (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
                  subscription.PeriodStartUtc > now
                    ? "Scheduled"
                    : subscription.Status == "Scheduled" &&
                      subscription.PeriodStartUtc <= now && subscription.PeriodEndUtc > now
                        ? "Active"
                        : subscription.Status)
        .Select(group => new { status = group.Key, count = group.Count() })
        .OrderByDescending(group => group.count)
        .ThenBy(group => group.status)
        .ToListAsync(ct);

    return Results.Ok(new
    {
        generatedUtc = now,
        tenantCount,
        activeTenantCount,
        trialSubscriptionCount,
        activeSubscriptionCount,
        printerCount,
        activePrinterCount,
        agentCount,
        onlineAgentCount,
        jobsLast30Days,
        subscriptionsByStatus
    });
});

        platform.MapGet("/tenants", async (
    string? search,
    string? status,
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

    if (status is not null && status.Trim().Length > 24)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["status"] = ["Status must be 24 characters or fewer."]
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

    if (!string.IsNullOrWhiteSpace(status))
    {
        var statusFilter = status.Trim();
        query = query.Where(tenant => tenant.Status == statusFilter);
    }

    var totalCount = await query.CountAsync(ct);
    var recentEnd = DateTimeOffset.UtcNow;
    var recentStart = recentEnd.AddDays(-30);
    var onlineAfter = recentEnd.AddMinutes(-2);
    var items = await query
        .OrderByDescending(tenant => tenant.CreatedUtc)
        .Select(tenant => new
        {
            tenant.Id,
            tenant.Slug,
            tenant.BusinessName,
            tenant.Status,
            tenant.CreatedUtc,
            PlanCode = db.TenantSubscriptions.AsNoTracking()
                .Where(subscription => subscription.TenantId == tenant.Id)
                .OrderByDescending(subscription =>
                    (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
                    subscription.PeriodStartUtc <= recentEnd && subscription.PeriodEndUtc > recentEnd)
                .ThenByDescending(subscription =>
                    (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
                    subscription.PeriodEndUtc > recentEnd)
                .ThenBy(subscription => subscription.PeriodStartUtc)
                .ThenByDescending(subscription => subscription.CreatedUtc)
                .Select(subscription => subscription.PlanCode)
                .FirstOrDefault(),
            SubscriptionStatus = db.TenantSubscriptions.AsNoTracking()
                .Where(subscription => subscription.TenantId == tenant.Id)
                .OrderByDescending(subscription =>
                    (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
                    subscription.PeriodStartUtc <= recentEnd && subscription.PeriodEndUtc > recentEnd)
                .ThenByDescending(subscription =>
                    (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
                    subscription.PeriodEndUtc > recentEnd)
                .ThenBy(subscription => subscription.PeriodStartUtc)
                .ThenByDescending(subscription => subscription.CreatedUtc)
                .ThenByDescending(subscription => subscription.Id)
                .Select(subscription =>
                    (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
                    subscription.PeriodEndUtc <= recentEnd
                        ? "Expired"
                        : (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
                          subscription.PeriodStartUtc > recentEnd
                            ? "Scheduled"
                            : subscription.Status == "Scheduled"
                                ? "Active"
                                : subscription.Status)
                .FirstOrDefault(),
            SubscriptionPeriodEndUtc = db.TenantSubscriptions.AsNoTracking()
                .Where(subscription => subscription.TenantId == tenant.Id)
                .OrderByDescending(subscription =>
                    (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
                    subscription.PeriodStartUtc <= recentEnd && subscription.PeriodEndUtc > recentEnd)
                .ThenByDescending(subscription =>
                    (subscription.Status == "Active" || subscription.Status == "Trial" || subscription.Status == "Scheduled") &&
                    subscription.PeriodEndUtc > recentEnd)
                .ThenBy(subscription => subscription.PeriodStartUtc)
                .ThenByDescending(subscription => subscription.CreatedUtc)
                .Select(subscription => (DateTimeOffset?)subscription.PeriodEndUtc)
                .FirstOrDefault(),
            PrinterCount = db.PrinterDevices.AsNoTracking()
                .Count(printer => printer.TenantId == tenant.Id),
            ActivePrinterCount = db.PrinterDevices.AsNoTracking()
                .Count(printer => printer.TenantId == tenant.Id && printer.IsActive),
            AgentCount = db.AgentDevices.AsNoTracking()
                .Count(agent => agent.TenantId == tenant.Id && agent.RevokedUtc == null && agent.Status != "Revoked"),
            OnlineAgentCount = db.AgentDevices.AsNoTracking()
                .Count(agent => agent.TenantId == tenant.Id && agent.RevokedUtc == null &&
                                agent.Status != "Revoked" && agent.LastSeenUtc != null &&
                                agent.LastSeenUtc >= onlineAfter),
            OfflineAgentCount = db.AgentDevices.AsNoTracking()
                .Count(agent => agent.TenantId == tenant.Id && agent.RevokedUtc == null &&
                                agent.Status != "Revoked" &&
                                (agent.LastSeenUtc == null || agent.LastSeenUtc < onlineAfter)),
            LatestAgentHeartbeatUtc = db.AgentDevices.AsNoTracking()
                .Where(agent => agent.TenantId == tenant.Id && agent.RevokedUtc == null && agent.Status != "Revoked")
                .Max(agent => agent.LastSeenUtc),
            JobsLast30Days = db.PrintJobs.AsNoTracking()
                .Count(job => job.TenantId == tenant.Id && job.CreatedUtc >= recentStart && job.CreatedUtc < recentEnd)
        })
        .Skip((pageNumber - 1) * boundedPageSize)
        .Take(boundedPageSize)
        .ToListAsync(ct);

    return Results.Ok(new
    {
        page = pageNumber,
        pageSize = boundedPageSize,
        totalCount,
        items
    });
});

        platform.MapGet("/tenants/{tenantId:guid}/status-history", async (
    Guid tenantId,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!await db.Tenants.AsNoTracking().AnyAsync(item => item.Id == tenantId, ct))
        return Results.NotFound();

    var history = await db.TenantStatusAudits.AsNoTracking()
        .Where(item => item.TenantId == tenantId)
        .OrderByDescending(item => item.CreatedUtc)
        .Select(item => new { item.Id, item.PreviousStatus, item.NewStatus, item.Reason, item.ActorUserId, item.CreatedUtc })
        .Take(100)
        .ToListAsync(ct);
    return Results.Ok(history);
});
        return platform;
    }
}
