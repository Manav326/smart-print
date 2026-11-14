using System.Security.Claims;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class GoogleAuthEndpointRulesTests
{
    [Fact]
    public void ShopGoogleLoginRequiresTenantScopedOwnerOrManager()
    {
        var tenantId = Guid.NewGuid();
        var owner = new AppUser { Id = Guid.NewGuid(), TenantId = tenantId };
        var manager = new AppUser { Id = Guid.NewGuid(), TenantId = tenantId };
        var global = new AppUser { Id = Guid.NewGuid(), TenantId = null };

        Assert.True(GoogleAuthEndpointRules.CanAuthenticateShop(owner, [new Claim("shop_role", "ShopOwner")]));
        Assert.True(GoogleAuthEndpointRules.CanAuthenticateShop(manager, [new Claim("shop_role", "ShopManager")]));
        Assert.False(GoogleAuthEndpointRules.CanAuthenticateShop(global, [new Claim("shop_role", "ShopOwner")]));
        Assert.False(GoogleAuthEndpointRules.CanAuthenticateShop(owner, [new Claim("shop_role", "Operator")]));
    }

    [Fact]
    public void CustomerGoogleLoginRequiresGlobalCustomerPrincipal()
    {
        var customer = new AppUser { Id = Guid.NewGuid(), TenantId = null };
        var shopOwner = new AppUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var customerClaim = new[] { new Claim(CustomerIdentity.RoleClaim, CustomerIdentity.RoleValue) };

        Assert.True(GoogleAuthEndpointRules.CanAuthenticateCustomer(customer, customerClaim));
        Assert.False(GoogleAuthEndpointRules.CanAuthenticateCustomer(shopOwner, customerClaim));
        Assert.False(GoogleAuthEndpointRules.CanAuthenticateCustomer(customer, [new Claim("shop_role", "ShopOwner")]));
    }
}
