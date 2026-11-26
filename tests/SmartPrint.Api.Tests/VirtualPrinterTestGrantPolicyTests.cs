using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class VirtualPrinterTestGrantPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FingerprintIsStableForEquivalentCaseAndWhitespace()
    {
        var agent = Guid.NewGuid();
        Assert.Equal(
            VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agent, "Office IPP", "Microsoft IPP Driver", "ipp://host/queue"),
            VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agent, " office ipp ", "microsoft ipp driver ", "IPP://HOST/QUEUE"));
    }

    [Fact]
    public void FingerprintChangesWhenAgentQueueDriverOrPortChanges()
    {
        var agent = Guid.NewGuid();
        var original = VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agent, "Office IPP", "IPP Driver", "ipp://host/queue");
        Assert.NotEqual(original, VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(Guid.NewGuid(), "Office IPP", "IPP Driver", "ipp://host/queue"));
        Assert.NotEqual(original, VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agent, "Office IPP 2", "IPP Driver", "ipp://host/queue"));
        Assert.NotEqual(original, VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agent, "Office IPP", "Other Driver", "ipp://host/queue"));
        Assert.NotEqual(original, VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agent, "Office IPP", "IPP Driver", "ipp://other/queue"));
    }

    [Fact]
    public void GrantRequiresMatchingTenantAgentAndCurrentIdentity()
    {
        var tenant = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var fingerprint = VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agent, "Queue", "Driver", "ipp://host");
        Assert.True(VirtualPrinterTestGrantPolicy.IsGrantActive(tenant, tenant, agent, agent, fingerprint, fingerprint, Now.AddDays(-1), Now.AddDays(1), null, Now));
        Assert.False(VirtualPrinterTestGrantPolicy.IsGrantActive(tenant, Guid.NewGuid(), agent, agent, fingerprint, fingerprint, Now.AddDays(-1), Now.AddDays(1), null, Now));
        Assert.False(VirtualPrinterTestGrantPolicy.IsGrantActive(tenant, tenant, Guid.NewGuid(), agent, fingerprint, fingerprint, Now.AddDays(-1), Now.AddDays(1), null, Now));
        Assert.False(VirtualPrinterTestGrantPolicy.IsGrantActive(tenant, tenant, agent, agent, "changed", fingerprint, Now.AddDays(-1), Now.AddDays(1), null, Now));
    }

    [Fact]
    public void RevokedExpiredAndNotYetEffectiveGrantsAreRejected()
    {
        var tenant = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var fingerprint = "ABC";
        Assert.False(VirtualPrinterTestGrantPolicy.IsGrantActive(tenant, tenant, agent, agent, fingerprint, fingerprint, Now.AddDays(-1), Now.AddDays(1), Now.AddHours(-1), Now));
        Assert.False(VirtualPrinterTestGrantPolicy.IsGrantActive(tenant, tenant, agent, agent, fingerprint, fingerprint, Now.AddDays(-2), Now, null, Now));
        Assert.False(VirtualPrinterTestGrantPolicy.IsGrantActive(tenant, tenant, agent, agent, fingerprint, fingerprint, Now.AddMinutes(1), Now.AddDays(1), null, Now));
    }

    [Theory]
    [InlineData("UNSUPPORTED_VIRTUAL", "Ready", true, true)]
    [InlineData("UNSUPPORTED_VIRTUAL", "Busy", true, true)]
    [InlineData("UNSUPPORTED_VIRTUAL", "Offline", true, false)]
    [InlineData("UNSUPPORTED_VIRTUAL", "Ready", false, false)]
    [InlineData("PHYSICAL", "Ready", true, false)]
    [InlineData("PDF_EXPORT", "Ready", true, false)]
    public void OnlyGrantedFreshReadyVirtualQueuesAreEligible(
        string outputMode, string status, bool grantActive, bool expected)
    {
        Assert.Equal(expected, VirtualPrinterTestGrantPolicy.IsEligibleForSelection(
            outputMode, status, Now.AddSeconds(-30), grantActive, Now));
    }

    [Fact]
    public void SelectionRejectsStaleInventory()
    {
        Assert.False(VirtualPrinterTestGrantPolicy.IsEligibleForSelection(
            "UNSUPPORTED_VIRTUAL", "Ready", Now.AddMinutes(-2).AddTicks(-1), true, Now));
    }

    [Fact]
    public void GrantLifetimeMustBeBetweenOneHourAndThirtyDays()
    {
        Assert.True(VirtualPrinterTestGrantPolicy.IsValidGrantLifetime(Now, Now.AddDays(7)));
        Assert.False(VirtualPrinterTestGrantPolicy.IsValidGrantLifetime(Now, Now.AddMinutes(59)));
        Assert.False(VirtualPrinterTestGrantPolicy.IsValidGrantLifetime(Now, Now.AddDays(30).AddTicks(1)));
        Assert.False(VirtualPrinterTestGrantPolicy.IsValidGrantLifetime(Now.AddHours(1), Now));
    }
}
