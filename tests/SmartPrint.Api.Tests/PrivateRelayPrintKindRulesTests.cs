using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrivateRelayPrintKindRulesTests
{
    [Theory]
    [InlineData("DOCUMENT", "DOCUMENT")]
    [InlineData("ID_DOCUMENT", "DOCUMENT")]
    [InlineData("PHOTO", "PHOTO")]
    public void TicketKindMapsToTheSupportedPrintJobKind(string ticketKind, string expectedJobKind)
    {
        var mapped = PrivateRelayPrintKindRules.TryMapTicketKindToPrintJobKind(
            ticketKind, out var printJobKind);

        Assert.True(mapped);
        Assert.Equal(expectedJobKind, printJobKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    [InlineData("ID-PHOTO")]
    public void UnsupportedTicketKindIsNotMapped(string? ticketKind)
    {
        var mapped = PrivateRelayPrintKindRules.TryMapTicketKindToPrintJobKind(
            ticketKind, out var printJobKind);

        Assert.False(mapped);
        Assert.Equal(string.Empty, printJobKind);
    }

    [Fact]
    public void IdDocumentStillRequiresExactlyOneA4PortraitPage()
    {
        const string validA4Portrait =
            """[{"widthPoints":595.28,"heightPoints":841.89}]""";

        Assert.True(IdCardPdfRules.HasExactlyA4PortraitPage(validA4Portrait));
        Assert.False(IdCardPdfRules.HasExactlyA4PortraitPage(
            """[{"widthPoints":595.28,"heightPoints":841.89},{"widthPoints":595.28,"heightPoints":841.89}]"""));
        Assert.False(IdCardPdfRules.HasExactlyA4PortraitPage(
            """[{"widthPoints":841.89,"heightPoints":595.28}]"""));
        Assert.False(IdCardPdfRules.HasExactlyA4PortraitPage(
            """[{"widthPoints":612,"heightPoints":792}]"""));
        Assert.False(IdCardPdfRules.HasExactlyA4PortraitPage("not-json"));
    }
}
