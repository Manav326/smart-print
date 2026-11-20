using System.Security.Cryptography;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.AgentConnectivity.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentHeartbeatRulesTests
{
    [Fact]
    public void MissingKeyIsAllowedBecauseHeartbeatMustClearAnyPreviouslyPersistedKey()
    {
        Assert.Equal(AgentDocumentEncryptionKeyFailure.None,
            AgentHeartbeatRules.ValidateDocumentEncryptionPublicKey(null));
        Assert.Equal(AgentDocumentEncryptionKeyFailure.None,
            AgentHeartbeatRules.ValidateDocumentEncryptionPublicKey(" "));
    }

    [Fact]
    public void AcceptsRsa2048PublicKey()
    {
        using var rsa = RSA.Create(2048);
        var key = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        Assert.Equal(AgentDocumentEncryptionKeyFailure.None,
            AgentHeartbeatRules.ValidateDocumentEncryptionPublicKey(key));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("e30=")]
    public void RejectsMalformedOrNonRsaPublicKeys(string key)
    {
        Assert.Equal(AgentDocumentEncryptionKeyFailure.InvalidPublicKey,
            AgentHeartbeatRules.ValidateDocumentEncryptionPublicKey(key));
    }

    [Fact]
    public void RejectsRsaKeysBelowTheMinimumStrength()
    {
        using var rsa = RSA.Create(1024);
        var key = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        Assert.Equal(AgentDocumentEncryptionKeyFailure.UnsupportedKeySize,
            AgentHeartbeatRules.ValidateDocumentEncryptionPublicKey(key));
    }

    [Fact]
    public void AcceptsOnlyNonNegativeFreshStorageSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = new AgentStorageSnapshotRequest(1, 2, 3, 4, 5, now);

        Assert.True(AgentHeartbeatRules.IsStorageSnapshotUsable(fresh, now));
        Assert.True(AgentHeartbeatRules.IsStorageSnapshotUsable(
            fresh with { MeasuredUtc = now.AddHours(-1) }, now));
        Assert.True(AgentHeartbeatRules.IsStorageSnapshotUsable(
            fresh with { MeasuredUtc = now.AddMinutes(1) }, now));
        Assert.False(AgentHeartbeatRules.IsStorageSnapshotUsable(
            fresh with { LogicalDocumentBytes = -1 }, now));
        Assert.False(AgentHeartbeatRules.IsStorageSnapshotUsable(
            fresh with { MeasuredUtc = now.AddHours(-1).AddTicks(-1) }, now));
        Assert.False(AgentHeartbeatRules.IsStorageSnapshotUsable(
            fresh with { MeasuredUtc = now.AddMinutes(1).AddTicks(1) }, now));
    }
}
