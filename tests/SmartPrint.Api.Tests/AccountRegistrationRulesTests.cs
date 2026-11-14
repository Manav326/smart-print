using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AccountRegistrationRulesTests
{
    [Fact]
    public void NormalizesShopRegistrationFields()
    {
        var request = new RegisterShopRequest(
            "  Sunrise Prints  ",
            "  Ananya Sharma  ",
            "  OWNER@example.com  ",
            "strong-password");

        Assert.True(AccountRegistrationRules.TryNormalizeShop(request, out var business, out var owner, out var email));
        Assert.Equal("Sunrise Prints", business);
        Assert.Equal("Ananya Sharma", owner);
        Assert.Equal("OWNER@example.com", email);
    }

    [Fact]
    public void RejectsMissingOrOverlongShopRegistrationFields()
    {
        Assert.False(AccountRegistrationRules.TryNormalizeShop(
            new RegisterShopRequest("", "Owner", "owner@example.com", "password"), out _, out _, out _));
        Assert.False(AccountRegistrationRules.TryNormalizeShop(
            new RegisterShopRequest(new string('x', 181), "Owner", "owner@example.com", "password"), out _, out _, out _));
        Assert.False(AccountRegistrationRules.TryNormalizeShop(
            new RegisterShopRequest("Shop", new string('x', 161), "owner@example.com", "password"), out _, out _, out _));
        Assert.False(AccountRegistrationRules.TryNormalizeShop(
            new RegisterShopRequest("Shop", "Owner", new string('x', 257), "password"), out _, out _, out _));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsInvalidShopEmail(string email)
    {
        Assert.False(AccountRegistrationRules.TryNormalizeShop(
            new RegisterShopRequest("Shop", "Owner", email, "password"), out _, out _, out _));
    }

    [Fact]
    public void RejectsMissingShopPassword()
    {
        Assert.False(AccountRegistrationRules.TryNormalizeShop(
            new RegisterShopRequest("Shop", "Owner", "owner@example.com", " "), out _, out _, out _));
    }

    [Fact]
    public void NormalizesCustomerRegistrationFields()
    {
        var request = new RegisterCustomerRequest("  Priya  ", "  priya@example.com  ", "strong-password");

        Assert.True(AccountRegistrationRules.TryNormalizeCustomer(request, out var name, out var email));
        Assert.Equal("Priya", name);
        Assert.Equal("priya@example.com", email);
    }

    [Fact]
    public void RejectsOverlongCustomerName()
    {
        Assert.False(AccountRegistrationRules.TryNormalizeCustomer(
            new RegisterCustomerRequest(new string('x', 161), "customer@example.com", "password"), out _, out _));
    }
}
