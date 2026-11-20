using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Modules.ShopWorkspace.Endpoints;

/// <summary>
/// Read-only shop workspace queries for subscription entitlement, shop profile,
/// printer and QR-station inventories, and the effective pricing snapshot.
/// </summary>
public static class ShopWorkspaceQueryEndpoints
{
    public static RouteGroupBuilder MapShopWorkspaceQueries(this RouteGroupBuilder shop)
    {
        shop.MapGet("/subscription", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var entitlement = await entitlements.GetForTenantAsync(tenantId, ct);
            var subscription = entitlement.SubscriptionId is Guid subscriptionId
                ? await db.TenantSubscriptions.AsNoTracking()
                    .Where(item => item.Id == subscriptionId && item.TenantId == tenantId)
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
                    .SingleOrDefaultAsync(ct)
                : null;
            if (subscription is null)
            {
                return Results.NotFound();
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
        
            return Results.Ok(new
            {
                generatedUtc = now,
                subscription = new
                {
                    subscription.Id,
                    subscription.PlanCode,
                    status = entitlement.SubscriptionStatus,
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
            });
        });

        

        shop.MapGet("/printers", async (ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var onlineAfter = DateTimeOffset.UtcNow.AddMinutes(-2);
            var printers = await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.TenantId == tenantId)
                .OrderBy(printer => printer.DisplayName)
                .Select(printer => new
                {
                    printer.Id,
                    printer.DisplayName,
                    printer.Manufacturer,
                    printer.Model,
                    printer.ConnectionType,
                    printer.OutputMode,
                    printer.ConnectionAddress,
                    printer.QueueName,
                    printer.IsDefault,
                    printer.AgentDeviceId,
                    printer.CapabilitiesJson,
                    Status = printer.Status == "AgentRevoked"
                        ? "AgentRevoked"
                        : printer.AgentDeviceId != null &&
                          !db.AgentDevices.AsNoTracking().Any(agent =>
                              agent.Id == printer.AgentDeviceId && agent.TenantId == tenantId &&
                              agent.RevokedUtc == null && agent.Status != "Revoked" &&
                              agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter)
                            ? "AgentOffline"
                            : printer.AgentDeviceId != null && printer.OutputMode == "PDF_EXPORT"
                                ? "Ready"
                                : printer.AgentDeviceId != null &&
                                  (printer.LastSeenUtc == null || printer.LastSeenUtc < onlineAfter)
                                    ? "InventoryStale"
                                    : printer.Status,
                    printer.IsActive,
                    printer.LastSeenUtc
                })
                .ToListAsync(ct);
            return Results.Ok(printers);
        });

        shop.MapGet("/qr-stations", async (ClaimsPrincipal principal, SmartPrintDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var stations = await db.QrStations.AsNoTracking()
                .Where(station => station.TenantId == tenantId)
                .OrderBy(station => station.DisplayName)
                .Select(station => new
                {
                    station.Id,
                    station.PrinterDeviceId,
                    station.DisplayName,
                    station.PublicCodeHint,
                    station.PublicCode,
                    station.IsEnabled,
                    station.CreatedUtc
                })
                .ToListAsync(ct);
            var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            return Results.Ok(stations.Select(station => new
            {
                station.Id,
                station.PrinterDeviceId,
                station.DisplayName,
                station.PublicCodeHint,
                station.IsEnabled,
                station.CreatedUtc,
                PublicUrl = station.PublicCode is not null ? $"{portalBase}/s/{station.PublicCode}" : null
            }));
        });

        shop.MapGet("/pricing", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var pricing = await db.ShopPricingProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
            return pricing is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    pricing.IsConfigured,
                    pricing.MonoPerSideMinor,
                    pricing.ColourPerSideMinor,
                    pricing.MonoDuplexPerSheetMinor,
                    pricing.ColourDuplexPerSheetMinor,
                    pricing.MinimumChargeMinor,
                    pricing.Photo35x45Minor,
                    pricing.Photo25x35Minor,
                    pricing.Photo2x2Minor,
                    pricing.Photo4x6Minor,
                    pricing.IdDocumentPriceMinor,
                    pricing.Version,
                    pricing.UpdatedUtc
                });
        });
        return shop;
    }
}
