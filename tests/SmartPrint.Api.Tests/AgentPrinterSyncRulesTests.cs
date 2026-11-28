using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentPrinterSyncRulesTests
{
    [Fact]
    public void AcceptsValidPrinterMetadataAndReturnsTheValidatedQueues()
    {
        var printer = Printer("HP LaserJet", "HP Universal", "USB001");
        var result = AgentPrinterSyncRules.Validate([printer]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Equal([printer], result.Printers);
    }

    [Fact]
    public void RejectsMissingOrExcessivePrinterLists()
    {
        Assert.Equal(AgentPrinterSyncValidationFailure.PrinterLimitExceeded,
            AgentPrinterSyncRules.Validate(null).Failure);

        var excessive = Enumerable.Range(0, AgentPrinterSyncRules.MaximumPrinters + 1)
            .Select(index => Printer($"Queue {index}", "Driver", "Port"))
            .ToArray();
        Assert.Equal(AgentPrinterSyncValidationFailure.PrinterLimitExceeded,
            AgentPrinterSyncRules.Validate(excessive).Failure);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    public void InvalidCapabilitiesKeepQueueVisibleWithUnknownCapabilities(string capabilitiesJson)
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer("HP LaserJet", "HP Universal", "USB001", capabilitiesJson: capabilitiesJson)
        ]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Equal(1, result.CapabilitiesUnknownCount);
        Assert.Equal("{}", Assert.Single(result.Printers).CapabilitiesJson);
    }

    [Fact]
    public void RejectsNullPrinterEntriesWithoutRejectingOtherQueues()
    {
        var printers = new AgentPrinterDiscoveryRequest[]
        {
            Printer("Queue A", "Driver", "Port A"),
            null!,
            Printer("Queue C", "Driver", "Port C")
        };
        var result = AgentPrinterSyncRules.Validate(printers);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Equal(1, result.RejectedCount);
        Assert.Equal(2, result.Printers.Count);
    }

    [Theory]
    [InlineData("", "Driver", "Port", "Ready", "PHYSICAL")]
    [InlineData("Queue", "", "Port", "Ready", "PHYSICAL")]
    [InlineData("Queue", "Driver", "Port", "UnknownStatus", "PHYSICAL")]
    [InlineData("Queue", "Driver", "Port", "Ready", "UNSUPPORTED")]
    public void RejectsOnlyTheQueueWithInvalidMetadata(
        string queueName, string driverName, string portName, string status, string outputMode)
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer(queueName, driverName, portName, status, outputMode)
        ]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Equal(1, result.RejectedCount);
        Assert.Empty(result.Printers);
    }

    [Fact]
    public void AcceptsUnsupportedVirtualQueueForVisibilityWithoutMarkingItPhysical()
    {
        var printer = Printer("Adobe PDF", "Adobe PDF", "PORTPROMPT:", outputMode: "UNSUPPORTED_VIRTUAL");
        var result = AgentPrinterSyncRules.Validate([printer]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Equal("UNSUPPORTED_VIRTUAL", Assert.Single(result.Printers).OutputMode);
    }

    [Fact]
    public void DuplicateQueueNamesPreserveFirstQueueAndRejectOnlyTheDuplicate()
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer("HP LaserJet", "Driver A", "Port A"),
            Printer(" hp laserjet ", "Driver B", "Port B")
        ]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Single(result.Printers);
        Assert.Equal(1, result.RejectedCount);
        Assert.Equal("Driver A", result.Printers[0].DriverName);
    }

    [Fact]
    public void EnforcesQueueMetadataAndCapabilitiesLengthLimitsIndependently()
    {
        var invalidQueue = AgentPrinterSyncRules.Validate([
            Printer(new string('Q', AgentPrinterSyncRules.MaximumQueueNameLength + 1), "Driver", "Port")
        ]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, invalidQueue.Failure);
        Assert.Equal(1, invalidQueue.RejectedCount);

        var invalidCapabilities = AgentPrinterSyncRules.Validate([
            Printer("Queue", "Driver", "Port",
                capabilitiesJson: "{ \"payload\": \"" + new string('x', AgentPrinterSyncRules.MaximumCapabilitiesJsonLength) + "\" }")
        ]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, invalidCapabilities.Failure);
        Assert.Equal(1, invalidCapabilities.CapabilitiesUnknownCount);
        Assert.Equal("{}", Assert.Single(invalidCapabilities.Printers).CapabilitiesJson);
    }

    [Fact]
    public void MetadataOnlySyncPreservesLastKnownCapabilitiesForTheSameDriver()
    {
        const string known = "{\"color\":true,\"duplex\":true}";
        Assert.Equal(known, AgentPrinterSyncRules.ResolveCapabilitiesJson(known, "{}", driverChanged: false));
        Assert.Equal(known, AgentPrinterSyncRules.ResolveCapabilitiesJson(known, null, driverChanged: false));
    }

    [Fact]
    public void CapabilitySnapshotIsClearedWhenTheWindowsDriverChanges()
    {
        const string oldSnapshot = "{\"color\":true}";
        Assert.Equal("{}", AgentPrinterSyncRules.ResolveCapabilitiesJson(oldSnapshot, "{}", driverChanged: true));
        const string newSnapshot = "{\"duplex\":true}";
        Assert.Equal(newSnapshot, AgentPrinterSyncRules.ResolveCapabilitiesJson(oldSnapshot, newSnapshot, driverChanged: true));
    }

    [Fact]
    public void UnknownExistingCapabilitiesRemainUnknownUntilAValidSnapshotArrives()
    {
        Assert.Equal("{}", AgentPrinterSyncRules.ResolveCapabilitiesJson("{}", null, driverChanged: false));
    }

    [Fact]
    public void AcceptsAnEmptyPrinterDiscoveryResult()
    {
        var result = AgentPrinterSyncRules.Validate([]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Empty(result.Printers);
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Busy")]
    [InlineData("Offline")]
    [InlineData("Paused")]
    [InlineData("Error")]
    [InlineData("Unknown")]
    public void AcceptsEverySupportedPrinterStatus(string status)
    {
        var result = AgentPrinterSyncRules.Validate([Printer("Queue", "Driver", "Port", status)]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Single(result.Printers);
    }

    [Fact]
    public void AcceptsExactQueueDriverPortAndCapabilitiesLengthLimits()
    {
        var capabilities = "{\"x\":\"" + new string('x', AgentPrinterSyncRules.MaximumCapabilitiesJsonLength - 8) + "\"}";
        Assert.Equal(AgentPrinterSyncRules.MaximumCapabilitiesJsonLength, capabilities.Length);
        var result = AgentPrinterSyncRules.Validate([
            Printer(
                new string('Q', AgentPrinterSyncRules.MaximumQueueNameLength),
                new string('D', AgentPrinterSyncRules.MaximumDriverNameLength),
                new string('P', AgentPrinterSyncRules.MaximumPortNameLength),
                capabilitiesJson: capabilities)
        ]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Single(result.Printers);
    }

    [Fact]
    public void RejectsDriverAndPortNamesAboveTheirConfiguredLimitsIndividually()
    {
        var invalidDriver = AgentPrinterSyncRules.Validate([
            Printer("Queue", new string('D', AgentPrinterSyncRules.MaximumDriverNameLength + 1), "Port")
        ]);
        Assert.Equal(1, invalidDriver.RejectedCount);

        var invalidPort = AgentPrinterSyncRules.Validate([
            Printer("Queue", "Driver", new string('P', AgentPrinterSyncRules.MaximumPortNameLength + 1))
        ]);
        Assert.Equal(1, invalidPort.RejectedCount);
    }

    [Fact]
    public void AcceptsExactlyOneHundredDistinctPrinterQueues()
    {
        var printers = Enumerable.Range(0, AgentPrinterSyncRules.MaximumPrinters)
            .Select(index => Printer($"Queue {index}", "Driver", $"Port {index}"))
            .ToArray();
        var result = AgentPrinterSyncRules.Validate(printers);
        Assert.Equal(AgentPrinterSyncRules.MaximumPrinters, result.Printers.Count);
    }

    [Fact]
    public void CapabilitiesJsonAboveTheLimitKeepsTheQueueVisible()
    {
        var tooLong = "{\"x\":\"" + new string('x', AgentPrinterSyncRules.MaximumCapabilitiesJsonLength - 7) + "\"}";
        Assert.True(tooLong.Length > AgentPrinterSyncRules.MaximumCapabilitiesJsonLength);
        var result = AgentPrinterSyncRules.Validate([
            Printer("Queue", "Driver", "Port", capabilitiesJson: tooLong)
        ]);
        Assert.Equal(1, result.CapabilitiesUnknownCount);
        Assert.Equal("{}", Assert.Single(result.Printers).CapabilitiesJson);
    }

    [Fact]
    public void TreatsMissingCapabilitiesAsUnknownButKeepsQueue()
    {
        var result = AgentPrinterSyncRules.Validate([Printer("Queue", "Driver", "Port", capabilitiesJson: null!)]);
        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Equal(1, result.CapabilitiesUnknownCount);
        Assert.Single(result.Printers);
    }

    [Fact]
    public void EightDiscoveredQueuesCanBeVisibleWithOnlyFiveSelected()
    {
        var queues = Enumerable.Range(0, 8)
            .Select(index => Printer($"Queue {index + 1}", "Driver", $"Port {index + 1}"))
            .ToArray();
        var result = AgentPrinterSyncRules.Validate(queues);
        Assert.Equal(8, result.Printers.Count);

        var selectedCount = 0;
        var selections = result.Printers.Select(printer =>
        {
            var selected = AgentPrinterSyncRules.ShouldAutoSelectQueue(
                printer.OutputMode, printer.Status, true, selectedCount, 5);
            if (selected) selectedCount++;
            return selected;
        }).ToArray();

        Assert.Equal(5, selectedCount);
        Assert.Equal(5, selections.Count(selected => selected));
        Assert.Equal(3, selections.Count(selected => !selected));
    }

    [Fact]
    public void OnlyReadySupportedQueuesCanUseAnAvailableActivePlanSlot()
    {
        Assert.True(AgentPrinterSyncRules.ShouldAutoSelectQueue("PHYSICAL", "Ready", true, 0, 5));
        Assert.True(AgentPrinterSyncRules.ShouldAutoSelectQueue("PDF_EXPORT", "Busy", true, 0, 5));
        Assert.True(AgentPrinterSyncRules.ShouldAutoSelectQueue("UNSUPPORTED_VIRTUAL", "Ready", true, 0, 5));
        Assert.False(AgentPrinterSyncRules.ShouldAutoSelectQueue("PHYSICAL", "Offline", true, 0, 5));
        Assert.False(AgentPrinterSyncRules.ShouldAutoSelectQueue("PHYSICAL", "Ready", false, 0, 5));
        Assert.False(AgentPrinterSyncRules.ShouldAutoSelectQueue("PHYSICAL", "Ready", true, 5, 5));
        Assert.True(AgentPrinterSyncRules.ShouldAutoSelectQueue("PHYSICAL", "Ready", true, 80, null));
    }

    [Fact]
    public void OneInvalidQueueDoesNotRejectOtherDiscoveredQueues()
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer("Queue A", "Driver", "Port A"),
            Printer("", "Driver", "Port B"),
            Printer("Queue C", "Driver", "Port C")
        ]);
        Assert.Equal(2, result.Printers.Count);
        Assert.Equal(1, result.RejectedCount);
    }

    private static AgentPrinterDiscoveryRequest Printer(
        string queueName,
        string driverName,
        string portName,
        string status = "Ready",
        string outputMode = "PHYSICAL",
        string capabilitiesJson = "{}") =>
        new(queueName, driverName, portName, status, false, outputMode, capabilitiesJson);
}
