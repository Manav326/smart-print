using System.Security.Cryptography;
using System.Text;

namespace SmartPrint.Api.Security;

/// <summary>
/// Hashes opaque bearer values before persistence or lookup.
/// </summary>
public static class OpaqueTokenHasher
{
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
