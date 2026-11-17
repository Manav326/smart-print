using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public sealed class DataTransferRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<DataTransferRetentionWorker> logger) : BackgroundService
{
    private const int BatchSize = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepSafelyAsync(stoppingToken);
        }
    }

    private async Task SweepSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var aggregateUpserts = await SweepOnceAsync(cancellationToken);
            if (aggregateUpserts > 0)
            {
                logger.LogInformation(
                    "Rolled {AggregateUpsertCount} durable daily aggregate upserts for expired transfer events.",
                    aggregateUpserts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception exception)
        {
            // Retention is operational maintenance. A failed sweep must not stop the API
            // or interfere with a customer upload; the next scheduled run retries it.
            logger.LogError(exception, "Data-transfer retention/rollup sweep failed; raw events were left available for a later retry.");
        }
    }

    public async Task<long> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
        var configuredDays = await db.PlatformOperationalSettings.AsNoTracking()
            .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
            .Select(item => (int?)item.DataTransferRawRetentionDays)
            .SingleOrDefaultAsync(cancellationToken);
        var retentionDays = Math.Clamp(configuredDays ?? 90, 30, 3650);
        var now = DateTimeOffset.UtcNow;
        var staleAttemptCutoffUtc = now.AddMinutes(-30);
        await db.DataTransferEvents
            .Where(item => item.Status == "STARTED" && item.CreatedUtc < staleAttemptCutoffUtc)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, "FAILED")
                .SetProperty(item => item.MeasurementSource, "TRANSFER_ATTEMPT_TIMED_OUT"), cancellationToken);

        var cutoffUtc = now.AddDays(-retentionDays);
        long aggregateUpserts = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // DELETE ... RETURNING and the aggregate upsert are one PostgreSQL statement.
            // If the insert fails or the process stops, PostgreSQL rolls back the delete too.
            // SKIP LOCKED allows another API replica to process a different batch safely.
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH batch AS MATERIALIZED (
                    SELECT "Id"
                    FROM "DataTransferEvents"
                    WHERE "CreatedUtc" < {cutoffUtc}
                    ORDER BY "CreatedUtc"
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED
                ),
                deleted AS (
                    DELETE FROM "DataTransferEvents" AS transfer
                    USING batch
                    WHERE transfer."Id" = batch."Id"
                    RETURNING
                        (transfer."CreatedUtc" AT TIME ZONE 'UTC')::date AS "UtcDate",
                        transfer."TenantId",
                        transfer."Channel",
                        transfer."Direction",
                        transfer."Status",
                        transfer."PayloadBytes",
                        transfer."NetworkBytes",
                        transfer."DurationMilliseconds"
                ),
                grouped AS (
                    SELECT
                        "UtcDate",
                        "TenantId",
                        "Channel",
                        "Direction",
                        "Status",
                        COUNT(*)::bigint AS "TransferCount",
                        COALESCE(SUM("PayloadBytes"), 0)::bigint AS "PayloadBytes",
                        SUM("NetworkBytes")::bigint AS "NetworkBytes",
                        COALESCE(SUM("DurationMilliseconds"), 0)::bigint AS "TotalDurationMilliseconds",
                        COUNT(*) FILTER (WHERE "DurationMilliseconds" > 0)::bigint AS "DurationSampleCount"
                    FROM deleted
                    GROUP BY "UtcDate", "TenantId", "Channel", "Direction", "Status"
                )
                INSERT INTO "DataTransferDailyAggregates" (
                    "UtcDate", "TenantId", "Channel", "Direction", "Status",
                    "TransferCount", "PayloadBytes", "NetworkBytes",
                    "TotalDurationMilliseconds", "DurationSampleCount"
                )
                SELECT
                    "UtcDate", "TenantId", "Channel", "Direction", "Status",
                    "TransferCount", "PayloadBytes", "NetworkBytes",
                    "TotalDurationMilliseconds", "DurationSampleCount"
                FROM grouped
                ON CONFLICT ("UtcDate", "TenantId", "Channel", "Direction", "Status")
                DO UPDATE SET
                    "TransferCount" = "DataTransferDailyAggregates"."TransferCount" + EXCLUDED."TransferCount",
                    "PayloadBytes" = "DataTransferDailyAggregates"."PayloadBytes" + EXCLUDED."PayloadBytes",
                    "NetworkBytes" = CASE
                        WHEN "DataTransferDailyAggregates"."NetworkBytes" IS NULL AND EXCLUDED."NetworkBytes" IS NULL
                            THEN NULL
                        ELSE COALESCE("DataTransferDailyAggregates"."NetworkBytes", 0) + COALESCE(EXCLUDED."NetworkBytes", 0)
                    END,
                    "TotalDurationMilliseconds" = "DataTransferDailyAggregates"."TotalDurationMilliseconds" + EXCLUDED."TotalDurationMilliseconds",
                    "DurationSampleCount" = "DataTransferDailyAggregates"."DurationSampleCount" + EXCLUDED."DurationSampleCount";
                """, cancellationToken);

            if (affected <= 0)
                break;

            aggregateUpserts += affected;
        }

        return aggregateUpserts;
    }
}
