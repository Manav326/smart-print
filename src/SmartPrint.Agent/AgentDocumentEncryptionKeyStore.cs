using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace SmartPrint.Agent;

/// <summary>
/// Per-Agent RSA key used only to wrap per-upload AES keys. Production storage
/// uses DPAPI CurrentUser under the Agent service identity; older LocalMachine
/// protected keys are migrated after successful decryption and validation.
/// </summary>
public sealed class AgentDocumentEncryptionKeyStore
{
    private static readonly byte[] Entropy = "SmartPrint.Agent.PrivateDocumentKey.v1"u8.ToArray();
    private readonly string keyPath;
    private readonly bool applyWindowsAcl;
    private readonly DataProtectionScope protectionScope;
    private readonly object sync = new();

    public AgentDocumentEncryptionKeyStore(string? keyPath = null, DataProtectionScope? protectionScope = null)
    {
        applyWindowsAcl = keyPath is null;
        this.keyPath = Path.GetFullPath(keyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "QRtoPrint", "LocalQueue", "document-encryption-key.dpapi"));
        this.protectionScope = protectionScope ?? (keyPath is null
            ? DataProtectionScope.CurrentUser
            : DataProtectionScope.LocalMachine);
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
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(systemSid);
            // The Agent service runs as LocalSystem; omit a routine read ACE for
            // interactive Administrators. A local administrator can still take
            // ownership or execute as SYSTEM, so this is defence in depth only.
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }

        if (File.Exists(keyPath))
        {
            var protectedBytes = File.ReadAllBytes(keyPath);
            byte[] privateBytes;
            var needsProtectionUpgrade = false;
            try
            {
                try
                {
                    privateBytes = ProtectedData.Unprotect(protectedBytes, Entropy, protectionScope);
                }
                catch (CryptographicException) when (protectionScope == DataProtectionScope.CurrentUser)
                {
                    // Existing installations used LocalMachine DPAPI. Upgrade the
                    // private key only after it has been successfully imported.
                    privateBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
                    needsProtectionUpgrade = true;
                }
            }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }

            try
            {
                var rsa = RSA.Create();
                try
                {
                    rsa.ImportPkcs8PrivateKey(privateBytes, out _);
                    if (rsa.KeySize < 2048)
                        throw new CryptographicException("The Agent document key is too small.");

                    if (needsProtectionUpgrade)
                        SaveProtectedPrivateKey(privateBytes);
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
        var protectedBytes = ProtectedData.Protect(privateBytes, Entropy, protectionScope);
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