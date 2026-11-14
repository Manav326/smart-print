using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopPrinterMutationFailure
{
    None,
    InvalidPrinter,
    InvalidCapabilities,
    QuotaExceeded,
    NotFound,
    ActiveOutput
}

public sealed record ShopPrinterMutationResult(
    ShopPrinterMutationFailure Failure,
    PrinterDevice? Printer = null,
    string? ActiveJobNumber = null,
    string? QuotaCode = null,
    string? QuotaMessage = null,
    long? QuotaCurrent = null,
    long? QuotaMaximum = null)
{
    public bool Succeeded => Failure == ShopPrinterMutationFailure.None;
}

public sealed class ShopPrinterService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<ShopPrinterMutationResult> CreateAsync(
        Guid tenantId,
        CreatePrinterRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ShopPrinterRules.Validate(request, out var capabilities, out var connectionType);
        if (validation == ShopPrinterValidationFailure.InvalidPrinter)
            return new ShopPrinterMutationResult(ShopPrinterMutationFailure.InvalidPrinter);
        if (validation == ShopPrinterValidationFailure.InvalidCapabilities)
            return new ShopPrinterMutationResult(ShopPrinterMutationFailure.InvalidCapabilities);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.Printer, cancellationToken);
        var quota = await entitlements.CheckCapacityAsync(tenantId, TenantQuotaResource.Printer, cancellationToken);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrinterMutationResult(
                ShopPrinterMutationFailure.QuotaExceeded,
                QuotaCode: quota.Code,
                QuotaMessage: quota.Message,
                QuotaCurrent: quota.Current,
                QuotaMaximum: quota.Maximum);
        }

        var printer = new PrinterDevice
        {
            TenantId = tenantId,
            DisplayName = request.DisplayName.Trim(),
            Manufacturer = request.Manufacturer.Trim(),
            Model = request.Model.Trim(),
            ConnectionType = connectionType,
            ConnectionAddress = string.IsNullOrWhiteSpace(request.ConnectionAddress) ? null : request.ConnectionAddress.Trim(),
            QueueName = string.IsNullOrWhiteSpace(request.QueueName) ? null : request.QueueName.Trim(),
            CapabilitiesJson = capabilities,
            Status = "Unknown",
            IsActive = true
        };
        db.PrinterDevices.Add(printer);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ShopPrinterMutationResult(ShopPrinterMutationFailure.None, printer);
    }

    public async Task<ShopPrinterMutationResult> SetActiveAsync(
        Guid tenantId,
        Guid printerId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var printer = await db.PrinterDevices.SingleOrDefaultAsync(
            item => item.Id == printerId && item.TenantId == tenantId,
            cancellationToken);
        if (printer is null)
            return new ShopPrinterMutationResult(ShopPrinterMutationFailure.NotFound);

        if (!isActive)
        {
            var activeJobNumber = await db.PrintJobs.AsNoTracking()
                .Where(job => job.TenantId == tenantId &&
                              job.PrinterDeviceId == printerId &&
                              (job.Status == "LOCAL_QUEUE_CLAIMED" ||
                               job.Status == "LOCAL_QUEUE_READY" ||
                               job.Status == "PRINT_AUTHORISED" ||
                               job.Status == "PRINT_SUBMITTING" ||
                               job.Status == "SPOOLER_ACCEPTED" ||
                               job.Status == "PRINTING"))
                .OrderByDescending(job => job.CreatedUtc)
                .Select(job => job.JobNumber)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(activeJobNumber))
                return new ShopPrinterMutationResult(
                    ShopPrinterMutationFailure.ActiveOutput,
                    ActiveJobNumber: activeJobNumber);
        }

        printer.IsActive = isActive;
        printer.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new ShopPrinterMutationResult(ShopPrinterMutationFailure.None, printer);
    }

    public async Task<object> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
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
            .ToListAsync(cancellationToken);
        return printers;
    }

}
