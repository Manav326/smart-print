using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrivatePrintingRulesTests
{
    [Fact]
    public void PrivatePrintingNeverAllowsCloudDocumentStorageOrManagedPdfExport()
    {
        Assert.False(PrivatePrintingRules.AllowsCloudDocumentStorage(true));
        Assert.False(PrivatePrintingRules.AllowsManagedPdfExport(true));
        Assert.True(PrivatePrintingRules.AllowsCloudDocumentStorage(false));
        Assert.True(PrivatePrintingRules.AllowsManagedPdfExport(false));
    }

    [Theory]
    [InlineData("COMPLETED", "PHYSICAL", true, true)]
    [InlineData("COMPLETED", "PDF_EXPORT", true, false)]
    [InlineData("OUTPUT_UNCONFIRMED", "PHYSICAL", true, false)]
    [InlineData("PRINTING", "PHYSICAL", true, false)]
    [InlineData("COMPLETED", "PHYSICAL", false, false)]
    public void SourceIsErasedOnlyAfterConfirmedPhysicalOutput(
        string status, string outputType, bool privatePrinting, bool expected)
    {
        var confirmed = status == "COMPLETED" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        Assert.Equal(expected, PrivatePrintingRules.ShouldEraseLocalSource(
            privatePrinting, status, outputType, confirmed));
    }

    [Fact]
    public void SourceIsNotErasedWithoutOutputConfirmation()
    {
        Assert.False(PrivatePrintingRules.ShouldEraseLocalSource(
            true, "COMPLETED", "PHYSICAL", null));
    }

    [Fact]
    public void ShopFacingLabelDoesNotRevealPrivateFileName()
    {
        Assert.Equal("Private document",
            PrivatePrintingRules.SafeDocumentLabel(true, "salary-statement.pdf"));
        Assert.Equal("salary-statement.pdf",
            PrivatePrintingRules.SafeDocumentLabel(false, "salary-statement.pdf"));
    }
}