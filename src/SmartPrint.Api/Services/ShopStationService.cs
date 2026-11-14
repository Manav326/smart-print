using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public enum ShopStationMutationFailure
{
    None,
    InvalidPublicUrl,
    InvalidDisplayName,
    PrinterUnavailable,
    PrinterOutputUnsupported,
    QuotaExceeded,
    NotFound
}

public sealed record ShopStationMutationResult(
    ShopStationMutationFailure Failure,
    QrStation? Station = null,
    string? PublicUrl = null,
    string? QuotaCode = null,
    string? QuotaMessage = null,
    long? QuotaCurrent = null,
    long? QuotaMaximum = null)
{
    public bool Succeeded => Failure == ShopStationMutationFailure.None;
}

public sealed class ShopStationService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<ShopStationMutationResult> CreateAsync(
        Guid tenantId,
        CreateQrStationRequest request,
        string? configuredPublicBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!ShopStationRules.TryNormalizePublicPortalBaseUrl(configuredPublicBaseUrl, out var portalBase))
            return new ShopStationMutationResult(ShopStationMutationFailure.InvalidPublicUrl);

        if (!ShopStationRules.TryNormalizeDisplayName(request.DisplayName, out var displayName))
            return new ShopStationMutationResult(ShopStationMutationFailure.InvalidDisplayName);

        if (request.PrinterDeviceId is Guid printerId)
        {
            var selectedPrinterOutputMode = await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.Id == printerId && printer.TenantId == tenantId && printer.IsActive)
                .Select(printer => printer.OutputMode)
                .SingleOrDefaultAsync(cancellationToken);
            if (selectedPrinterOutputMode is null)
                return new ShopStationMutationResult(ShopStationMutationFailure.PrinterUnavailable);
            if (!ShopStationRules.IsSupportedOutputMode(selectedPrinterOutputMode))
                return new ShopStationMutationResult(ShopStationMutationFailure.PrinterOutputUnsupported);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.QrStation, cancellationToken);
        var quota = await entitlements.CheckCapacityAsync(
            tenantId, TenantQuotaResource.QrStation, cancellationToken);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopStationMutationResult(
                ShopStationMutationFailure.QuotaExceeded,
                QuotaCode: quota.Code,
                QuotaMessage: quota.Message,
                QuotaCurrent: quota.Current,
                QuotaMaximum: quota.Maximum);
        }

        var publicCode = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var station = new QrStation
        {
            TenantId = tenantId,
            PrinterDeviceId = request.PrinterDeviceId,
            DisplayName = displayName,
            PublicCodeHash = HashOpaqueToken(publicCode),
            PublicCode = publicCode,
            PublicCodeHint = publicCode[^4..]
        };
        db.QrStations.Add(station);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ShopStationMutationResult(
            ShopStationMutationFailure.None,
            station,
            $"{portalBase}/s/{publicCode}");
    }

    public async Task<ShopStationMutationResult> RotateCodeAsync(
        Guid tenantId,
        Guid stationId,
        string? configuredPublicBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!ShopStationRules.TryNormalizePublicPortalBaseUrl(configuredPublicBaseUrl, out var portalBase))
            return new ShopStationMutationResult(ShopStationMutationFailure.InvalidPublicUrl);

        var station = await db.QrStations.SingleOrDefaultAsync(
            item => item.Id == stationId && item.TenantId == tenantId,
            cancellationToken);
        if (station is null)
            return new ShopStationMutationResult(ShopStationMutationFailure.NotFound);

        var publicCode = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        station.PublicCodeHash = HashOpaqueToken(publicCode);
        station.PublicCode = publicCode;
        station.PublicCodeHint = publicCode[^4..];
        await db.SaveChangesAsync(cancellationToken);

        return new ShopStationMutationResult(
            ShopStationMutationFailure.None,
            station,
            $"{portalBase}/s/{publicCode}");
    }

    public async Task<ShopStationMutationResult> SetEnabledAsync(
        Guid tenantId,
        Guid stationId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var station = await db.QrStations.SingleOrDefaultAsync(
            item => item.Id == stationId && item.TenantId == tenantId,
            cancellationToken);
        if (station is null)
            return new ShopStationMutationResult(ShopStationMutationFailure.NotFound);

        station.IsEnabled = isEnabled;
        await db.SaveChangesAsync(cancellationToken);
        return new ShopStationMutationResult(ShopStationMutationFailure.None, station);
    }

    public async Task<object> ListAsync(
        Guid tenantId,
        string? configuredPublicBaseUrl,
        CancellationToken cancellationToken)
    {
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
            .ToListAsync(cancellationToken);
        var portalBase = (configuredPublicBaseUrl ?? string.Empty).TrimEnd('/');
        return stations.Select(station => new
        {
            station.Id,
            station.PrinterDeviceId,
            station.DisplayName,
            station.PublicCodeHint,
            station.IsEnabled,
            station.CreatedUtc,
            PublicUrl = station.PublicCode is not null ? $"{portalBase}/s/{station.PublicCode}" : null
        }).ToArray();
    }

}
