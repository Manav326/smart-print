using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Services;

public enum AccountRegistrationFailure
{
    None,
    InvalidInput,
    EmailAlreadyRegistered,
    IdentityValidationFailed,
    PermissionAssignmentFailed
}

public sealed record ShopRegistrationResult(
    AccountRegistrationFailure Failure,
    RegisterShopResponse? Response = null,
    Dictionary<string, string[]>? Errors = null)
{
    public bool Succeeded => Failure == AccountRegistrationFailure.None;
}

public sealed record CustomerRegistrationResult(
    AccountRegistrationFailure Failure,
    TokenResponse? Tokens = null,
    IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public bool Succeeded => Failure == AccountRegistrationFailure.None;
}

public sealed class AccountRegistrationService(
    UserManager<AppUser> userManager,
    SmartPrintDbContext db,
    TokenIssuer tokenIssuer)
{
    public async Task<ShopRegistrationResult> RegisterShopAsync(
        RegisterShopRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AccountRegistrationRules.TryNormalizeShop(request, out var businessName, out var ownerName, out var email))
            return new ShopRegistrationResult(AccountRegistrationFailure.InvalidInput);

        if (await userManager.FindByEmailAsync(email) is not null)
            return new ShopRegistrationResult(AccountRegistrationFailure.EmailAlreadyRegistered);

        var slugBase = TenantSlugPolicy.Create(businessName);
        var generatedSlug = $"{slugBase}-{Guid.NewGuid():N}";
        var slug = generatedSlug[..Math.Min(generatedSlug.Length, slugBase.Length + 9)];
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var tenant = new Tenant
        {
            Slug = slug,
            BusinessName = businessName,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        db.Tenants.Add(tenant);
        db.ShopProfiles.Add(new ShopProfile
        {
            TenantId = tenant.Id,
            DisplayName = businessName,
            UpdatedUtc = now
        });
        db.ShopPaymentProfiles.Add(new ShopPaymentProfile
        {
            TenantId = tenant.Id,
            UpiEnabled = false,
            UpiId = null,
            PayeeName = null,
            CashEnabled = false,
            UpdatedUtc = now
        });
        db.ShopPricingProfiles.Add(new ShopPricingProfile
        {
            TenantId = tenant.Id,
            IsConfigured = false,
            Version = 1,
            UpdatedUtc = now
        });
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenant.Id,
            PlanVersionId = SubscriptionPlanDefaults.TrialVersionId,
            PlanCode = "TRIAL",
            Status = "Trial",
            BillingInterval = "Trial",
            PeriodStartUtc = now,
            PeriodEndUtc = now.AddDays(14),
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserName = email,
            Email = email,
            DisplayName = ownerName,
            EmailConfirmed = false
        };
        var createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopRegistrationResult(
                AccountRegistrationFailure.IdentityValidationFailed,
                Errors: ToErrors(createResult.Errors));
        }

        var claimResult = await userManager.AddClaimAsync(user, new Claim("shop_role", "ShopOwner"));
        if (!claimResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopRegistrationResult(AccountRegistrationFailure.PermissionAssignmentFailed);
        }

        var tokens = await tokenIssuer.CreateAsync(user);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenIssuer.HashRefreshToken(tokens.RefreshToken),
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(30)
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = new RegisterShopResponse(
            tenant.Id,
            tenant.Slug,
            tenant.BusinessName,
            "Trial",
            new TokenResponse("Bearer", tokens.AccessToken, tokens.RefreshToken, tokens.AccessTokenExpiresUtc));
        return new ShopRegistrationResult(AccountRegistrationFailure.None, response);
    }

    public async Task<CustomerRegistrationResult> RegisterCustomerAsync(
        RegisterCustomerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AccountRegistrationRules.TryNormalizeCustomer(request, out var displayName, out var email))
            return new CustomerRegistrationResult(AccountRegistrationFailure.InvalidInput);

        if (await userManager.FindByEmailAsync(email) is not null)
            return new CustomerRegistrationResult(AccountRegistrationFailure.EmailAlreadyRegistered);

        var now = DateTimeOffset.UtcNow;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            UserName = email,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = false
        };
        var createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            return new CustomerRegistrationResult(
                AccountRegistrationFailure.IdentityValidationFailed,
                Errors: ToErrors(createResult.Errors));
        }

        var claimResult = await userManager.AddClaimAsync(user, new Claim("customer_role", "Customer"));
        if (!claimResult.Succeeded)
        {
            await userManager.DeleteAsync(user);
            return new CustomerRegistrationResult(AccountRegistrationFailure.PermissionAssignmentFailed);
        }

        var tokens = await tokenIssuer.CreateAsync(user);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenIssuer.HashRefreshToken(tokens.RefreshToken),
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(30)
        });
        await db.SaveChangesAsync(cancellationToken);

        return new CustomerRegistrationResult(
            AccountRegistrationFailure.None,
            new TokenResponse("Bearer", tokens.AccessToken, tokens.RefreshToken, tokens.AccessTokenExpiresUtc));
    }

    private static Dictionary<string, string[]> ToErrors(IEnumerable<IdentityError> errors) =>
        errors.GroupBy(error => error.Code)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Description).ToArray());
}
