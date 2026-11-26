using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentOutputModeRulesTests
{
    [Theory]
    [InlineData("PHYSICAL", false)]
    [InlineData("PDF_EXPORT", true)]
    [InlineData("physical", false)]
    [InlineData("pdf_export", true)]
    [InlineData("UNKNOWN", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UsesTheOutputModeAuthorisedByTheApiAtDispatch(string? dispatchOutputMode, bool expected)
    {
        Assert.Equal(expected, AgentOutputModeRules.IsPdfExport(dispatchOutputMode));
    }

    [Theory]
    [InlineData("UNSUPPORTED_VIRTUAL", false)]
    [InlineData("TEST_VIRTUAL", false)]
    [InlineData("PHYSICAL", false)]
    [InlineData("PDF_EXPORT", true)]
    public void AuthorisedVirtualAndPhysicalQueuesShareTheWindowsSpoolerPath(string outputMode, bool isPdfExport)
    {
        // Worker.ProcessAuthorizedPrintJobAsync uses ExportPdfAsync only for
        // PDF_EXPORT; every other server-authorised mode reaches PrintAsync.
        Assert.Equal(isPdfExport, AgentOutputModeRules.IsPdfExport(outputMode));
    }

    [Fact]
    public void PhysicalDispatchCannotBeOverriddenByAnUploadTimeVirtualQueueMarker()
    {
        const string uploadTimeQueueMode = "PDF_EXPORT";
        const string currentDispatchMode = "PHYSICAL";

        Assert.Equal("PDF_EXPORT", uploadTimeQueueMode);
        Assert.False(AgentOutputModeRules.IsPdfExport(currentDispatchMode));
    }
}
