using SmartPrint.Api.Modules.ShopPayments.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopPaymentSettingsRulesTests
{
    [Fact]
    public void AcceptsValidUpiAndCashConfiguration()
    {
        var errors = ShopPaymentSettingsRules.Validate(
            upiEnabled: true,
            normalizedUpiId: "smartprint@bank",
            normalizedPayeeName: "Smart Print Shop",
            cashEnabled: true);

        Assert.Empty(errors);
    }

    [Fact]
    public void AcceptsCashOnlyConfigurationWithoutUpiDetails()
    {
        var errors = ShopPaymentSettingsRules.Validate(
            upiEnabled: false,
            normalizedUpiId: null,
            normalizedPayeeName: null,
            cashEnabled: true);

        Assert.Empty(errors);
    }

    [Fact]
    public void RequiresValidUpiIdAndPayeeNameWhenUpiIsEnabled()
    {
        var errors = ShopPaymentSettingsRules.Validate(
            upiEnabled: true,
            normalizedUpiId: "invalid-upi",
            normalizedPayeeName: " ",
            cashEnabled: true);

        Assert.Contains("upi", errors.Keys);
    }

    [Fact]
    public void RejectsPayeeNamesLongerThanTheExistingLimit()
    {
        var errors = ShopPaymentSettingsRules.Validate(
            upiEnabled: true,
            normalizedUpiId: "smartprint@bank",
            normalizedPayeeName: new string('S', 121),
            cashEnabled: true);

        Assert.Contains("upi", errors.Keys);
    }

    [Fact]
    public void RejectsUpiIdsLongerThanTheExistingLimitEvenWhenUpiIsDisabled()
    {
        var errors = ShopPaymentSettingsRules.Validate(
            upiEnabled: false,
            normalizedUpiId: new string('a', 161),
            normalizedPayeeName: null,
            cashEnabled: true);

        Assert.Contains("upiId", errors.Keys);
    }

    [Fact]
    public void RequiresAtLeastOnePaymentMethod()
    {
        var errors = ShopPaymentSettingsRules.Validate(
            upiEnabled: false,
            normalizedUpiId: null,
            normalizedPayeeName: null,
            cashEnabled: false);

        Assert.Contains("methods", errors.Keys);
        Assert.Equal(
            "Enable at least one payment method so customers can pay for approved print jobs.",
            errors["methods"][0]);
    }

    [Theory]
    [InlineData("smartprint@bank", true)]
    [InlineData("shop.owner@upi-provider", true)]
    [InlineData("local-name", false)]
    [InlineData("a@bank", false)]
    [InlineData("shop@b", false)]
    [InlineData("shop name@bank", false)]
    [InlineData("shop@bank/provider", false)]
    public void ValidatesUpiIdentifiersUsingTheExistingFormat(string value, bool expected)
    {
        Assert.Equal(expected, ShopPaymentSettingsRules.IsValidUpiId(value));
    }
}
