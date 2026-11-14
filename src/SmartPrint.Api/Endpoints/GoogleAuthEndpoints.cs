using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class GoogleAuthEndpoints
{
    public static RouteGroupBuilder MapGoogleAuthEndpoints(this RouteGroupBuilder auth)
    {
        auth.MapPost("/google/challenge", (GoogleChallengeRequest request, GoogleLoginNonceStore nonces, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (request.Surface is not ("shop" or "customer"))
                return Results.BadRequest(new { code = "google_surface_invalid", message = "The requested sign-in type is invalid." });

            try
            {
                return Results.Ok(new { nonce = nonces.Issue(request.Surface), expiresInSeconds = 300 });
            }
            catch (InvalidOperationException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "google_sign_in_busy",
                    detail: "Google sign-in is temporarily busy. Please try again.");
            }
        }).AllowAnonymous();

        auth.MapGet("/google/config", (GoogleIdentityService google, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                configured = google.IsConfigured,
                clientId = google.IsConfigured ? google.WebClientId : string.Empty
            });
        }).AllowAnonymous();

        auth.MapPost("/google/shop", async (
            GoogleAuthRequest request,
            GoogleIdentityService google,
            GoogleLoginNonceStore nonces,
            UserManager<AppUser> userManager,
            SmartPrintDbContext db,
            TokenIssuer tokenIssuer,
            CancellationToken ct) =>
        {
            try
            {
                var identity = await google.VerifyAsync(request.Credential, request.Nonce);
                if (!nonces.TryConsume("shop", request.Nonce))
                    return AuthProblem(StatusCodes.Status401Unauthorized, "google_nonce_expired", "This Google sign-in request has expired. Please start again.");
                var user = await userManager.FindByLoginAsync(GoogleIdentityService.LoginProvider, identity.Subject);

                if (user is null)
                {
                    user = await userManager.FindByEmailAsync(identity.Email);
                    if (user is null)
                    {
                        return AuthProblem(
                            StatusCodes.Status401Unauthorized,
                            "shop_google_account_not_found",
                            "No SmartPrint shop account is registered with this Google email. Create your shop or sign in with the email already on your account.");
                    }

                    var claims = await userManager.GetClaimsAsync(user);
                    if (!GoogleAuthEndpointRules.CanAuthenticateShop(user, claims))
                    {
                        return AuthProblem(
                            StatusCodes.Status409Conflict,
                            "google_account_type_mismatch",
                            "This Google account belongs to a different SmartPrint account type. It cannot be used to sign in as a shop owner.");
                    }

                    var linked = await LinkVerifiedGoogleIdentityAsync(user, identity, userManager);
                    if (linked is not null) return linked;
                }

                var currentClaims = await userManager.GetClaimsAsync(user);
                if (!GoogleAuthEndpointRules.CanAuthenticateShop(user, currentClaims))
                {
                    return AuthProblem(
                        StatusCodes.Status401Unauthorized,
                        "shop_google_account_not_authorized",
                        "This Google account does not have a SmartPrint shop-owner or shop-manager identity.");
                }

                return Results.Ok(await CreateSessionAsync(user, db, tokenIssuer, ct));
            }
            catch (GoogleIdentityException exception)
            {
                return GoogleProblem(exception);
            }
        }).AllowAnonymous();

        auth.MapPost("/google/customer", async (
            GoogleAuthRequest request,
            GoogleIdentityService google,
            GoogleLoginNonceStore nonces,
            UserManager<AppUser> userManager,
            SmartPrintDbContext db,
            TokenIssuer tokenIssuer,
            CancellationToken ct) =>
        {
            try
            {
                var identity = await google.VerifyAsync(request.Credential, request.Nonce);
                if (!nonces.TryConsume("customer", request.Nonce))
                    return AuthProblem(StatusCodes.Status401Unauthorized, "google_nonce_expired", "This Google sign-in request has expired. Please start again.");
                var user = await userManager.FindByLoginAsync(GoogleIdentityService.LoginProvider, identity.Subject);

                if (user is null)
                {
                    user = await userManager.FindByEmailAsync(identity.Email);
                    if (user is null)
                    {
                        user = new AppUser
                        {
                            Id = Guid.NewGuid(),
                            TenantId = null,
                            UserName = identity.Email,
                            Email = identity.Email,
                            DisplayName = identity.DisplayName,
                            EmailConfirmed = true
                        };

                        var created = await userManager.CreateAsync(user);
                        if (!created.Succeeded)
                        {
                            return AuthProblem(
                                StatusCodes.Status409Conflict,
                                "google_customer_account_create_failed",
                                "A SmartPrint customer account could not be created for this Google email. Please try again or contact support.");
                        }

                        var customerClaim = await userManager.AddClaimAsync(
                            user,
                            new Claim(CustomerIdentity.RoleClaim, CustomerIdentity.RoleValue));
                        if (!customerClaim.Succeeded)
                        {
                            await userManager.DeleteAsync(user);
                            return AuthProblem(
                                StatusCodes.Status500InternalServerError,
                                "google_customer_identity_create_failed",
                                "SmartPrint could not finish setting up your customer account. Please try again.");
                        }
                    }
                    else
                    {
                        var existingClaims = await userManager.GetClaimsAsync(user);
                        if (!GoogleAuthEndpointRules.CanAuthenticateCustomer(user, existingClaims))
                        {
                            return AuthProblem(
                                StatusCodes.Status409Conflict,
                                "google_account_type_mismatch",
                                "This Google email is already used by a different SmartPrint account type. The customer account was not changed.");
                        }
                    }

                    var linked = await LinkVerifiedGoogleIdentityAsync(user, identity, userManager);
                    if (linked is not null) return linked;
                }

                var currentClaims = await userManager.GetClaimsAsync(user);
                if (!GoogleAuthEndpointRules.CanAuthenticateCustomer(user, currentClaims))
                {
                    return AuthProblem(
                        StatusCodes.Status409Conflict,
                        "google_account_type_mismatch",
                        "This Google identity is not linked to a SmartPrint customer account.");
                }

                return Results.Ok(await CreateSessionAsync(user, db, tokenIssuer, ct));
            }
            catch (GoogleIdentityException exception)
            {
                return GoogleProblem(exception);
            }
        }).AllowAnonymous();

        return auth;
    }

    private static async Task<IResult?> LinkVerifiedGoogleIdentityAsync(
        AppUser user,
        VerifiedGoogleIdentity identity,
        UserManager<AppUser> userManager)
    {
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            var updated = await userManager.UpdateAsync(user);
            if (!updated.Succeeded)
            {
                return AuthProblem(
                    StatusCodes.Status409Conflict,
                    "google_account_link_failed",
                    "SmartPrint could not securely link this Google account. Please try again.");
            }
        }

        var result = await userManager.AddLoginAsync(
            user,
            new UserLoginInfo(GoogleIdentityService.LoginProvider, identity.Subject, "Google"));

        return result.Succeeded
            ? null
            : AuthProblem(
                StatusCodes.Status409Conflict,
                "google_account_link_conflict",
                "This Google account is already linked to another SmartPrint identity. Contact support if you believe this is an error.");
    }

    private static async Task<object> CreateSessionAsync(
        AppUser user,
        SmartPrintDbContext db,
        TokenIssuer tokenIssuer,
        CancellationToken ct)
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
        await db.SaveChangesAsync(ct);

        return new
        {
            tokenType = "Bearer",
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            accessTokenExpiresUtc = tokens.AccessTokenExpiresUtc
        };
    }

    private static IResult GoogleProblem(GoogleIdentityException exception) =>
        AuthProblem(exception.StatusCode, exception.Code, exception.Message);

    private static IResult AuthProblem(int statusCode, string code, string detail) =>
        Results.Problem(
            statusCode: statusCode,
            title: code,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record GoogleAuthRequest(string Credential, string Nonce);
public sealed record GoogleChallengeRequest(string Surface);
