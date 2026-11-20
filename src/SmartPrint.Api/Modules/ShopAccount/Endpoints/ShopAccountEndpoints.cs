using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.RateLimiting;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.ShopAccount.Services;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.ShopAccount.Endpoints;

/// <summary>
/// HTTP adapter for shop-owner account updates. Password verification, lockout,
/// email uniqueness and the update transaction are owned by the application service.
/// </summary>
public static class ShopAccountEndpoints
{
    public static RouteGroupBuilder MapShopAccountEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPut("/account", async (
            ShopOwnerAccountRequest request,
            ClaimsPrincipal principal,
            ShopAccountService service,
            CancellationToken ct) =>
        {
            if (!ShopIdentity.CanManageShop(principal) ||
                !TenantIdentity.TryGetTenantId(principal, out var tenantId) ||
                !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
                return Results.Forbid();

            var result = await service.UpdateAsync(tenantId, userId, request, ct);
            return result.Kind switch
            {
                ShopAccountUpdateKind.InvalidRequest =>
                    Results.ValidationProblem(result.ValidationErrors!),
                ShopAccountUpdateKind.Forbidden =>
                    Results.Forbid(),
                ShopAccountUpdateKind.CurrentPasswordRequired =>
                    Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["currentPassword"] = ["Enter your current password to change your sign-in email."]
                    }),
                ShopAccountUpdateKind.LockedOut =>
                    Results.Problem(statusCode: StatusCodes.Status423Locked,
                        title: "This account is temporarily locked after repeated password attempts. Try again later."),
                ShopAccountUpdateKind.IncorrectPassword =>
                    Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["currentPassword"] = ["The current password is incorrect."]
                    }),
                ShopAccountUpdateKind.EmailAlreadyRegistered =>
                    Results.Conflict(new
                    {
                        code = "email_already_registered",
                        message = "That email address is already used by another account."
                    }),
                _ => Results.Ok(result.Response)
            };
        }).RequireRateLimiting("authentication");

        return shop;
    }
}
