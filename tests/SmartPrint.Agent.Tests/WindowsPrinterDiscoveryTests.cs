using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class WindowsPrinterDiscoveryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(7, true)]
    [InlineData(6, false)]
    public void PdfExportDestinationIsReadyRegardlessOfWindowsVirtualQueueStatus(int printerStatus, bool offline)
    {
        Assert.Equal(
            "Ready",
            WindowsPrinterDiscovery.ResolvePublishedStatus(PrinterOutputModeRules.PdfExport, printerStatus, offline));
    }

    [Theory]
    [InlineData(3, false, "Ready")]
    [InlineData(4, false, "Busy")]
    [InlineData(5, false, "Busy")]
    [InlineData(6, false, "Paused")]
    [InlineData(7, false, "Offline")]
    [InlineData(0, false, "Unknown")]
    [InlineData(3, true, "Offline")]
    public void PhysicalPrinterRetainsItsWindowsQueueStatus(int printerStatus, bool offline, string expected)
    {
        Assert.Equal(
            expected,
            WindowsPrinterDiscovery.ResolvePublishedStatus(PrinterOutputModeRules.Physical, printerStatus, offline));
    }

    [Fact]
    public void KeepsDifferentWindowsQueuesSeparateEvenWhenTheyShareAPort()
    {
        var first = new DiscoveredPrinter("HP Lobby", "HP Universal", "USB001", "Ready", true);
        var second = new DiscoveredPrinter("HP Lobby (Copy 1)", "HP Universal", "USB001", "Ready", false);

        var normalized = WindowsPrinterDiscovery.NormalizeDiscoveredQueues([first, second]);

        Assert.Equal(2, normalized.Count);
        Assert.Equal(new[] { "HP Lobby", "HP Lobby (Copy 1)" }, normalized.Select(item => item.QueueName));
    }

    [Fact]
    public void CollapsesOnlyCaseInsensitiveDuplicateQueueNames()
    {
        var first = new DiscoveredPrinter("HP Lobby", "HP Universal", "USB001", "Ready", true);
        var duplicate = new DiscoveredPrinter("hp lobby", "Other driver", "USB002", "Busy", false);
        var second = new DiscoveredPrinter("HP Back Office", "HP Universal", "USB001", "Ready", false);

        var normalized = WindowsPrinterDiscovery.NormalizeDiscoveredQueues([first, duplicate, second]);

        Assert.Equal(2, normalized.Count);
        Assert.Contains(normalized, item => item.QueueName == "HP Lobby");
        Assert.Contains(normalized, item => item.QueueName == "HP Back Office");
    }

    [Fact]
    public void WinspoolStatusRefreshResolvesOnlyUnknownWmiQueueStates()
    {
        var wmi = new[]
        {
            new DiscoveredPrinter("IppVirtual", "Microsoft IPP Class Driver",
                "http://Android.local:10631/p/virtual", "Unknown", false,
                PrinterOutputModeRules.UnsupportedVirtual),
            new DiscoveredPrinter("USB Office", "Office Driver", "USB001", "Ready", true,
                PrinterOutputModeRules.Physical)
        };
        var winspool = new[]
        {
            new DiscoveredPrinter("IppVirtual", "Microsoft IPP Class Driver",
                "http://Android.local:10631/p/virtual", "Ready", true,
                PrinterOutputModeRules.UnsupportedVirtual),
            new DiscoveredPrinter("USB Office", "Office Driver", "USB001", "Busy", false,
                PrinterOutputModeRules.Physical)
        };

        var refreshed = WindowsPrinterDiscovery.ApplyWinspoolStatusSnapshots(wmi, winspool);

        Assert.Equal("Ready", refreshed[0].Status);
        Assert.Equal("http://Android.local:10631/p/virtual", refreshed[0].PortName);
        // The second WMI queue already had a fresh Ready status; the
        // supplemental WinSpool snapshot must not overwrite it with Busy.
        Assert.Equal("Ready", refreshed[1].Status);
        Assert.True(refreshed[1].IsDefault);
    }

    [Fact]
    public void WinspoolUnknownStatusDoesNotPromoteOrOverwriteWmiUnknownStatus()
    {
        var wmi = new[]
        {
            new DiscoveredPrinter("IppVirtual", "Microsoft IPP Class Driver",
                "http://Android.local:10631/p/virtual", "Unknown", false,
                PrinterOutputModeRules.UnsupportedVirtual)
        };
        var winspool = new[]
        {
            new DiscoveredPrinter("IppVirtual", "Microsoft IPP Class Driver",
                "http://Android.local:10631/p/virtual", "Unknown", false,
                PrinterOutputModeRules.UnsupportedVirtual)
        };

        var refreshed = Assert.Single(
            WindowsPrinterDiscovery.ApplyWinspoolStatusSnapshots(wmi, winspool));

        Assert.Equal("Unknown", refreshed.Status);
    }

    [Fact]
    public void MicrosoftPrintToPdfIsIdentifiedAsAgentSidePdfExport()
    {
        Assert.Equal(
            PrinterOutputModeRules.PdfExport,
            PrinterOutputModeRules.Detect("Microsoft Print to PDF", "Microsoft Print To PDF"));
    }

    [Fact]
    public void UnknownThirdPartyVirtualDriversFailClosedInsteadOfBecomingPhysical()
    {
        Assert.Equal(
            PrinterOutputModeRules.UnsupportedVirtual,
            PrinterOutputModeRules.Detect("Adobe PDF", "Adobe PDF"));
        Assert.Equal(
            PrinterOutputModeRules.UnsupportedVirtual,
            PrinterOutputModeRules.Detect("Foxit PDF Printer", "Foxit PDF Printer"));
        Assert.Equal(
            PrinterOutputModeRules.UnsupportedVirtual,
            PrinterOutputModeRules.Detect("Microsoft XPS Document Writer", "Microsoft XPS Document Writer"));
        Assert.Equal(
            PrinterOutputModeRules.UnsupportedVirtual,
            PrinterOutputModeRules.Detect("Generic PDF Queue", "Generic Driver", "PORTPROMPT:"));
    }

    [Theory]
    [InlineData("IppVirtual - Microsoft IPP", "Microsoft IPP Class Driver", "http://Android.local:10631/p/virtual")]
    [InlineData("Android Virtual Printer", "Microsoft IPP Class Driver", "http://Android.local:10631/p/virtual")]
    [InlineData("Android Virtual Printer", "Microsoft IPP Class Driver", "ipp://android.local/printer")]
    public void AndroidIppVirtualQueueRemainsVirtualUntilThePlatformTestPolicyPromotesIt(string queue, string driver, string port)
    {
        Assert.Equal(
            PrinterOutputModeRules.UnsupportedVirtual,
            WindowsPrinterDiscovery.ResolveDetectedOutputMode(queue, driver, port));
    }

    [Fact]
    public void OrdinaryIppPhysicalQueueRemainsPhysical()
    {
        Assert.Equal(
            PrinterOutputModeRules.Physical,
            WindowsPrinterDiscovery.ResolveDetectedOutputMode(
                "Office IPP Printer", "Microsoft IPP Class Driver", "ipp://printer.local/ipp/print"));
    }

    [Fact]
    public void AndroidVirtualIppQueueRemainsVirtualWhenWindowsHidesTheEndpointBehindWsd()
    {
        Assert.Equal(
            PrinterOutputModeRules.UnsupportedVirtual,
            WindowsPrinterDiscovery.ResolveDetectedOutputMode(
                "SmartPrint Android Virtual IPP CI 46108060059-1",
                "Microsoft IPP Class Driver",
                "WSD-205bd056-1e66-4158-a0b0-8bc8e003f891"));
    }

    [Fact]
    public void OrdinaryPhysicalIppQueueOnWsdPortRemainsPhysical()
    {
        Assert.Equal(
            PrinterOutputModeRules.Physical,
            WindowsPrinterDiscovery.ResolveDetectedOutputMode(
                "Office Front Desk Printer",
                "Microsoft IPP Class Driver",
                "WSD-205bd056-1e66-4158-a0b0-8bc8e003f891"));
    }

    [Fact]
    public void GenericVirtualPrinterWithoutIppTransportRemainsUnsupported()
    {
        Assert.Equal(
            PrinterOutputModeRules.UnsupportedVirtual,
            PrinterOutputModeRules.Detect("Generic Virtual Printer", "Generic Virtual Driver", "USB001"));
    }

    [Fact]
    public void CapabilityEnrichmentMergesByQueueAndDriverWithoutOverwritingFreshQueueStatus()
    {
        var current =
            new DiscoveredPrinter("HP Lobby", "HP Universal", "USB002", "Offline", false,
                OutputMode: PrinterOutputModeRules.Physical);
        var oldSnapshot =
            new DiscoveredPrinter("hp lobby", "HP Universal", "USB001", "Ready", true,
                OutputMode: PrinterOutputModeRules.Physical,
                CapabilitiesJson: "{\"color\":true}");
        var result = WindowsPrinterDiscovery.ApplyCapabilitySnapshots([current], [oldSnapshot]);

        var merged = Assert.Single(result);
        Assert.Equal("Offline", merged.Status);
        Assert.Equal("USB002", merged.PortName);
        Assert.False(merged.IsDefault);
        Assert.Equal("{\"color\":true}", merged.CapabilitiesJson);
    }

    [Fact]
    public void CapabilityEnrichmentDoesNotApplySnapshotFromAChangedDriver()
    {
        var current =
            new DiscoveredPrinter("HP Lobby", "New Driver", "USB002", "Ready", true,
                OutputMode: PrinterOutputModeRules.Physical);
        var oldSnapshot =
            new DiscoveredPrinter("HP Lobby", "Old Driver", "USB001", "Ready", true,
                OutputMode: PrinterOutputModeRules.Physical,
                CapabilitiesJson: "{\"duplex\":true}");

        var result = Assert.Single(WindowsPrinterDiscovery.ApplyCapabilitySnapshots([current], [oldSnapshot]));

        Assert.Equal("New Driver", result.DriverName);
        Assert.Equal("{}", result.CapabilitiesJson);
    }

    [Fact]
    public void WindowsDriverCapabilityDiscoveryReturnsStructuredReportForInstalledPdfPrinter()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var json = WindowsPrinterCapabilityDiscovery.DiscoverJson("Microsoft Print to PDF", "Microsoft Print To PDF");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("WINDOWS_DRIVER", root.GetProperty("source").GetString());
        Assert.Equal("Microsoft Print To PDF", root.GetProperty("driverName").GetString());
        Assert.True(root.GetProperty("discoveredAtUtc").ValueKind is JsonValueKind.String);
        Assert.True(root.GetProperty("orientation").ValueKind is JsonValueKind.Object);
        Assert.True(root.GetProperty("paperSizes").ValueKind is JsonValueKind.Array);
        Assert.True(root.GetProperty("mediaTypes").ValueKind is JsonValueKind.Array);
        Assert.True(root.GetProperty("pageMediaTypes").ValueKind is JsonValueKind.Array);
        Assert.Contains(
            root.GetProperty("pageMediaTypesStatus").GetString(),
            new[] { "supported", "unsupported", "unknown" });
        Assert.True(root.GetProperty("resolutions").ValueKind is JsonValueKind.Array);
        Assert.Contains(root.GetProperty("colorStatus").GetString(), new[] { "supported", "unsupported", "unknown" });
        Assert.Contains(root.GetProperty("duplexStatus").GetString(), new[] { "supported", "unsupported", "unknown" });
        Assert.Contains(root.GetProperty("pagesPerSheetStatus").GetString(), new[] { "supported", "unsupported", "unknown" });
        Assert.Contains(root.GetProperty("pagesPerSheetDirectionStatus").GetString(), new[] { "supported", "unsupported", "unknown" });

        var paperSizes = root.GetProperty("paperSizes");
        Assert.NotEmpty(paperSizes.EnumerateArray());
        foreach (var paper in paperSizes.EnumerateArray())
        {
            Assert.True(paper.GetProperty("code").GetInt32() > 0);
            Assert.False(string.IsNullOrWhiteSpace(paper.GetProperty("name").GetString()));
        }
    }

    [Fact]
    public void PrinterInventorySyncSerializesDriverCapabilitiesAsWebJson()
    {
        const string capabilityReport = """{"schemaVersion":1,"color":true,"duplex":false,"paperSizes":[{"code":9,"name":"A4"}]}""";
        var printer = new DiscoveredPrinter(
            "HP Lobby", "HP Universal", "USB001", "Ready", true,
            CapabilitiesJson: capabilityReport);

        var payload = JsonSerializer.Serialize(
            new { printers = new[] { printer } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(payload);
        var serializedReport = document.RootElement
            .GetProperty("printers")[0]
            .GetProperty("capabilitiesJson")
            .GetString();

        Assert.Equal(capabilityReport, serializedReport);
    }

    [Fact]
    public async Task DiscoversTwoSeparateWindowsQueuesOnTheSamePort()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
            return;

        Assert.True(OperatingSystem.IsWindows(), "The live Windows queue discovery check requires Windows.");
        var probe = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            executeInCurrentProcess: true);
        var discovery = new WindowsPrinterDiscovery(
            NullLogger<WindowsPrinterDiscovery>.Instance,
            probe,
            executeInCurrentProcess: true);
        var result = await discovery.DiscoverInProcessAsync(CancellationToken.None);
        Assert.True(result.Success, "Windows WMI printer discovery failed.");
        var discovered = result.Printers;

        var primary = Assert.Single(discovered, item => item.QueueName == "Microsoft Print to PDF");
        var secondary = Assert.Single(discovered, item => item.QueueName == "SmartPrint CI Secondary Queue");
        Assert.NotEqual(primary.QueueName, secondary.QueueName);
        Assert.Equal(primary.DriverName, secondary.DriverName);
        Assert.Equal(primary.PortName, secondary.PortName);
    }
    [Theory]
    [InlineData("Office USB", "Office Laser Driver", "USB001", "PHYSICAL")]
    [InlineData("Office LAN", "Office Laser Driver", "IP_192.168.1.20", "PHYSICAL")]
    [InlineData("Office WSD", "Microsoft IPP Class Driver", "WSD-12345678", "PHYSICAL")]
    [InlineData("Office Shared", "Office Laser Driver", @"\\PRINT-SERVER\Office", "PHYSICAL")]
    [InlineData("Office IPP", "Microsoft IPP Class Driver", "ipp://printer.local/ipp/print", "PHYSICAL")]
    [InlineData("Office IPPS", "Microsoft IPP Class Driver", "ipps://printer.local/ipp/print", "PHYSICAL")]
    [InlineData("SmartPrint Android Virtual IPP CI", "Microsoft IPP Class Driver", "ipps://printer.local/ipp/print", "UNSUPPORTED_VIRTUAL")]
    public void PhysicalOfficeQueueClassificationIsIndependentOfConnectionFamily(
        string queueName, string driverName, string portName, string expectedOutputMode)
    {
        Assert.Equal(
            expectedOutputMode,
            WindowsPrinterDiscovery.ResolveDetectedOutputMode(queueName, driverName, portName));
    }

    [Fact]
    public async Task InProcessWindowsDiscoveryAndCapabilityEnrichmentHandleInstalledQueue()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
            return;

        Assert.True(OperatingSystem.IsWindows(), "The production discovery-worker acceptance requires Windows.");
        var probe = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            executeInCurrentProcess: true);
        var discovery = new WindowsPrinterDiscovery(
            NullLogger<WindowsPrinterDiscovery>.Instance,
            probe,
            executeInCurrentProcess: true);
        var result = await discovery.TryDiscoverAsync(CancellationToken.None);

        Assert.True(result.Success, "The isolated WMI/WinSpool discovery and capability-enrichment pipeline failed.");
        var queue = Assert.Single(result.Printers, item =>
            string.Equals(item.QueueName, "Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PrinterOutputModeRules.PdfExport, queue.OutputMode);
        using var capabilities = JsonDocument.Parse(queue.CapabilitiesJson);
        Assert.Equal(JsonValueKind.Object, capabilities.RootElement.ValueKind);
        Assert.Equal(2, capabilities.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task WinspoolFallbackKeepsAWindowsQueueVisibleWhenWmiEnumerationFails()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var queueJson = """[{"queueName":"Office LAN","driverName":"Office Driver","portName":"IP_192.168.1.20","status":"Ready","isDefault":false,"outputMode":"PHYSICAL","capabilitiesJson":"{}"}]""";
        var runner = new SequenceChildProcessRunner(
            new AgentChildProcessResult(1, string.Empty, "simulated WMI failure", false),
            new AgentChildProcessResult(0, queueJson, string.Empty, false));
        var discovery = new WindowsPrinterDiscovery(
            NullLogger<WindowsPrinterDiscovery>.Instance,
            childProcessRunner: runner);

        var result = await discovery.TryDiscoverMetadataAsync(CancellationToken.None);

        Assert.True(result.Success);
        var queue = Assert.Single(result.Printers);
        Assert.Equal("Office LAN", queue.QueueName);
        Assert.Equal("IP_192.168.1.20", queue.PortName);
        Assert.Equal(new[] { "--discover-printers", "--discover-printers-winspool" }, runner.Commands);
    }

    [Fact]
    public async Task CapabilityProbeTimeoutDoesNotHideDiscoveredQueue()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var queue = new DiscoveredPrinter(
            "Office USB", "Slow vendor driver", "USB001", "Ready", false,
            OutputMode: PrinterOutputModeRules.Physical);
        var runner = new SequenceChildProcessRunner(
            new AgentChildProcessResult(-1, string.Empty, "simulated timeout", true));
        var probe = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            childProcessRunner: runner);
        var discovery = new WindowsPrinterDiscovery(
            NullLogger<WindowsPrinterDiscovery>.Instance,
            probe,
            executeInCurrentProcess: true);

        var result = await discovery.EnrichCapabilitiesAsync([queue], CancellationToken.None);

        Assert.True(result.Success);
        var retained = Assert.Single(result.Printers);
        Assert.Equal(queue.QueueName, retained.QueueName);
        Assert.Equal(queue.Status, retained.Status);
        Assert.Equal("{}", retained.CapabilitiesJson);
        Assert.Equal(1, runner.CallCount);
    }

    private sealed class SequenceChildProcessRunner(params AgentChildProcessResult[] results)
        : IAgentChildProcessRunner
    {
        private readonly Queue<AgentChildProcessResult> responses = new(results);
        public List<string> Commands { get; } = [];
        public int CallCount => Commands.Count;

        public Task<AgentChildProcessResult> ExecuteAsync(
            string command,
            string standardInput,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<bool>>? beforeSpoolerSubmission = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (!responses.TryDequeue(out var response))
                throw new InvalidOperationException("No fake child-process response was configured.");
            return Task.FromResult(response);
        }
    }

}
