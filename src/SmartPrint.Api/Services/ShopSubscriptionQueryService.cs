using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Services;

public sealed class ShopSubscriptionQueryService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<object?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        var subscription = await db.TenantSubscriptions.AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.PlanCode,
                item.Status,
                item.BillingInterval,
                item.PeriodStartUtc,
                item.PeriodEndUtc,
                item.CreatedUtc,
                item.PlanVersionId
            })
            .FirstOrDefaultAsync(ct);
        if (subscription is null)
        {
            return null;
        }

        var plan = subscription.PlanVersionId is Guid planVersionId
            ? await (
                from version in db.SubscriptionPlanVersions.AsNoTracking()
                join definition in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals definition.Id
                where version.Id == planVersionId
                select new
                {
                    version.Id,
                    definition.Code,
                    definition.Name,
                    definition.Description,
                    definition.IsActive,
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
                    version.PublishedUtc
                })
                .SingleOrDefaultAsync(ct)
            : null;

        var entitlement = await entitlements.GetForTenantAsync(tenantId, ct);
        var now = DateTimeOffset.UtcNow;
        var periodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = periodStart.AddMonths(1);

        var printerCount = await db.PrinterDevices.AsNoTracking()
            .LongCountAsync(item => item.TenantId == tenantId, ct);
        var agentDeviceCount = await db.AgentDevices.AsNoTracking()
            .LongCountAsync(item => item.TenantId == tenantId &&
                                    item.RevokedUtc == null && item.Status != "Revoked", ct);
        var qrStationCount = await db.QrStations.AsNoTracking()
            .LongCountAsync(item => item.TenantId == tenantId, ct);
        var monthlyPrintJobCount = await entitlements.GetMonthlyPrintJobUsageAsync(tenantId, ct);

        return new
        {
            generatedUtc = now,
            subscription = new
            {
                subscription.Id,
                subscription.PlanCode,
                subscription.Status,
                subscription.BillingInterval,
                subscription.PeriodStartUtc,
                subscription.PeriodEndUtc,
                subscription.CreatedUtc
            },
            plan,
            entitlements = new
            {
                entitlement.MaxPrinters,
                entitlement.MaxAgentDevices,
                entitlement.MaxQrStations,
                entitlement.MaxMonthlyPrintJobs,
                entitlement.MaxDocumentBytes,
                entitlement.CloudAssistedPrintingEnabled,
                entitlement.WhiteLabelEnabled,
                entitlement.AdvancedReportsEnabled,
                entitlement.IsLegacyUnconfigured,
                entitlement.IsAccessActive
            },
            usage = new
            {
                printerCount,
                agentDeviceCount,
                qrStationCount,
                monthlyPrintJobCount,
                monthlyPeriodStartUtc = periodStart,
                monthlyPeriodEndUtc = periodEnd
            }
        };
    }
}