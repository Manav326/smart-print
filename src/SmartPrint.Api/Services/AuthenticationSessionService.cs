using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Services;

public enum AuthenticationSessionFailure
{
    None,
    Unauthorized
}

public sealed record AuthenticationSessionResult(
    AuthenticationSessionFailure Failure,
    TokenResponse? Tokens = null)
{
    public bool Succeeded => Failure == AuthenticationSessionFailure.None;
}

public sealed class AuthenticationSessionService(
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    SmartPrintDbContext db,
    TokenIssuer tokenIssuer)
{
    public async Task<AuthenticationSessionResult> CustomerLoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);

        var claims = await userManager.GetClaimsAsync(user);
        if (!CustomerIdentity.IsCustomer(user, claims))
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);

        var signIn = await signInManager.CheckPasswordSignInAsync(
            user, request.Password, lockoutOnFailure: true);
        if (!signIn.Succeeded)
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);

        return await CreateSessionAsync(user, cancellationToken);
    }

    public async Task<AuthenticationSessionResult> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);

        var claims = await userManager.GetClaimsAsync(user);
        if (user.TenantId is null && !PlatformAdminAuthorization.IsPlatformAdmin(user.TenantId, claims))
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);

        var signIn = await signInManager.CheckPasswordSignInAsync(
            user, request.Password, lockoutOnFailure: true);
        if (!signIn.Succeeded)
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);

        return await CreateSessionAsync(user, cancellationToken);
    }

    public async Task<AuthenticationSessionResult> RefreshAsync(
        RefreshRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);

        var hash = TokenIssuer.HashRefreshToken(request.RefreshToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var existing = await db.RefreshTokens.SingleOrDefaultAsync(token =>
            token.TokenHash == hash && token.RevokedUtc == null && token.ExpiresUtc > now,
            cancellationToken);

        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);
        }

        var claimed = await db.RefreshTokens
            .Where(token => token.Id == existing.Id && token.RevokedUtc == null && token.ExpiresUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedUtc, now), cancellationToken);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);
        }

        var user = await userManager.FindByIdAsync(existing.UserId.ToString());
        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);
        }

        var userClaims = await userManager.GetClaimsAsync(user);
        var isCustomer = CustomerIdentity.IsCustomer(user, userClaims);
        if (user.TenantId is null &&
            !PlatformAdminAuthorization.IsPlatformAdmin(user.TenantId, userClaims) &&
            !isCustomer)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuthenticationSessionResult(AuthenticationSessionFailure.Unauthorized);
        }

        var pair = await tokenIssuer.CreateAsync(user);
        var replacement = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenIssuer.HashRefreshToken(pair.RefreshToken),
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(30)
        };
        existing.ReplacedByTokenId = replacement.Id;
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AuthenticationSessionResult(
            AuthenticationSessionFailure.None,
            new TokenResponse("Bearer", pair.AccessToken, pair.RefreshToken, pair.AccessTokenExpiresUtc));
    }

    public async Task LogoutAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return;

        var hash = TokenIssuer.HashRefreshToken(request.RefreshToken);
        var now = DateTimeOffset.UtcNow;
        await db.RefreshTokens
            .Where(token => token.TokenHash == hash && token.RevokedUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedUtc, now), cancellationToken);
    }

    private async Task<AuthenticationSessionResult> CreateSessionAsync(
        AppUser user,
        CancellationToken cancellationToken)
    {
        var tokens = await tokenIssuer.CreateAsync(user);
        var now = DateTimeOffset.UtcNow;
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenIssuer.HashRefreshToken(tokens.RefreshToken),
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(30)
        });
        await db.SaveChangesAsync(cancellationToken);
        return new AuthenticationSessionResult(
            AuthenticationSessionFailure.None,
            new TokenResponse("Bearer", tokens.AccessToken, tokens.RefreshToken, tokens.AccessTokenExpiresUtc));
    }
}
