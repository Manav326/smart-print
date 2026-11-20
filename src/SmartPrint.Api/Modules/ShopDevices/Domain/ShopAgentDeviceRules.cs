using System.Text.Json;

namespace SmartPrint.Api.Modules.ShopDevices.Domain;

/// <summary>
/// Pure parsing rules for endpoint hints reported by a paired local Agent.
/// </summary>
public static class ShopAgentDeviceRules
{
    /// <summary>
    /// Reads the Agent's advertised endpoint list, failing closed to an empty list for malformed data.
    /// </summary>
    public static string[] ReadLocalAgentEndpoints(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(json) ? "[]" : json)
                ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
