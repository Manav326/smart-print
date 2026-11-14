using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public enum ShopPaymentSettingsValidationFailure
{
    None,
    InvalidUpiConfiguration,
    UpiIdTooLong,
    NoPaymentMethodEnabled
}

public static class ShopPaymentSettingsRules
{
    public static ShopPaymentSettingsValidationFailure Validate(
        ShopPaymentSettingsRequest request,
        out string? upiId,
        out string? payeeName)
    {
        ArgumentNullException.ThrowIfNull(request);
        upiId = request.UpiId?.Trim();
        payeeName = request.PayeeName?.Trim();

        if (request.UpiEnabled &&
            (!IsValidUpiId(upiId) || string.IsNullOrWhiteSpace(payeeName) || payeeName.Length > 120))
        {
            return ShopPaymentSettingsValidationFailure.InvalidUpiConfiguration;
        }

        if (upiId is { Length: > 160 })
            return ShopPaymentSettingsValidationFailure.UpiIdTooLong;

        if (!request.UpiEnabled && !request.CashEnabled)
            return ShopPaymentSettingsValidationFailure.NoPaymentMethodEnabled;

        return ShopPaymentSettingsValidationFailure.None;
    }

    public static bool IsValidUpiId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
            return false;

        var parts = value.Split('@');
        return parts.Length == 2 &&
               parts[0].Length is >= 2 and <= 128 &&
               parts[1].Length is >= 2 and <= 64 &&
               parts[0].All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-') &&
               parts[1].All(character => char.IsLetterOrDigit(character) || character is '.' or '-');
    }
}
