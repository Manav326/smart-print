using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SmartPrint.Agent;

public sealed class AgentCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QRtoPrint.Agent.Identity.v1");
    private readonly string identityPath;
    private readonly string revocationMarkerPath;
    private readonly object sync = new();

    public AgentCredentialStore(string? identityPath = null)
    {
        this.identityPath = string.IsNullOrWhiteSpace(identityPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "QRtoPrint",
                "agent.identity")
            : Path.GetFullPath(identityPath);

        revocationMarkerPath = this.identityPath + ".revoked";
    }

    public AgentCredentials? Load()
    {
        lock (sync)
        {
            return LoadCore();
        }
    }

    public bool IsRevoked()
    {
        lock (sync)
        {
            return !File.Exists(identityPath) && File.Exists(revocationMarkerPath);
        }
    }

    public AgentCredentials? LoadForCloudRequests(string? configuredApiBaseUrl = null)
    {
        lock (sync)
        {
            var credentials = LoadCore();
            if (credentials is null) return null;

            // Validate before any request can attach the Agent secret. Older
            // installations may have a remote HTTP origin saved; migrate that
            // identity only to the HTTPS origin configured by the Windows installer.
            Uri apiUri;
            try
            {
                apiUri = AgentPairing.ValidateApiBaseUrl(credentials.ApiBaseUrl);
            }
            catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(configuredApiBaseUrl))
            {
                apiUri = AgentPairing.ValidateApiBaseUrl(configuredApiBaseUrl);
                if (apiUri.Scheme != Uri.UriSchemeHttps && !apiUri.IsLoopback)
                    throw;
            }

            var normalizedOrigin = apiUri.GetLeftPart(UriPartial.Authority);
            if (!string.Equals(credentials.ApiBaseUrl.TrimEnd('/'), normalizedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                credentials = credentials with { ApiBaseUrl = normalizedOrigin };
                Save(credentials);
            }

            return credentials;
        }
    }

    private AgentCredentials? LoadCore()
    {
        if (!File.Exists(identityPath))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(identityPath);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return JsonSerializer.Deserialize<AgentCredentials>(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void Delete()
    {
        lock (sync)
        {
            DeleteCore();
            DeleteRevocationMarkerCore();
        }
    }

    public bool DeleteIfMatches(AgentCredentials expectedCredentials)
    {
        ArgumentNullException.ThrowIfNull(expectedCredentials);
        lock (sync)
        {
            var current = LoadCore();
            if (current is null ||
                current.AgentDeviceId != expectedCredentials.AgentDeviceId ||
                !string.Equals(current.ApiBaseUrl, expectedCredentials.ApiBaseUrl, StringComparison.Ordinal) ||
                !SecretsMatch(current.AgentSecret, expectedCredentials.AgentSecret))
            {
                return false;
            }

            // Persist the revocation tombstone before removing the credential.
            // If marker creation fails (disk/ACL issues), keep the old identity so
            // the desktop can never mistake a failed revoke for a fresh installation.
            WriteRevocationMarker();
            DeleteCore();
            return true;
        }
    }

    private void DeleteCore()
    {
        if (File.Exists(identityPath))
        {
            File.Delete(identityPath);
        }

        var temporaryPath = identityPath + ".tmp";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }

    private void DeleteRevocationMarkerCore()
    {
        if (File.Exists(revocationMarkerPath))
        {
            File.Delete(revocationMarkerPath);
        }
    }

    private void WriteRevocationMarker()
    {
        var directory = Path.GetDirectoryName(identityPath)
            ?? throw new InvalidOperationException("Could not determine the agent identity directory.");
        Directory.CreateDirectory(directory);
        SecurePermissions(directory);
        File.WriteAllText(revocationMarkerPath, DateTimeOffset.UtcNow.ToString("O"), Encoding.UTF8);
    }

    private static bool SecretsMatch(string currentSecret, string expectedSecret)
    {
        var currentBytes = Encoding.UTF8.GetBytes(currentSecret);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSecret);
        try
        {
            return currentBytes.Length == expectedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(currentBytes, expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    public void Save(AgentCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        lock (sync)
        {
            var directory = Path.GetDirectoryName(identityPath)
                ?? throw new InvalidOperationException("Could not determine the agent identity directory.");
            Directory.CreateDirectory(directory);
            SecurePermissions(directory);

            var plainBytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
            try
            {
                var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.LocalMachine);
                try
                {
                    var tempPath = identityPath + ".tmp";
                    File.WriteAllBytes(tempPath, protectedBytes);
                    if (File.Exists(identityPath))
                    {
                        File.Replace(tempPath, identityPath, null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(tempPath, identityPath);
                    }

                    // A fresh, explicit enrollment clears a previous server revocation.
                    DeleteRevocationMarkerCore();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    private void SecurePermissions(string directory)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var localServiceSid = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");

        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        directorySecurity.SetOwner(ownerSid);
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            localServiceSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            administratorsSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(directorySecurity);

        if (File.Exists(identityPath))
        {
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fileSecurity.SetOwner(ownerSid);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            fileSecurity.AddAccessRule(new FileSystemAccessRule(localServiceSid, FileSystemRights.FullControl, AccessControlType.Allow));
            fileSecurity.AddAccessRule(new FileSystemAccessRule(administratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(identityPath).SetAccessControl(fileSecurity);
        }
    }
}
