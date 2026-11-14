using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SmartPrint.Api.Services;

/// <summary>
/// Issues short-lived, single-use nonces for Google sign-in.
/// The nonce is bound into Google's signed ID token and consumed once by the
/// matching shop/customer endpoint. This store is process-local by design;
/// deployments with multiple API replicas must use a shared atomic store.
/// </summary>
public sealed class GoogleLoginNonceStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaximumOutstandingNonces = 10_000;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);

    public string Issue(string surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        if (surface is not ("shop" or "customer"))
            throw new ArgumentOutOfRangeException(nameof(surface));

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _nonces)
        {
            if (entry.Value <= now)
                _nonces.TryRemove(entry.Key, out _);
        }

        if (_nonces.Count >= MaximumOutstandingNonces)
            throw new InvalidOperationException("Google sign-in is temporarily busy. Please try again.");

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _nonces[Hash(surface, nonce)] = now.Add(Lifetime);
        return nonce;
    }

    public bool TryConsume(string surface, string? nonce)
    {
        if (surface is not ("shop" or "customer") ||
            string.IsNullOrWhiteSpace(nonce) ||
            nonce.Length != 64)
            return false;

        if (!_nonces.TryRemove(Hash(surface, nonce), out var expiresUtc))
            return false;

        return expiresUtc > DateTimeOffset.UtcNow;
    }

    private static string Hash(string surface, string nonce) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{surface}:{nonce}"))).ToLowerInvariant();
}
