using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartPrint.Agent;

/// <summary>
/// WMI queue enumeration runs in a disposable child process. Driver capabilities
/// are then queried by the long-lived Agent through one disposable process per queue,
/// allowing the Agent to cache failures and continue discovering other queues.
/// </summary>
public sealed class WindowsPrinterDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<WindowsPrinterDiscovery> logger;
    private readonly WindowsPrinterCapabilityProbeRunner capabilityProbe;
    private readonly IAgentChildProcessRunner childProcessRunner;
    private readonly bool executeInCurrentProcess;
    private readonly TimeSpan discoveryTimeout;

    public WindowsPrinterDiscovery(
        ILogger<WindowsPrinterDiscovery> logger,
        WindowsPrinterCapabilityProbeRunner? capabilityProbe = null,
        IConfiguration? configuration = null,
        bool executeInCurrentProcess = false,
        IAgentChildProcessRunner? childProcessRunner = null)
    {
        this.logger = logger;
        this.capabilityProbe = capabilityProbe ??
            new WindowsPrinterCapabilityProbeRunner(NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance);
        this.childProcessRunner = childProcessRunner ?? new AgentChildProcessRunner();
        this.executeInCurrentProcess = executeInCurrentProcess;
        discoveryTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration?.GetValue<int?>("Agent:PrinterDiscoveryTimeoutSeconds") ?? 90, 15, 300));
    }

    public bool TryDiscover(out IReadOnlyList<DiscoveredPrinter> discovered)
    {
        var result = TryDiscoverAsync(CancellationToken.None).GetAwaiter().GetResult();
        discovered = result.Printers;
        return result.Success;
    }

    public async Task<(bool Success, IReadOnlyList<DiscoveredPrinter> Printers)> TryDiscoverAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogWarning("Windows printer discovery was requested on a non-Windows host.");
            return (false, Array.Empty<DiscoveredPrinter>());
        }

        (bool Success, IReadOnlyList<DiscoveredPrinter> Printers) metadata;
        if (executeInCurrentProcess)
        {
            metadata = await DiscoverInProcessAsync(cancellationToken);
        }
        else
        {
            try
            {
                var child = await childProcessRunner.ExecuteAsync(
                    "--discover-printers", string.Empty, discoveryTimeout, cancellationToken);
                if (child.TimedOut || child.ExitCode != 0 || child.StandardOutput.Length > 2 * 1024 * 1024)
                {
                    logger.LogWarning(
                        "The isolated Windows discovery worker did not complete successfully. ExitCode={ExitCode}; TimedOut={TimedOut}; Error={Error}",
                        child.ExitCode, child.TimedOut, Truncate(child.StandardError, 400));
                    return (false, Array.Empty<DiscoveredPrinter>());
                }

                var printers = JsonSerializer.Deserialize<List<DiscoveredPrinter>>(
                    child.StandardOutput, JsonOptions);
                if (printers is null)
                {
                    logger.LogWarning("The isolated Windows discovery worker returned an empty response.");
                    return (false, Array.Empty<DiscoveredPrinter>());
                }
                metadata = (true, NormalizeDiscoveredQueues(printers));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The isolated Windows discovery worker failed; the previous server inventory is retained.");
                return (false, Array.Empty<DiscoveredPrinter>());
            }
        }

        if (!metadata.Success)
            return metadata;

        // Run the per-queue capability probes in this long-lived Agent process so
        // a broken-driver backoff survives the short-lived WMI discovery worker.
        var enriched = await Task.WhenAll(metadata.Printers.Select(async printer =>
            printer with
            {
                CapabilitiesJson = await capabilityProbe.ProbeAsync(
                    printer.QueueName, printer.DriverName, cancellationToken)
            }));
        return (true, NormalizeDiscoveredQueues(enriched));
    }

    /// <summary>Runs only inside the disposable discovery worker; it never loads printer drivers.</summary>
    public async Task<(bool Success, IReadOnlyList<DiscoveredPrinter> Printers)> DiscoverInProcessAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return (false, Array.Empty<DiscoveredPrinter>());

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverName, PortName, WorkOffline, PrinterStatus, Default FROM Win32_Printer");
            using var results = searcher.Get();
            var printers = new List<DiscoveredPrinter>();

            foreach (ManagementObject printer in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (printer)
                {
                    var name = ReadString(printer, "Name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var driver = ReadString(printer, "DriverName");
                    var port = ReadString(printer, "PortName");
                    var offline = ReadBoolean(printer, "WorkOffline");
                    var printerStatus = ReadInteger(printer, "PrinterStatus");
                    var isDefault = ReadBoolean(printer, "Default");
                    var outputMode = PrinterOutputModeRules.Detect(name, driver, port);

                    printers.Add(new DiscoveredPrinter(
                        name.Trim(),
                        string.IsNullOrWhiteSpace(driver) ? "Unknown driver" : driver.Trim(),
                        port?.Trim() ?? string.Empty,
                        ResolvePublishedStatus(outputMode, printerStatus, offline),
                        isDefault,
                        outputMode));
                }
            }

            return (true, NormalizeDiscoveredQueues(printers));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ManagementException or UnauthorizedAccessException or
                COMException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Windows printer enumeration through WMI failed; retaining the last successful inventory.");
            return (false, Array.Empty<DiscoveredPrinter>());
        }
    }

    private static string ReadString(ManagementBaseObject source, string property) =>
        source[property]?.ToString() ?? string.Empty;

    private static bool ReadBoolean(ManagementBaseObject source, string property)
    {
        try { return Convert.ToBoolean(source[property] ?? false); }
        catch (FormatException) { return false; }
        catch (InvalidCastException) { return false; }
    }

    private static int ReadInteger(ManagementBaseObject source, string property)
    {
        try { return Convert.ToInt32(source[property] ?? 0); }
        catch (FormatException) { return 0; }
        catch (InvalidCastException) { return 0; }
    }

    public static IReadOnlyList<DiscoveredPrinter> NormalizeDiscoveredQueues(IEnumerable<DiscoveredPrinter> printers) =>
        printers
            .Where(printer => !string.IsNullOrWhiteSpace(printer.QueueName))
            // Different Windows queues can share a port but still have different drivers or settings.
            // Keep them separate; collapse only duplicate queue names.
            .GroupBy(printer => printer.QueueName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(printer => printer.QueueName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string ResolvePublishedStatus(string? outputMode, int printerStatus, bool offline) =>
        string.Equals(outputMode, PrinterOutputModeRules.PdfExport, StringComparison.OrdinalIgnoreCase)
            ? "Ready"
            : MapStatus(printerStatus, offline);

    private static string MapStatus(int printerStatus, bool offline)
    {
        if (offline || printerStatus == 7) return "Offline";
        return printerStatus switch
        {
            3 => "Ready",
            4 or 5 => "Busy",
            6 => "Paused",
            1 or 2 => "Unknown",
            _ => "Unknown"
        };
    }

    private static string Truncate(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maximum ? value : value[..maximum];
}
