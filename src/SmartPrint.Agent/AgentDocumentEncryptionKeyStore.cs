using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace SmartPrint.Agent;

/// <summary>
/// Per-Agent RSA key used only to wrap per-upload AES keys. The private key is
/// protected with DPAPI LocalMachine and stored inside a SYSTEM-only directory.
/// </summary>
public sealed class AgentDocumentEncryptionKeyStore
{
    private static readonly byte[] Entropy = "SmartPrint.Agent.PrivateDocumentKey.v1"u8.ToArray();
    private readonly string keyPath;
    private readonly bool applyWindowsAcl;
    private readonly object sync = new();

    public AgentDocumentEncryptionKeyStore(string? keyPath = null)
    {
        applyWindowsAcl = keyPath is null;
        this.keyPath = Path.GetFullPath(keyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "QRtoPrint", "LocalQueue", "document-encryption-key.dpapi"));
    }

    public string GetOrCreatePublicKey()
    {
        lock (sync)
        {
            using var rsa = LoadOrCreate();
            return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        }
    }

    public byte[] DecryptWrappedAesKey(string wrappedKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(wrappedKeyBase64) || wrappedKeyBase64.Length > 2048)
            throw new CryptographicException("The wrapped document key is invalid.");
        byte[] wrapped;
        try { wrapped = Convert.FromBase64String(wrappedKeyBase64); }
        catch (FormatException exception) { throw new CryptographicException("The wrapped document key is invalid.", exception); }
        try
        {
            lock (sync)
            {
                using var rsa = LoadOrCreate();
                return rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
            }
        }
        finally { CryptographicOperations.ZeroMemory(wrapped); }
    }

    private RSA LoadOrCreate()
    {
        var directory = Path.GetDirectoryName(keyPath)
            ?? throw new InvalidOperationException("The Agent document key directory is unavailable.");
        Directory.CreateDirectory(directory);
        if (applyWindowsAcl && OperatingSystem.IsWindows())
        {
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            using var identity = WindowsIdentity.GetCurrent();
            var ownerSid = identity.User
                ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(ownerSid);
            // The production Agent service runs as LocalSystem, so the key folder
            // remains SYSTEM-owned and SYSTEM-only. Interactive diagnostics and
            // lifecycle QA retain access only through their own identity.
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            if (!ownerSid.Equals(systemSid))
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    ownerSid,
                    FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
            new DirectoryInfo(directory).SetAccessControl(security);
        }

        if (File.Exists(keyPath))
        {
            var protectedBytes = File.ReadAllBytes(keyPath);
            byte[] privateBytes;
            try { privateBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            try
            {
                var rsa = RSA.Create();
                try
                {
                    rsa.ImportPkcs8PrivateKey(privateBytes, out _);
                    if (rsa.KeySize < 2048)
                        throw new CryptographicException("The Agent document key is too small.");
                    return rsa;
                }
                catch
                {
                    rsa.Dispose();
                    throw;
                }
            }
            finally { CryptographicOperations.ZeroMemory(privateBytes); }
        }

        var created = RSA.Create(3072);
        var plain = created.ExportPkcs8PrivateKey();
        try
        {
            SaveProtectedPrivateKey(plain);
        }
        catch
        {
            created.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return created;
    }

    private void SaveProtectedPrivateKey(byte[] privateBytes)
    {
        var protectedBytes = ProtectedData.Protect(privateBytes, Entropy, DataProtectionScope.LocalMachine);
        var temporary = keyPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(keyPath)) File.Replace(temporary, keyPath, null, true);
            else File.Move(temporary, keyPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}