using System.Security.Claims;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Security;

/// <summary>
/// Keeps Google login aligned with SmartPrint's separate shop and customer principals.
/// A provider identity can never turn one account type into the other.
/// </summary>
public static class GoogleAuthEndpointRules
{
    public static bool CanAuthenticateShop(AppUser user, IEnumerable<Claim> claims) =>
        user.TenantId is not null &&
        claims.Any(claim =>
            claim.Type == "shop_role" &&
            claim.Value is "ShopOwner" or "ShopManager");

    public static bool CanAuthenticateCustomer(AppUser user, IEnumerable<Claim> claims) =>
        CustomerIdentity.IsCustomer(user, claims);
}
