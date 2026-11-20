using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentPrivateDocumentPurgeRulesTests
{
    private static readonly Guid AgentId = Guid.Parse("a6f8dded-5873-49ad-977d-30a2c31a187b");

    [Fact]
    public void AllowsPurgeAfterConfirmedPhysicalOutputAndExplicitRequestFromTheClaimingAgent()
    {
        Assert.Equal(
            AgentPrivateDocumentPurgeAuthorizationFailure.None,
            AgentPrivateDocumentPurgeRules.ValidateAcknowledgement(
                true, "COMPLETED", PrivatePrintingOutputPolicy.Physical,
                DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, AgentId, AgentId));
    }

    [Fact]
    public void AllowsSeparatelyRecordedNonProductionTestOutput()
    {
        Assert.Equal(
            AgentPrivateDocumentPurgeAuthorizationFailure.None,
            AgentPrivateDocumentPurgeRules.ValidateAcknowledgement(
                true, "TEST_OUTPUT_READY", PrivatePrintingOutputPolicy.TestVirtual,
                null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, AgentId, AgentId));
    }

    [Theory]
    [InlineData(false, "COMPLETED", "PHYSICAL", true, false, true, false)]
    [InlineData(true, "PRINTING", "PHYSICAL", false, false, true, false)]
    [InlineData(true, "COMPLETED", "PHYSICAL", false, false, true, false)]
    [InlineData(true, "TEST_OUTPUT_READY", "PHYSICAL", false, true, true, false)]
    [InlineData(true, "COMPLETED", "PHYSICAL", true, false, false, false)]
    [InlineData(true, "COMPLETED", "PHYSICAL", true, false, true, true)]
    public void RejectsUnverifiedOrUnownedPurgeAcknowledgements(
        bool privatePrinting,
        string status,
        string outputType,
        bool outputConfirmed,
        bool outputCompleted,
        bool purgeRequested,
        bool wrongAgent)
    {
        var otherAgent = Guid.NewGuid();
        var failure = AgentPrivateDocumentPurgeRules.ValidateAcknowledgement(
            privatePrinting,
            status,
            outputType,
            outputConfirmed ? DateTimeOffset.UtcNow : null,
            outputCompleted ? DateTimeOffset.UtcNow : null,
            purgeRequested ? DateTimeOffset.UtcNow : null,
            wrongAgent ? otherAgent : AgentId,
            AgentId);

        Assert.NotEqual(AgentPrivateDocumentPurgeAuthorizationFailure.None, failure);
    }
}
