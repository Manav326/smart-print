using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopPrintDispatchFailure
{
    None,
    NotFound,
    JobNotReady,
    BatchPrinterMismatch,
    PrerequisitesNotMet,
    PdfExportNotRecoverable,
    PdfExportLocalCopyMissing,
    PdfExportPrerequisitesNotMet,
    PdfExportConfirmationNotAllowed,
    OutputNotReadyForConfirmation
}

public sealed record PrintDispatchPrinterSnapshot(
    string DisplayName,
    string? QueueName,
    string OutputMode,
    Guid AgentId,
    string AgentName);

public sealed record ShopPrintDispatchResult(
    ShopPrintDispatchFailure Failure,
    PrintJob? Job = null,
    PrintDispatchPrinterSnapshot? Printer = null,
    string? OutputDestinationName = null,
    bool DuplicateRequest = false);

public sealed class ShopPrintJobDispatchService(SmartPrintDbContext db, IHostEnvironment environment)
{
    public async Task<ShopPrintDispatchResult> DispatchAsync(
        Guid jobId,
        Guid tenantId,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var batchInfo = await db.PrintJobs.AsNoTracking()
            .Where(item => item.Id == jobId && item.TenantId == tenantId)
            .Select(item => new { item.BatchId })
            .SingleOrDefaultAsync(cancellationToken);
        if (batchInfo is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.NotFound);
        }

        List<PrintJob> batchJobs;
        if (batchInfo.BatchId is Guid batchId)
        {
            batchJobs = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
                .ToListAsync(cancellationToken);
        }
        else
        {
            batchJobs = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {jobId} FOR UPDATE")
                .ToListAsync(cancellationToken);
        }

