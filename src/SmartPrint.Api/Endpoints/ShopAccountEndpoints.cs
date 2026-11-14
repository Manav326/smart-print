using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.RateLimiting;
using SmartPrint.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopAccountEndpoints
{
    public static RouteGroupBuilder MapShopAccountEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/profile", async (
            ClaimsPrincipal principal,
            ShopAccountService accounts,
            CancellationToken ct) =>
        {
            if (!principal.FindAll("shop_role").Any(claim => claim.Value == "ShopOwner") ||
                !TryGetTenantId(principal, out var tenantId) ||
                !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            {
                return Results.Forbid();
            }

            var result = await accounts.GetProfileAsync(tenantId, userId, ct);
            if (result.Failure == ShopAccountProfileReadFailure.OwnerNotFound)
            {
                return Results.Forbid();
            }
            if (result.Failure == ShopAccountProfileReadFailure.NotFound)
            {
                return Results.NotFound();
            }

            return Results.Ok(result.Profile);
        });

        shop.MapPut("/account", async (
            ShopOwnerAccountRequest request,
            ClaimsPrincipal principal,
            ShopAccountService accounts,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId) ||
                !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            {
                return Results.Forbid();
            }

            var result = await accounts.UpdateOwnerAccountAsync(tenantId, userId, request, ct);
            if (result.Failure == ShopOwnerAccountUpdateFailure.InvalidInput)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["account"] = ["Enter a name between 1 and 160 characters and a valid email address."]
                });
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.OwnerNotFound)
                return Results.Forbid();

            if (result.Failure == ShopOwnerAccountUpdateFailure.CurrentPasswordRequired)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["Enter your current password to change your sign-in email."]
                });
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.AccountLocked)
            {
                return Results.Problem(statusCode: StatusCodes.Status423Locked,
                    title: "This account is temporarily locked after repeated password attempts. Try again later.");
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.InvalidCurrentPassword)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["The current password is incorrect."]
                });
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.EmailAlreadyRegistered)
            {
                return Results.Conflict(new
                {
                    code = "email_already_registered",
                    message = "That email address is already used by another account."
                });
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.IdentityUpdateFailed)
                return Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>());

            var owner = result.User!;
            return Results.Ok(new
            {
                OwnerName = owner.DisplayName,
                OwnerEmail = owner.Email ?? string.Empty,
                EmailChanged = result.EmailChanged
            });
        }).RequireRateLimiting("authentication");

        shop.MapPut("/profile", async (
            ShopProfileRequest request,
            ClaimsPrincipal principal,
            ShopAccountService accounts,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await accounts.UpdateProfileAsync(tenantId, request, ct);
            if (result.Failure == ShopProfileUpdateFailure.InvalidInput)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["profile"] = ["Provide a display name, a six-digit hex accent colour, and an optional HTTPS logo URL."]
                });
            }

            if (result.Failure == ShopProfileUpdateFailure.CustomBrandingNotIncluded)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Custom shop branding is not included in this shop's current plan.");
            }

            if (result.Failure == ShopProfileUpdateFailure.NotFound)
                return Results.NotFound();

            var profile = result.Profile!;
            return Results.Ok(new
            {
                profile.DisplayName,
                profile.LogoUrl,
                profile.AccentColor,
                profile.UpdatedUtc
            });
        });

        return shop;
    }
}
