using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentLocalUploadStatusBatchRulesTests
{
    [Fact]
    public void AcceptsBoundedUniqueJobIds()
    {
        Assert.True(AgentLocalUploadStatusBatchRules.IsValid([Guid.NewGuid()]));
        Assert.True(AgentLocalUploadStatusBatchRules.IsValid(
            Enumerable.Range(0, AgentLocalUploadStatusBatchRules.MaximumItems)
                .Select(_ => Guid.NewGuid()).ToArray()));
    }

    [Fact]
    public void RejectsEmptyOversizedDuplicateAndEmptyGuidBatches()
    {
        Assert.False(AgentLocalUploadStatusBatchRules.IsValid(null));
        Assert.False(AgentLocalUploadStatusBatchRules.IsValid([]));
        Assert.False(AgentLocalUploadStatusBatchRules.IsValid(
            Enumerable.Range(0, AgentLocalUploadStatusBatchRules.MaximumItems + 1)
                .Select(_ => Guid.NewGuid()).ToArray()));

        var duplicate = Guid.NewGuid();
        Assert.False(AgentLocalUploadStatusBatchRules.IsValid([duplicate, duplicate]));
        Assert.False(AgentLocalUploadStatusBatchRules.IsValid([Guid.Empty]));
    }
}
