using System.Security.Cryptography;
using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Modules.AgentConnectivity.Domain;

public enum AgentDocumentEncryptionKeyFailure
{
    None,
    InvalidPublicKey,
    UnsupportedKeySize
}

/// <summary>
/// Validation rules for sensitive and time-bounded Agent heartbeat data.
/// </summary>
public static class AgentHeartbeatRules
{
    /// <summary>
    /// Missing key material is allowed because the caller clears any previously persisted key.
    /// Non-empty values must be RSA SubjectPublicKeyInfo keys from 2048 to 4096 bits.
    /// </summary>
    public static AgentDocumentEncryptionKeyFailure ValidateDocumentEncryptionPublicKey(string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
            return AgentDocumentEncryptionKeyFailure.None;

        byte[]? bytes = null;
        try
        {
            bytes = Convert.FromBase64String(publicKey);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(bytes, out _);
            return rsa.KeySize is >= 2048 and <= 4096
                ? AgentDocumentEncryptionKeyFailure.None
                : AgentDocumentEncryptionKeyFailure.UnsupportedKeySize;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return AgentDocumentEncryptionKeyFailure.InvalidPublicKey;
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Accepts only non-negative storage counters measured within the existing heartbeat window.
    /// </summary>
    public static bool IsStorageSnapshotUsable(AgentStorageSnapshotRequest snapshot, DateTimeOffset now) =>
        snapshot.DocumentCount >= 0 &&
        snapshot.LogicalDocumentBytes >= 0 &&
        snapshot.QueueDiskBytes >= 0 &&
        snapshot.OutputFileCount >= 0 &&
        snapshot.OutputFileBytes >= 0 &&
        snapshot.MeasuredUtc >= now.AddHours(-1) &&
        snapshot.MeasuredUtc <= now.AddMinutes(1);
}
