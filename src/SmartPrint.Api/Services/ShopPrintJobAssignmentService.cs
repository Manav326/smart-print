using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopPrintJobAssignmentFailure
{
    None,
    NotFound,
    JobNotAssignable,
    PrinterNotAvailable,
    Conflict,
    PrivatePrintingOutputRequired
}

public sealed record ShopPrintJobAssignmentResult(
    ShopPrintJobAssignmentFailure Failure,
    Guid JobId = default,
    Guid? BatchId = null,
    string Status = "",
    Guid PrinterDeviceId = default,
    string PrinterName = "",
    string? QueueName = null,
    Guid? AgentDeviceId = null,
    int AssignedJobCount = 0,
    DateTimeOffset UpdatedUtc = default,
    string? ConflictCode = null)
{
    public bool Succeeded => Failure == ShopPrintJobAssignmentFailure.None;
}

public sealed class ShopPrintJobAssignmentService(SmartPrintDbContext db, IHostEnvironment environment)
{
    public async Task<ShopPrintJobAssignmentResult> AssignAsync(
        Guid tenantId,
        Guid? actorId,
        Guid jobId,
        Guid printerDeviceId,
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
            return new ShopPrintJobAssignmentResult(ShopPrintJobAssignmentFailure.NotFound);
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

        var selectedJob = batchJobs.SingleOrDefault(item => item.Id == jobId);
        if (selectedJob is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobAssignmentResult(ShopPrintJobAssignmentFailure.NotFound);
        }

        var now = DateTimeOffset.UtcNow;
        var hasVerifiedPayment = await db.PrintPayments.AnyAsync(
            payment => payment.TenantId == tenantId &&
                       payment.PrintJobId == selectedJob.Id &&
                       (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"),
            cancellationToken);
        var expiredAgentLease = selectedJob.Status == "LOCAL_QUEUE_CLAIMED" &&
            (selectedJob.AgentLeaseExpiresUtc == null || selectedJob.AgentLeaseExpiresUtc <= now);
        var readyQueueCanBeReassigned = selectedJob.Status == "LOCAL_QUEUE_READY" &&
            selectedJob.LocalQueueReceivedUtc != null &&
            selectedJob.SpoolerJobId == null &&
            string.IsNullOrWhiteSpace(selectedJob.SpoolerJobName) &&
            selectedJob.OutputConfirmedUtc == null;
        var canAssign = hasVerifiedPayment &&
            (selectedJob.Status == "PAID_AUTHORISED" || expiredAgentLease || readyQueueCanBeReassigned);
        if (!canAssign)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobAssignmentResult(ShopPrintJobAssignmentFailure.JobNotAssignable);
        }

        var onlineAfter = now.AddMinutes(-2);
        var printer = await db.PrinterDevices.AsNoTracking()
            .Where(item => item.Id == printerDeviceId &&
                           item.TenantId == tenantId &&
                           item.IsActive &&
                           item.AgentDeviceId != null &&
                           (item.OutputMode == "PHYSICAL" || item.OutputMode == "PDF_EXPORT") &&
                           ((item.OutputMode == "PDF_EXPORT" && !selectedJob.PrivatePrinting) ||
                            (item.LastSeenUtc != null && item.LastSeenUtc >= onlineAfter &&
                             (item.Status == "Ready" || item.Status == "Busy"))) &&
                           db.AgentDevices.Any(agent =>
                               agent.Id == item.AgentDeviceId &&
                               agent.TenantId == tenantId &&
                               agent.RevokedUtc == null &&
                               agent.LastSeenUtc != null &&
                               agent.LastSeenUtc >= onlineAfter))
            .Select(item => new
            {
                item.Id,
                item.DisplayName,
                item.QueueName,
                item.AgentDeviceId,
                item.OutputMode
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (printer is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobAssignmentResult(ShopPrintJobAssignmentFailure.PrinterNotAvailable);
        }

        // Production always denies virtual output, even if a stale or mistakenly
        // enabled operational setting exists in the database.
        var allowPrivateVirtualOutput = !environment.IsProduction() &&
            (await db.PlatformOperationalSettings.AsNoTracking()
                .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
                .Select(item => (bool?)item.AllowPrivatePrintingVirtualOutput)
                .SingleOrDefaultAsync(cancellationToken) ?? false);
        if (selectedJob.PrivatePrinting &&
            !PrivatePrintingOutputPolicy.IsEligible(
                allowPrivateVirtualOutput, environment.IsProduction(), printer.OutputMode))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobAssignmentResult(
                ShopPrintJobAssignmentFailure.PrivatePrintingOutputRequired);
        }

        var activeJobs = batchJobs
            .Where(item => item.Status is not ("REJECTED" or "REFUNDED"))
            .ToList();
        var activeJobIds = activeJobs.Select(item => item.Id).ToArray();
        var documents = await db.PrintDocuments.AsNoTracking()
            .Where(item => item.TenantId == tenantId && activeJobIds.Contains(item.PrintJobId))
            .Select(item => new { item.PrintJobId, item.StorageMode, item.AgentDeviceId })
            .ToListAsync(cancellationToken);
        var documentsByJob = documents.ToDictionary(item => item.PrintJobId);
        var states = activeJobs.Select(item =>
        {
            documentsByJob.TryGetValue(item.Id, out var document);
            var outputLocked = item.Status is "PRINT_AUTHORISED" or "PRINT_SUBMITTING" or
                "SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED" or "COMPLETED";
            var activeAgentLease = item.Status == "LOCAL_QUEUE_CLAIMED" &&
                item.AgentLeaseExpiresUtc is DateTimeOffset lease && lease > now;
            return new BatchPrinterAssignmentState(
                item.PrinterDeviceId,
                document?.AgentDeviceId,
                document?.StorageMode == "LOCAL_AGENT",
                outputLocked,
                activeAgentLease);
        }).ToArray();

        var assignmentConflict = BatchPrinterAssignmentRules.FindConflict(
            states, printer.Id, printer.AgentDeviceId!.Value);
        if (assignmentConflict is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobAssignmentResult(
                ShopPrintJobAssignmentFailure.Conflict,
                ConflictCode: assignmentConflict);
        }

        var assigned = 0;
        foreach (var batchJob in activeJobs)
        {
            var needsQueueReset =
                (batchJob.Status == "LOCAL_QUEUE_READY" && batchJob.PrinterDeviceId != printer.Id) ||
                (batchJob.Status == "LOCAL_QUEUE_CLAIMED" &&
                 (batchJob.AgentLeaseExpiresUtc == null || batchJob.AgentLeaseExpiresUtc <= now));
            var printerChanged = batchJob.PrinterDeviceId != printer.Id;
            if (!printerChanged && !needsQueueReset)
            {
                assigned++;
                continue;
            }

            var previousStatus = batchJob.Status;
            batchJob.PrinterDeviceId = printer.Id;
            if (needsQueueReset)
            {
                batchJob.Status = "PAID_AUTHORISED";
                batchJob.ClaimedByAgentDeviceId = null;
                batchJob.AgentLeaseExpiresUtc = null;
                batchJob.AgentClaimedUtc = null;
                batchJob.LocalQueueReceivedUtc = null;
                batchJob.FailureCode = null;
                batchJob.SpoolerStatus = null;
            }

            batchJob.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = batchJob.Id,
                PreviousStatus = previousStatus,
                NewStatus = batchJob.Status,
                ActorType = "ShopUser",
                ActorId = actorId,
                ReasonCode = selectedJob.BatchId is null ? "PRINTER_ASSIGNED" : "BATCH_PRINTER_ASSIGNED",
                CreatedUtc = now
            });
            assigned++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ShopPrintJobAssignmentResult(
            ShopPrintJobAssignmentFailure.None,
            JobId: selectedJob.Id,
            BatchId: selectedJob.BatchId,
            Status: selectedJob.Status,
            PrinterDeviceId: printer.Id,
            PrinterName: printer.DisplayName,
            QueueName: printer.QueueName,
            AgentDeviceId: printer.AgentDeviceId,
            AssignedJobCount: assigned,
            UpdatedUtc: now);
    }
}
