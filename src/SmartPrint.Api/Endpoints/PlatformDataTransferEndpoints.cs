using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class PlatformDataTransferEndpoints
{
    private const int AgentStorageFreshnessMinutes = 15;

    private sealed record TransferSummary(
        string Channel,
        string Direction,
        string Status,
        long TransferCount,
        long PayloadBytes,
        long? NetworkBytes,
        long TotalDurationMilliseconds,
        long DurationSampleCount);

    private sealed record ShopTransferSummary(
        Guid TenantId,
        long TransferCount,
        long PayloadBytes,
        long CloudIngressBytes,
        long CloudEgressBytes,
        long ShopLanBytes,
        long PartialTransferCount,
        long FailedTransferCount,
        long StartedTransferCount);

    public static RouteGroupBuilder MapPlatformDataTransferEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/data-network", async (
            int? days,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var windowDays = days ?? 30;
            if (windowDays is not (7 or 30 or 90))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["days"] = ["Choose a reporting window of 7, 30 or 90 days."]
                });
            }

            var generatedUtc = DateTimeOffset.UtcNow;
            var requestedFromUtc = generatedUtc.AddDays(-windowDays);
            var settings = await db.PlatformOperationalSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, ct);
            var measurementStartedUtc = settings?.DataTransferMeasurementStartedUtc ?? generatedUtc;
            // Daily aggregates cannot be split at an arbitrary clock time. Align a requested
            // window to UTC midnight, but never report earlier than telemetry collection began.
            var requestedWindowStartUtc = new DateTimeOffset(
                new DateTime(
                    requestedFromUtc.UtcDateTime.Year,
                    requestedFromUtc.UtcDateTime.Month,
                    requestedFromUtc.UtcDateTime.Day,
                    0, 0, 0,
                    DateTimeKind.Utc));
            var effectiveFromUtc = measurementStartedUtc > requestedWindowStartUtc
                ? measurementStartedUtc
                : requestedWindowStartUtc;
            var rawRetentionDays = Math.Clamp(settings?.DataTransferRawRetentionDays ?? 90, 30, 3650);
            var storageFreshAfterUtc = generatedUtc.AddMinutes(-AgentStorageFreshnessMinutes);

            var events = db.DataTransferEvents.AsNoTracking()
                .Where(item => item.CreatedUtc >= effectiveFromUtc && item.CreatedUtc < generatedUtc);
            var aggregateStartDate = DateOnly.FromDateTime(effectiveFromUtc.UtcDateTime);
            var aggregateEndDate = DateOnly.FromDateTime(generatedUtc.UtcDateTime);
            var aggregates = db.DataTransferDailyAggregates.AsNoTracking()
                .Where(item => item.UtcDate >= aggregateStartDate && item.UtcDate <= aggregateEndDate);

            var rawChannels = await events
                .GroupBy(item => new { item.Channel, item.Direction, item.Status })
                .Select(group => new TransferSummary(
                    group.Key.Channel,
                    group.Key.Direction,
                    group.Key.Status,
                    group.LongCount(),
                    group.Sum(item => item.PayloadBytes),
                    group.Sum(item => item.NetworkBytes),
                    group.Sum(item => item.DurationMilliseconds),
                    group.LongCount(item => item.DurationMilliseconds > 0)))
                .ToListAsync(ct);

            var aggregateChannels = await aggregates
                .GroupBy(item => new { item.Channel, item.Direction, item.Status })
                .Select(group => new TransferSummary(
                    group.Key.Channel,
                    group.Key.Direction,
                    group.Key.Status,
                    group.Sum(item => item.TransferCount),
                    group.Sum(item => item.PayloadBytes),
                    group.Sum(item => item.NetworkBytes),
                    group.Sum(item => item.TotalDurationMilliseconds),
                    group.Sum(item => item.DurationSampleCount)))
                .ToListAsync(ct);

            var channels = rawChannels.Concat(aggregateChannels)
                .GroupBy(item => new { item.Channel, item.Direction, item.Status })
                .Select(group => new
                {
                    channel = group.Key.Channel,
                    direction = group.Key.Direction,
                    status = group.Key.Status,
                    transferCount = group.Sum(item => item.TransferCount),
                    payloadBytes = group.Sum(item => item.PayloadBytes),
                    measuredNetworkBytes = group.Sum(item => item.NetworkBytes),
                    totalDurationMilliseconds = group.Sum(item => item.TotalDurationMilliseconds),
                    durationSampleCount = group.Sum(item => item.DurationSampleCount)
                })
                .OrderBy(item => item.channel)
                .ThenBy(item => item.status)
                .ToList();

            var rawShopTransfers = await events
                .GroupBy(item => item.TenantId)
                .Select(group => new ShopTransferSummary(
                    group.Key,
                    group.LongCount(),
                    group.Sum(item => item.PayloadBytes),
                    group.Where(item => item.Channel == "CLOUD_CUSTOMER_UPLOAD").Sum(item => (long?)item.PayloadBytes) ?? 0L,
                    group.Where(item => item.Channel == "CLOUD_AGENT_DOWNLOAD").Sum(item => (long?)item.PayloadBytes) ?? 0L,
                    group.Where(item => item.Channel == "SHOP_LAN_CUSTOMER_UPLOAD").Sum(item => (long?)item.PayloadBytes) ?? 0L,
                    group.LongCount(item => item.Status == "PARTIAL"),
                    group.LongCount(item => item.Status == "FAILED"),
                    group.LongCount(item => item.Status == "STARTED")))
                .ToListAsync(ct);

            var aggregateShopTransfers = await aggregates
                .GroupBy(item => item.TenantId)
                .Select(group => new ShopTransferSummary(
                    group.Key,
                    group.Sum(item => item.TransferCount),
                    group.Sum(item => item.PayloadBytes),
                    group.Where(item => item.Channel == "CLOUD_CUSTOMER_UPLOAD").Sum(item => (long?)item.PayloadBytes) ?? 0L,
                    group.Where(item => item.Channel == "CLOUD_AGENT_DOWNLOAD").Sum(item => (long?)item.PayloadBytes) ?? 0L,
                    group.Where(item => item.Channel == "SHOP_LAN_CUSTOMER_UPLOAD").Sum(item => (long?)item.PayloadBytes) ?? 0L,
                    group.Sum(item => item.Status == "PARTIAL" ? item.TransferCount : 0L),
                    group.Sum(item => item.Status == "FAILED" ? item.TransferCount : 0L),
                    group.Sum(item => item.Status == "STARTED" ? item.TransferCount : 0L)))
                .ToListAsync(ct);

            var transferByTenant = rawShopTransfers.Concat(aggregateShopTransfers)
                .GroupBy(item => item.TenantId)
                .ToDictionary(
                    group => group.Key,
                    group => new ShopTransferSummary(
                        group.Key,
                        group.Sum(item => item.TransferCount),
                        group.Sum(item => item.PayloadBytes),
                        group.Sum(item => item.CloudIngressBytes),
                        group.Sum(item => item.CloudEgressBytes),
                        group.Sum(item => item.ShopLanBytes),
                        group.Sum(item => item.PartialTransferCount),
                        group.Sum(item => item.FailedTransferCount),
                        group.Sum(item => item.StartedTransferCount)));

            var documentWindow = db.PrintDocuments.AsNoTracking()
                .Where(item => item.CreatedUtc >= effectiveFromUtc && item.CreatedUtc < generatedUtc);
            var uniqueDocumentCount = await documentWindow.LongCountAsync(ct);
            var uniqueDocumentBytes = await documentWindow.SumAsync(item => (long?)item.SizeBytes, ct) ?? 0L;

            var cloudStorage = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.StorageMode == "CLOUD" && item.PurgedUtc == null)
                .GroupBy(item => item.StorageMode)
                .Select(group => new
                {
                    documentCount = group.LongCount(),
                    logicalBytes = group.Sum(item => item.SizeBytes)
                })
                .SingleOrDefaultAsync(ct);

            var agentSnapshots = await db.AgentDevices.AsNoTracking()
                .Where(item => item.RevokedUtc == null)
                .Select(item => new
                {
                    item.Id,
                    item.TenantId,
                    item.LocalQueueDocumentCount,
                    item.LocalQueueLogicalBytes,
                    item.LocalQueueDiskBytes,
                    item.LocalOutputFileCount,
                    item.LocalOutputFileBytes,
                    item.LocalStorageMeasuredUtc
                })
                .ToListAsync(ct);

            var freshAgentSnapshots = agentSnapshots
                .Where(item => item.LocalStorageMeasuredUtc >= storageFreshAfterUtc)
                .ToList();
            var staleAgentSnapshotCount = agentSnapshots.LongCount(item =>
                item.LocalStorageMeasuredUtc != null && item.LocalStorageMeasuredUtc < storageFreshAfterUtc);
            var missingAgentSnapshotCount = agentSnapshots.LongCount(item => item.LocalStorageMeasuredUtc == null);

            var agentStorageByTenant = agentSnapshots
                .GroupBy(item => item.TenantId)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var fresh = group.Where(item => item.LocalStorageMeasuredUtc >= storageFreshAfterUtc).ToList();
                        return new
                        {
                            totalAgentCount = group.LongCount(),
                            freshAgentCount = (long)fresh.Count,
                            staleAgentCount = group.LongCount(item =>
                                item.LocalStorageMeasuredUtc != null && item.LocalStorageMeasuredUtc < storageFreshAfterUtc),
                            missingAgentSnapshotCount = group.LongCount(item => item.LocalStorageMeasuredUtc == null),
                            localQueueDocumentCount = fresh.Sum(item => item.LocalQueueDocumentCount ?? 0L),
                            localQueueLogicalBytes = fresh.Sum(item => item.LocalQueueLogicalBytes ?? 0L),
                            localQueueDiskBytes = fresh.Sum(item => item.LocalQueueDiskBytes ?? 0L),
                            localOutputFileCount = fresh.Sum(item => item.LocalOutputFileCount ?? 0L),
                            localOutputFileBytes = fresh.Sum(item => item.LocalOutputFileBytes ?? 0L)
                        };
                    });

            var tenantRows = await db.Tenants.AsNoTracking()
                .Select(item => new { item.Id, item.BusinessName, item.Status })
                .ToListAsync(ct);

            var shops = tenantRows
                .Select(tenant =>
                {
                    var transfer = transferByTenant.GetValueOrDefault(tenant.Id);
                    var local = agentStorageByTenant.GetValueOrDefault(tenant.Id);
                    return new
                    {
                        tenantId = tenant.Id,
                        shopName = tenant.BusinessName,
                        shopStatus = tenant.Status,
                        transferCount = transfer?.TransferCount ?? 0L,
                        measuredPayloadBytes = transfer?.PayloadBytes ?? 0L,
                        cloudIngressBytes = transfer?.CloudIngressBytes ?? 0L,
                        cloudEgressBytes = transfer?.CloudEgressBytes ?? 0L,
                        shopLanBytes = transfer?.ShopLanBytes ?? 0L,
                        partialTransferCount = transfer?.PartialTransferCount ?? 0L,
                        failedTransferCount = transfer?.FailedTransferCount ?? 0L,
                        startedTransferCount = transfer?.StartedTransferCount ?? 0L,
                        agentCount = local?.totalAgentCount ?? 0L,
                        freshAgentCount = local?.freshAgentCount ?? 0L,
                        staleAgentCount = local?.staleAgentCount ?? 0L,
                        missingAgentSnapshotCount = local?.missingAgentSnapshotCount ?? 0L,
                        localQueueDiskBytes = local?.localQueueDiskBytes ?? 0L,
                        localOutputFileBytes = local?.localOutputFileBytes ?? 0L
                    };
                })
                .OrderByDescending(item => item.measuredPayloadBytes)
                .ThenBy(item => item.shopName)
                .Take(100)
                .ToList();

            long SumFor(string channel) => channels
                .Where(item => item.channel == channel)
                .Sum(item => item.payloadBytes);
            long SumCountForStatus(string status) => channels
                .Where(item => item.status == status)
                .Sum(item => item.transferCount);

            var freshStorageComplete = freshAgentSnapshots.Count == agentSnapshots.Count;
            return Results.Ok(new
            {
                generatedUtc,
                requestedFromUtc,
                fromUtc = effectiveFromUtc,
                measurementStartedUtc,
                coverageComplete = measurementStartedUtc <= requestedFromUtc,
                toUtc = generatedUtc,
                days = windowDays,
                measurementCoverageDays = Math.Max(0, (generatedUtc - effectiveFromUtc).TotalDays),
                telemetryHealth = new
                {
                    scope = "CURRENT_API_PROCESS",
                    processStartedUtc = DataTransferRecorder.ProcessStartedUtc,
                    successfulWriteCount = DataTransferRecorder.SuccessfulWriteCount,
                    failedWriteCount = DataTransferRecorder.FailedWriteCount,
                    lastFailureUtc = DataTransferRecorder.LastFailureUtc
                },
                summary = new
                {
                    uniqueDocumentCount,
                    uniqueDocumentBytes,
                    cloudIngressBytes = SumFor("CLOUD_CUSTOMER_UPLOAD"),
                    cloudEgressBytes = SumFor("CLOUD_AGENT_DOWNLOAD"),
                    shopLanBytes = SumFor("SHOP_LAN_CUSTOMER_UPLOAD"),
                    measuredPayloadBytes = channels.Sum(item => item.payloadBytes),
                    transferAttemptCount = channels.Sum(item => item.transferCount),
                    partialTransferCount = SumCountForStatus("PARTIAL"),
                    failedTransferCount = SumCountForStatus("FAILED"),
                    startedTransferCount = SumCountForStatus("STARTED"),
                    wireBytesMeasured = false
                },
                channels,
                shops,
                storage = new
                {
                    cloudLogicalDocumentCount = cloudStorage?.documentCount ?? 0,
                    cloudLogicalDocumentBytes = cloudStorage?.logicalBytes ?? 0,
                    agentCount = agentSnapshots.Count,
                    freshAgentCount = freshAgentSnapshots.Count,
                    staleAgentCount = staleAgentSnapshotCount,
                    missingAgentSnapshotCount,
                    localQueueDocumentCount = freshAgentSnapshots.Sum(item => item.LocalQueueDocumentCount ?? 0L),
                    localQueueLogicalBytes = freshAgentSnapshots.Sum(item => item.LocalQueueLogicalBytes ?? 0L),
                    localQueueDiskBytes = freshAgentSnapshots.Sum(item => item.LocalQueueDiskBytes ?? 0L),
                    localOutputFileCount = freshAgentSnapshots.Sum(item => item.LocalOutputFileCount ?? 0L),
                    localOutputFileBytes = freshAgentSnapshots.Sum(item => item.LocalOutputFileBytes ?? 0L),
                    snapshotFreshnessMinutes = AgentStorageFreshnessMinutes,
                    storageMeasurementComplete = freshStorageComplete,
                    physicalDiskBytesMeasured = false
                },
                retention = new
                {
                    rawEventRetentionDays = rawRetentionDays,
                    dailyAggregatesRetained = true
                },
                measurementScope = new
                {
                    payloadBytesOnly = true,
                    wireBytesMeasured = false,
                    unmeasuredPaths = new[]
                    {
                        "HTTP headers, TLS framing and multipart boundaries",
                        "Agent heartbeat, job polling and other control-plane API traffic",
                        "Portal assets, previews, public release downloads and backups",
                        "Shop LAN retransmissions that disconnect before a measured Agent receive completes",
                        "Windows printer spool and rasterised output bytes"
                    }
                }
            });
        });

        return platform;
    }
}
