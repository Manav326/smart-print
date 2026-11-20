using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.Authentication.Abstractions;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.Authentication.Services;

/// <summary>
/// Owns single-use refresh-token rotation and logout revocation.
/// Token claim/rotation and replacement persistence remain one transaction.
/// </summary>
public sealed class AuthenticationSessionService(
    SmartPrintDbContext db,
    IAuthenticationUserAccess users,
    TokenIssuer tokenIssuer)
{
    public async Task<TokenPair?> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        var hash = TokenIssuer.HashRefreshToken(refreshToken);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var existing = await db.RefreshTokens.SingleOrDefaultAsync(token =>
            token.TokenHash == hash && token.RevokedUtc == null && token.ExpiresUtc > now, ct);

        if (existing is null)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var claimed = await db.RefreshTokens
            .Where(token => token.Id == existing.Id && token.RevokedUtc == null && token.ExpiresUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedUtc, now), ct);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var user = await users.FindByIdAsync(existing.UserId);
        if (user is null)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var userClaims = await users.GetClaimsAsync(user);
        var isCustomer = CustomerIdentity.IsCustomer(user, userClaims);
        if (user.TenantId is null &&
            !PlatformAdminAuthorization.IsPlatformAdmin(user.TenantId, userClaims) &&
            !isCustomer)
        {
            await transaction.RollbackAsync(ct);
            return null;
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
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return pair;
    }

    public async Task RevokeAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;

        var hash = TokenIssuer.HashRefreshToken(refreshToken);
        var now = DateTimeOffset.UtcNow;
        await db.RefreshTokens
            .Where(token => token.TokenHash == hash && token.RevokedUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedUtc, now), ct);
    }
}
