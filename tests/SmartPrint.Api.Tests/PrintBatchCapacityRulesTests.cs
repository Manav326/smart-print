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
    public void AllowsExactlyFiftyMegabytesPerFileAndRejectsOneByteOver()
    {
        Assert.True(PrintBatchCapacityRules.CanAdd(0, 0, UploadLimits.MaxFileBytes));
        Assert.False(PrintBatchCapacityRules.CanAdd(0, 0, UploadLimits.MaxFileBytes + 1));
    }

    [Fact]
    public void AllowsTwentyFilesAtFiveHundredMegabytesAndRejectsTheTwentyFirst()
    {
        const long fileBytes = 25L * 1024 * 1024;
        long currentBytes = 0;
        for (var index = 0; index < UploadLimits.MaxBatchFiles; index++)
        {
            Assert.True(PrintBatchCapacityRules.CanAdd(index, currentBytes, fileBytes));
            currentBytes += fileBytes;
        }

        Assert.Equal(UploadLimits.MaxBatchBytes, currentBytes);
        Assert.False(PrintBatchCapacityRules.CanAdd(UploadLimits.MaxBatchFiles, currentBytes, 1));
    }

    [Fact]
    public void AllowsTenFiftyMegabyteFilesAtExactlyFiveHundredMegabytes()
    {
        long currentBytes = 0;
        for (var index = 0; index < 10; index++)
        {
            Assert.True(PrintBatchCapacityRules.CanAdd(index, currentBytes, UploadLimits.MaxFileBytes));
            currentBytes += UploadLimits.MaxFileBytes;
        }

        Assert.Equal(UploadLimits.MaxBatchBytes, currentBytes);
        Assert.False(PrintBatchCapacityRules.CanAdd(10, currentBytes, 1));
    }

    [Fact]
    public void RejectsOneByteAboveTheFiveHundredMegabyteBatchLimit()
    {
        Assert.False(PrintBatchCapacityRules.CanAdd(19, UploadLimits.MaxBatchBytes - 1, 2));
    }

    [Fact]
    public void RejectsFiveHundredMegabytesAsOneOversizedFile()
    {
        Assert.False(PrintBatchCapacityRules.CanAdd(0, 0, UploadLimits.MaxBatchBytes));
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