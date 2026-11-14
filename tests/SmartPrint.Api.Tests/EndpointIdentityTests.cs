using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class EndpointIdentityTests
{
    [Fact]
    public void ReadsTenantIdOnlyFromAValidTenantClaim()
    {
        var tenantId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tenantId.ToString())], "test"));

        Assert.True(EndpointIdentity.TryGetTenantId(principal, out var resolved));
        Assert.Equal(tenantId, resolved);
    }

    [Fact]
    public void RejectsMissingOrMalformedTenantClaim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", "not-a-guid")], "test"));

        Assert.False(EndpointIdentity.TryGetTenantId(principal, out _));
    }

    [Fact]
    public void ReadsActorIdFromTheSubjectClaim()
    {
        var actorId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, actorId.ToString())], "test"));

        Assert.Equal(actorId, EndpointIdentity.GetActorId(principal));
    }

    [Fact]
    public void AcceptsShopOwnerAndManagerRolesButNotCustomer()
    {
        static ClaimsPrincipal PrincipalFor(string role) =>
            new(new ClaimsIdentity([new Claim("shop_role", role)], "test"));

        Assert.True(EndpointIdentity.IsShopOwner(PrincipalFor("ShopOwner")));
        Assert.True(EndpointIdentity.IsShopOwner(PrincipalFor("ShopManager")));
        Assert.False(EndpointIdentity.IsShopOwner(PrincipalFor("Customer")));
    }
}
