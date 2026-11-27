using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrinterDiagnosticRulesTests
{
    [Fact]
    public void UnknownCapabilitiesUseConservativeA4MonochromeSimplexSettings()
    {
        Assert.True(PrinterDiagnosticRules.TryResolveSettings("{}", out var settings, out var reason));
        Assert.Empty(reason);
        Assert.Equal("A4", settings.PaperSize);
        Assert.Equal("Mono", settings.ColourMode);
        Assert.False(settings.IsDuplex);
        Assert.Equal(1, settings.PageCount);
        Assert.Contains("COLOUR_NOT_CONFIRMED", settings.CapabilityEvidence);
        Assert.Contains("DUPLEX_NOT_CONFIRMED", settings.CapabilityEvidence);
    }

    [Fact]
    public void EnablesColourAndDuplexOnlyWhenBothValueAndStatusAreExplicitlySupported()
    {
        const string json = """
        {
          "color": true,
          "colorStatus": "supported",
          "duplex": true,
          "duplexStatus": "supported",
          "paperSizesStatus": "supported",
          "paperSizes": [{ "name": "A4" }, { "name": "Letter" }]
        }
        """;

        Assert.True(PrinterDiagnosticRules.TryResolveSettings(json, out var settings, out _));
        Assert.Equal("A4", settings.PaperSize);
        Assert.Equal("Colour", settings.ColourMode);
        Assert.True(settings.IsDuplex);
        Assert.Equal(2, settings.PageCount);
    }

    [Theory]
    [InlineData("""{"color":true,"colorStatus":"unknown","duplex":true,"duplexStatus":"supported"}""", "Mono", true, 2)]
    [InlineData("""{"color":true,"colorStatus":"supported","duplex":true,"duplexStatus":"unknown"}""", "Colour", false, 1)]
    [InlineData("""{"color":true,"colorStatus":"unknown","duplex":true,"duplexStatus":"unknown"}""", "Mono", false, 1)]
    [InlineData("""{"color":false,"colorStatus":"unsupported","duplex":false,"duplexStatus":"unsupported"}""", "Mono", false, 1)]
    public void DoesNotPromoteUnknownOrUnsupportedCapabilities(
        string json, string colourMode, bool duplex, int pageCount)
    {
        Assert.True(PrinterDiagnosticRules.TryResolveSettings(json, out var settings, out _));
        Assert.Equal(colourMode, settings.ColourMode);
        Assert.Equal(duplex, settings.IsDuplex);
        Assert.Equal(pageCount, settings.PageCount);
    }

    [Fact]
    public void RejectsDriverThatReportsMediaButNoRecognisedStandardPaper()
    {
        const string json = """{"paperSizesStatus":"supported","paperSizes":[{"name":"Custom 7x9"}]}""";
        Assert.False(PrinterDiagnosticRules.TryResolveSettings(json, out _, out var reason));
        Assert.Contains("none of the standard", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("QUEUED", "SPOOLER_ACCEPTED", false)]
    [InlineData("PRINTING", "PRINTING", false)]
    [InlineData("RETAINED", "SPOOLER_ACCEPTED", false)]
    public void DoesNotAllowOwnerConfirmationWhileWindowsStillHasAnActiveJob(
        string spoolerStatus, string jobStatus, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(expected, PrinterDiagnosticRules.IsTerminalOutputObservation(
            PrinterTypePolicy.Physical, jobStatus, spoolerStatus,
            now.AddMinutes(-1), now, null, 123, 0, 1, null, now));
    }

    [Fact]
    public void AllowsPhysicalConfirmationOnlyAfterPrintedObservationAndCompletePageProgress()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(PrinterDiagnosticRules.IsTerminalOutputObservation(
            PrinterTypePolicy.Physical, "PRINTING", "PRINTED",
            now.AddMinutes(-1), now, null, 123, 2, 2, null, now));
        Assert.False(PrinterDiagnosticRules.IsTerminalOutputObservation(
            PrinterTypePolicy.Physical, "PRINTING", "PRINTED",
            now.AddMinutes(-1), now, null, 123, 1, 2, null, now));
    }

    [Fact]
    public void AllowsOwnerToReportNoOutputAfterVerifiedPreSubmissionBlock()
    {
        Assert.True(PrinterDiagnosticRules.IsTerminalOutputObservation(
            PrinterTypePolicy.Physical, "LOCAL_QUEUE_READY", "BLOCKED_BEFORE_SUBMISSION",
            null, null, "DRIVER_PREPARATION_FAILED", null, 0, 0, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AllowsNoOutputDecisionAfterVerifiedSpoolerAbsenceOnly()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(PrinterDiagnosticRules.IsTerminalOutputObservation(
            PrinterTypePolicy.Physical, "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED",
            now.AddMinutes(-1), now, "SPOOLER_JOB_NO_LONGER_VISIBLE", 123, 0, 1, null, now));
        Assert.False(PrinterDiagnosticRules.IsTerminalOutputObservation(
            PrinterTypePolicy.Physical, "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED",
            now.AddMinutes(-1), now, "AGENT_RESTART_DURING_PRINT_SUBMISSION", null, 0, 1, null, now));
    }

    [Fact]
    public void VirtualOutputNeedsItsOwnCompletedTestArtifactOrVerifiedAbsence()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(PrinterDiagnosticRules.IsTerminalOutputObservation(
            PrivatePrintingOutputPolicy.TestVirtual, "TEST_OUTPUT_READY", "PRINTED",
            now.AddMinutes(-1), now, null, 123, 1, 1, now, now));
        Assert.False(PrinterDiagnosticRules.IsTerminalOutputObservation(
            PrivatePrintingOutputPolicy.TestVirtual, "SPOOLER_ACCEPTED", "QUEUED",
            now.AddMinutes(-1), now, null, 123, 0, 1, null, now));
    }

    [Fact]
    public void PrintedEvidenceRemainsTerminalAfterTheAgentReportsTheQueueEntryMissing()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(PrinterDiagnosticRules.IsTerminalOutputObservation(
            PrinterTypePolicy.Physical, "OUTPUT_UNCONFIRMED", "PRINTED",
            now.AddMinutes(-1), now, "SPOOLER_JOB_NO_LONGER_VISIBLE",
            123, 2, 2, null, now));
    }

    [Theory]
    [InlineData("TEST_VIRTUAL", "PRINTED", true)]
    [InlineData("TEST_VIRTUAL", "QUEUED", false)]
    [InlineData("PHYSICAL", "PRINTED", false)]
    public void VirtualOutputReceiptRequiresTheExplicitVirtualModeAndPrintedObservation(
        string outputType, string spoolerStatus, bool expected) =>
        Assert.Equal(expected, PrinterDiagnosticRules.IsVirtualOutputPrintCompleted(outputType, spoolerStatus));

    [Fact]
    public void MatchesResolvedSettingsAsAnImmutableServerDecision()
    {
        var settings = new PrinterDiagnosticSettings("A4", "Mono", false, 1, "EVIDENCE");
        Assert.True(PrinterDiagnosticRules.MatchesResolvedSettings(settings, "A4", "Mono", false, 1));
        Assert.False(PrinterDiagnosticRules.MatchesResolvedSettings(settings, "Letter", "Mono", false, 1));
        Assert.False(PrinterDiagnosticRules.MatchesResolvedSettings(settings, "A4", "Colour", false, 1));
        Assert.False(PrinterDiagnosticRules.MatchesResolvedSettings(settings, "A4", "Mono", true, 2));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    public void RejectsMalformedCapabilitySnapshot(string json)
    {
        Assert.False(PrinterDiagnosticRules.TryResolveSettings(json, out _, out var reason));
        Assert.NotEmpty(reason);
    }
}
