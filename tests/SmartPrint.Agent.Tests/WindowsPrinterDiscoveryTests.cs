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

    [Fact]
    public void WindowsDriverCapabilityDiscoveryReturnsStructuredReportForInstalledPdfPrinter()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var json = WindowsPrinterCapabilityDiscovery.DiscoverJson("Microsoft Print to PDF");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("WINDOWS_DRIVER", root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("discoveredAtUtc").ValueKind is JsonValueKind.String);
        Assert.True(root.GetProperty("orientation").ValueKind is JsonValueKind.Object);
        Assert.True(root.GetProperty("paperSizes").ValueKind is JsonValueKind.Array);
        Assert.True(root.GetProperty("mediaTypes").ValueKind is JsonValueKind.Array);
        Assert.True(root.GetProperty("resolutions").ValueKind is JsonValueKind.Array);

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
    public void DiscoversTwoSeparateWindowsQueuesOnTheSamePort()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
            return;

        Assert.True(OperatingSystem.IsWindows(), "The live Windows queue discovery check requires Windows.");
        var discovery = new WindowsPrinterDiscovery(NullLogger<WindowsPrinterDiscovery>.Instance);
        Assert.True(discovery.TryDiscover(out var discovered), "Windows WMI printer discovery failed.");

        var primary = Assert.Single(discovered, item => item.QueueName == "Microsoft Print to PDF");
        var secondary = Assert.Single(discovered, item => item.QueueName == "SmartPrint CI Secondary Queue");
        Assert.NotEqual(primary.QueueName, secondary.QueueName);
        Assert.Equal(primary.DriverName, secondary.DriverName);
        Assert.Equal(primary.PortName, secondary.PortName);
    }
}
