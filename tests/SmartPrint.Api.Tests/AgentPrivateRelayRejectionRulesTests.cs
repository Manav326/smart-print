using SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentPrivateRelayRejectionRulesTests
{
    [Theory]
    [InlineData("document_not_supported")]
    [InlineData("document_content_type_mismatch")]
    [InlineData("private_document_encryption_required")]
    [InlineData("private_document_integrity_failed")]
    [InlineData("private_document_decryption_failed")]
    public void AcceptsOnlyTheExistingDocumentRejectionCodes(string code)
    {
        Assert.True(AgentPrivateRelayRejectionRules.IsValidRequest(code, "The Agent could not process this document."));
    }

    [Theory]
    [InlineData("DOCUMENT_NOT_SUPPORTED")]
    [InlineData("unknown_failure")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsUnknownOrNonCanonicalFailureCodes(string? code)
    {
        Assert.False(AgentPrivateRelayRejectionRules.IsValidRequest(code, "The Agent could not process this document."));
    }

    [Fact]
    public void RequiresAUsefulMessageAndEnforcesTheExisting400CharacterLimit()
    {
        Assert.False(AgentPrivateRelayRejectionRules.IsValidRequest("document_not_supported", null));
        Assert.False(AgentPrivateRelayRejectionRules.IsValidRequest("document_not_supported", ""));
        Assert.False(AgentPrivateRelayRejectionRules.IsValidRequest("document_not_supported", "   "));
        Assert.True(AgentPrivateRelayRejectionRules.IsValidRequest("document_not_supported", new string('x', 400)));
        Assert.False(AgentPrivateRelayRejectionRules.IsValidRequest("document_not_supported", new string('x', 401)));
    }
}
