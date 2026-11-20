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

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.Printer, ct);
        var quota = await entitlements.CheckCapacityAsync(tenantId, TenantQuotaResource.Printer, ct);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(ct);
            return ShopDeviceOperationResult<PrinterCreatedResponse>.Conflict(
                quota.Code, quota.Message, quota.Current, quota.Maximum);
        }

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
            IsActive = true
        };
        db.PrinterDevices.Add(printer);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ShopDeviceOperationResult<PrinterCreatedResponse>.Success(new PrinterCreatedResponse(
            printer.Id, printer.DisplayName, printer.Manufacturer, printer.Model, printer.ConnectionType,
            printer.OutputMode, printer.ConnectionAddress, printer.QueueName, printer.CapabilitiesJson,
            printer.Status, printer.IsActive));
    }

    public async Task<ShopDeviceOperationResult<PrinterActivationResponse>> SetPrinterActiveAsync(
        Guid tenantId, Guid printerId, bool isActive, CancellationToken ct)
    {
        var printer = await db.PrinterDevices.SingleOrDefaultAsync(
            item => item.Id == printerId && item.TenantId == tenantId, ct);
        if (printer is null) return ShopDeviceOperationResult<PrinterActivationResponse>.NotFound();

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
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(activeJobNumber))
                return ShopDeviceOperationResult<PrinterActivationResponse>.PrinterHasActiveOutput(activeJobNumber);
        }

        printer.IsActive = isActive;
        printer.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ShopDeviceOperationResult<PrinterActivationResponse>.Success(
            new PrinterActivationResponse(printer.Id, printer.IsActive, printer.UpdatedUtc));
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
