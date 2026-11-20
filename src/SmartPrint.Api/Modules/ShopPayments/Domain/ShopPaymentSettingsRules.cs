namespace SmartPrint.Api.Modules.ShopPayments.Domain;

/// <summary>
/// Pure validation for the customer-facing payment methods a shop can accept.
/// </summary>
public static class ShopPaymentSettingsRules
{
    public static Dictionary<string, string[]> Validate(
        bool upiEnabled,
        string? normalizedUpiId,
        string? normalizedPayeeName,
        bool cashEnabled)
    {
        var errors = new Dictionary<string, string[]>();
        if (upiEnabled &&
            (!IsValidUpiId(normalizedUpiId) ||
             string.IsNullOrWhiteSpace(normalizedPayeeName) ||
             normalizedPayeeName.Length > 120))
        {
            errors["upi"] = ["Enter a valid UPI ID and payee/business name before enabling UPI."];
        }

        if (normalizedUpiId is { Length: > 160 })
            errors["upiId"] = ["UPI ID cannot exceed 160 characters."];

        if (!upiEnabled && !cashEnabled)
            errors["methods"] = ["Enable at least one payment method so customers can pay for approved print jobs."];

        return errors;
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
