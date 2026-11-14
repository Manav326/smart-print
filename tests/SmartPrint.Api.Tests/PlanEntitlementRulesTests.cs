using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PlanEntitlementRulesTests
{
    [Fact]
    public void UnsetQuotaIsUnlimited()
    {
        Assert.True(PlanEntitlementRules.CanAdd(null, 0));
        Assert.True(PlanEntitlementRules.CanAdd(null, 1000));
    }

    [Fact]
    public void QuotaAllowsCreationUntilConfiguredCapacityIsReached()
    {
        Assert.True(PlanEntitlementRules.CanAdd(3, 0));
        Assert.True(PlanEntitlementRules.CanAdd(3, 2));
        Assert.True(PlanEntitlementRules.CanAdd(3, 1, 2));
        Assert.False(PlanEntitlementRules.CanAdd(3, 3));
        Assert.False(PlanEntitlementRules.CanAdd(3, 2, 2));
    }

    [Fact]
    public void QuotaRejectsInvalidCountsAndRequests()
    {
        Assert.False(PlanEntitlementRules.CanAdd(3, -1));
        Assert.False(PlanEntitlementRules.CanAdd(3, 0, -1));
        Assert.False(PlanEntitlementRules.CanAdd(0, 0));
    }

    [Fact]
    public void SubscriptionAccessRequiresAnActiveStatusAndAnUnexpiredPeriod()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        Assert.True(PlanEntitlementRules.IsSubscriptionAccessActive("Trial", now.AddMinutes(1), now));
        Assert.True(PlanEntitlementRules.IsSubscriptionAccessActive("Active", now.AddDays(1), now));
        Assert.False(PlanEntitlementRules.IsSubscriptionAccessActive("Expired", now.AddDays(1), now));
        Assert.False(PlanEntitlementRules.IsSubscriptionAccessActive("Cancelled", now.AddDays(1), now));
        Assert.False(PlanEntitlementRules.IsSubscriptionAccessActive("Trial", now, now));
        Assert.False(PlanEntitlementRules.IsSubscriptionAccessActive("Active", now.AddTicks(-1), now));
    }

    [Fact]
    public void EffectiveStatusBecomesExpiredWhenTrialOrPaidPeriodEnds()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("Expired", PlanEntitlementRules.GetEffectiveSubscriptionStatus("Active", now, now));
        Assert.Equal("Expired", PlanEntitlementRules.GetEffectiveSubscriptionStatus("Trial", now.AddTicks(-1), now));
        Assert.Equal("Active", PlanEntitlementRules.GetEffectiveSubscriptionStatus("Active", now.AddTicks(1), now));
        Assert.Equal("Superseded", PlanEntitlementRules.GetEffectiveSubscriptionStatus("Superseded", now.AddDays(-1), now));
        Assert.Equal("Unconfigured", PlanEntitlementRules.GetEffectiveSubscriptionStatus(null, now, now));
    }

    [Fact]
    public void DocumentSizeMustBePositiveAndNoGreaterThanPlanMaximum()
    {
        Assert.True(PlanEntitlementRules.AllowsDocument(null, 1));
        Assert.True(PlanEntitlementRules.AllowsDocument(1_000_000, 1_000_000));
        Assert.False(PlanEntitlementRules.AllowsDocument(1_000_000, 1_000_001));
        Assert.False(PlanEntitlementRules.AllowsDocument(1_000_000, 0));
        Assert.False(PlanEntitlementRules.AllowsDocument(0, 1));
    }

    
    [Fact]
    public void ZeroAdditionalQuotaRequestIsAllowedWithoutCrossingCapacity()
    {
        Assert.True(PlanEntitlementRules.CanAdd(3, 3, 0));
        Assert.True(PlanEntitlementRules.CanAdd(null, 0, 0));
    }

    [Fact]
    public void RejectsNegativeMaximumAndAlreadyExceededCapacity()
    {
        Assert.False(PlanEntitlementRules.CanAdd(-1, 0));
        Assert.False(PlanEntitlementRules.CanAdd(3, 4, 0));
    }

    [Theory]
    [InlineData("trial", true)]
    [InlineData("ACTIVE", true)]
    [InlineData("Suspended", false)]
    [InlineData("Inactive", false)]
    [InlineData("PastDue", false)]
    [InlineData("", false)]
    public void SubscriptionStatusComparisonIsCaseInsensitiveButNotPermissive(string status, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(expected, PlanEntitlementRules.IsSubscriptionAccessActive(status, now.AddMinutes(1), now));
    }

    [Fact]
    public void DocumentEntitlementRejectsNegativeAndZeroLimitsAndRequests()
    {
        Assert.False(PlanEntitlementRules.AllowsDocument(100, -1));
        Assert.False(PlanEntitlementRules.AllowsDocument(-1, 1));
        Assert.False(PlanEntitlementRules.AllowsDocument(0, 1));
    }
}
