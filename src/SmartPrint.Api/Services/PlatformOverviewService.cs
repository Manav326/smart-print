using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Services;

public sealed class PlatformOverviewService(SmartPrintDbContext db)
{
    public async Task<object> GetOverviewAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var recentStart = now.AddDays(-30);
        var onlineAfter = now.AddMinutes(-2);

        var tenantCount = await db.Tenants.AsNoTracking().CountAsync(ct);
        var activeTenantCount = await db.Tenants.AsNoTracking()
            .CountAsync(tenant => tenant.Status == "Active", ct);
        var trialSubscriptionCount = await db.TenantSubscriptions.AsNoTracking()
            .CountAsync(subscription => subscription.Status == "Trial" && subscription.PeriodEndUtc > now, ct);
        var activeSubscriptionCount = await db.TenantSubscriptions.AsNoTracking()
            .CountAsync(subscription => subscription.Status == "Active" && subscription.PeriodEndUtc > now, ct);
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
            .GroupBy(subscription => subscription.Status)
            .Select(group => new { status = group.Key, count = group.Count() })
            .OrderByDescending(group => group.count)
            .ThenBy(group => group.status)
            .ToListAsync(ct);

        return new
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
        };
    }

    public async Task<object> ListTenantsAsync(
        string? search,
        string? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
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
                    .OrderByDescending(subscription => subscription.CreatedUtc)
                    .Select(subscription => subscription.PlanCode)
                    .FirstOrDefault(),
                SubscriptionStatus = db.TenantSubscriptions.AsNoTracking()
                    .Where(subscription => subscription.TenantId == tenant.Id)
                    .OrderByDescending(subscription => subscription.CreatedUtc)
                    .Select(subscription => subscription.Status)
                    .FirstOrDefault(),
                SubscriptionPeriodEndUtc = db.TenantSubscriptions.AsNoTracking()
                    .Where(subscription => subscription.TenantId == tenant.Id)
                    .OrderByDescending(subscription => subscription.CreatedUtc)
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
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new { page, pageSize, totalCount, items };
    }

    public async Task<object?> GetStatusHistoryAsync(Guid tenantId, CancellationToken ct)
    {
        if (!await db.Tenants.AsNoTracking().AnyAsync(item => item.Id == tenantId, ct))
        {
            return null;
        }

        var history = await db.TenantStatusAudits.AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .OrderByDescending(item => item.CreatedUtc)
            .Select(item => new
            {
                item.Id,
                item.PreviousStatus,
                item.NewStatus,
                item.Reason,
                item.ActorUserId,
                item.CreatedUtc
            })
            .Take(100)
            .ToListAsync(ct);
        return history;
    }
}