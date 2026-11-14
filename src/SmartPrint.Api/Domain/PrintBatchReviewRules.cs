namespace SmartPrint.Api.Domain;

public sealed record PrintBatchReviewSelection(
    Guid[] ReviewedJobIds,
    Guid[] AcceptedJobIds);

public static class PrintBatchReviewRules
{
    public static bool TryNormalize(
        IEnumerable<Guid>? reviewedJobIds,
        IEnumerable<Guid>? acceptedJobIds,
        out PrintBatchReviewSelection selection)
    {
        var reviewed = reviewedJobIds?.Distinct().ToArray() ?? [];
        var accepted = acceptedJobIds?.Distinct().ToArray() ?? [];

        if (reviewed.Length is < 1 or > 20 ||
            accepted.Length > reviewed.Length ||
            accepted.Except(reviewed).Any())
        {
            selection = new PrintBatchReviewSelection([], []);
            return false;
        }

        selection = new PrintBatchReviewSelection(reviewed, accepted);
        return true;
    }

    public static bool CanEnablePdfBranding(
        bool enabled,
        IEnumerable<string?> printKinds) =>
        !enabled || !printKinds.Contains("ID_DOCUMENT", StringComparer.Ordinal);

}
