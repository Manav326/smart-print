using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopAccountProfileReadFailure
{
    None,
    OwnerNotFound,
    NotFound
}

public sealed record ShopAccountProfileReadResult(
    ShopAccountProfileReadFailure Failure,
    object? Profile = null);

public enum ShopOwnerAccountUpdateFailure
{
    None,
    InvalidInput,
    OwnerNotFound,
    CurrentPasswordRequired,
    AccountLocked,
    InvalidCurrentPassword,
    EmailAlreadyRegistered,
    IdentityUpdateFailed
}

public sealed record ShopOwnerAccountUpdateResult(
    ShopOwnerAccountUpdateFailure Failure,
    AppUser? User = null,
    bool EmailChanged = false,
    Dictionary<string, string[]>? Errors = null)
{
    public bool Succeeded => Failure == ShopOwnerAccountUpdateFailure.None;
}

public enum ShopProfileUpdateFailure
{
    None,
    InvalidInput,
    CustomBrandingNotIncluded,
    NotFound
}

public sealed record ShopProfileUpdateResult(
    ShopProfileUpdateFailure Failure,
    ShopProfile? Profile = null)
{
    public bool Succeeded => Failure == ShopProfileUpdateFailure.None;
}

public sealed class ShopAccountService(
    SmartPrintDbContext db,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    TenantEntitlementService entitlements)
{
    public async Task<ShopOwnerAccountUpdateResult> UpdateOwnerAccountAsync(
        Guid tenantId,
        Guid userId,
        ShopOwnerAccountRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ShopOwnerAccountRules.TryNormalizeOwnerName(request.OwnerName, out var ownerName) ||
            !ShopOwnerAccountRules.TryNormalizeEmail(request.Email, out var email))
        {
            return new ShopOwnerAccountUpdateResult(ShopOwnerAccountUpdateFailure.InvalidInput);
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.TenantId != tenantId)
            return new ShopOwnerAccountUpdateResult(ShopOwnerAccountUpdateFailure.OwnerNotFound);

        var emailChanged = ShopOwnerAccountRules.EmailChanged(user.Email, email);
        if (emailChanged)
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword))
                return new ShopOwnerAccountUpdateResult(ShopOwnerAccountUpdateFailure.CurrentPasswordRequired);

            var passwordCheck = await signInManager.CheckPasswordSignInAsync(
                user, request.CurrentPassword, lockoutOnFailure: true);
            if (passwordCheck.IsLockedOut)
                return new ShopOwnerAccountUpdateResult(ShopOwnerAccountUpdateFailure.AccountLocked);

            if (!passwordCheck.Succeeded)
                return new ShopOwnerAccountUpdateResult(ShopOwnerAccountUpdateFailure.InvalidCurrentPassword);

            var existingUser = await userManager.FindByEmailAsync(email);
            if (existingUser is not null && existingUser.Id != user.Id)
                return new ShopOwnerAccountUpdateResult(ShopOwnerAccountUpdateFailure.EmailAlreadyRegistered);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        user.DisplayName = ownerName;
        if (emailChanged)
        {
            user.Email = email;
            user.UserName = email;
        }

        var update = await userManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            var errors = update.Errors
                .GroupBy(error => error.Code)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Description).ToArray());
            return new ShopOwnerAccountUpdateResult(
                ShopOwnerAccountUpdateFailure.IdentityUpdateFailed,
                Errors: errors);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ShopOwnerAccountUpdateResult(
            ShopOwnerAccountUpdateFailure.None,
            user,
            emailChanged);
    }

    public async Task<ShopProfileUpdateResult> UpdateProfileAsync(
        Guid tenantId,
        ShopProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DisplayName) ||
            request.DisplayName.Trim().Length > 180 ||
            !ShopProfileRules.IsHexColor(request.AccentColor) ||
            (request.LogoUrl is not null &&
             (!Uri.TryCreate(request.LogoUrl, UriKind.Absolute, out var logoUri) ||
              logoUri.Scheme != Uri.UriSchemeHttps)))
        {
            return new ShopProfileUpdateResult(ShopProfileUpdateFailure.InvalidInput);
        }

        var plan = await entitlements.GetForTenantAsync(tenantId, cancellationToken);
        var requestsCustomBranding = request.LogoUrl is not null ||
            !string.Equals(request.AccentColor, "#7662E8", StringComparison.OrdinalIgnoreCase);
        if (requestsCustomBranding && !plan.WhiteLabelEnabled)
            return new ShopProfileUpdateResult(ShopProfileUpdateFailure.CustomBrandingNotIncluded);

        var profile = await db.ShopProfiles.SingleOrDefaultAsync(
            item => item.TenantId == tenantId,
            cancellationToken);
        if (profile is null)
            return new ShopProfileUpdateResult(ShopProfileUpdateFailure.NotFound);

        profile.DisplayName = request.DisplayName.Trim();
        profile.LogoUrl = request.LogoUrl;
        profile.AccentColor = request.AccentColor.ToUpperInvariant();
        profile.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new ShopProfileUpdateResult(ShopProfileUpdateFailure.None, profile);
    }

    public async Task<ShopAccountProfileReadResult> GetProfileAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var owner = await userManager.FindByIdAsync(userId.ToString());
        if (owner is null || owner.TenantId != tenantId)
        {
            return new(ShopAccountProfileReadFailure.OwnerNotFound);
        }

        var profile = await db.ShopProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId, cancellationToken);
        var tenant = await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tenantId && item.Status == "Active", cancellationToken);
        if (profile is null || tenant is null)
        {
            return new(ShopAccountProfileReadFailure.NotFound);
        }

        return new(ShopAccountProfileReadFailure.None, new
        {
            tenant.Id,
            tenant.Slug,
            tenant.BusinessName,
            tenant.Status,
            OwnerName = owner.DisplayName,
            OwnerEmail = owner.Email ?? string.Empty,
            profile.DisplayName,
            profile.LogoUrl,
            profile.AccentColor,
            profile.Currency,
            profile.TimeZone,
            profile.WhiteLabelEnabled
        });
    }

}
