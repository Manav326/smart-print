using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.ComponentModel;
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
        var metadata = await TryDiscoverMetadataAsync(cancellationToken);
        if (!metadata.Success)
            return metadata;
        return await EnrichCapabilitiesAsync(metadata.Printers, cancellationToken);
    }

    /// <summary>
    /// Discovers queue identity and live Windows status without waiting for vendor
    /// capability queries. The Agent can publish this inventory immediately while
    /// capability enrichment continues independently.
    /// </summary>
    public async Task<(bool Success, IReadOnlyList<DiscoveredPrinter> Printers)> TryDiscoverMetadataAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogWarning("Windows printer discovery was requested on a non-Windows host.");
            return (false, Array.Empty<DiscoveredPrinter>());
        }

        if (executeInCurrentProcess)
            return await DiscoverInProcessAsync(cancellationToken);

        try
        {
            var child = await childProcessRunner.ExecuteAsync(
                "--discover-printers", string.Empty, discoveryTimeout, cancellationToken);
            var printers = TryParseDiscoveryResult(child);
            if (printers is null || printers.Count == 0)
            {
                logger.LogWarning(
                    "WMI printer enumeration did not return a usable queue list. Trying isolated Windows spooler enumeration. ExitCode={ExitCode}; TimedOut={TimedOut}; Error={Error}",
                    child.ExitCode, child.TimedOut, Truncate(child.StandardError, 400));

                var fallback = await childProcessRunner.ExecuteAsync(
                    "--discover-printers-winspool", string.Empty, discoveryTimeout, cancellationToken);
                printers = TryParseDiscoveryResult(fallback);
                if (printers is null)
                {
                    logger.LogWarning(
                        "Both isolated Windows queue enumeration methods failed. ExitCode={ExitCode}; TimedOut={TimedOut}; Error={Error}",
                        fallback.ExitCode, fallback.TimedOut, Truncate(fallback.StandardError, 400));
                    return (false, Array.Empty<DiscoveredPrinter>());
                }
            }

            return (true, NormalizeDiscoveredQueues(printers));
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

    /// <summary>
    /// Enriches already-published Windows queues. A failed vendor probe leaves that
    /// queue's capability state unknown without delaying queue visibility or other work.
    /// </summary>
    public async Task<(bool Success, IReadOnlyList<DiscoveredPrinter> Printers)> EnrichCapabilitiesAsync(
        IReadOnlyList<DiscoveredPrinter> printers,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return (false, Array.Empty<DiscoveredPrinter>());

        var enriched = await Task.WhenAll(printers.Select(async printer =>
            printer with
            {
                CapabilitiesJson = await capabilityProbe.ProbeAsync(
                    printer.QueueName, printer.DriverName, cancellationToken)
            }));
        return (true, NormalizeDiscoveredQueues(enriched));
    }

    private static List<DiscoveredPrinter>? TryParseDiscoveryResult(AgentChildProcessResult child)
    {
        if (child.TimedOut || child.ExitCode != 0 || child.StandardOutput.Length > 2 * 1024 * 1024)
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<DiscoveredPrinter>>(child.StandardOutput, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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

    private const uint PrinterEnumLocal = 0x00000002;
    private const uint PrinterEnumConnections = 0x00000004;
    private const uint PrinterAttributeDefault = 0x00000004;
    private const uint PrinterStatusPaused = 0x00000001;
    private const uint PrinterStatusOffline = 0x00000080;
    private const uint PrinterStatusBusy =
        0x00000100 | 0x00000200 | 0x00000400 | 0x00000800 |
        0x00001000 | 0x00002000 | 0x00004000 | 0x00008000 |
        0x00010000;
    private const int MaximumPrinterEnumerationBytes = 4 * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo4
    {
        public IntPtr PrinterName;
        public IntPtr ServerName;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr ServerName;
        public IntPtr PrinterName;
        public IntPtr ShareName;
        public IntPtr PortName;
        public IntPtr DriverName;
        public IntPtr Comment;
        public IntPtr Location;
        public IntPtr DevMode;
        public IntPtr SeparatorFile;
        public IntPtr PrintProcessor;
        public IntPtr DataType;
        public IntPtr Parameters;
        public IntPtr SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint JobCount;
        public uint AveragePagesPerMinute;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrintersW(
        uint flags, string? name, uint level, IntPtr printers, uint bufferBytes,
        out uint neededBytes, out uint returnedCount);

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string printerName, out IntPtr printer, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    private static extern bool GetPrinterW(
        IntPtr printer, uint level, IntPtr buffer, uint bufferBytes, out uint neededBytes);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printer);

    /// <summary>
    /// Fallback queue inventory using Windows' spooler API. This method is only
    /// invoked inside a disposable child process and never queries driver capabilities.
    /// </summary>
    public static IReadOnlyList<DiscoveredPrinter> DiscoverWinspoolQueues()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<DiscoveredPrinter>();

        var querySucceeded = EnumPrintersW(
            PrinterEnumLocal | PrinterEnumConnections,
            null,
            4,
            IntPtr.Zero,
            0,
            out var neededBytes,
            out _);
        var queryError = querySucceeded ? 0 : Marshal.GetLastWin32Error();

        if (neededBytes == 0)
        {
            if (querySucceeded || queryError == 122)
                return Array.Empty<DiscoveredPrinter>();
            throw new Win32Exception(queryError, "Windows could not enumerate installed printer queues.");
        }
        if (neededBytes > MaximumPrinterEnumerationBytes)
            throw new InvalidDataException("The Windows printer inventory exceeds the supported metadata limit.");

        var buffer = Marshal.AllocHGlobal(checked((int)neededBytes));
        try
        {
            if (!EnumPrintersW(
                    PrinterEnumLocal | PrinterEnumConnections,
                    null,
                    4,
                    buffer,
                    neededBytes,
                    out _,
                    out var returnedCount))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the installed printer queues.");

            var result = new List<DiscoveredPrinter>(checked((int)returnedCount));
            var itemSize = Marshal.SizeOf<PrinterInfo4>();
            for (var index = 0; index < returnedCount; index++)
            {
                var item = Marshal.PtrToStructure<PrinterInfo4>(
                    IntPtr.Add(buffer, checked((int)index * itemSize)));
                var name = Marshal.PtrToStringUni(item.PrinterName)?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var driverName = "Unknown driver";
                var portName = string.Empty;
                var status = "Unknown";
                var isDefault = (item.Attributes & PrinterAttributeDefault) != 0;
                IntPtr printerHandle = IntPtr.Zero;
                try
                {
                    if (OpenPrinterW(name, out printerHandle, IntPtr.Zero) && printerHandle != IntPtr.Zero)
                    {
                        _ = GetPrinterW(printerHandle, 2, IntPtr.Zero, 0, out var infoBytes);
                        if (infoBytes is > 0 and <= MaximumPrinterEnumerationBytes)
                        {
                            var infoBuffer = Marshal.AllocHGlobal(checked((int)infoBytes));
                            try
                            {
                                if (GetPrinterW(printerHandle, 2, infoBuffer, infoBytes, out _))
                                {
                                    var info = Marshal.PtrToStructure<PrinterInfo2>(infoBuffer);
                                    var resolvedDriver = Marshal.PtrToStringUni(info.DriverName)?.Trim();
                                    if (!string.IsNullOrWhiteSpace(resolvedDriver))
                                        driverName = resolvedDriver;
                                    portName = Marshal.PtrToStringUni(info.PortName)?.Trim() ?? string.Empty;
                                    status = ResolveWinspoolStatus(info.Status);
                                    isDefault = (info.Attributes & PrinterAttributeDefault) != 0;
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(infoBuffer);
                            }
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is Win32Exception or ExternalException or InvalidOperationException)
                {
                    // Keep the queue visible even when its detailed metadata cannot be read.
                }
                finally
                {
                    if (printerHandle != IntPtr.Zero)
                        _ = ClosePrinter(printerHandle);
                }

                var outputMode = PrinterOutputModeRules.Detect(name, driverName, portName);
                result.Add(new DiscoveredPrinter(name, driverName, portName, status, isDefault, outputMode));
            }

            return NormalizeDiscoveredQueues(result);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ResolveWinspoolStatus(uint status)
    {
        if ((status & PrinterStatusOffline) != 0) return "Offline";
        if ((status & PrinterStatusPaused) != 0) return "Paused";
        if ((status & PrinterStatusBusy) != 0) return "Busy";
        return status == 0 ? "Ready" : "Error";
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
