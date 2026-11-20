using System.Runtime.InteropServices;
using System.Text.Json;
using System.Printing;

namespace SmartPrint.Agent;

/// <summary>
/// Reads only capabilities reported by the installed Windows printer driver.
/// A missing or failed driver query is represented as unknown, never as false.
/// </summary>
public static class WindowsPrinterCapabilityDiscovery
{
    private sealed record PagesPerSheetCapabilityResult(IReadOnlyList<int> Values, string Status);
    private sealed record PagesPerSheetDirectionResult(string? Value, string Status);

    private const short DcPapers = 2;
    private const short DcPaperSizes = 3;
    private const short DcDuplex = 7;
    private const short DcEnumResolutions = 13;
    private const short DcPaperNames = 16;
    private const short DcOrientation = 17;
    private const short DcColorDevice = 32;
    private const short DcNup = 33;
    private const short DcMediaTypes = 34;
    private const short DcMediaTypeNames = 35;
    private const int MaximumReportedItems = 128;
    private const int FixedNameCharacters = 64;

    public static string DiscoverJson(string queueName, string? driverName = null)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(queueName))
            return "{}";

        try
        {
            var colour = ReadBoolean(queueName, DcColorDevice);
            var duplex = ReadBoolean(queueName, DcDuplex);
            var paperSizes = ReadPaperSizes(queueName);
            var mediaTypes = ReadMediaTypes(queueName);
            var pageMediaTypes = ReadPageMediaTypes(queueName);
            var resolutions = ReadResolutions(queueName);
            var pagesPerSheet = ReadPagesPerSheet(queueName);
            var pagesPerSheetDirection = ReadPagesPerSheetDirection(queueName);
            var payload = new
            {
                schemaVersion = 2,
                source = "WINDOWS_DRIVER",
                queueName = queueName.Trim(),
                driverName = string.IsNullOrWhiteSpace(driverName) ? null : driverName.Trim(),
                discoveredAtUtc = DateTimeOffset.UtcNow,
                color = colour,
                colorStatus = ResolveBooleanCapabilityState(colour),
                duplex,
                duplexStatus = ResolveBooleanCapabilityState(duplex),
                orientation = ReadOrientation(queueName),
                paperSizes,
                paperSizesStatus = paperSizes.Count > 0 ? "supported" : "unknown",
                pagesPerSheet = pagesPerSheet.Values,
                pagesPerSheetStatus = pagesPerSheet.Status,
                pagesPerSheetDirection = pagesPerSheetDirection.Value,
                pagesPerSheetDirectionStatus = pagesPerSheetDirection.Status,
                mediaTypes,
                mediaTypesStatus = mediaTypes.Count > 0 ? "supported" : "unknown",
                pageMediaTypes = pageMediaTypes.Values,
                pageMediaTypesStatus = pageMediaTypes.Status,
                resolutions,
                resolutionsStatus = resolutions.Count > 0 ? "supported" : "unknown"
            };
            return JsonSerializer.Serialize(payload);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or
                ExternalException or ArgumentException or OverflowException or
                MarshalDirectiveException)
        {
            return "{}";
        }
    }

    private static string ResolveBooleanCapabilityState(bool? value) => value switch
    {
        true => "supported",
        false => "unsupported",
        _ => "unknown"
    };

    private static bool? ReadBoolean(string queueName, short capability)
    {
        var result = DeviceCapabilitiesW(queueName, null, capability, IntPtr.Zero, IntPtr.Zero);
        return result < 0 ? null : result > 0;
    }

    private static object ReadOrientation(string queueName)
    {
        var result = DeviceCapabilitiesW(queueName, null, DcOrientation, IntPtr.Zero, IntPtr.Zero);
        bool? landscape = result switch
        {
            0 => false,
            90 or 270 => true,
            _ => null
        };
        return new
        {
            portrait = result >= 0 ? true : (bool?)null,
            landscape
        };
    }

    private static IReadOnlyList<object> ReadPaperSizes(string queueName)
    {
        var count = ReadCount(queueName, DcPapers);
        if (count == 0) return Array.Empty<object>();

        var codeBuffer = Marshal.AllocHGlobal(count * sizeof(short));
        var sizeBuffer = Marshal.AllocHGlobal(count * Marshal.SizeOf<WinPoint>());
        var nameBuffer = Marshal.AllocHGlobal(count * FixedNameCharacters * sizeof(char));
        try
        {
            var codeCount = DeviceCapabilitiesW(queueName, null, DcPapers, codeBuffer, IntPtr.Zero);
            var sizeCount = DeviceCapabilitiesW(queueName, null, DcPaperSizes, sizeBuffer, IntPtr.Zero);
            var nameCount = DeviceCapabilitiesW(queueName, null, DcPaperNames, nameBuffer, IntPtr.Zero);
            var actual = Math.Min(count, Math.Max(0, codeCount));
            if (actual == 0) return Array.Empty<object>();

            var result = new List<object>(actual);
            for (var index = 0; index < actual; index++)
            {
                var code = Marshal.ReadInt16(codeBuffer, index * sizeof(short));
                var hasSize = index < sizeCount;
                var point = hasSize
                    ? Marshal.PtrToStructure<WinPoint>(IntPtr.Add(sizeBuffer, index * Marshal.SizeOf<WinPoint>()))
                    : default;
                var name = index < nameCount
                    ? Marshal.PtrToStringUni(
                        IntPtr.Add(nameBuffer, index * FixedNameCharacters * sizeof(char)),
                        FixedNameCharacters)?.TrimEnd('\0', ' ')
                    : null;
                result.Add(new
                {
                    code,
                    name = string.IsNullOrWhiteSpace(name) ? StandardPaperName(code) : name,
                    widthTenthMm = hasSize ? point.X : 0,
                    heightTenthMm = hasSize ? point.Y : 0
                });
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(codeBuffer);
            Marshal.FreeHGlobal(sizeBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static PagesPerSheetDirectionResult ReadPagesPerSheetDirection(string queueName)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(queueName);
            queue.Refresh();
            var capabilities = queue.GetPrintCapabilities();
            var supported = capabilities.PagesPerSheetDirectionCapability;
            var resolved = ResolvePagesPerSheetDirection(queue, capabilities);
            if (resolved.HasValue)
                return new PagesPerSheetDirectionResult(resolved.Value.ToString(), "supported");

            // A successful Print Schema query with an empty direction list is an
            // explicit driver response. A failed query is kept distinct as unknown.
            return new PagesPerSheetDirectionResult(
                null,
                supported is { Count: 0 } ? "unsupported" : "unknown");
        }
        catch (Exception exception) when (
            exception is PrintQueueException or InvalidOperationException or
                ExternalException or ArgumentException or UnauthorizedAccessException)
        {
            return new PagesPerSheetDirectionResult(null, "unknown");
        }
    }

    /// <summary>
    /// Resolves one deterministic, driver-supported N-up order. The same rule is
    /// used for capability publication and final PrintTicket configuration so
    /// the customer preview and physical driver use the same page sequence.
    /// </summary>
    internal static PagesPerSheetDirection? ResolvePagesPerSheetDirection(
        PrintQueue queue,
        PrintCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(capabilities);
        var supported = capabilities.PagesPerSheetDirectionCapability;
        if (supported is null || supported.Count == 0)
            return null;

        var current = queue.DefaultPrintTicket.PagesPerSheetDirection;
        if (current.HasValue && supported.Contains(current.Value))
            return current.Value;

        if (supported.Contains(PagesPerSheetDirection.RightBottom))
            return PagesPerSheetDirection.RightBottom;

        return supported[0];
    }

    private static PagesPerSheetCapabilityResult ReadPagesPerSheet(string queueName)
    {
        // Prefer Print Schema because it reports both supported N-up values and
        // the page-reading order used by the final PrintTicket.
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(queueName);
            queue.Refresh();
            var schemaOptions = queue.GetPrintCapabilities().PagesPerSheetCapability
                .Where(value => value > 1 && value <= 500)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            return schemaOptions.Length > 0
                ? new PagesPerSheetCapabilityResult(new[] { 1 }.Concat(schemaOptions).ToArray(), "supported")
                : new PagesPerSheetCapabilityResult([1], "unsupported");
        }
        catch (Exception exception) when (
            exception is PrintQueueException or InvalidOperationException or
                ExternalException or ArgumentException or UnauthorizedAccessException)
        {
            // A legacy driver may expose DC_NUP even when Print Schema is unavailable.
        }

        var count = DeviceCapabilitiesW(queueName, null, DcNup, IntPtr.Zero, IntPtr.Zero);
        // Win32 returns zero for several "not exposed" cases. Do not turn that
        // ambiguous result into a false claim that the printer cannot do N-up.
        if (count <= 0 || count > MaximumReportedItems)
            return new PagesPerSheetCapabilityResult([1], "unknown");

        // DC_NUP returns a DWORD array, unlike DC_PAPERS which returns WORD values.
        var buffer = Marshal.AllocHGlobal(checked(count * sizeof(uint)));
        try
        {
            var actual = DeviceCapabilitiesW(queueName, null, DcNup, buffer, IntPtr.Zero);
            if (actual <= 0 || actual > count)
                return new PagesPerSheetCapabilityResult([1], "unknown");

            var values = new SortedSet<int> { 1 };
            var foundValidValue = false;
            for (var index = 0; index < actual; index++)
            {
                var pagesPerSheet = Marshal.ReadInt32(buffer, checked(index * sizeof(uint)));
                if (pagesPerSheet is >= 1 and <= 500)
                {
                    foundValidValue = true;
                    if (pagesPerSheet > 1)
                        values.Add(pagesPerSheet);
                }
            }

            if (!foundValidValue)
                return new PagesPerSheetCapabilityResult([1], "unknown");

            return new PagesPerSheetCapabilityResult(
                values.ToArray(),
                values.Count > 1 ? "supported" : "unsupported");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed record PageMediaTypeCapabilityResult(IReadOnlyList<string> Values, string Status);

    private static PageMediaTypeCapabilityResult ReadPageMediaTypes(string queueName)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(queueName);
            queue.Refresh();
            var values = queue.GetPrintCapabilities().PageMediaTypeCapability
                .Select(value => value.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            return new PageMediaTypeCapabilityResult(
                values,
                values.Length > 0 ? "supported" : "unsupported");
        }
        catch (Exception exception) when (
            exception is PrintQueueException or InvalidOperationException or
                ExternalException or ArgumentException or UnauthorizedAccessException)
        {
            return new PageMediaTypeCapabilityResult(Array.Empty<string>(), "unknown");
        }
    }

    /// <summary>
    /// Converts a canonical Print Schema media choice through the installed
    /// printer's own PrintTicket provider. This avoids relying on localized
    /// vendor display strings when the driver exposes standard PageMediaType.
    /// Returns null only when the schema does not advertise a matching finish.
    /// </summary>
    internal static byte[]? CreatePhotoMediaDevMode(string queueName, string finish)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(queueName);
            queue.Refresh();
            var capabilities = queue.GetPrintCapabilities();
            var selectedName = PrinterCapabilityRules.SelectPhotoPrintSchemaMediaType(
                capabilities.PageMediaTypeCapability.Select(value => value.ToString()),
                finish);
            if (selectedName is null)
                return null;

            var requestedType = Enum.Parse<PageMediaType>(selectedName, ignoreCase: false);
            var requestedTicket = new PrintTicket { PageMediaType = requestedType };
            var validation = queue.MergeAndValidatePrintTicket(
                queue.DefaultPrintTicket,
                requestedTicket,
                PrintTicketScope.JobScope);
            if (validation.ValidatedPrintTicket.PageMediaType != requestedType)
                throw new InvalidOperationException(
                    "The printer changed the requested photo media finish while validating its PrintTicket.");

            using var converter = new System.Printing.Interop.PrintTicketConverter(
                queueName,
                System.Printing.Interop.PrintTicketConverter.MaxPrintSchemaVersion);
            return converter.ConvertPrintTicketToDevMode(
                validation.ValidatedPrintTicket,
                System.Printing.Interop.BaseDevModeType.PrinterDefault,
                PrintTicketScope.JobScope);
        }
        catch (Exception exception) when (
            exception is PrintQueueException or InvalidOperationException or
                ExternalException or ArgumentException or UnauthorizedAccessException)
        {
            // If Print Schema advertised a matching type but rejected conversion,
            // surface the failure. Do not silently guess another media code.
            throw new PrintExecutionBlockedException(
                "PRINTER_SETTINGS_REJECTED",
                "Windows could not convert the driver's advertised photo media option into a validated printer setting.",
                exception);
        }
    }

    private static IReadOnlyList<object> ReadMediaTypes(string queueName)
    {
        var count = ReadCount(queueName, DcMediaTypes);
        if (count == 0) return Array.Empty<object>();

        var codeBuffer = Marshal.AllocHGlobal(count * sizeof(short));
        var nameBuffer = Marshal.AllocHGlobal(count * FixedNameCharacters * sizeof(char));
        try
        {
            var codeCount = DeviceCapabilitiesW(queueName, null, DcMediaTypes, codeBuffer, IntPtr.Zero);
            var nameCount = DeviceCapabilitiesW(queueName, null, DcMediaTypeNames, nameBuffer, IntPtr.Zero);
            var actual = Math.Min(count, Math.Max(0, codeCount));
            var result = new List<object>(actual);
            for (var index = 0; index < actual; index++)
            {
                var code = Marshal.ReadInt16(codeBuffer, index * sizeof(short));
                var name = index < nameCount
                    ? Marshal.PtrToStringUni(
                        IntPtr.Add(nameBuffer, index * FixedNameCharacters * sizeof(char)),
                        FixedNameCharacters)?.TrimEnd('\0', ' ')
                    : null;
                result.Add(new
                {
                    code,
                    name = string.IsNullOrWhiteSpace(name) ? "Driver media " + code : name
                });
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(codeBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static IReadOnlyList<object> ReadResolutions(string queueName)
    {
        var count = ReadCount(queueName, DcEnumResolutions);
        if (count == 0) return Array.Empty<object>();

        var buffer = Marshal.AllocHGlobal(count * sizeof(int) * 2);
        try
        {
            var actual = DeviceCapabilitiesW(queueName, null, DcEnumResolutions, buffer, IntPtr.Zero);
            if (actual <= 0) return Array.Empty<object>();

            var result = new List<object>(Math.Min(count, actual));
            for (var index = 0; index < Math.Min(count, actual); index++)
            {
                var xDpi = Marshal.ReadInt32(buffer, index * sizeof(int) * 2);
                var yDpi = Marshal.ReadInt32(buffer, index * sizeof(int) * 2 + sizeof(int));
                if (xDpi > 0 && yDpi > 0)
                    result.Add(new { xDpi, yDpi });
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string StandardPaperName(short code) => code switch
    {
        1 => "Letter",
        5 => "Legal",
        8 => "A3",
        9 => "A4",
        11 => "A5",
        13 => "B5",
        _ => "Driver paper " + code
    };

    internal static short? ResolvePhotoMediaTypeCode(string queueName, IntPtr devMode, string finish)
    {
        if (string.IsNullOrWhiteSpace(queueName)) return null;
        var count = DeviceCapabilitiesW(queueName, null, DcMediaTypes, IntPtr.Zero, devMode);
        if (count is <= 0 or > MaximumReportedItems) return null;

        var codeBuffer = Marshal.AllocHGlobal(count * sizeof(short));
        var nameBuffer = Marshal.AllocHGlobal(count * FixedNameCharacters * sizeof(char));
        try
        {
            var codeCount = DeviceCapabilitiesW(queueName, null, DcMediaTypes, codeBuffer, devMode);
            var nameCount = DeviceCapabilitiesW(queueName, null, DcMediaTypeNames, nameBuffer, devMode);
            if (codeCount != count || nameCount != count) return null;

            var values = new List<(short Code, string Name)>(count);
            for (var index = 0; index < count; index++)
            {
                var code = Marshal.ReadInt16(codeBuffer, index * sizeof(short));
                var name = Marshal.PtrToStringUni(
                    IntPtr.Add(nameBuffer, index * FixedNameCharacters * sizeof(char)),
                    FixedNameCharacters)?.TrimEnd('\0', ' ');
                if (string.IsNullOrWhiteSpace(name)) continue;
                values.Add((code, name));
            }
            return values.Count == count ? PrinterCapabilityRules.SelectPhotoMediaTypeCode(values, finish) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(codeBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static int ReadCount(string queueName, short capability)
    {
        var count = DeviceCapabilitiesW(queueName, null, capability, IntPtr.Zero, IntPtr.Zero);
        return count is > 0 and <= MaximumReportedItems ? count : 0;
    }

    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeviceCapabilitiesW(
        string deviceName,
        string? port,
        short capability,
        IntPtr output,
        IntPtr devMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }
}
