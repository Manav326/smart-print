namespace SmartPrint.Api.Domain;

public static class AgentLocalUploadStatusBatchRules
{
    public const int MaximumItems = 50;

    public static bool IsValid(IReadOnlyList<Guid>? jobIds) =>
        jobIds is { Count: > 0 and <= MaximumItems } &&
        jobIds.All(id => id != Guid.Empty) &&
        jobIds.Distinct().Count() == jobIds.Count;
}
