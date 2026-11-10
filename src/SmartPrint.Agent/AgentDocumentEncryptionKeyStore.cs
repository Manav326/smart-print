using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace SmartPrint.Agent;

/// <summary>
/// Per-Agent RSA key used only to wrap per-upload AES keys. The private key is
/// protected with DPAPI LocalMachine and stored inside the protected queue.
/// </summary>
public sealed class AgentDocumentEncryptionKeyStore
{
    private static readonly byte[] Entropy = "SmartPrint.Agent.PrivateDocumentKey.v1"u8.ToArray();
    private readonly string keyPath;
    private readonly object sync = new();

    public AgentDocumentEncryptionKeyStore(string? keyPath = null)
    {
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
        if (File.Exists(keyPath))
        {
            var protectedBytes = File.ReadAllBytes(keyPath);
            byte[] privateBytes;
            try { privateBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            try
            {
                var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(privateBytes, out _);
                if (rsa.KeySize < 2048)
                {
                    rsa.Dispose();
                    throw new CryptographicException("The Agent document key is too small.");
                }
                return rsa;
            }
            finally { CryptographicOperations.ZeroMemory(privateBytes); }
        }

        var directory = Path.GetDirectoryName(keyPath)
            ?? throw new InvalidOperationException("The Agent document key directory is unavailable.");
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }

        var created = RSA.Create(3072);
        var plain = created.ExportPkcs8PrivateKey();
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                var temporary = keyPath + ".tmp";
                File.WriteAllBytes(temporary, protectedBytes);
                if (File.Exists(keyPath)) File.Replace(temporary, keyPath, null, true);
                else File.Move(temporary, keyPath);
            }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        catch
        {
            created.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return created;
    }
}