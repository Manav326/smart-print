using Microsoft.AspNetCore.Identity;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.ShopAccount.Abstractions;

namespace SmartPrint.Api.Modules.ShopAccount.Infrastructure;

/// <summary>
/// ASP.NET Identity adapter for account lookup, password/lockout validation and persistence.
/// </summary>
public sealed class IdentityShopAccountIdentity(
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager) : IShopAccountIdentity
{
    public Task<AppUser?> FindByIdAsync(Guid userId) =>
        userManager.FindByIdAsync(userId.ToString());

    public async Task<ShopAccountPasswordCheck> CheckCurrentPasswordAsync(AppUser user, string password)
    {
        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.IsLockedOut) return ShopAccountPasswordCheck.LockedOut;
        return result.Succeeded ? ShopAccountPasswordCheck.Valid : ShopAccountPasswordCheck.Invalid;
    }

    public async Task<bool> EmailBelongsToAnotherUserAsync(string email, Guid currentUserId)
    {
        var existingUser = await userManager.FindByEmailAsync(email);
        return existingUser is not null && existingUser.Id != currentUserId;
    }

    public async Task<ShopAccountIdentityUpdateResult> UpdateAsync(AppUser user)
    {
        var result = await userManager.UpdateAsync(user);
        var errors = result.Errors
            .GroupBy(error => error.Code)
            .ToDictionary(group => group.Key,
                group => group.Select(error => error.Description).ToArray());
        return new ShopAccountIdentityUpdateResult(result.Succeeded, errors);
    }
}
