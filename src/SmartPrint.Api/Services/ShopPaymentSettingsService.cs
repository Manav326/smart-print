using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopPaymentSettingsUpdateFailure
{
    None,
    InvalidUpiConfiguration,
    UpiIdTooLong,
    NoPaymentMethodEnabled,
    NotFound
}

public sealed record ShopPaymentSettingsUpdateResult(
    ShopPaymentSettingsUpdateFailure Failure,
    ShopPaymentProfile? Settings = null)
{
    public bool Succeeded => Failure == ShopPaymentSettingsUpdateFailure.None;
}

public sealed class ShopPaymentSettingsService(SmartPrintDbContext db)
{
    public async Task<ShopPaymentSettingsUpdateResult> UpdateAsync(
        Guid tenantId,
        ShopPaymentSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ShopPaymentSettingsRules.Validate(request, out var upiId, out var payeeName);
        if (validation != ShopPaymentSettingsValidationFailure.None)
        {
            var failure = validation switch
            {
                ShopPaymentSettingsValidationFailure.InvalidUpiConfiguration => ShopPaymentSettingsUpdateFailure.InvalidUpiConfiguration,
                ShopPaymentSettingsValidationFailure.UpiIdTooLong => ShopPaymentSettingsUpdateFailure.UpiIdTooLong,
                _ => ShopPaymentSettingsUpdateFailure.NoPaymentMethodEnabled
            };
            return new ShopPaymentSettingsUpdateResult(failure);
        }

        var settings = await db.ShopPaymentProfiles.SingleOrDefaultAsync(
            item => item.TenantId == tenantId,
            cancellationToken);
        if (settings is null)
            return new ShopPaymentSettingsUpdateResult(ShopPaymentSettingsUpdateFailure.NotFound);

        settings.UpiEnabled = request.UpiEnabled;
        settings.UpiId = request.UpiEnabled ? upiId : null;
        settings.PayeeName = request.UpiEnabled ? payeeName : null;
        settings.CashEnabled = request.CashEnabled;
        settings.InstantPaymentEnabled = request.InstantPaymentEnabled;
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new ShopPaymentSettingsUpdateResult(ShopPaymentSettingsUpdateFailure.None, settings);
    }
}
