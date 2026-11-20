using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Modules.ShopAccount.Abstractions;

/// <summary>
/// Boundary for ASP.NET Identity operations used by the shop account use case.
/// Application services consume this interface without depending on the web framework.
/// </summary>
public interface IShopAccountIdentity
{
    Task<AppUser?> FindByIdAsync(Guid userId);
    Task<ShopAccountPasswordCheck> CheckCurrentPasswordAsync(AppUser user, string password);
    Task<bool> EmailBelongsToAnotherUserAsync(string email, Guid currentUserId);
    Task<ShopAccountIdentityUpdateResult> UpdateAsync(AppUser user);
}

public enum ShopAccountPasswordCheck
{
    Valid,
    Invalid,
    LockedOut
}

public sealed record ShopAccountIdentityUpdateResult(
    bool Succeeded,
    Dictionary<string, string[]> Errors);
