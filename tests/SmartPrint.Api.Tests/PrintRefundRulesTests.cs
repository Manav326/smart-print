using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;

namespace SmartPrint.Api.Tests;

public sealed class PrintRefundRulesTests
{
    [Fact]
    public void SimplexAllocationsIncludeEveryCopyAndSumToThePaidQuote()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(4, 2, "Mono", false, Profile(monoPerSide: 10));
        Assert.Equal([20L, 20L, 20L, 20L], allocations);
        Assert.Equal(80L, allocations.Sum());
    }

    [Fact]
    public void DuplexEvenPagesSplitSheetsAcrossBothSidesAndCopies()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(4, 2, "Mono", true, Profile(monoDuplexPerSheet: 12));
        Assert.Equal([12L, 12L, 12L, 12L], allocations);
        Assert.Equal(48L, allocations.Sum());
    }

    [Fact]
    public void DuplexOddLastPageReceivesTheFullUnpairedSheetShare()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(3, 1, "Mono", true, Profile(monoDuplexPerSheet: 12));
        Assert.Equal([6L, 6L, 12L], allocations);
        Assert.Equal(24L, allocations.Sum());
    }

    [Fact]
    public void MinimumChargeIsAllocatedAcrossPagesWithoutLosingMinorUnits()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(3, 1, "Mono", false, Profile(monoPerSide: 2, minimumCharge: 100));
        Assert.Equal([34L, 33L, 33L], allocations);
        Assert.Equal(100L, allocations.Sum());
    }

    [Fact]
    public void CumulativePartialRefundsReuseTheSameSavedAllocation()
    {
        var allocations = new long[] { 34, 33, 33 };
        var first = PrintRefundRules.CalculateAdditionalRefundAmount(100, 0, [1], allocations);
        var second = PrintRefundRules.CalculateAdditionalRefundAmount(100, first, [1, 3], allocations);
        var final = PrintRefundRules.CalculateAdditionalRefundAmount(100, first + second, [1, 2, 3], allocations);
        Assert.Equal(34, first);
        Assert.Equal(33, second);
        Assert.Equal(33, final);
        Assert.Equal(100, first + second + final);
    }

    [Fact]
    public void SavedAllocationsMustMatchOriginalPaidAmount()
        => Assert.Throws<InvalidOperationException>(() => PrintRefundRules.NormalizePageAllocations(100, 3, [30L, 30L, 30L]));

    [Fact]
    public void LegacyQuotesReceiveDeterministicEqualAllocation()
        => Assert.Equal([34L, 33L, 33L], PrintRefundRules.NormalizePageAllocations(100, 3, []));

    [Fact]
    public void FullPageRejectionRefundsExactlyTheWholeAmount()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(3, 1, "Mono", false, Profile(monoPerSide: 11));
        Assert.Equal(33, PrintRefundRules.CalculateCumulativeRefundAmount(allocations, [1, 2, 3]));
    }

    [Fact]
    public void RejectsInvalidOrDuplicatePageNumbers()
    {
        Assert.Throws<ArgumentException>(() => PrintRefundRules.NormalizeRejectedPages(4, [1, 1], []));
        Assert.Throws<ArgumentException>(() => PrintRefundRules.NormalizeRejectedPages(4, [5], []));
    }

    [Fact]
    public void PreviouslyRefundedPagesCannotBeRestored()
        => Assert.Throws<ArgumentException>(() => PrintRefundRules.NormalizeRejectedPages(4, [1], [1, 2]));

    [Fact]
    public void RequiresPaymentForSameTenantAndJob()
    {
        var job = new PrintJob { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var partial = new PrintPayment { TenantId = job.TenantId, PrintJobId = job.Id, Status = "PARTIALLY_REFUNDED", AmountMinor = 100, RefundedAmountMinor = 25 };
        var otherJob = new PrintPayment { TenantId = job.TenantId, PrintJobId = Guid.NewGuid(), Status = "VERIFIED", AmountMinor = 100 };
        Assert.True(PrintRefundRules.HasRefundableVerifiedPayment(job, partial));
        Assert.False(PrintRefundRules.HasRefundableVerifiedPayment(job, otherJob));
    }

    [Fact]
    public void AllowsRejectedInstantPaymentFileToBeRefundedBeforeHandoff()
    {
        Assert.True(PrintRefundRules.CanRefundBeforePrintHandoff(new PrintJob { Status = "REJECTED" }));
        Assert.False(PrintRefundRules.CanRefundBeforePrintHandoff(new PrintJob { Status = "REJECTED", LocalQueueReceivedUtc = DateTimeOffset.UtcNow }));
    }

    [Fact]
    public void BlocksRefundAfterComputerHandoff()
    {
        Assert.False(PrintRefundRules.CanRefundBeforePrintHandoff(new PrintJob { Status = "PAID_AUTHORISED", LocalQueueReceivedUtc = DateTimeOffset.UtcNow }));
        Assert.False(PrintRefundRules.CanRefundBeforePrintHandoff(new PrintJob { Status = "PAID_AUTHORISED", SpoolerSubmittedUtc = DateTimeOffset.UtcNow }));
    }

    [Fact]
    public void RejectsMissingOrMismatchedRefundPaymentIdentity()
    {
        var job = new PrintJob { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        Assert.False(PrintRefundRules.HasRefundableVerifiedPayment(job, null));
        Assert.False(PrintRefundRules.HasRefundableVerifiedPayment(job, new PrintPayment
        {
            TenantId = Guid.NewGuid(), PrintJobId = job.Id, Status = "VERIFIED", AmountMinor = 100
        }));
    }

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("AWAITING_CUSTOMER_PAYMENT")]
    [InlineData("CUSTOMER_REPORTED")]
    [InlineData("REFUNDED")]
    public void RejectsPaymentStatusesThatAreNotRefundable(string status)
    {
        var job = new PrintJob { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var payment = new PrintPayment { TenantId = job.TenantId, PrintJobId = job.Id, Status = status, AmountMinor = 100 };
        Assert.False(PrintRefundRules.HasRefundableVerifiedPayment(job, payment));
    }

    [Fact]
    public void RejectsZeroValueAndInvalidRefundBalances()
    {
        var job = new PrintJob { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var payment = new PrintPayment { TenantId = job.TenantId, PrintJobId = job.Id, Status = "VERIFIED", AmountMinor = 0 };
        Assert.False(PrintRefundRules.HasRefundableVerifiedPayment(job, payment));
        payment.AmountMinor = 100;
        payment.RefundedAmountMinor = -1;
        Assert.False(PrintRefundRules.HasRefundableVerifiedPayment(job, payment));
        payment.RefundedAmountMinor = 101;
        Assert.False(PrintRefundRules.HasRefundableVerifiedPayment(job, payment));
        payment.RefundedAmountMinor = 100;
        Assert.True(PrintRefundRules.HasRefundableVerifiedPayment(job, payment));
    }

    [Theory]
    [InlineData("AWAITING_PAYMENT")]
    [InlineData("AWAITING_SHOP_APPROVAL")]
    [InlineData("PAYMENT_REJECTED")]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    public void RefundBeforeHandoffRequiresAnEligibleJobStatus(string status)
        => Assert.False(PrintRefundRules.CanRefundBeforePrintHandoff(new PrintJob { Status = status }));

    [Fact]
    public void EveryAgentOrSpoolerHandoffMarkerBlocksPreHandoffRefund()
    {
        var now = DateTimeOffset.UtcNow;
        var handoffMutations = new Action<PrintJob>[]
        {
            job => job.ClaimedByAgentDeviceId = Guid.NewGuid(),
            job => job.AgentClaimedUtc = now,
            job => job.LocalQueueReceivedUtc = now,
            job => job.PrintDispatchLeaseExpiresUtc = now,
            job => job.PrintDispatchAttempts = 1,
            job => job.SpoolerJobId = 1,
            job => job.SpoolerJobName = "SmartPrint QA",
            job => job.SpoolerStatus = "Queued",
            job => job.SpoolerSubmittedUtc = now,
            job => job.SpoolerObservedUtc = now,
            job => job.OutputConfirmedUtc = now,
            job => job.OutputCompletedUtc = now
        };
        foreach (var mutate in handoffMutations)
        {
            var job = new PrintJob { Status = "PAID_AUTHORISED" };
            mutate(job);
            Assert.False(PrintRefundRules.CanRefundBeforePrintHandoff(job));
        }
    }

    [Fact]
    public void RejectedPageNormalizationSortsValidPagesAndPreservesPriorRefunds()
        => Assert.Equal([1, 3, 500], PrintRefundRules.NormalizeRejectedPages(500, [500, 3, 1], [1]));

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void RejectedPageNormalizationRejectsPageCountsOutsideBounds(int pageCount)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PrintRefundRules.NormalizeRejectedPages(pageCount, [], []));

    [Fact]
    public void RejectedPageNormalizationRejectsNullPageCollections()
    {
        Assert.Throws<ArgumentNullException>(() => PrintRefundRules.NormalizeRejectedPages(1, null!, []));
        Assert.Throws<ArgumentNullException>(() => PrintRefundRules.NormalizeRejectedPages(1, [], null!));
    }

    [Fact]
    public void LegacyEqualPageAllocationHandlesAmountsSmallerThanPageCount()
        => Assert.Equal([1L, 1L, 0L], PrintRefundRules.NormalizePageAllocations(2, 3, null));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void PageAllocationRejectsNonPositiveOriginalAmounts(long amountMinor)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PrintRefundRules.NormalizePageAllocations(amountMinor, 1, []));

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void PageAllocationRejectsPageCountsOutsideBounds(int pageCount)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PrintRefundRules.NormalizePageAllocations(100, pageCount, []));

    [Fact]
    public void PageAllocationRejectsNegativeEntriesAndWrongAllocationLength()
    {
        Assert.Throws<InvalidOperationException>(() => PrintRefundRules.NormalizePageAllocations(100, 2, [50L, -1L]));
        Assert.Throws<InvalidOperationException>(() => PrintRefundRules.NormalizePageAllocations(100, 3, [50L, 50L]));
    }

    [Fact]
    public void PageAllocationUsesCheckedArithmeticForOverflow()
        => Assert.Throws<OverflowException>(() => PrintRefundRules.NormalizePageAllocations(long.MaxValue, 2, [long.MaxValue, 1]));

    [Fact]
    public void CumulativeRefundRejectsInvalidAllocationShapes()
    {
        Assert.Throws<ArgumentException>(() => PrintRefundRules.CalculateCumulativeRefundAmount([], []));
        Assert.Throws<ArgumentException>(() => PrintRefundRules.CalculateCumulativeRefundAmount(Enumerable.Repeat(1L, 501).ToArray(), []));
        Assert.Throws<ArgumentException>(() => PrintRefundRules.CalculateCumulativeRefundAmount([10L, -1L], []));
    }

    [Fact]
    public void CumulativeRefundRejectsOutOfRangeAndDuplicatePages()
    {
        Assert.Throws<ArgumentException>(() => PrintRefundRules.CalculateCumulativeRefundAmount([10L, 20L], [0]));
        Assert.Throws<ArgumentException>(() => PrintRefundRules.CalculateCumulativeRefundAmount([10L, 20L], [3]));
        Assert.Throws<ArgumentException>(() => PrintRefundRules.CalculateCumulativeRefundAmount([10L, 20L], [1, 1]));
    }

    [Fact]
    public void CumulativeRefundReturnsZeroForNoRejectedPagesAndDetectsOverflow()
    {
        Assert.Equal(0L, PrintRefundRules.CalculateCumulativeRefundAmount([10L, 20L], []));
        Assert.Throws<OverflowException>(() => PrintRefundRules.CalculateCumulativeRefundAmount([long.MaxValue, 1L], [1, 2]));
    }

    [Fact]
    public void AdditionalRefundRejectsInvalidPreviousRefundAndAllocationTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintRefundRules.CalculateAdditionalRefundAmount(100, -1, [1], [100]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintRefundRules.CalculateAdditionalRefundAmount(100, 101, [1], [100]));
        Assert.Throws<ArgumentException>(() => PrintRefundRules.CalculateAdditionalRefundAmount(100, 0, [1], [99]));
    }

    [Fact]
    public void AdditionalRefundRejectsASelectionBelowAlreadyRefundedAmount()
        => Assert.Throws<InvalidOperationException>(() => PrintRefundRules.CalculateAdditionalRefundAmount(100, 75, [1], [50, 50]));

    [Fact]
    public void MixedFileBatchRefundsEveryRejectedPageAndLeavesOnlyAcceptedPageValuePaid()
    {
        // One verified batch payment contains a three-page PDF, one JPEG and one PNG.
        var pdfRefund = PrintRefundRules.CalculateAdditionalRefundAmount(
            3_000L, 0L, [2, 3], [1_000L, 1_000L, 1_000L]);
        var jpegRefund = PrintRefundRules.CalculateAdditionalRefundAmount(
            800L, 0L, [1], [800L]);
        var pngRefund = PrintRefundRules.CalculateAdditionalRefundAmount(
            1_200L, 0L, [1], [1_200L]);

        var originalBatchPayment = 3_000L + 800L + 1_200L;
        var completeRejectedPageRefund = checked(pdfRefund + jpegRefund + pngRefund);
        var acceptedPageValue = originalBatchPayment - completeRejectedPageRefund;

        Assert.Equal(2_000L, pdfRefund);
        Assert.Equal(800L, jpegRefund);
        Assert.Equal(1_200L, pngRefund);
        Assert.Equal(4_000L, completeRejectedPageRefund);
        Assert.Equal(1_000L, acceptedPageValue);
        Assert.Equal(originalBatchPayment, completeRejectedPageRefund + acceptedPageValue);
        Assert.Equal(1, 3 - new[] { 2, 3 }.Length);
    }

    private static ShopPricingProfile Profile(long monoPerSide = 0, long monoDuplexPerSheet = 0, long minimumCharge = 0)
        => new() { IsConfigured = true, MonoPerSideMinor = monoPerSide, MonoDuplexPerSheetMinor = monoDuplexPerSheet, MinimumChargeMinor = minimumCharge };
}
