using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintBatchReviewRulesTests
{
    [Fact]
    public void NormalizesDuplicateIdsWithoutChangingTheSelection()
    {
        var reviewedA = Guid.NewGuid();
        var reviewedB = Guid.NewGuid();

        var valid = PrintBatchReviewRules.TryNormalize(
            new[] { reviewedA, reviewedA, reviewedB },
            new[] { reviewedB, reviewedB },
            out var selection);

        Assert.True(valid);
        Assert.Equal(new[] { reviewedA, reviewedB }, selection.ReviewedJobIds);
        Assert.Equal(new[] { reviewedB }, selection.AcceptedJobIds);
    }

    [Fact]
    public void AllowsEveryReviewedFileToBeRejected()
    {
        var reviewed = Guid.NewGuid();

        var valid = PrintBatchReviewRules.TryNormalize(
            new[] { reviewed },
            Array.Empty<Guid>(),
            out var selection);

        Assert.True(valid);
        Assert.Equal(new[] { reviewed }, selection.ReviewedJobIds);
        Assert.Empty(selection.AcceptedJobIds);
    }

    [Fact]
    public void RejectsAnEmptyReview()
    {
        Assert.False(PrintBatchReviewRules.TryNormalize(
            Array.Empty<Guid>(),
            Array.Empty<Guid>(),
            out _));
    }

    [Fact]
    public void RejectsMoreThanTwentyDistinctReviewedFiles()
    {
        var reviewed = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();

        Assert.False(PrintBatchReviewRules.TryNormalize(
            reviewed,
            Array.Empty<Guid>(),
            out _));
    }

    [Fact]
    public void RejectsAnAcceptedFileOutsideTheReviewedBatch()
    {
        Assert.False(PrintBatchReviewRules.TryNormalize(
            new[] { Guid.NewGuid() },
            new[] { Guid.NewGuid() },
            out _));
    }

    [Fact]
    public void RejectsPdfBrandingWhenAnyReviewedFileIsAnIdDocument() =>
        Assert.False(PrintBatchReviewRules.CanEnablePdfBranding(
            true, new[] { "DOCUMENT", "ID_DOCUMENT" }));

    [Fact]
    public void AllowsBrandingForDocumentBatches() =>
        Assert.True(PrintBatchReviewRules.CanEnablePdfBranding(
            true, new[] { "DOCUMENT", "PHOTO" }));

    [Fact]
    public void AllowsDisablingBrandingForIdDocumentBatches() =>
        Assert.True(PrintBatchReviewRules.CanEnablePdfBranding(
            false, new[] { "ID_DOCUMENT" }));

    [Fact]
    public void TreatsMissingQuoteExpiryAsExpired() =>
        Assert.True(PrintBatchReviewRules.IsQuoteExpired(null, DateTimeOffset.UtcNow));

    [Fact]
    public void TreatsQuoteAtOrBeforeNowAsExpired()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(PrintBatchReviewRules.IsQuoteExpired(now, now));
        Assert.True(PrintBatchReviewRules.IsQuoteExpired(now.AddTicks(-1), now));
    }

    [Fact]
    public void KeepsFutureQuoteValid() =>
        Assert.False(PrintBatchReviewRules.IsQuoteExpired(
            DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow));
}
