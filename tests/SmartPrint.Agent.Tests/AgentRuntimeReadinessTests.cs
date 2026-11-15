using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentRuntimeReadinessTests
{
    [Fact]
    public void LocalUploadRequiresAnInitializedProtectedQueueAndFreshAgentIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();

        readiness.MarkHeartbeatSuccess(now);
        readiness.SetPrintWorkerRunning(true);
        readiness.MarkPrinterInventorySuccess(2, now);

        Assert.False(readiness.GetSnapshot(now).LocalUploadReady);

        readiness.SetQueueInitialized(true);
        var snapshot = readiness.GetSnapshot(now);

        Assert.True(snapshot.QueueInitialized);
        Assert.True(snapshot.HeartbeatFresh);
        Assert.True(snapshot.LocalUploadReady);
    }

    [Fact]
    public void LocalUploadCanBeAcceptedWhilePrintWorkerOrPrinterInventoryIsRecovering()
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();

        readiness.SetQueueInitialized(true);
        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventoryFailure("printer_discovery_failed");
        readiness.SetPrintWorkerRunning(false);

        var snapshot = readiness.GetSnapshot(now);

        Assert.False(snapshot.PrintWorkerRunning);
        Assert.False(snapshot.PrinterInventoryFresh);
        Assert.Equal("printer_discovery_failed", snapshot.LastFailureCode);
        Assert.True(snapshot.LocalUploadReady);
    }

    [Fact]
    public void LocalUploadRequiresFreshAuthenticatedAgentHeartbeat()
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();
        readiness.SetQueueInitialized(true);
        readiness.SetPrintWorkerRunning(true);
        readiness.MarkHeartbeatSuccess(now.AddMinutes(-3));
        readiness.MarkPrinterInventorySuccess(1, now);

        Assert.False(readiness.GetSnapshot(now).LocalUploadReady);

        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventorySuccess(1, now.AddMinutes(-3));

        var snapshot = readiness.GetSnapshot(now);
        Assert.False(snapshot.PrinterInventoryFresh);
        Assert.True(snapshot.LocalUploadReady);
    }

    [Fact]
    public void LocalUploadReadinessAllowsVirtualOutputWhenWindowsInventoryIsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();
        readiness.SetQueueInitialized(true);
        readiness.SetPrintWorkerRunning(true);
        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventorySuccess(0, now);

        var snapshot = readiness.GetSnapshot(now);

        Assert.True(snapshot.PrinterInventoryFresh);
        Assert.True(snapshot.LocalUploadReady);
    }


    [Fact]
    public void PrintingReadinessRemainsHealthyWhenTheOptionalDocumentWorkerIsUnavailable()
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();
        readiness.SetQueueInitialized(true);
        readiness.SetPrintWorkerRunning(true);
        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventorySuccess(1, now);

        // Document parser health is deliberately not part of print-worker readiness.
        // A failed upload/parser request must not pause already accepted print jobs.
        var snapshot = readiness.GetSnapshot(now);

        Assert.True(snapshot.PrintWorkerRunning);
        Assert.True(snapshot.QueueInitialized);
        Assert.True(snapshot.HeartbeatFresh);
        Assert.True(snapshot.LocalUploadReady);
    }

    [Theory]
    [InlineData("not_paired")]
    [InlineData("identity_unavailable")]
    [InlineData("heartbeat_unauthorized")]
    [InlineData("heartbeat_forbidden")]
    public void CriticalAgentIdentityFailureImmediatelyDisablesLocalUpload(string failureCode)
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();
        readiness.SetQueueInitialized(true);
        readiness.SetPrintWorkerRunning(true);
        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventorySuccess(1, now);

        Assert.True(readiness.GetSnapshot(now).LocalUploadReady);

        readiness.MarkHeartbeatFailure(failureCode);

        Assert.False(readiness.GetSnapshot(now).LocalUploadReady);
    }
}
