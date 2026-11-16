using System.Runtime.InteropServices;
using System.Text.Json;

namespace SmartPrint.Agent;

/// <summary>
/// Reads only capabilities reported by the installed Windows printer driver.
/// A missing or failed driver query is represented as unknown, never as false.
/// </summary>
public static class WindowsPrinterCapabilityDiscovery
{
    private const short DcPapers = 2;
    private const short DcPaperSizes = 3;
    private const short DcDuplex = 7;
    private const short DcEnumResolutions = 13;
    private const short DcPaperNames = 16;
    private const short DcOrientation = 17;
    private const short DcColorDevice = 32;
    private const short DcMediaTypes = 34;
    private const short DcMediaTypeNames = 35;
    private const int MaximumReportedItems = 128;
    private const int FixedNameCharacters = 64;

    public static string DiscoverJson(string queueName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(queueName))
            return "{}";

        try
        {
            var payload = new
            {
                schemaVersion = 1,
                source = "WINDOWS_DRIVER",
                discoveredAtUtc = DateTimeOffset.UtcNow,
                color = ReadBoolean(queueName, DcColorDevice),
                duplex = ReadBoolean(queueName, DcDuplex),
                orientation = ReadOrientation(queueName),
                paperSizes = ReadPaperSizes(queueName),
                mediaTypes = ReadMediaTypes(queueName),
                resolutions = ReadResolutions(queueName)
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

    internal static short? ResolveFourBySixPaperCode(string queueName, IntPtr devMode)
    {
        if (string.IsNullOrWhiteSpace(queueName)) return null;
        var count = DeviceCapabilitiesW(queueName, null, DcPapers, IntPtr.Zero, devMode);
        if (count is <= 0 or > MaximumReportedItems) return null;

        var codeBuffer = Marshal.AllocHGlobal(count * sizeof(short));
        var sizeBuffer = Marshal.AllocHGlobal(count * Marshal.SizeOf<WinPoint>());
        var nameBuffer = Marshal.AllocHGlobal(count * FixedNameCharacters * sizeof(char));
        try
        {
            var codeCount = DeviceCapabilitiesW(queueName, null, DcPapers, codeBuffer, devMode);
            var sizeCount = DeviceCapabilitiesW(queueName, null, DcPaperSizes, sizeBuffer, devMode);
            var nameCount = DeviceCapabilitiesW(queueName, null, DcPaperNames, nameBuffer, devMode);
            if (codeCount != count || sizeCount != count || nameCount != count) return null;

            var values = new List<(short Code, string Name, int WidthTenthMm, int HeightTenthMm)>(count);
            for (var index = 0; index < count; index++)
            {
                var code = Marshal.ReadInt16(codeBuffer, index * sizeof(short));
                var point = Marshal.PtrToStructure<WinPoint>(
                    IntPtr.Add(sizeBuffer, index * Marshal.SizeOf<WinPoint>()));
                var name = Marshal.PtrToStringUni(
                    IntPtr.Add(nameBuffer, index * FixedNameCharacters * sizeof(char)),
                    FixedNameCharacters)?.TrimEnd('\0', ' ') ?? string.Empty;
                values.Add((code, name, point.X, point.Y));
            }
            return PrinterCapabilityRules.SelectFourBySixPaperCode(values);
        }
        finally
        {
            Marshal.FreeHGlobal(codeBuffer);
            Marshal.FreeHGlobal(sizeBuffer);
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
