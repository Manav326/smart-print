using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SmartPrint.Agent;

/// <summary>
/// The Native Wi-Fi API can load WLAN provider code. Keep it out of the long-lived
/// Agent service just like printer-driver capability calls. SSID is display metadata,
/// so a timeout/failure safely falls back to the adapter's Windows friendly name.
/// </summary>
internal static class WindowsWifiNetworkName
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromSeconds(5);

    public static string? TryGetCurrentSsid(string networkInterfaceId)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(networkInterfaceId, out _))
            return null;

        if (Cache.TryGetValue(networkInterfaceId, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            return cached.Ssid;

        // The apphost name is stable for the installed Windows service. Do not fall
        // back to an in-process WLAN call in test hosts or dotnet tooling.
        if (!string.Equals(
                Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                "SmartPrint.Agent",
                StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var request = JsonSerializer.Serialize(networkInterfaceId);
            var result = new AgentChildProcessRunner().ExecuteAsync(
                "--get-current-ssid", request, TimeSpan.FromSeconds(5), CancellationToken.None)
                .GetAwaiter().GetResult();

            string? ssid = null;
            if (!result.TimedOut && result.ExitCode == 0)
            {
                var line = result.StandardOutput
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault(value => value.StartsWith("RESULT|", StringComparison.Ordinal));
                if (line is not null)
                {
                    var separator = line.IndexOf('|');
                    if (separator >= 0 && separator < line.Length - 1)
                        ssid = JsonSerializer.Deserialize<string>(line[(separator + 1)..]);
                }
            }

            if (string.IsNullOrWhiteSpace(ssid) || ssid.Any(char.IsControl))
                ssid = null;

            Cache[networkInterfaceId] = new CacheEntry(
                DateTimeOffset.UtcNow.Add(ssid is null ? FailureCacheLifetime : SuccessCacheLifetime), ssid);
            return ssid;
        }
        catch (Exception exception) when (
            exception is AgentChildProcessStartException or InvalidOperationException or
                System.ComponentModel.Win32Exception or IOException or JsonException)
        {
            Cache[networkInterfaceId] = new CacheEntry(DateTimeOffset.UtcNow.Add(FailureCacheLifetime), null);
            return null;
        }
    }

    public static string? TryGetCurrentSsidInProcess(string networkInterfaceId)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(networkInterfaceId, out var interfaceGuid))
            return null;

        return WindowsWifiNativeQuery.TryGetCurrentSsid(networkInterfaceId);
    }

    private sealed record CacheEntry(DateTimeOffset ExpiresUtc, string? Ssid);
}

internal static class WindowsWifiNativeQuery
{
    private const uint ClientVersion = 2;
    private const int CurrentConnectionOpcode = 7;
    private const int ConnectionAttributesSsidOffset = 520;
    private const int Dot11SsidBytesOffset = 524;
    private const int MaximumSsidBytes = 32;

    [DllImport("wlanapi.dll", EntryPoint = "WlanOpenHandle")]
    private static extern uint WlanOpenHandle(
        uint clientVersion,
        IntPtr reserved,
        out uint negotiatedVersion,
        out IntPtr clientHandle);

    [DllImport("wlanapi.dll", EntryPoint = "WlanQueryInterface")]
    private static extern uint WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        int opcode,
        IntPtr reserved,
        out uint dataSize,
        out IntPtr data,
        out int valueType);

    [DllImport("wlanapi.dll", EntryPoint = "WlanFreeMemory")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll", EntryPoint = "WlanCloseHandle")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    public static string? TryGetCurrentSsid(string networkInterfaceId)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(networkInterfaceId, out var interfaceGuid))
            return null;

        IntPtr clientHandle = IntPtr.Zero;
        IntPtr data = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out clientHandle) != 0 ||
                clientHandle == IntPtr.Zero)
                return null;

            if (WlanQueryInterface(
                    clientHandle,
                    ref interfaceGuid,
                    CurrentConnectionOpcode,
                    IntPtr.Zero,
                    out var dataSize,
                    out data,
                    out _) != 0 ||
                data == IntPtr.Zero ||
                dataSize < Dot11SsidBytesOffset)
                return null;

            var ssidLength = Marshal.ReadInt32(data, ConnectionAttributesSsidOffset);
            if (ssidLength is < 1 or > MaximumSsidBytes ||
                dataSize < (uint)(Dot11SsidBytesOffset + ssidLength))
                return null;

            var bytes = new byte[ssidLength];
            Marshal.Copy(IntPtr.Add(data, Dot11SsidBytesOffset), bytes, 0, ssidLength);
            // Preserve the exact SSID bytes for the saved network identity.
            // The owner-facing display name is trimmed separately by discovery.
            var ssid = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(ssid) || ssid.Any(char.IsControl) ? null : ssid;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
            if (clientHandle != IntPtr.Zero) _ = WlanCloseHandle(clientHandle, IntPtr.Zero);
        }
    }
}
