using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.ShopAccount.Contracts;
using SmartPrint.Api.Modules.ShopAccount.Abstractions;

namespace SmartPrint.Api.Modules.ShopAccount.Services;

/// <summary>
/// Owns shop-account identity updates, sensitive email-change verification,
/// lockout handling, duplicate-email checks and the transactional Identity update.
/// </summary>
public sealed class ShopAccountService(
    SmartPrintDbContext db,
    IShopAccountIdentity identity)
{
    public async Task<ShopAccountUpdateResult> UpdateAsync(
        Guid tenantId,
        Guid userId,
        ShopOwnerAccountRequest request,
        CancellationToken ct)
    {
        if (!ShopOwnerAccountRules.TryNormalizeOwnerName(request.OwnerName, out var ownerName) ||
            !ShopOwnerAccountRules.TryNormalizeEmail(request.Email, out var email))
            return ShopAccountUpdateResult.Invalid(new Dictionary<string, string[]>
            {
                ["account"] = ["Enter a name between 1 and 160 characters and a valid email address."]
            });

        var user = await identity.FindByIdAsync(userId);
        if (user is null || user.TenantId != tenantId)
            return ShopAccountUpdateResult.Forbidden();

        var emailChanged = ShopOwnerAccountRules.EmailChanged(user.Email, email);
        if (emailChanged)
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword))
                return ShopAccountUpdateResult.CurrentPasswordRequired();

            var passwordCheck = await identity.CheckCurrentPasswordAsync(user, request.CurrentPassword);
            if (passwordCheck == ShopAccountPasswordCheck.LockedOut)
                return ShopAccountUpdateResult.LockedOut();
            if (passwordCheck != ShopAccountPasswordCheck.Valid)
                return ShopAccountUpdateResult.IncorrectPassword();

            if (await identity.EmailBelongsToAnotherUserAsync(email, user.Id))
                return ShopAccountUpdateResult.EmailAlreadyRegistered();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        user.DisplayName = ownerName;
        if (emailChanged)
        {
            user.Email = email;
            user.UserName = email;
        }

        var update = await identity.UpdateAsync(user);
        if (!update.Succeeded)
        {
            await transaction.RollbackAsync(ct);
            return ShopAccountUpdateResult.Invalid(update.Errors);
        }

        await transaction.CommitAsync(ct);
        return ShopAccountUpdateResult.Updated(new ShopAccountUpdateResponse(
            user.DisplayName,
            user.Email ?? string.Empty,
            emailChanged));
    }
}

public enum ShopAccountUpdateKind
{
    Updated,
    InvalidRequest,
    Forbidden,
    CurrentPasswordRequired,
    LockedOut,
    IncorrectPassword,
    EmailAlreadyRegistered
}

public sealed record ShopAccountUpdateResult(
    ShopAccountUpdateKind Kind,
    Dictionary<string, string[]>? ValidationErrors = null,
    ShopAccountUpdateResponse? Response = null)
{
    public static ShopAccountUpdateResult Updated(ShopAccountUpdateResponse response) =>
        new(ShopAccountUpdateKind.Updated, Response: response);

    public static ShopAccountUpdateResult Invalid(Dictionary<string, string[]> errors) =>
        new(ShopAccountUpdateKind.InvalidRequest, ValidationErrors: errors);

    public static ShopAccountUpdateResult Forbidden() => new(ShopAccountUpdateKind.Forbidden);
    public static ShopAccountUpdateResult CurrentPasswordRequired() => new(ShopAccountUpdateKind.CurrentPasswordRequired);
    public static ShopAccountUpdateResult LockedOut() => new(ShopAccountUpdateKind.LockedOut);
    public static ShopAccountUpdateResult IncorrectPassword() => new(ShopAccountUpdateKind.IncorrectPassword);
    public static ShopAccountUpdateResult EmailAlreadyRegistered() => new(ShopAccountUpdateKind.EmailAlreadyRegistered);
}
