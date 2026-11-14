using System.Security.Cryptography;
using System.Text;

namespace SmartPrint.Api.Security;

public static class OpaqueTokenHasher
{
    public static string HashOpaqueToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
