using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopPaymentSettingsRulesTests
{
    private static ShopPaymentSettingsRequest Request(
        bool upi = true,
        string? upiId = "shop@bank",
        string? payee = "Shop",
        bool cash = false) =>
        new(upi, upiId, payee, cash);

    [Fact]
    public void NormalizesUpiAndPayeeWhenValid()
    {
        var failure = ShopPaymentSettingsRules.Validate(
            Request(upiId: "  shop@bank  ", payee: "  Main Shop  "),
            out var upi,
            out var payee);

        Assert.Equal(ShopPaymentSettingsValidationFailure.None, failure);
        Assert.Equal("shop@bank", upi);
        Assert.Equal("Main Shop", payee);
    }

    [Theory]
    [InlineData("x@bank")]
    [InlineData("shop@")]
    [InlineData("shop@bank@extra")]
    [InlineData("shop name@bank")]
    [InlineData("shop#name@bank")]
    public void RejectsMalformedUpiIdsWhenUpiIsEnabled(string upiId)
    {
        Assert.Equal(
            ShopPaymentSettingsValidationFailure.InvalidUpiConfiguration,
            ShopPaymentSettingsRules.Validate(Request(upiId: upiId), out _, out _));
    }

    [Fact]
    public void RequiresPayeeNameWhenUpiIsEnabled()
    {
        Assert.Equal(
            ShopPaymentSettingsValidationFailure.InvalidUpiConfiguration,
            ShopPaymentSettingsRules.Validate(Request(payee: "  "), out _, out _));
        Assert.Equal(
            ShopPaymentSettingsValidationFailure.InvalidUpiConfiguration,
            ShopPaymentSettingsRules.Validate(Request(payee: new string('x', 121)), out _, out _));
    }

    [Fact]
    public void RejectsOverlongUpiIdEvenWhenUpiIsDisabled()
    {
        Assert.Equal(
            ShopPaymentSettingsValidationFailure.UpiIdTooLong,
            ShopPaymentSettingsRules.Validate(Request(upi: false, upiId: new string('x', 161)), out _, out _));
    }

    [Fact]
    public void RequiresAtLeastOneEnabledPaymentMethod()
    {
        Assert.Equal(
            ShopPaymentSettingsValidationFailure.NoPaymentMethodEnabled,
            ShopPaymentSettingsRules.Validate(Request(upi: false, cash: false), out _, out _));
        Assert.Equal(
            ShopPaymentSettingsValidationFailure.None,
            ShopPaymentSettingsRules.Validate(Request(upi: false, cash: true), out _, out _));
    }
}
