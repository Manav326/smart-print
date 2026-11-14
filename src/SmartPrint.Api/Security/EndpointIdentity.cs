using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace SmartPrint.Api.Security;

public static class EndpointIdentity
{
    public static bool TryGetTenantId(ClaimsPrincipal principal, out Guid tenantId) =>
        Guid.TryParse(principal.FindFirstValue("tenant_id"), out tenantId);

    public static Guid? GetActorId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var actorId) ? actorId : null;

    public static bool IsShopOwner(ClaimsPrincipal principal) =>
        principal.FindAll("shop_role").Any(claim => claim.Value is "ShopOwner" or "ShopManager");
}
