using System.Security.Cryptography;

using SmartPrint.Documents;

namespace SmartPrint.Api.Storage;

public sealed class DocumentStorage : IDisposable
{
    private const long MaximumFileBytes = 20 * 1024 * 1024;
    private readonly IConfiguration configuration;
    private readonly IWebHostEnvironment environment;
    private readonly byte[] encryptionKey;

    public DocumentStorage(IConfiguration configuration, IWebHostEnvironment environment)
    {
        this.configuration = configuration;
        this.environment = environment;
        encryptionKey = LoadEncryptionKey(configuration, environment);
    }

    public async Task<StoredDocument> ValidateAndStoreAsync(
        Guid tenantId,
        Guid documentId,
        string originalFileName,
        Stream input,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        if (declaredLength is <= 0 or > MaximumFileBytes)
        {
            throw new DocumentValidationException("File size must be between 1 byte and 20 MB.");
        }

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
        {
            throw new DocumentValidationException("Only PDF, JPG, JPEG and PNG files are supported in this release.");
        }

        await using var memory = new MemoryStream((int)declaredLength);
        await input.CopyToAsync(memory, cancellationToken);
        if (memory.Length != declaredLength || memory.Length > MaximumFileBytes)
        {
            throw new DocumentValidationException("The uploaded file is incomplete or exceeds the 20 MB limit.");
        }

        var bytes = memory.ToArray();
        var analysis = DocumentAnalyzer.Analyse(bytes, extension);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var storageKey = $"{tenantId:N}/{documentId:N}{extension}";
        var fullPath = ResolveStoragePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var encryptedBytes = Encrypt(bytes, encryptionKey);

        await using (var output = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(encryptedBytes, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        return new StoredDocument(storageKey, sha, bytes.LongLength, analysis);
    }

    // Stores already-encrypted Private Printing payloads as opaque bytes. This method
    // deliberately does not inspect, analyse, or decrypt the customer's document.
    public async Task<string> StoreOpaquePrivateRelayAsync(
        Guid tenantId,
        Guid ticketId,
        Stream input,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || ticketId == Guid.Empty ||
            declaredLength is <= 16 or > MaximumFileBytes + 16)
            throw new InvalidDataException("The encrypted private relay payload has an invalid size or identity.");

        await using var memory = new MemoryStream(checked((int)declaredLength));
        await input.CopyToAsync(memory, cancellationToken);
        if (memory.Length != declaredLength)
            throw new InvalidDataException("The encrypted private relay payload is incomplete.");

        var opaqueCiphertext = memory.ToArray();
        var protectedCiphertext = Encrypt(opaqueCiphertext, encryptionKey);
        var storageKey = $"private-relay/{tenantId:N}/{ticketId:N}/{Guid.NewGuid():N}.relay";
        var fullPath = ResolveStoragePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            await using var output = new FileStream(
                fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await output.WriteAsync(protectedCiphertext, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        catch
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(opaqueCiphertext);
            CryptographicOperations.ZeroMemory(protectedCiphertext);
        }

        return storageKey;
    }

    /// <summary>
    /// Lists only old, canonical private-relay objects. Callers must compare these
    /// keys with durable ticket references before deleting them. The age grace
    /// period prevents racing an upload between object creation and DB commit.
    /// </summary>
    public IReadOnlyList<string> GetStalePrivateRelayStorageKeys(TimeSpan minimumAge, int maximumItems)
    {
        if (minimumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumAge));
        if (maximumItems <= 0)
            return Array.Empty<string>();

        var relayRoot = Path.Combine(ResolveStorageRoot(), "private-relay");
        if (!Directory.Exists(relayRoot))
            return Array.Empty<string>();

        var cutoffUtc = DateTime.UtcNow - minimumAge;
        var candidates = new List<string>(Math.Min(maximumItems, 256));
        foreach (var path in Directory.EnumerateFiles(relayRoot, "*.relay", SearchOption.AllDirectories))
        {
            if (candidates.Count >= maximumItems)
                break;

            var file = new FileInfo(path);
            if (!file.Exists || file.LastWriteTimeUtc > cutoffUtc ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            var relative = Path.GetRelativePath(ResolveStorageRoot(), path).Replace('\\', '/');
            var segments = relative.Split('/');
            if (segments.Length != 4 ||
                !string.Equals(segments[0], "private-relay", StringComparison.Ordinal) ||
                !Guid.TryParseExact(segments[1], "N", out _) ||
                !Guid.TryParseExact(segments[2], "N", out _) ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(segments[3]), "N", out _))
                continue;

            candidates.Add(relative);
        }

        return candidates;
    }

    public Task DeleteAsync(string storageKey)
    {
        var path = ResolveStoragePath(storageKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public MemoryStream OpenRead(string storageKey)
    {
        var path = ResolveStoragePath(storageKey);
        var encryptedBytes = File.ReadAllBytes(path);
        var plaintext = Decrypt(encryptedBytes, encryptionKey);
        return new MemoryStream(plaintext, writable: false);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(encryptionKey);

    private static byte[] LoadEncryptionKey(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["Documents:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(configured) && environment.IsDevelopment())
        {
            configured = Convert.ToBase64String(Convert.FromHexString(
                "0123456789ABCDEFFEDCBA98765432100123456789ABCDEFFEDCBA9876543210"));
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("Documents:EncryptionKey is required outside Development.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Documents:EncryptionKey must be a base64-encoded 32-byte key.");
        }

        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("Documents:EncryptionKey must decode to exactly 32 bytes.");
        }

        return key;
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[4 + nonce.Length + tag.Length + ciphertext.Length];
        "QSP1"u8.CopyTo(output);
        nonce.CopyTo(output, 4);
        tag.CopyTo(output, 4 + nonce.Length);
        ciphertext.CopyTo(output, 4 + nonce.Length + tag.Length);
        return output;
    }

    private static byte[] Decrypt(byte[] encrypted, byte[] key)
    {
        const int headerLength = 4 + 12 + 16;
        if (encrypted.Length < headerLength || !encrypted.AsSpan(0, 4).SequenceEqual("QSP1"u8))
        {
            throw new InvalidDataException("The stored document has an unsupported encrypted format.");
        }

        var nonce = encrypted.AsSpan(4, 12);
        var tag = encrypted.AsSpan(16, 16);
        var ciphertext = encrypted.AsSpan(headerLength);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("The stored document could not be authenticated or decrypted.");
        }
    }

    private string ResolveStoragePath(string storageKey)
    {
        var root = ResolveStorageRoot();
        var full = Path.GetFullPath(Path.Combine(root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid document storage key.");
        }
        return full;
    }

    private string ResolveStorageRoot()
    {
        var rootSetting = configuration["Documents:StorageRoot"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(rootSetting)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "documents")
            : rootSetting);
    }
}

public sealed record StoredDocument(string StorageKey, string Sha256, long SizeBytes, DocumentAnalysis Analysis);
