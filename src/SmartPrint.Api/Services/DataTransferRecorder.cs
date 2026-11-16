using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public static class DataTransferRecorder
{
    private static long successfulWriteCount;
    private static long failedWriteCount;
    private static long lastFailureUnixMilliseconds;

    public static DateTimeOffset ProcessStartedUtc { get; } = DateTimeOffset.UtcNow;
    public static long SuccessfulWriteCount => Interlocked.Read(ref successfulWriteCount);
    public static long FailedWriteCount => Interlocked.Read(ref failedWriteCount);
    public static DateTimeOffset? LastFailureUtc
    {
        get
        {
            var value = Interlocked.Read(ref lastFailureUnixMilliseconds);
            return value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }

    private static void MarkWriteSuccess() => Interlocked.Increment(ref successfulWriteCount);

    private static void MarkWriteFailure()
    {
        Interlocked.Increment(ref failedWriteCount);
        Interlocked.Exchange(ref lastFailureUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public static async Task TryRecordAsync(
        SmartPrintDbContext db,
        ILogger logger,
        DataTransferEvent transferEvent)
    {
        try
        {
            db.DataTransferEvents.Add(transferEvent);
            await db.SaveChangesAsync(CancellationToken.None);
            MarkWriteSuccess();
        }
        catch (Exception exception)
        {
            db.Entry(transferEvent).State = EntityState.Detached;
            MarkWriteFailure();
            logger.LogWarning(
                exception,
                "Data-transfer telemetry could not be recorded for channel {Channel}; the customer operation remains successful.",
                transferEvent.Channel);
        }
    }

    public static async Task TryCompleteAttemptAsync(
        SmartPrintDbContext db,
        ILogger logger,
        DataTransferEvent completedAttempt)
    {
        DataTransferEvent? existing = null;
        try
        {
            existing = await db.DataTransferEvents.SingleOrDefaultAsync(
                item => item.TransferId == completedAttempt.TransferId &&
                        item.AttemptNumber == completedAttempt.AttemptNumber,
                CancellationToken.None);

            if (existing is null)
            {
                db.DataTransferEvents.Add(completedAttempt);
            }
            else
            {
                existing.AgentDeviceId = completedAttempt.AgentDeviceId;
                existing.PrinterDeviceId = completedAttempt.PrinterDeviceId;
                existing.QrStationId = completedAttempt.QrStationId;
                existing.PrintJobId = completedAttempt.PrintJobId;
                existing.PrintDocumentId = completedAttempt.PrintDocumentId;
                existing.Status = completedAttempt.Status;
                existing.PayloadBytes = completedAttempt.PayloadBytes;
                existing.NetworkBytes = completedAttempt.NetworkBytes;
                existing.DurationMilliseconds = completedAttempt.DurationMilliseconds;
                existing.MeasurementSource = completedAttempt.MeasurementSource;
                // Keep the original CreatedUtc so the transfer remains attributed
                // to its start time and cannot jump reporting windows after a retry.
            }

            await db.SaveChangesAsync(CancellationToken.None);
            MarkWriteSuccess();
        }
        catch (Exception exception)
        {
            MarkWriteFailure();
            if (existing is not null)
                db.Entry(existing).State = EntityState.Unchanged;
            else
                db.Entry(completedAttempt).State = EntityState.Detached;

            logger.LogWarning(
                exception,
                "Data-transfer attempt completion could not be recorded for channel {Channel}; the customer operation remains successful.",
                completedAttempt.Channel);
        }
    }
}
