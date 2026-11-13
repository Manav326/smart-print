using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintBatchCapacityRulesTests
{
    [Fact]
    public void UsesFiftyMegabytesPerFileAndFiveHundredMegabytesPerBatch()
    {
        Assert.Equal(50L * 1024 * 1024, UploadLimits.MaxFileBytes);
        Assert.Equal(500L * 1024 * 1024, UploadLimits.MaxBatchBytes);
        Assert.Equal(51L * 1024 * 1024, UploadLimits.MaxMultipartRequestBytes);
    }

    [Fact]
    public void AllowsTheTwentiethFileAndRejectsTheTwentyFirst()
    {
        Assert.True(PrintBatchCapacityRules.CanAdd(19, 19, 1));
        Assert.False(PrintBatchCapacityRules.CanAdd(20, 20, 1));
    }

    [Fact]
    public void AllowsExactlyFiveHundredMegabytesAndRejectsAnyByteOver()
    {
        Assert.True(PrintBatchCapacityRules.CanAdd(0, 0, PrintBatchCapacityRules.MaximumTotalBytes));
        Assert.False(PrintBatchCapacityRules.CanAdd(1, PrintBatchCapacityRules.MaximumTotalBytes, 1));
    }

    [Fact]
    public void RejectsOversizedIncomingFilesAndNegativeInputs()
    {
        Assert.False(PrintBatchCapacityRules.CanAdd(0, 0, PrintBatchCapacityRules.MaximumTotalBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintBatchCapacityRules.CanAdd(-1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintBatchCapacityRules.CanAdd(0, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintBatchCapacityRules.CanAdd(0, 0, 0));
    }

    [Fact]
    public void DoesNotOverflowWhenExistingByteCountIsLarge()
        => Assert.False(PrintBatchCapacityRules.CanAdd(1, long.MaxValue, 1));
}