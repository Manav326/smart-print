using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class TenantStatusChangeRulesTests
{
    [Fact]
    public void NormalizesStatusAndAuditReason()
    {
        Assert.True(TenantStatusChangeRules.TryNormalize(
            new ChangeTenantStatusRequest(" Suspended ", "  Security review completed  "),
            out var status,
            out var reason));
        Assert.Equal("Suspended", status);
        Assert.Equal("Security review completed", reason);
    }

    [Theory]
    [InlineData("Unknown", "A valid audit reason")]
    [InlineData("", "A valid audit reason")]
    [InlineData("Active", "bad")]
    [InlineData("Active", "")]
    public void RejectsUnknownStatusesAndShortAuditReasons(string status, string reason)
    {
        Assert.False(TenantStatusChangeRules.TryNormalize(
            new ChangeTenantStatusRequest(status, reason), out _, out _));
    }

    [Fact]
    public void RejectsAuditReasonsLongerThanFiveHundredCharacters()
    {
        Assert.False(TenantStatusChangeRules.TryNormalize(
            new ChangeTenantStatusRequest("Active", new string('x', 501)), out _, out _));
    }

    [Fact]
    public void AcceptsFiveAndFiveHundredCharacterAuditReasons()
    {
        Assert.True(TenantStatusChangeRules.TryNormalize(
            new ChangeTenantStatusRequest("Active", new string('x', 5)), out _, out _));
        Assert.True(TenantStatusChangeRules.TryNormalize(
            new ChangeTenantStatusRequest("Active", new string('x', 500)), out _, out _));
    }
}
