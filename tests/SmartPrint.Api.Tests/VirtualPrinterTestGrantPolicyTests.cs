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
            VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agent, " office ipp ", "microsoft ipp driver ", "IPP://HOST/queue"));
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
    public void IppPortFingerprintPreservesCaseSensitivePathAndQuery()
    {
        var agent = Guid.NewGuid();
        var original = VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(
            agent, "Android Virtual", "Microsoft IPP Class Driver", "ipp://ANDROID.local:631/Printer/Office?mode=Color");
        Assert.Equal(original, VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(
            agent, " android virtual ", "microsoft ipp class driver", "IPP://android.LOCAL:631/Printer/Office?mode=Color"));
        Assert.NotEqual(original, VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(
            agent, "Android Virtual", "Microsoft IPP Class Driver", "ipp://android.local:631/printer/Office?mode=Color"));
        Assert.NotEqual(original, VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(
            agent, "Android Virtual", "Microsoft IPP Class Driver", "ipp://android.local:631/Printer/Office?mode=color"));
    }

    [Fact]
    public void GrantRequiresMatchingShopAgentAndCurrentQueueIdentity()
    {
        var tenant = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var fingerprint = VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agent, "Queue", "Driver", "ipp://host");
        Assert.True(Active(tenant, tenant, tenant, agent, agent, fingerprint, fingerprint));
        Assert.False(Active(tenant, Guid.NewGuid(), tenant, agent, agent, fingerprint, fingerprint));
        Assert.False(Active(tenant, tenant, Guid.NewGuid(), agent, agent, fingerprint, fingerprint));
        Assert.False(Active(tenant, tenant, tenant, Guid.NewGuid(), agent, fingerprint, fingerprint));
        Assert.False(Active(tenant, tenant, tenant, agent, agent, "changed", fingerprint));
    }

    [Fact]
    public void RevokedExpiredAndNotYetEffectiveGrantsAreRejected()
    {
        var tenant = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var fingerprint = "ABC";
        Assert.False(Active(tenant, tenant, tenant, agent, agent, fingerprint, fingerprint,
            createdUtc: Now.AddDays(-1), expiresUtc: Now.AddDays(1), revokedUtc: Now.AddHours(-1)));
        Assert.False(Active(tenant, tenant, tenant, agent, agent, fingerprint, fingerprint,
            createdUtc: Now.AddDays(-2), expiresUtc: Now));
        Assert.False(Active(tenant, tenant, tenant, agent, agent, fingerprint, fingerprint,
            createdUtc: Now.AddMinutes(1), expiresUtc: Now.AddDays(1)));
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
            outputMode, status, Now.AddSeconds(-30), grantActive, Now, false));
    }

    [Fact]
    public void SelectionRejectsVirtualQueueInProductionEvenWithGrant()
    {
        Assert.False(VirtualPrinterTestGrantPolicy.IsEligibleForSelection(
            "UNSUPPORTED_VIRTUAL", "Ready", Now.AddSeconds(-30), true, Now, true));
    }

    [Fact]
    public void SelectionRejectsStaleInventory()
    {
        Assert.False(VirtualPrinterTestGrantPolicy.IsEligibleForSelection(
            "UNSUPPORTED_VIRTUAL", "Ready", Now.AddMinutes(-2).AddTicks(-1), true, Now, false));
    }

    [Fact]
    public void ThreeShopIsolationScenarioAllowsOnlyApprovedShopAndHonoursRevocation()
    {
        var shopA = Guid.NewGuid();
        var shopB = Guid.NewGuid();
        var shopC = Guid.NewGuid();
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        var agentC = Guid.NewGuid();
        var fingerprintA = VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agentA, "IPP A", "Driver A", "ipp://a/print");
        var fingerprintB = VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agentB, "IPP B", "Driver B", "ipp://b/print");
        var fingerprintC = VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(agentC, "IPP C", "Driver C", "ipp://c/print");

        Assert.True(Active(shopA, shopA, shopA, agentA, agentA, fingerprintA, fingerprintA));
        // Shop B attempts to borrow the grant record from Shop A.
        Assert.False(Active(shopB, shopB, shopA, agentB, agentA, fingerprintB, fingerprintA));
        // Shop C's previously valid approval is revoked.
        Assert.False(Active(shopC, shopC, shopC, agentC, agentC, fingerprintC, fingerprintC,
            revokedUtc: Now.AddMinutes(-1)));
    }

    [Theory]
    [InlineData("UNSUPPORTED_VIRTUAL", "TEST_VIRTUAL", true, false, true)]
    [InlineData("UNSUPPORTED_VIRTUAL", "TEST_VIRTUAL", false, false, false)]
    [InlineData("UNSUPPORTED_VIRTUAL", "PHYSICAL", true, false, false)]
    [InlineData("UNSUPPORTED_VIRTUAL", "TEST_VIRTUAL", true, true, false)]
    [InlineData("PHYSICAL", "PHYSICAL", false, false, true)]
    [InlineData("PHYSICAL", "TEST_VIRTUAL", true, false, false)]
    [InlineData("PDF_EXPORT", "PDF_EXPORT", true, false, false)]
    public void FinalSubmissionRequiresMatchingOutputClassificationAndEnvironment(
        string outputMode, string outputType, bool grantActive, bool isProduction, bool expected)
    {
        Assert.Equal(expected, VirtualPrinterTestGrantPolicy.CanAuthorizeSpoolerSubmission(
            outputMode, outputType, grantActive, isProduction));
    }

    [Fact]
    public void GrantRevokedOrExpiredDuringDocumentPreparationIsInactiveAtSubmissionBoundary()
    {
        var tenant = Guid.NewGuid();
        var agent = Guid.NewGuid();
        const string fingerprint = "queue-identity";
        var preparedUtc = Now;
        var submittedUtc = preparedUtc.AddMinutes(1);

        Assert.True(VirtualPrinterTestGrantPolicy.IsGrantActive(
            tenant, tenant, tenant, agent, agent, fingerprint, fingerprint,
            preparedUtc.AddMinutes(-1), submittedUtc.AddMinutes(1), null, preparedUtc));

        Assert.False(VirtualPrinterTestGrantPolicy.IsGrantActive(
            tenant, tenant, tenant, agent, agent, fingerprint, fingerprint,
            preparedUtc.AddMinutes(-1), submittedUtc.AddMinutes(1), preparedUtc.AddSeconds(30), submittedUtc));
        Assert.False(VirtualPrinterTestGrantPolicy.IsGrantActive(
            tenant, tenant, tenant, agent, agent, fingerprint, fingerprint,
            preparedUtc.AddMinutes(-1), preparedUtc, null, submittedUtc));
        Assert.False(VirtualPrinterTestGrantPolicy.IsGrantActive(
            tenant, tenant, tenant, agent, agent, fingerprint, "changed-queue",
            preparedUtc.AddMinutes(-1), submittedUtc.AddMinutes(1), null, submittedUtc));
    }

    [Fact]
    public void GrantLifetimeMustBeBetweenOneHourAndThirtyDays()
    {
        Assert.True(VirtualPrinterTestGrantPolicy.IsValidGrantLifetime(Now, Now.AddDays(7)));
        Assert.False(VirtualPrinterTestGrantPolicy.IsValidGrantLifetime(Now, Now.AddMinutes(59)));
        Assert.False(VirtualPrinterTestGrantPolicy.IsValidGrantLifetime(Now, Now.AddDays(30).AddTicks(1)));
        Assert.False(VirtualPrinterTestGrantPolicy.IsValidGrantLifetime(Now.AddHours(1), Now));
    }

    private static bool Active(
        Guid tenantId,
        Guid printerTenantId,
        Guid grantTenantId,
        Guid? printerAgentId,
        Guid grantAgentId,
        string? printerFingerprint,
        string grantFingerprint,
        DateTimeOffset? createdUtc = null,
        DateTimeOffset? expiresUtc = null,
        DateTimeOffset? revokedUtc = null) =>
        VirtualPrinterTestGrantPolicy.IsGrantActive(
            tenantId, printerTenantId, grantTenantId, printerAgentId, grantAgentId,
            printerFingerprint, grantFingerprint,
            createdUtc ?? Now.AddHours(-1), expiresUtc ?? Now.AddDays(1), revokedUtc, Now);
}
