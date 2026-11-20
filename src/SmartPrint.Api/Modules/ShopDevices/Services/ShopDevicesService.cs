using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.ShopDevices.Contracts;
using SmartPrint.Api.Modules.ShopDevices.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Modules.ShopDevices.Services;

/// <summary>
/// Owns shop printer and QR-station lifecycle workflows, including quota locks,
/// transactions, tenant scoping and public-code persistence.
/// </summary>
public sealed class ShopDevicesService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    IConfiguration configuration)
{
    public async Task<ShopDeviceOperationResult<PrinterCreatedResponse>> CreatePrinterAsync(
        Guid tenantId, CreatePrinterRequest request, CancellationToken ct)
    {
        var capabilities = request.CapabilitiesJson ?? "{}";
        var errors = ShopDeviceRules.ValidatePrinter(request, capabilities);
        if (errors.Count != 0) return ShopDeviceOperationResult<PrinterCreatedResponse>.Invalid(errors);

        // Manual entries are only visible placeholders. They do not consume a
        // selected-printer quota until a supported, live Windows queue is selected.
        var printer = new PrinterDevice
        {
            TenantId = tenantId,
            DisplayName = request.DisplayName.Trim(),
            Manufacturer = request.Manufacturer.Trim(),
            Model = request.Model.Trim(),
            ConnectionType = ShopDeviceRules.NormalizeConnectionType(request.ConnectionType),
            ConnectionAddress = string.IsNullOrWhiteSpace(request.ConnectionAddress) ? null : request.ConnectionAddress.Trim(),
            QueueName = string.IsNullOrWhiteSpace(request.QueueName) ? null : request.QueueName.Trim(),
            CapabilitiesJson = capabilities,
            Status = "Unknown",
            IsActive = false
        };
        db.PrinterDevices.Add(printer);
        await db.SaveChangesAsync(ct);

        return ShopDeviceOperationResult<PrinterCreatedResponse>.Success(new PrinterCreatedResponse(
            printer.Id, printer.DisplayName, printer.Manufacturer, printer.Model, printer.ConnectionType,
            printer.OutputMode, printer.ConnectionAddress, printer.QueueName, printer.CapabilitiesJson,
            printer.Status, printer.IsActive));
    }

