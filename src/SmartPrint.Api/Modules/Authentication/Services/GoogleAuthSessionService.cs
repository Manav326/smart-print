using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.Authentication.Contracts;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.Authentication.Services;

/// <summary>
/// Creates Google-authenticated API sessions and persists the refresh-token record.
/// Token issuance and refresh-token persistence remain a single application operation.
/// </summary>
public sealed class GoogleAuthSessionService(
    SmartPrintDbContext db,
    TokenIssuer tokenIssuer)
{
    public async Task<GoogleAuthSessionResponse> CreateAsync(AppUser user, CancellationToken cancellationToken)
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

        return new GoogleAuthSessionResponse(
            "Bearer",
            tokens.AccessToken,
            tokens.RefreshToken,
            tokens.AccessTokenExpiresUtc);
    }
}