        var job = batchJobs.SingleOrDefault(item => item.Id == jobId);
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.NotFound);
        }

        if (job.Status == "PRINT_AUTHORISED")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintDispatchResult(
                ShopPrintDispatchFailure.None, Job: job, DuplicateRequest: true);
        }

        if (job.Status != "LOCAL_QUEUE_READY" ||
            job.LocalQueueReceivedUtc is null ||
            job.PrinterDeviceId is null ||
            job.ClaimedByAgentDeviceId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.JobNotReady, Job: job);
        }

        if (batchJobs.Any(other =>
                other.Id != job.Id &&
                other.Status != "REJECTED" &&
                other.Status != "REFUNDED" &&
                other.PrinterDeviceId != job.PrinterDeviceId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.BatchPrinterMismatch, Job: job);
        }

        var now = DateTimeOffset.UtcNow;
        var onlineAfter = now.AddMinutes(-2);
        var ready = await (
            from printer in db.PrinterDevices.AsNoTracking()
            join agent in db.AgentDevices.AsNoTracking()
                on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
                equals new { agent.TenantId, AgentId = agent.Id }
            where printer.Id == job.PrinterDeviceId &&
                  printer.TenantId == tenantId &&
                  printer.AgentDeviceId == job.ClaimedByAgentDeviceId &&
                  printer.IsActive &&
                  printer.QueueName != null &&
                  printer.QueueName != "" &&
                  (printer.OutputMode == "PHYSICAL" || printer.OutputMode == "PDF_EXPORT") &&
                  ((printer.OutputMode == "PDF_EXPORT" && !job.PrivatePrinting) ||
                   (printer.LastSeenUtc != null &&
                    printer.LastSeenUtc >= onlineAfter &&
                    (printer.Status == "Ready" || printer.Status == "Busy"))) &&
                  agent.RevokedUtc == null &&
                  agent.LastSeenUtc != null &&
                  agent.LastSeenUtc >= onlineAfter
            select new PrintDispatchPrinterSnapshot(
                printer.DisplayName,
                printer.QueueName,
                printer.OutputMode,
                agent.Id,
                agent.DeviceName)
        ).SingleOrDefaultAsync(cancellationToken);
        var verified = await db.PrintPayments.AnyAsync(
            item => item.TenantId == tenantId &&
                    item.PrintJobId == job.Id &&
                    (item.Status == "VERIFIED" || item.Status == "PARTIALLY_REFUNDED"),
            cancellationToken);
        if (ready is null || !verified)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.PrerequisitesNotMet, Job: job);
        }

        var allowPrivateVirtualOutput = !environment.IsProduction() &&
            (await db.PlatformOperationalSettings.AsNoTracking()
                .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
                .Select(item => (bool?)item.AllowPrivatePrintingVirtualOutput)
                .SingleOrDefaultAsync(cancellationToken) ?? false);
        if (job.PrivatePrinting &&
            !PrivatePrintingOutputPolicy.IsEligible(
                allowPrivateVirtualOutput, environment.IsProduction(), ready.OutputMode))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintDispatchResult(
                ShopPrintDispatchFailure.PrerequisitesNotMet,
                Job: job);
        }

        var isPrivateTestOutput = job.PrivatePrinting && ready.OutputMode == "PDF_EXPORT";
        var previous = job.Status;
        job.Status = "PRINT_AUTHORISED";
        job.OutputType = isPrivateTestOutput
            ? PrivatePrintingOutputPolicy.TestVirtual
            : ready.OutputMode;
        job.PrinterNameSnapshot = ready.DisplayName;
        job.PrinterQueueNameSnapshot = ready.QueueName;
        job.PrinterAgentNameSnapshot = ready.AgentName;
        job.PrinterSnapshotUtc = now;
        job.SpoolerJobName = ready.OutputMode == "PDF_EXPORT"
            ? $"{(isPrivateTestOutput ? "PRIVATE-TEST-PDF" : "PDF-EXPORT")}-{job.JobNumber}-{job.Id:N}"
            : $"SmartPrint-{job.JobNumber}-{job.Id:N}";
        job.SpoolerJobId = null;
        job.SpoolerStatus = "AWAITING_AGENT";
        job.PrintDispatchLeaseExpiresUtc = null;
        job.FailureCode = null;
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previous,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = actorId,
            ReasonCode = isPrivateTestOutput
                ? "SHOP_EXPLICITLY_AUTHORISED_PRIVATE_TEST_VIRTUAL_OUTPUT"
                : ready.OutputMode == "PDF_EXPORT"
                    ? "SHOP_EXPLICITLY_AUTHORISED_PDF_EXPORT"
                    : "SHOP_EXPLICITLY_AUTHORISED_PHYSICAL_PRINT",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ShopPrintDispatchResult(
            ShopPrintDispatchFailure.None, Job: job, Printer: ready);
    }

    public async Task<ShopPrintDispatchResult> RetryPdfExportAsync(
        Guid jobId,
        Guid tenantId,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == tenantId,
            cancellationToken);
        if (job is null)
        {
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.NotFound);
        }

        var printerIsPdfExport = job.PrinterDeviceId is Guid assignedPrinterId &&
            await db.PrinterDevices.AnyAsync(printer =>
                printer.Id == assignedPrinterId &&
                printer.TenantId == tenantId &&
                printer.OutputMode == "PDF_EXPORT",
                cancellationToken);
        if (job.Status != "OUTPUT_UNCONFIRMED" ||
            (job.OutputType != "PDF_EXPORT" && !printerIsPdfExport))
        {
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.PdfExportNotRecoverable, Job: job);
        }

        if (job.PrinterDeviceId is not Guid printerId ||
            job.ClaimedByAgentDeviceId is not Guid agentId ||
            job.LocalQueueReceivedUtc is null)
        {
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.PdfExportLocalCopyMissing, Job: job);
        }

        var now = DateTimeOffset.UtcNow;
        var onlineAfter = now.AddMinutes(-2);
        var output = await (
            from printer in db.PrinterDevices.AsNoTracking()
            join agent in db.AgentDevices.AsNoTracking()
                on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
                equals new { agent.TenantId, AgentId = agent.Id }
            where printer.Id == printerId &&
                  printer.TenantId == tenantId &&
                  printer.AgentDeviceId == agentId &&
                  printer.OutputMode == "PDF_EXPORT" &&
                  printer.IsActive &&
                  agent.RevokedUtc == null &&
                  agent.LastSeenUtc != null &&
                  agent.LastSeenUtc >= onlineAfter
            select printer.DisplayName
        ).SingleOrDefaultAsync(cancellationToken);
        var verified = await db.PrintPayments.AnyAsync(
            payment => payment.TenantId == tenantId &&
                       payment.PrintJobId == job.Id &&
                       (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"),
            cancellationToken);
        if (output is null || !verified)
        {
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.PdfExportPrerequisitesNotMet, Job: job);
        }

        var previous = job.Status;
        job.Status = "LOCAL_QUEUE_READY";
        job.OutputType = "PDF_EXPORT";
        job.OutputFilePath = null;
        job.OutputFileName = null;
        job.OutputSha256 = null;
        job.OutputSizeBytes = null;
        job.OutputCompletedUtc = null;
        job.OutputConfirmedUtc = null;
        job.SpoolerJobId = null;
        job.SpoolerJobName = null;
        job.SpoolerSubmittedUtc = null;
        job.SpoolerObservedUtc = null;
        job.PrintDispatchLeaseExpiresUtc = null;
        job.FailureCode = null;
        job.SpoolerStatus = null;
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previous,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = actorId,
            ReasonCode = "SHOP_CONFIRMED_NO_PDF_FILE_AND_RETRY_REQUESTED",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new ShopPrintDispatchResult(
            ShopPrintDispatchFailure.None,
            Job: job,
            OutputDestinationName: output);
    }

    public async Task<ShopPrintDispatchResult> ConfirmOutputAsync(
        Guid jobId,
        Guid tenantId,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == tenantId,
            cancellationToken);
        if (job is null)
        {
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.NotFound);
        }

        if (job.OutputType == "PDF_EXPORT")
        {
            return new ShopPrintDispatchResult(ShopPrintDispatchFailure.PdfExportConfirmationNotAllowed, Job: job);
        }

        if (job.Status == "COMPLETED" && job.OutputConfirmedUtc is not null)
        {
            return new ShopPrintDispatchResult(
                ShopPrintDispatchFailure.None, Job: job, DuplicateRequest: true);
        }

        if (job.Status is not ("SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED"))
        {
            return new ShopPrintDispatchResult(
                ShopPrintDispatchFailure.OutputNotReadyForConfirmation, Job: job);
        }

        var now = DateTimeOffset.UtcNow;
        var previous = job.Status;
        job.Status = "COMPLETED";
        job.OutputConfirmedUtc = now;
        job.SpoolerObservedUtc = now;
        job.SpoolerStatus = "SHOP_CONFIRMED_OUTPUT";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previous,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = actorId,
            ReasonCode = "SHOP_OPERATOR_CONFIRMED_PHYSICAL_OUTPUT",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new ShopPrintDispatchResult(ShopPrintDispatchFailure.None, Job: job);
    }
}