    public async Task<ShopDeviceOperationResult<PrinterActivationResponse>> SetPrinterActiveAsync(
        Guid tenantId, Guid printerId, bool isActive, Guid? replacePrinterId, CancellationToken ct)
    {
        if (replacePrinterId is not null && !isActive)
        {
            return ShopDeviceOperationResult<PrinterActivationResponse>.BadRequest(
                "invalid_printer_replacement",
                "Choose a replacement queue to enable; do not combine replacement with disabling.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.Printer, ct);

        var printer = await db.PrinterDevices.SingleOrDefaultAsync(
            item => item.Id == printerId && item.TenantId == tenantId, ct);
        if (printer is null)
        {
            await transaction.RollbackAsync(ct);
            return ShopDeviceOperationResult<PrinterActivationResponse>.NotFound();
        }

        if (replacePrinterId is not null && printer.IsActive)
        {
            await transaction.RollbackAsync(ct);
            return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                "printer_already_selected",
                "This queue was selected in another shop session. Refresh the printer list and choose again; no other printer was deselected.");
        }

        if (isActive && !printer.IsActive)
        {
            if (printer.OutputMode is not ("PHYSICAL" or "PDF_EXPORT") || printer.AgentDeviceId is null)
            {
                await transaction.RollbackAsync(ct);
                return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                    "printer_not_ready_for_selection",
                    "This queue is not linked to a Windows Print Agent yet, or its driver is not supported for SmartPrint output. Keep it visible while the Agent discovers the installed Windows queue.");
            }

            var selectionNow = DateTimeOffset.UtcNow;
            var selectionAgent = await db.AgentDevices.AsNoTracking()
                .Where(agent => agent.Id == printer.AgentDeviceId && agent.TenantId == tenantId)
                .Select(agent => new { agent.Status, agent.LastSeenUtc, agent.RevokedUtc })
                .SingleOrDefaultAsync(ct);
            var agentOnline = selectionAgent is not null &&
                selectionAgent.Status == "Online" &&
                selectionAgent.RevokedUtc is null &&
                selectionAgent.LastSeenUtc is DateTimeOffset agentLastSeen &&
                agentLastSeen >= selectionNow.AddMinutes(-2);

            if (!PrinterSelectionRules.CanSelect(
                    printer.OutputMode,
                    printer.Status,
                    printer.LastSeenUtc,
                    agentOnline,
                    selectionAgent?.RevokedUtc is not null,
                    selectionAgent?.LastSeenUtc,
                    selectionNow))
            {
                var nextStep = !agentOnline
                    ? "Reconnect the shop Windows computer and wait for its Print Agent to report online."
                    : $"Windows currently reports this queue as {printer.Status}. Clear its printer error, paper or connection issue, then wait for the next automatic scan.";
                await transaction.RollbackAsync(ct);
                return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                    "printer_not_ready_for_selection",
                    $"{printer.DisplayName} remains visible, but it cannot use a plan slot yet. {nextStep} No selected printer is being replaced.");
            }

            PrinterDevice? replacedPrinter = null;
            if (replacePrinterId is Guid previousId)
            {
                if (previousId == printerId)
                {
                    await transaction.RollbackAsync(ct);
                    return ShopDeviceOperationResult<PrinterActivationResponse>.BadRequest(
                        "invalid_printer_replacement",
                        "Choose a different selected queue to replace.");
                }

                replacedPrinter = await db.PrinterDevices.SingleOrDefaultAsync(
                    item => item.Id == previousId && item.TenantId == tenantId && item.IsActive, ct);
                if (replacedPrinter is null)
                {
                    await transaction.RollbackAsync(ct);
                    return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                        "printer_replacement_source_unavailable",
                        "The selected queue to replace is no longer selected. Refresh the printer list and try again.");
                }

                // Lock the previous queue's in-flight jobs before deciding whether the
                // owner may safely move the plan slot to another physical destination.
                var queueJobsUnderLock = await db.PrintJobs.FromSqlInterpolated(
                    $"""
                    SELECT * FROM "PrintJobs"
                    WHERE "TenantId" = {tenantId}
                      AND "PrinterDeviceId" = {previousId}
                      AND "Status" IN (
                        'PAID_AUTHORISED', 'LOCAL_QUEUE_READY', 'LOCAL_QUEUE_CLAIMED',
                        'PRINT_AUTHORISED', 'PRINT_SUBMITTING', 'SPOOLER_ACCEPTED',
                        'PRINTING', 'OUTPUT_UNCONFIRMED')
                    FOR UPDATE
                    """)
                    .ToListAsync(ct);
                var activeJobNumber = queueJobsUnderLock
                    .Where(job =>
                        (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                         job.AgentLeaseExpiresUtc != null &&
                         job.AgentLeaseExpiresUtc > DateTimeOffset.UtcNow) ||
                        (job.Status == "PRINT_AUTHORISED" &&
                         !PrintDispatchReassignmentRules.CanReassignBeforeSpoolerSubmission(
                             job.Status, job.SpoolerStatus, job.SpoolerJobId,
                             job.PrintDispatchLeaseExpiresUtc, job.SpoolerSubmittedUtc,
                             job.SpoolerObservedUtc, job.OutputConfirmedUtc)) ||
                        job.Status == "PRINT_SUBMITTING" ||
                        job.Status == "SPOOLER_ACCEPTED" ||
                        job.Status == "PRINTING" ||
                        job.Status == "OUTPUT_UNCONFIRMED")
                    .OrderByDescending(job => job.CreatedUtc)
                    .Select(job => job.JobNumber)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(activeJobNumber))
                {
                    await transaction.RollbackAsync(ct);
                    return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                        "printer_has_active_output",
                        $"Queue {replacedPrinter.DisplayName} still has job {activeJobNumber} in progress or awaiting output reconciliation. Resolve that same job before replacing the queue; SmartPrint will not cancel or duplicate Windows output.");
                }

                var partiallyCompletedBatchJobNumber = await db.PrintJobs.AsNoTracking()
                    .Where(job => job.TenantId == tenantId &&
                                  job.PrinterDeviceId == previousId &&
                                  job.BatchId != null &&
                                  job.Status == "COMPLETED" &&
                                  db.PrintJobs.Any(remaining =>
                                      remaining.TenantId == tenantId &&
                                      remaining.BatchId == job.BatchId &&
                                      remaining.Status != "REJECTED" &&
                                      remaining.Status != "REFUNDED" &&
                                      remaining.Status != "CANCELLED" &&
                                      remaining.Status != "COMPLETED" &&
                                      remaining.Status != "PDF_EXPORTED"))
                    .OrderByDescending(job => job.CreatedUtc)
                    .Select(job => job.JobNumber)
                    .FirstOrDefaultAsync(ct);
                if (!string.IsNullOrWhiteSpace(partiallyCompletedBatchJobNumber))
                {
                    await transaction.RollbackAsync(ct);
                    return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                        "printer_batch_output_locked",
                        $"A file in batch containing {partiallyCompletedBatchJobNumber} has already completed on {replacedPrinter.DisplayName}, while other files in that batch are still pending. Finish or resolve the remaining files on that same printer before replacing it; the batch will not be split or silently moved.");
                }

                var privateDocumentOnAnotherComputer = await (
                    from job in db.PrintJobs.AsNoTracking()
                    join document in db.PrintDocuments.AsNoTracking()
                        on new { job.TenantId, PrintJobId = job.Id }
                        equals new { document.TenantId, document.PrintJobId }
                    where job.TenantId == tenantId &&
                          job.PrinterDeviceId == previousId &&
                          document.StorageMode == "LOCAL_AGENT" &&
                          document.AgentDeviceId != printer.AgentDeviceId &&
                          job.Status != "REJECTED" && job.Status != "REFUNDED" &&
                          job.Status != "CANCELLED" && job.Status != "COMPLETED"
                    select job.Id
                ).AnyAsync(ct);
                if (privateDocumentOnAnotherComputer)
                {
                    await transaction.RollbackAsync(ct);
                    return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                        "private_printing_computer_locked",
                        "A local-only document is secured on the original Windows computer. Finish that file there, or choose another queue connected to the same computer, before replacing this queue.");
                }

                replacedPrinter.IsActive = false;
                replacedPrinter.UpdatedUtc = DateTimeOffset.UtcNow;

                // Existing QR codes keep working after the swap. Only station defaults move;
                // local private-upload tickets remain pinned to their original Agent.
                var stations = await db.QrStations
                    .Where(station => station.TenantId == tenantId && station.PrinterDeviceId == previousId)
                    .ToListAsync(ct);
                foreach (var station in stations)
                    station.PrinterDeviceId = printer.Id;
            }

            // Make the source deselection visible to the quota query inside this
            // transaction before checking the replacement selection.
            if (replacedPrinter is not null)
                await db.SaveChangesAsync(ct);

            var quota = await entitlements.CheckCapacityAsync(tenantId, TenantQuotaResource.Printer, ct);
            if (!quota.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                    quota.Code, quota.Message, quota.Current, quota.Maximum);
            }

            printer.IsActive = true;
        }
        else if (!isActive && printer.IsActive)
        {
            var queueJobsUnderLock = await db.PrintJobs.FromSqlInterpolated(
                $"""
                SELECT * FROM "PrintJobs"
                WHERE "TenantId" = {tenantId}
                  AND "PrinterDeviceId" = {printerId}
                  AND "Status" IN (
                    'PAID_AUTHORISED', 'LOCAL_QUEUE_READY', 'LOCAL_QUEUE_CLAIMED',
                    'PRINT_AUTHORISED', 'PRINT_SUBMITTING', 'SPOOLER_ACCEPTED',
                    'PRINTING', 'OUTPUT_UNCONFIRMED')
                FOR UPDATE
                """)
                .ToListAsync(ct);
            var activeJobNumber = queueJobsUnderLock
                .Where(job =>
                    (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                     job.AgentLeaseExpiresUtc != null &&
                     job.AgentLeaseExpiresUtc > DateTimeOffset.UtcNow) ||
                    (job.Status == "PRINT_AUTHORISED" &&
                     !PrintDispatchReassignmentRules.CanReassignBeforeSpoolerSubmission(
                         job.Status, job.SpoolerStatus, job.SpoolerJobId,
                         job.PrintDispatchLeaseExpiresUtc, job.SpoolerSubmittedUtc,
                         job.SpoolerObservedUtc, job.OutputConfirmedUtc)) ||
                    job.Status == "PRINT_SUBMITTING" ||
                    job.Status == "SPOOLER_ACCEPTED" ||
                    job.Status == "PRINTING" ||
                    job.Status == "OUTPUT_UNCONFIRMED")
                .OrderByDescending(job => job.CreatedUtc)
                .Select(job => job.JobNumber)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(activeJobNumber))
            {
                await transaction.RollbackAsync(ct);
                return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                    "printer_has_active_output",
                    $"Printer cannot be deselected while job {activeJobNumber} is active in the Windows Agent or spooler, or awaiting output reconciliation. Resolve that same job first; disabling it does not cancel Windows output.");
            }

            var partiallyCompletedBatchJobNumber = await db.PrintJobs.AsNoTracking()
                .Where(job => job.TenantId == tenantId &&
                              job.PrinterDeviceId == printerId &&
                              job.BatchId != null &&
                              job.Status == "COMPLETED" &&
                              db.PrintJobs.Any(remaining =>
                                  remaining.TenantId == tenantId &&
                                  remaining.BatchId == job.BatchId &&
                                  remaining.Status != "REJECTED" &&
                                  remaining.Status != "REFUNDED" &&
                                  remaining.Status != "CANCELLED" &&
                                  remaining.Status != "COMPLETED" &&
                                  remaining.Status != "PDF_EXPORTED"))
                .OrderByDescending(job => job.CreatedUtc)
                .Select(job => job.JobNumber)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(partiallyCompletedBatchJobNumber))
            {
                await transaction.RollbackAsync(ct);
                return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                    "printer_batch_output_locked",
                    $"A file in batch containing {partiallyCompletedBatchJobNumber} has already completed on {printer.DisplayName}, while other files in that batch are still pending. Finish or resolve the remaining files on that same printer before deselecting it; the batch will not be split or silently moved.");
            }

            var enabledStationCount = await db.QrStations.AsNoTracking()
                .CountAsync(station => station.TenantId == tenantId &&
                                       station.PrinterDeviceId == printerId &&
                                       station.IsEnabled, ct);
            if (enabledStationCount > 0)
            {
                await transaction.RollbackAsync(ct);
                return ShopDeviceOperationResult<PrinterActivationResponse>.Conflict(
                    "printer_is_qr_station_default",
                    $"This queue is the default for {enabledStationCount} enabled QR station(s). Replace it with another selected queue to keep those stations working, or disable the affected stations first.");
            }

            printer.IsActive = false;
        }

        printer.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return ShopDeviceOperationResult<PrinterActivationResponse>.Success(
            new PrinterActivationResponse(printer.Id, printer.IsActive, replacePrinterId, printer.UpdatedUtc));
    }

    public async Task<ShopDeviceOperationResult<QrStationCreatedResponse>> CreateQrStationAsync(
        Guid tenantId, CreateQrStationRequest request, CancellationToken ct)
    {
        var portalBase = GetCanonicalPortalBase();
        if (portalBase is null) return ShopDeviceOperationResult<QrStationCreatedResponse>.PortalNotConfigured();

        var errors = ShopDeviceRules.ValidateQrStationName(request.DisplayName);
        if (errors.Count != 0) return ShopDeviceOperationResult<QrStationCreatedResponse>.Invalid(errors);

        if (request.PrinterDeviceId is Guid printerId)
        {
            var outputMode = await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.Id == printerId && printer.TenantId == tenantId && printer.IsActive)
                .Select(printer => printer.OutputMode)
                .SingleOrDefaultAsync(ct);
            if (outputMode is null)
                return ShopDeviceOperationResult<QrStationCreatedResponse>.InvalidPrinterSelection(new Dictionary<string, string[]>
                {
                    ["printerDeviceId"] = ["Select an active printer that belongs to this shop."]
                });
            if (outputMode is not ("PHYSICAL" or "PDF_EXPORT"))
                return ShopDeviceOperationResult<QrStationCreatedResponse>.InvalidPrinterSelection(new Dictionary<string, string[]>
                {
                    ["printerDeviceId"] = ["This virtual/document driver is not supported for SmartPrint output. Select a physical printer or Microsoft Print to PDF for non-production testing."]
                });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.QrStation, ct);
        var quota = await entitlements.CheckCapacityAsync(tenantId, TenantQuotaResource.QrStation, ct);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(ct);
            return ShopDeviceOperationResult<QrStationCreatedResponse>.Conflict(
                quota.Code, quota.Message, quota.Current, quota.Maximum);
        }

        var publicCode = Base64UrlTokenEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var station = new QrStation
        {
            TenantId = tenantId,
            PrinterDeviceId = request.PrinterDeviceId,
            DisplayName = request.DisplayName.Trim(),
            PublicCodeHash = OpaqueTokenHasher.Hash(publicCode),
            PublicCode = publicCode,
            PublicCodeHint = publicCode[^4..]
        };
        db.QrStations.Add(station);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return ShopDeviceOperationResult<QrStationCreatedResponse>.Success(new QrStationCreatedResponse(
            station.Id, station.DisplayName, station.PrinterDeviceId, station.PublicCodeHint,
            station.IsEnabled, $"{portalBase}/s/{publicCode}"));
    }

    public async Task<ShopDeviceOperationResult<QrStationCodeResponse>> RotateQrCodeAsync(
        Guid tenantId, Guid stationId, CancellationToken ct)
    {
        var portalBase = GetCanonicalPortalBase();
        if (portalBase is null) return ShopDeviceOperationResult<QrStationCodeResponse>.PortalNotConfigured();

        var station = await db.QrStations.SingleOrDefaultAsync(
            item => item.Id == stationId && item.TenantId == tenantId, ct);
        if (station is null) return ShopDeviceOperationResult<QrStationCodeResponse>.NotFound();

        var publicCode = Base64UrlTokenEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        station.PublicCodeHash = OpaqueTokenHasher.Hash(publicCode);
        station.PublicCode = publicCode;
        station.PublicCodeHint = publicCode[^4..];
        await db.SaveChangesAsync(ct);

        return ShopDeviceOperationResult<QrStationCodeResponse>.Success(
            new QrStationCodeResponse(station.Id, station.PublicCodeHint, $"{portalBase}/s/{publicCode}"));
    }

    public async Task<ShopDeviceOperationResult<QrStationEnabledResponse>> SetQrStationEnabledAsync(
        Guid tenantId, Guid stationId, bool isEnabled, CancellationToken ct)
    {
        var station = await db.QrStations.SingleOrDefaultAsync(
            item => item.Id == stationId && item.TenantId == tenantId, ct);
        if (station is null) return ShopDeviceOperationResult<QrStationEnabledResponse>.NotFound();

        station.IsEnabled = isEnabled;
        await db.SaveChangesAsync(ct);
        return ShopDeviceOperationResult<QrStationEnabledResponse>.Success(
            new QrStationEnabledResponse(station.Id, station.IsEnabled));
    }

    private string? GetCanonicalPortalBase()
    {
        var configured = configuration["PublicPortal:BaseUrl"];
        return ShopDeviceRules.IsCanonicalPublicPortalBase(configured)
            ? ShopDeviceRules.NormalizePublicPortalBase(configured!)
            : null;
    }
}
