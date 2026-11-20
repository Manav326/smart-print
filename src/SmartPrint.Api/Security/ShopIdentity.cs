using System.Security.Claims;

namespace SmartPrint.Api.Security;

/// <summary>
/// Resolves the existing shop owner/manager role boundary from the authenticated principal.
/// </summary>
public static class ShopIdentity
{
    public static bool CanManageShop(ClaimsPrincipal principal) =>
        principal.FindAll("shop_role").Any(claim => claim.Value is "ShopOwner" or "ShopManager");
}
