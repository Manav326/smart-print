using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum TenantQuotaResource
{
    Printer,
    AgentDevice,
    QrStation,
    MonthlyPrintJob
}

public sealed record TenantEntitlementSnapshot(
    Guid TenantId,
    string PlanCode,
    string SubscriptionStatus,
    Guid? PlanVersionId,
    DateTimeOffset? PeriodEndUtc,
    int? MaxPrinters,
    int? MaxAgentDevices,
    int? MaxQrStations,
    int? MaxMonthlyPrintJobs,
    long? MaxDocumentBytes,
    bool CloudAssistedPrintingEnabled,
    bool WhiteLabelEnabled,
    bool AdvancedReportsEnabled,
    bool IsLegacyUnconfigured,
    bool IsAccessActive);

public sealed record TenantQuotaDecision(
    bool Allowed,
    string Code,
    string Message,
    long Current,
    long? Maximum);

public sealed class TenantEntitlementService(SmartPrintDbContext db)
{
    public async Task<TenantEntitlementSnapshot> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var subscription = await db.TenantSubscriptions.AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .Select(item => new { item.PlanCode, item.Status, item.PlanVersionId, item.PeriodEndUtc })
            .FirstOrDefaultAsync(cancellationToken);

        var tenantStatus = await db.Tenants.AsNoTracking()
            .Where(item => item.Id == tenantId)
            .Select(item => item.Status)
            .SingleOrDefaultAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var effectiveSubscriptionStatus = PlanEntitlementRules.GetEffectiveSubscriptionStatus(
            subscription?.Status, subscription?.PeriodEndUtc ?? now, now);
        var accessActive = TenantAccessRules.IsActive(tenantStatus) &&
            subscription is not null &&
            PlanEntitlementRules.IsSubscriptionAccessActive(
                subscription.Status, subscription.PeriodEndUtc, now);

