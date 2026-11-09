using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class TenantAccessRulesTests
{
    [Theory]
    [InlineData("Active", true)]
    [InlineData("active", true)]
    [InlineData("Suspended", false)]
    [InlineData("Inactive", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyAnExplicitActiveTenantCanUseShopOperations(string? status, bool expected)
        => Assert.Equal(expected, TenantAccessRules.IsActive(status));

    
    [Theory]
    [InlineData("ACTIVE", true)]
    [InlineData("aCtIvE", true)]
    [InlineData(" Active ", false)]
    [InlineData("Active ", false)]
    [InlineData("Trial", false)]
    [InlineData("Pending", false)]
    public void RequiresAnExactActiveStatusIgnoringOnlyLetterCase(string? status, bool expected)
        => Assert.Equal(expected, TenantAccessRules.IsActive(status));

    [Theory]
    [InlineData("GET", "/api/v1/shop/profile", true, false)]
    [InlineData("HEAD", "/api/v1/shop/profile", true, false)]
    [InlineData("GET", "/api/v1/shop/profile/", true, false)]
    [InlineData("GET", "/api/v1/shop/print-jobs", true, true)]
    [InlineData("GET", "/api/v1/shop/payments", true, true)]
    [InlineData("HEAD", "/api/v1/shop/refunds", true, true)]
    [InlineData("GET", "/api/v1/shop/profile", false, true)]
    [InlineData("POST", "/api/v1/shop/print-jobs", true, false)]
    [InlineData("DELETE", "/api/v1/shop/profile", true, false)]
    public void SuspendedShopRecoveryReadsExcludeTheShopProfile(
        string method, string path, bool shopWorkspace, bool expected)
        => Assert.Equal(expected, TenantAccessRules.IsReadRecoveryOperation(method, path, shopWorkspace));
}
