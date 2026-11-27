using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrivateRelayPhotoOutputRulesTests
{
    [Theory]
    [InlineData("A4_SHEET", "PLAIN")]
    [InlineData("A4_SHEET", "GLOSSY")]
    [InlineData("PHOTO_PAPER", "GLOSSY")]
    [InlineData("PHOTO_PAPER", "MATTE")]
    public void CompletedPrivateRelayJobRetainsTicketPhotoOutputOptions(
        string outputLayout,
        string paperFinish)
    {
        var ticket = new LocalPrintUploadTicket
        {
            PrintKind = "PHOTO",
            PhotoOutputLayout = outputLayout,
            PhotoPaperFinish = paperFinish
        };
        var job = new PrintJob();

        PrivateRelayPhotoOutputRules.ApplyTicketOptions(job, ticket);

        Assert.Equal(outputLayout, job.PhotoOutputLayout);
        Assert.Equal(paperFinish, job.PhotoPaperFinish);
    }
}
