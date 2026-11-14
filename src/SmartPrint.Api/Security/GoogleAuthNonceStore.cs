using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace SmartPrint.Api.Security;

/// <summary>
/// Issues short-lived, single-use challenges for Google ID-token nonce validation.
/// Only a one-way hash of each nonce is retained in memory.
/// </summary>
public sealed class GoogleAuthNonceStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> issued = new();

    public string Issue()
    {
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;

        foreach (var item in issued)
        {
            if (item.Value <= now)
            {
                issued.TryRemove(item.Key, out _);
            }
        }

        issued[Hash(nonce)] = now.AddMinutes(5);
        return nonce;
    }

    public bool TryConsume(string? signedNonce, string? expectedNonce)
    {
        if (string.IsNullOrWhiteSpace(signedNonce) ||
            string.IsNullOrWhiteSpace(expectedNonce))
        {
            return false;
        }

        var signedBytes = Encoding.UTF8.GetBytes(signedNonce);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedNonce);
        if (signedBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(signedBytes, expectedBytes))
        {
            return false;
        }

        if (!issued.TryRemove(Hash(signedNonce), out var expiresUtc))
        {
            return false;
        }

        return expiresUtc > DateTimeOffset.UtcNow;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
