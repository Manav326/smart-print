using System.Security.Cryptography;

using SmartPrint.Documents;

namespace SmartPrint.Api.Storage;

public sealed class DocumentStorage : IDisposable
{
    private const long MaximumFileBytes = 20 * 1024 * 1024;
    private readonly IConfiguration configuration;
    private readonly IWebHostEnvironment environment;
    private readonly byte[] encryptionKey;
    private readonly IDocumentThreatScanner threatScanner;
    private readonly IDocumentAnalyzer documentAnalyzer;

    public DocumentStorage(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IDocumentThreatScanner threatScanner,
        IDocumentAnalyzer documentAnalyzer)
    {
        this.configuration = configuration;
        this.environment = environment;
        this.threatScanner = threatScanner;
        this.documentAnalyzer = documentAnalyzer;
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
        byte[]? encryptedBytes = null;
        try
        {
            // Scan before parsing; scanner outages and detected malware both fail closed.
            await threatScanner.ScanAsync(bytes, cancellationToken);
            var analysis = await documentAnalyzer.AnalyzeAsync(bytes, extension, cancellationToken);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var storageKey = $"{tenantId:N}/{documentId:N}{extension}";
            var fullPath = ResolveStoragePath(storageKey);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            encryptedBytes = Encrypt(bytes, encryptionKey);

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
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (encryptedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(encryptedBytes);
            }
            if (memory.TryGetBuffer(out var buffer))
            {
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, checked((int)memory.Length)));
            }
        }
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
        var rootSetting = configuration["Documents:StorageRoot"];
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(rootSetting)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "documents")
            : rootSetting);
        var full = Path.GetFullPath(Path.Combine(root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid document storage key.");
        }
        return full;
    }
}

public sealed record StoredDocument(string StorageKey, string Sha256, long SizeBytes, DocumentAnalysis Analysis);
