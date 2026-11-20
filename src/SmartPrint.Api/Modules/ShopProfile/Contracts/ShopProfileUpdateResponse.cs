namespace SmartPrint.Api.Modules.ShopProfile.Contracts;

/// <summary>
/// Response for a successful shop profile update.
/// </summary>
public sealed record ShopProfileUpdateResponse(
    string DisplayName,
    string? LogoUrl,
    string AccentColor,
    DateTimeOffset UpdatedUtc);
