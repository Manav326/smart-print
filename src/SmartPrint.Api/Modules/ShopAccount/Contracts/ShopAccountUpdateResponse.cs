namespace SmartPrint.Api.Modules.ShopAccount.Contracts;

/// <summary>
/// Public response returned after a shop-owner account update.
/// JSON names preserve the existing response contract.
/// </summary>
public sealed record ShopAccountUpdateResponse(
    string OwnerName,
    string OwnerEmail,
    bool EmailChanged);
