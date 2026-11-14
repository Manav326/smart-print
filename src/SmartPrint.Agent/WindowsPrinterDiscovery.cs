using System.Management;

namespace SmartPrint.Agent;

public sealed class WindowsPrinterDiscovery(ILogger<WindowsPrinterDiscovery> logger)
{
    public bool TryDiscover(out IReadOnlyList<DiscoveredPrinter> discovered)
    {
        discovered = [];
        if (!OperatingSystem.IsWindows())
        {
            logger.LogWarning("Windows printer discovery was requested on a non-Windows host.");
            return false;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverName, PortName, WorkOffline, PrinterStatus, Default FROM Win32_Printer");
            using var results = searcher.Get();
            var printers = new List<DiscoveredPrinter>();

            foreach (ManagementObject printer in results)
            {
                using (printer)
                {
                    var name = ReadString(printer, "Name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var driver = ReadString(printer, "DriverName");
                    var port = ReadString(printer, "PortName");
                    var offline = ReadBoolean(printer, "WorkOffline");
                    var printerStatus = ReadInteger(printer, "PrinterStatus");
                    var isDefault = ReadBoolean(printer, "Default");
                    var outputMode = PrinterOutputModeRules.Detect(name, driver, port);

                    var status = ResolvePublishedStatus(outputMode, printerStatus, offline);

                    printers.Add(new DiscoveredPrinter(
                        name.Trim(),
                        string.IsNullOrWhiteSpace(driver) ? "Unknown driver" : driver.Trim(),
                        port?.Trim() ?? string.Empty,
                        status,
                        isDefault,
                        outputMode,
                        WindowsPrinterCapabilityDiscovery.DiscoverJson(name.Trim())));
                }
            }

            discovered = NormalizeDiscoveredQueues(printers);
            return true;
        }
        catch (ManagementException exception)
        {
            logger.LogWarning(exception, "Windows printer discovery through WMI failed; retaining the last successful printer inventory.");
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "The Agent could not read the Windows printer inventory; retaining the last successful inventory.");
            return false;
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
}
