using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class CustomerSessionHistoryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActiveUnexpiredSessionCanReadItsOwnStatus()
        => Assert.True(CustomerSessionHistoryRules.CanReadStatus("Active", null, Now.AddMinutes(1), Now));

    [Fact]
    public void ExpiredSessionCanReadStatusDuringTheReadOnlyGracePeriod()
        => Assert.True(CustomerSessionHistoryRules.CanReadStatus("Active", null, Now.AddDays(-6), Now));

    [Fact]
    public void ExactGraceBoundaryRemainsReadable()
        => Assert.True(CustomerSessionHistoryRules.CanReadStatus(
            "Active", null, Now - CustomerSessionHistoryRules.ReadOnlyStatusGracePeriod, Now));

    [Fact]
    public void SessionOutsideGracePeriodCannotReadStatus()
        => Assert.False(CustomerSessionHistoryRules.CanReadStatus("Active", null, Now.AddDays(-8), Now));

    [Fact]
    public void RevokedSessionCannotReadStatusEvenInsideGracePeriod()
        => Assert.False(CustomerSessionHistoryRules.CanReadStatus("Active", Now.AddMinutes(-1), Now.AddDays(-1), Now));

    [Theory]
    [InlineData("Expired")]
    [InlineData("Revoked")]
    [InlineData("Closed")]
    [InlineData(null)]
    public void NonActiveSessionCannotReadStatus(string? status)
        => Assert.False(CustomerSessionHistoryRules.CanReadStatus(status, null, Now.AddDays(1), Now));
}