        var operationalSettings = await db.PlatformOperationalSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, cancellationToken);
        var trialMonthlyLimit = operationalSettings?.TrialMaxMonthlyPrintJobs ??
            PlatformOperationalSettings.DefaultTrialMaxMonthlyPrintJobs;
        var isTrialPlan = string.Equals(subscription?.PlanCode, "TRIAL", StringComparison.OrdinalIgnoreCase);

        // Legacy subscriptions have no immutable version reference. Preserve their existing access
        // only while their recorded Trial/Active period is still valid. The platform trial quota
        // remains enforceable even for legacy trial rows without a plan-version reference.
        if (subscription?.PlanVersionId is not Guid planVersionId)
        {
            return new TenantEntitlementSnapshot(
                tenantId, subscription?.PlanCode ?? "UNCONFIGURED",
                effectiveSubscriptionStatus, null, subscription?.PeriodEndUtc,
                null, null, null, isTrialPlan ? trialMonthlyLimit : null, null,
                accessActive, false, accessActive, true, accessActive);
        }

        var version = await db.SubscriptionPlanVersions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == planVersionId, cancellationToken);
        if (version is null)
        {
            throw new InvalidOperationException(
                $"Subscription {subscription.PlanCode} for tenant {tenantId} references a missing plan version.");
        }

        return new TenantEntitlementSnapshot(
            tenantId, subscription.PlanCode, effectiveSubscriptionStatus, version.Id, subscription.PeriodEndUtc,
            version.MaxPrinters, version.MaxAgentDevices, version.MaxQrStations,
            isTrialPlan ? trialMonthlyLimit : version.MaxMonthlyPrintJobs, version.MaxDocumentBytes,
            version.CloudAssistedPrintingEnabled && accessActive,
            version.WhiteLabelEnabled && accessActive,
            version.AdvancedReportsEnabled && accessActive,
            false,
            accessActive);
    }

    public async Task AcquireQuotaLockAsync(Guid tenantId, TenantQuotaResource resource, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A database transaction is required before acquiring a quota lock.");
        }

        var lockKey = $"{tenantId:N}:{resource}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(0, hashtext({lockKey}))", cancellationToken);
    }

    public async Task<TenantQuotaDecision> CheckCapacityAsync(
        Guid tenantId,
        TenantQuotaResource resource,
        CancellationToken cancellationToken,
        long requestedCount = 1)
    {
        var entitlement = await GetForTenantAsync(tenantId, cancellationToken);
        if (!entitlement.IsAccessActive)
        {
            return new TenantQuotaDecision(
                false,
                "subscription_inactive",
                "Your shop's SmartPrint subscription is inactive. Open Subscription in your shop dashboard to review plans and restore access.",
                0,
                0);
        }

        long? maximum = resource switch
        {
            TenantQuotaResource.Printer => entitlement.MaxPrinters,
            TenantQuotaResource.AgentDevice => entitlement.MaxAgentDevices,
            TenantQuotaResource.QrStation => entitlement.MaxQrStations,
            TenantQuotaResource.MonthlyPrintJob => entitlement.MaxMonthlyPrintJobs,
            _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, "Unknown tenant quota resource.")
        };

        if (maximum is null || requestedCount == 0)
        {
            return new TenantQuotaDecision(true, string.Empty, string.Empty, 0, maximum);
        }

        if (requestedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedCount));
        }

        var current = resource switch
        {
            TenantQuotaResource.Printer => await db.PrinterDevices.AsNoTracking()
                .LongCountAsync(item => item.TenantId == tenantId, cancellationToken),
            TenantQuotaResource.AgentDevice => await db.AgentDevices.AsNoTracking()
                .LongCountAsync(item => item.TenantId == tenantId && item.RevokedUtc == null && item.Status != "Revoked", cancellationToken),
            TenantQuotaResource.QrStation => await db.QrStations.AsNoTracking()
                .LongCountAsync(item => item.TenantId == tenantId, cancellationToken),
            TenantQuotaResource.MonthlyPrintJob => await CountMonthlyJobsAsync(tenantId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, "Unknown tenant quota resource.")
        };

        var allowed = PlanEntitlementRules.CanAdd(maximum, current, requestedCount);
        var (code, plural) = resource switch
        {
            TenantQuotaResource.Printer => ("printer_limit_reached", "printers"),
            TenantQuotaResource.AgentDevice => ("agent_device_limit_reached", "paired computers"),
            TenantQuotaResource.QrStation => ("qr_station_limit_reached", "QR stations"),
            TenantQuotaResource.MonthlyPrintJob => ("monthly_print_job_limit_reached", "monthly print jobs"),
            _ => ("resource_limit_reached", "resources")
        };

        return new TenantQuotaDecision(
            allowed,
            allowed ? string.Empty : code,
            allowed ? string.Empty : $"This shop has reached its plan limit of {maximum} {plural}.",
            current,
            maximum);
    }

    public Task<long> GetMonthlyPrintJobUsageAsync(Guid tenantId, CancellationToken cancellationToken)
        => CountMonthlyJobsAsync(tenantId, cancellationToken);

    private async Task<long> CountMonthlyJobsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var periodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = periodStart.AddMonths(1);
        var createdJobs = await db.PrintJobs.AsNoTracking().LongCountAsync(
            item => item.TenantId == tenantId &&
                    item.CreatedUtc >= periodStart &&
                    item.CreatedUtc < periodEnd,
            cancellationToken);
        var unmaterializedLocalReservations = await db.LocalPrintUploadTickets.AsNoTracking().LongCountAsync(
            item => item.TenantId == tenantId &&
                    item.CreatedUtc >= periodStart &&
                    item.CreatedUtc < periodEnd &&
                    item.CreatedUtc >= now.AddHours(-24) &&
                    item.CompletedUtc == null &&
                    !db.PrintJobs.Any(job => job.TenantId == item.TenantId && job.Id == item.PrintJobId) &&
                    !db.PrintBatchPayments.Any(payment =>
                        payment.TenantId == item.TenantId &&
                        payment.CustomerSessionId == item.CustomerSessionId &&
                        payment.BatchId == item.BatchId &&
                        payment.Status != "REJECTED"),
            cancellationToken);
        return checked(createdJobs + unmaterializedLocalReservations);
    }
}
