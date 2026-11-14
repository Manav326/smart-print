using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class SubscriptionPlanModelTests
{
    [Fact]
    public void PlanCatalogueHasStableCodesAndImmutableVersionIdentity()
    {
        using var db = CreateContext();

        var plan = db.Model.FindEntityType(typeof(SubscriptionPlan));
        var version = db.Model.FindEntityType(typeof(SubscriptionPlanVersion));

        Assert.NotNull(plan);
        Assert.NotNull(version);
        Assert.Equal("SubscriptionPlans", plan.GetTableName());
        Assert.Equal("SubscriptionPlanVersions", version.GetTableName());
        Assert.Contains(plan.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual(new[] { "Code" }));
        Assert.Contains(version.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual(new[] { "PlanId", "Version" }));
        Assert.Contains(version.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { "PlanId" }) &&
            index.GetFilter() == "\"Status\" = 'Draft'");
    }

    [Fact]
    public void SubscriptionKeepsARestrictiveReferenceToItsCommercialTerms()
    {
        using var db = CreateContext();
        var subscription = db.Model.FindEntityType(typeof(TenantSubscription));
        Assert.NotNull(subscription);

        var planVersionRelationship = Assert.Single(
            subscription.GetForeignKeys(),
            relationship => relationship.Properties.Any(property =>
                property.Name == nameof(TenantSubscription.PlanVersionId)));

        Assert.Equal(DeleteBehavior.Restrict, planVersionRelationship.DeleteBehavior);
        Assert.True(planVersionRelationship.Properties.Single().IsNullable);
    }

    [Fact]
    public void TrialPlanReferencesAreStableAndDoNotContainCommercialDefaults()
    {
        var plan = new SubscriptionPlan
        {
            Id = SubscriptionPlanDefaults.TrialPlanId,
            Code = "TRIAL",
            IsSystemPlan = true
        };
        var version = new SubscriptionPlanVersion
        {
            Id = SubscriptionPlanDefaults.TrialVersionId,
            PlanId = SubscriptionPlanDefaults.TrialPlanId,
            Status = "Published",
            MonthlyPriceMinor = null,
            AnnualPriceMinor = null,
            MaxPrinters = null,
            MaxAgentDevices = null,
            MaxQrStations = null
        };

        Assert.Equal(plan.Id, version.PlanId);
        Assert.Null(version.MonthlyPriceMinor);
        Assert.Null(version.AnnualPriceMinor);
        Assert.Null(version.MaxPrinters);
        Assert.Null(version.MaxAgentDevices);
        Assert.Null(version.MaxQrStations);
        Assert.True(plan.IsSystemPlan);
    }

    [Fact]
    public void PlatformSubscriptionAuditHasUniqueIdempotencyAndAssignmentKeys()
    {
        using var db = CreateContext();
        var audit = db.Model.FindEntityType(typeof(PlatformSubscriptionAudit));

        Assert.NotNull(audit);
        Assert.Equal("PlatformSubscriptionAudits", audit.GetTableName());
        Assert.Contains(audit.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual(new[] { "IdempotencyKey" }));
        Assert.Contains(audit.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual(new[] { "AssignedSubscriptionId" }));
        Assert.Equal(100, audit.FindProperty(nameof(PlatformSubscriptionAudit.PaymentReference))?.GetMaxLength());
        Assert.Contains(audit.GetForeignKeys(), relationship =>
            relationship.Properties.Any(property => property.Name == nameof(PlatformSubscriptionAudit.TenantId)) &&
            relationship.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(audit.GetForeignKeys(), relationship =>
            relationship.Properties.Any(property => property.Name == nameof(PlatformSubscriptionAudit.PlanVersionId)) &&
            relationship.DeleteBehavior == DeleteBehavior.Restrict);
    }

    private static SmartPrintDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SmartPrintDbContext>()
            .UseNpgsql("Host=localhost;Database=qrtoprint;Username=postgres;Password=postgres")
            .Options;
        return new SmartPrintDbContext(options);
    }
}
