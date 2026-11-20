namespace SmartPrint.Api.Modules.ShopPayments.Contracts;

/// <summary>
/// Public response for the shop's accepted payment methods.
/// Field names preserve the existing payment-settings API contract.
/// </summary>
public sealed record ShopPaymentSettingsResponse(
    bool UpiEnabled,
    string? UpiId,
    string? PayeeName,
    bool CashEnabled,
    bool InstantPaymentEnabled,
    DateTimeOffset UpdatedUtc);
