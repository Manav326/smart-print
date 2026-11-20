using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Modules.ShopPayments.Contracts;

namespace SmartPrint.Api.Modules.ShopPayments.Services;

/// <summary>
/// Owns persistence for the shop payment-method configuration use case.
/// The caller supplies tenant scope only after the authenticated HTTP boundary resolves it.
/// </summary>
public sealed class ShopPaymentSettingsService(SmartPrintDbContext db)
{
    public async Task<ShopPaymentSettingsResponse?> UpdateAsync(
        Guid tenantId,
        ShopPaymentSettingsRequest request,
        CancellationToken ct)
    {
        var settings = await db.ShopPaymentProfiles.SingleOrDefaultAsync(
            item => item.TenantId == tenantId, ct);
        if (settings is null) return null;

        settings.UpiEnabled = request.UpiEnabled;
        settings.UpiId = request.UpiEnabled ? request.UpiId : null;
        settings.PayeeName = request.UpiEnabled ? request.PayeeName : null;
        settings.CashEnabled = request.CashEnabled;
        settings.InstantPaymentEnabled = request.InstantPaymentEnabled;
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ShopPaymentSettingsResponse(
            settings.UpiEnabled,
            settings.UpiId,
            settings.PayeeName,
            settings.CashEnabled,
            settings.InstantPaymentEnabled,
            settings.UpdatedUtc);
    }
}
