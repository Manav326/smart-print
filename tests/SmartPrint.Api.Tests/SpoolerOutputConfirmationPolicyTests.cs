using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class SpoolerOutputConfirmationPolicyTests
{
    private static readonly DateTimeOffset SubmittedUtc = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedUtc = SubmittedUtc.AddSeconds(20);
    private static readonly DateTimeOffset NowUtc = SubmittedUtc.AddMinutes(1);

    private static bool CanConfirm(
        string? jobStatus, string? spoolerStatus, string? printerStatus = "Ready",
        string? failureCode = null, int? spoolerJobId = 17,
        DateTimeOffset? submittedUtc = null, DateTimeOffset? observedUtc = null, DateTimeOffset? nowUtc = null) =>
        SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            jobStatus, spoolerStatus, submittedUtc ?? SubmittedUtc,
            observedUtc ?? ObservedUtc, nowUtc ?? NowUtc, printerStatus, failureCode, spoolerJobId);

    [Theory]
    [InlineData("SPOOLER_ACCEPTED", "RETAINED")]
    [InlineData("PRINTING", "retained")]
    [InlineData("OUTPUT_UNCONFIRMED", " Retained ")]
    public void RetainedWindowsJobCannotBeConfirmedFromAnyPhysicalOutputState(string jobStatus, string spoolerStatus) =>
        Assert.False(CanConfirm(jobStatus, spoolerStatus));

    [Theory]
    [InlineData("SPOOLER_ACCEPTED")]
    [InlineData("PRINTING")]
    [InlineData("OUTPUT_UNCONFIRMED")]
    public void FreshPrintedObservationAllowsPhysicalOutputConfirmation(string jobStatus) =>
        Assert.True(CanConfirm(jobStatus, "PRINTED"));

    [Theory]
    [InlineData("QUEUED", "QUEUED")]
    [InlineData("SPOOLING", "SPOOLING")]
    [InlineData("PRINTING", "PRINTING")]
    [InlineData("PRINTED", "QUEUED")]
    [InlineData("RETAINED", "QUEUED")]
    [InlineData("OUTPUT_UNCONFIRMED", "QUEUED")]
    [InlineData("PRINTER_UNAVAILABLE", "QUEUED")]
    [InlineData("unknown", "QUEUED")]
    public void InitialAcceptanceReceiptCannotMintTerminalOrAbsenceProof(string? reportedStatus, string expected) =>
        Assert.Equal(expected, SpoolerOutputConfirmationPolicy.NormalizeInitialSpoolerAcceptanceStatus(reportedStatus));

    [Theory]
    [InlineData("QUEUED")]
    [InlineData("SPOOLING")]
    [InlineData("PRINTING")]
    public void ActiveWindowsQueueStatesNeverAuthorizePhysicalOutputConfirmation(string spoolerStatus)
    {
        Assert.False(CanConfirm("SPOOLER_ACCEPTED", spoolerStatus));
        Assert.False(CanConfirm("PRINTING", spoolerStatus));
        Assert.False(CanConfirm("OUTPUT_UNCONFIRMED", spoolerStatus));
    }

    [Fact]
    public void VerifiedQueueAbsencePreservesManualPageInspectionPath() =>
        Assert.True(CanConfirm("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED",
            failureCode: "SPOOLER_JOB_NO_LONGER_VISIBLE", spoolerJobId: 17));

    [Theory]
    [InlineData("OFFLINE")]
    [InlineData("PAPEROUT")]
    [InlineData("PAUSED")]
    [InlineData("ERROR")]
    [InlineData("USERINTERVENTION")]
    public void WarningSpoolerStatesDoNotAuthorizeManualConfirmation(string spoolerStatus)
    {
        Assert.False(CanConfirm("SPOOLER_ACCEPTED", spoolerStatus));
        Assert.False(CanConfirm("OUTPUT_UNCONFIRMED", spoolerStatus));
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Paused")]
    public void CurrentPrinterWarningsBlockPrintedObservation(string printerStatus) =>
        Assert.False(CanConfirm("PRINTING", "PRINTED", printerStatus));

    [Fact]
    public void VerifiedQueueAbsenceCanBeManuallyInspectedEvenWhenPrinterIsNowOffline() =>
        Assert.True(CanConfirm("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", "Offline",
            "SPOOLER_JOB_NO_LONGER_VISIBLE", 17));

    [Fact]
    public void OutputUnconfirmedSpoolerObservationRequiresVerifiedAbsenceReason()
    {
        Assert.True(SpoolerOutputConfirmationPolicy.HasVerifiedAgentSpoolerObservation(
            "OUTPUT_UNCONFIRMED", SubmittedUtc, ObservedUtc, NowUtc, "SPOOLER_JOB_NO_LONGER_VISIBLE", 17));
        Assert.False(SpoolerOutputConfirmationPolicy.HasVerifiedAgentSpoolerObservation(
            "OUTPUT_UNCONFIRMED", SubmittedUtc, ObservedUtc, NowUtc, "UNEXPECTED_PRINT_EXECUTION_ERROR", 17));
        Assert.False(SpoolerOutputConfirmationPolicy.HasVerifiedAgentSpoolerObservation(
            "OUTPUT_UNCONFIRMED", SubmittedUtc, ObservedUtc, NowUtc, null, 17));
    }

    [Fact]
    public void AgentRestartWithoutSpoolerIdIsNotTreatedAsVerifiedQueueAbsence()
    {
        Assert.False(SpoolerOutputConfirmationPolicy.IsVerifiedSpoolerAbsenceReport("AGENT_RESTART_DURING_PRINT_SUBMISSION", null));
        Assert.False(CanConfirm("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED",
            failureCode: "AGENT_RESTART_DURING_PRINT_SUBMISSION", spoolerJobId: null));
    }

    [Fact]
    public void AgentRestartWithoutSpoolerIdRequiresExplicitManualQueueReconciliation()
    {
        Assert.True(SpoolerOutputConfirmationPolicy.CanManuallyReconcileUncertainOutput(
            "OUTPUT_UNCONFIRMED", "UNKNOWN", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null,
            "HP LaserJet Queue", "SmartPrint-SP-12345"));
        Assert.False(SpoolerOutputConfirmationPolicy.CanManuallyReconcileUncertainOutput(
            "OUTPUT_UNCONFIRMED", "RETAINED", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null,
            "HP LaserJet Queue", "SmartPrint-SP-12345"));
        Assert.False(SpoolerOutputConfirmationPolicy.CanManuallyReconcileUncertainOutput(
            "OUTPUT_UNCONFIRMED", "UNKNOWN", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null,
            null, "SmartPrint-SP-12345"));
        Assert.False(SpoolerOutputConfirmationPolicy.CanManuallyReconcileUncertainOutput(
            "OUTPUT_UNCONFIRMED", "UNKNOWN", "AGENT_RESTART_DURING_PRINT_SUBMISSION", 12,
            "HP LaserJet Queue", "SmartPrint-SP-12345"));
        Assert.False(SpoolerOutputConfirmationPolicy.CanManuallyReconcileUncertainOutput(
            "SPOOLER_ACCEPTED", "UNKNOWN", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null,
            "HP LaserJet Queue", "SmartPrint-SP-12345"));
        Assert.False(SpoolerOutputConfirmationPolicy.CanManuallyReconcileUncertainOutput(
            "OUTPUT_UNCONFIRMED", "PRINTED", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null,
            "HP LaserJet Queue", "SmartPrint-SP-12345"));
    }

    [Fact]
    public void StaleObservationCannotAuthorizeOutputOrPrivateDocumentPurge()
    {
        var staleObserved = NowUtc - SpoolerOutputConfirmationPolicy.MaximumSpoolerObservationAge - TimeSpan.FromSeconds(1);
        Assert.False(CanConfirm("PRINTING", "PRINTED",
            submittedUtc: staleObserved.AddMinutes(-1), observedUtc: staleObserved));
    }

    [Fact]
    public void ObservationMustBeAtOrAfterSubmissionAndNotInTheFuture()
    {
        Assert.False(CanConfirm("PRINTING", "PRINTED", observedUtc: SubmittedUtc.AddSeconds(-1)));
        Assert.False(CanConfirm("PRINTING", "PRINTED", observedUtc: NowUtc.AddSeconds(1)));
    }

    [Fact]
    public void FreshnessBoundaryIsInclusiveAndUsesServerTime()
    {
        var boundaryObserved = NowUtc - SpoolerOutputConfirmationPolicy.MaximumSpoolerObservationAge;
        var boundarySubmitted = boundaryObserved.AddMinutes(-1);
        Assert.True(CanConfirm("PRINTING", "PRINTED", submittedUtc: boundarySubmitted, observedUtc: boundaryObserved));
        Assert.False(CanConfirm("PRINTING", "PRINTED", submittedUtc: boundarySubmitted, observedUtc: boundaryObserved.AddSeconds(-1)));
    }

    [Fact]
    public void MissingObservationOrSubmissionTimestampCannotAuthorizeOutput()
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "PRINTING", "PRINTED", SubmittedUtc, null, NowUtc, "Ready"));
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "PRINTING", "PRINTED", null, ObservedUtc, NowUtc, "Ready"));
        Assert.False(CanConfirm("PRINTING", "UNKNOWN"));
    }

    [Theory]
    [InlineData("AWAITING_PAYMENT", "PRINTED")]
    [InlineData("COMPLETED", "PRINTED")]
    [InlineData(null, "QUEUED")]
    public void NonOutputStatesCannotBeConfirmed(string? jobStatus, string? spoolerStatus) =>
        Assert.False(CanConfirm(jobStatus, spoolerStatus));

    [Theory]
    [InlineData("SPOOLER_JOB_NO_LONGER_VISIBLE", 17, true)]
    [InlineData(" spooler_job_no_longer_visible ", 17, true)]
    [InlineData("SPOOLER_JOB_NO_LONGER_VISIBLE", null, false)]
    [InlineData("SPOOLER_JOB_NO_LONGER_VISIBLE", 0, false)]
    [InlineData("AGENT_RESTART_DURING_PRINT_SUBMISSION", null, false)]
    [InlineData("AGENT_RESTART_DURING_PRINT_SUBMISSION", 17, false)]
    [InlineData("LOCAL_QUEUE_PROOF_MISMATCH", 17, false)]
    [InlineData("AGENT_STOPPED_DURING_OUTPUT", 17, false)]
    public void OnlyJobIdBackedAbsenceReportsAreVerified(string reasonCode, int? spoolerJobId, bool expected) =>
        Assert.Equal(expected, SpoolerOutputConfirmationPolicy.IsVerifiedSpoolerAbsenceReport(reasonCode, spoolerJobId));

    [Theory]
    [InlineData("RETAINED")]
    [InlineData("retained")]
    [InlineData(" Retained ")]
    public void IdentifiesRetainedWindowsJobsRegardlessOfCasingOrWhitespace(string spoolerStatus) =>
        Assert.True(SpoolerOutputConfirmationPolicy.IsRetained(spoolerStatus));

    [Fact]
    public void TemporaryPrinterUnavailableObservationDoesNotClearLastKnownRetainedState()
    {
        Assert.True(SpoolerOutputConfirmationPolicy.ShouldPreserveLastKnownRetainedStatus("RETAINED", "PRINTER_UNAVAILABLE"));
        Assert.True(SpoolerOutputConfirmationPolicy.ShouldPreserveLastKnownRetainedStatus(" retained ", "printer_unavailable"));
    }

    [Theory]
    [InlineData("RETAINED", "PRINTED")]
    [InlineData("RETAINED", "OUTPUT_UNCONFIRMED")]
    [InlineData("RETAINED", "QUEUED")]
    [InlineData("PRINTING", "PRINTER_UNAVAILABLE")]
    [InlineData(null, "PRINTER_UNAVAILABLE")]
    public void FreshNonTransientAgentObservationReplacesThePreviousState(string? currentStatus, string? incomingStatus) =>
        Assert.False(SpoolerOutputConfirmationPolicy.ShouldPreserveLastKnownRetainedStatus(currentStatus, incomingStatus));

    [Theory]
    [InlineData("PRINTED", "OUTPUT_UNCONFIRMED", 2, 2, true)]
    [InlineData("PRINTED", "PRINTER_UNAVAILABLE", 0, 0, true)]
    [InlineData("PRINTED", "QUEUED", 1, 2, false)]
    [InlineData("PRINTING", "OUTPUT_UNCONFIRMED", 2, 2, false)]
    [InlineData("OUTPUT_UNCONFIRMED", "PRINTER_UNAVAILABLE", 2, 2, false)]
    public void PreservesOnlyCompleteTerminalPrintedEvidence(
        string currentStatus, string incomingStatus, int pagesPrinted, int totalPages, bool expected) =>
        Assert.Equal(expected, SpoolerOutputConfirmationPolicy.ShouldPreserveLastKnownPrintedStatus(
            currentStatus, incomingStatus, pagesPrinted, totalPages));

    [Fact]
    public void IncompletePageProgressCannotBeMarkedCompleteFromQueueAbsence()
    {
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED",
            SubmittedUtc, ObservedUtc, NowUtc, "Ready",
            "SPOOLER_JOB_NO_LONGER_VISIBLE", 17, 0, 8));
        Assert.False(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED",
            SubmittedUtc, ObservedUtc, NowUtc, "Ready",
            "SPOOLER_JOB_NO_LONGER_VISIBLE", 17, 3, 8));
        Assert.True(SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(
            "OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED",
            SubmittedUtc, ObservedUtc, NowUtc, "Ready",
            "SPOOLER_JOB_NO_LONGER_VISIBLE", 17, 8, 8));
    }

    [Fact]
    public void PageProgressIsMonotonicAcrossStaleWindowsObservations()
    {
        var merged = SpoolerOutputConfirmationPolicy.MergePageProgress(4, 12, 2, 10);
        Assert.Equal((4, 12), merged);

        merged = SpoolerOutputConfirmationPolicy.MergePageProgress(4, 12, null, null);
        Assert.Equal((4, 12), merged);

        merged = SpoolerOutputConfirmationPolicy.MergePageProgress(0, 0, 0, 12);
        Assert.Equal((0, 12), merged);
    }

    [Fact]
    public void OnlyFreshVerifiedZeroPagePhysicalAbsenceCanBeResumed()
    {
        bool CanResume(
            string? status = "OUTPUT_UNCONFIRMED",
            string? outputType = "PHYSICAL",
            string? spoolerStatus = "OUTPUT_UNCONFIRMED",
            string? failure = "SPOOLER_JOB_NO_LONGER_VISIBLE",
            int? spoolerJobId = 17,
            int pagesPrinted = 0,
            int totalPages = 12,
            DateTimeOffset? submitted = null,
            DateTimeOffset? observed = null,
            DateTimeOffset? outputConfirmed = null,
            DateTimeOffset? dispatchLease = null) =>
            SpoolerOutputConfirmationPolicy.CanResumeUnprintedPhysicalJob(
                status, outputType, spoolerStatus, failure, spoolerJobId,
                pagesPrinted, totalPages, submitted ?? SubmittedUtc,
                observed ?? ObservedUtc, outputConfirmed, dispatchLease, NowUtc);

        Assert.True(CanResume());
        Assert.False(CanResume(pagesPrinted: 1));
        Assert.False(CanResume(totalPages: 0));
        Assert.False(CanResume(spoolerJobId: null));
        Assert.False(CanResume(failure: "AGENT_RESTART_DURING_PRINT_SUBMISSION"));
        Assert.False(CanResume(spoolerStatus: "PRINTING"));
        Assert.False(CanResume(status: "PRINTING"));
        Assert.False(CanResume(outputType: "PDF_EXPORT"));
        Assert.False(CanResume(outputType: "TEST_VIRTUAL"));
        Assert.True(SpoolerOutputConfirmationPolicy.CanResumeUnprintedPhysicalJob(
            "OUTPUT_UNCONFIRMED", "TEST_VIRTUAL", "OUTPUT_UNCONFIRMED",
            "SPOOLER_JOB_NO_LONGER_VISIBLE", 17, 0, 12, SubmittedUtc,
            ObservedUtc, null, null, NowUtc, allowTestVirtualOutput: true));
        Assert.False(SpoolerOutputConfirmationPolicy.CanResumeUnprintedPhysicalJob(
            "OUTPUT_UNCONFIRMED", "TEST_VIRTUAL", "OUTPUT_UNCONFIRMED",
            "SPOOLER_JOB_NO_LONGER_VISIBLE", 17, 0, 12, SubmittedUtc,
            ObservedUtc, null, null, NowUtc));
        Assert.False(CanResume(outputConfirmed: NowUtc));
        Assert.False(CanResume(dispatchLease: NowUtc.AddMinutes(1)));
        Assert.False(CanResume(observed: NowUtc.AddMinutes(-11)));
    }

}
