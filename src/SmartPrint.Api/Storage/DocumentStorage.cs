using System.Security.Cryptography;

using SmartPrint.Api.Domain;
using SmartPrint.Documents;

namespace SmartPrint.Api.Storage;

public sealed class DocumentStorage : IDisposable
{
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
        if (declaredLength is <= 0 or > UploadLimits.MaxFileBytes)
        {
            throw new DocumentValidationException("File size must be within the upload size limit.");
        }

        await using var memory = new MemoryStream((int)declaredLength);
        await input.CopyToAsync(memory, cancellationToken);
        if (memory.Length != declaredLength || memory.Length > UploadLimits.MaxFileBytes)
        {
            throw new DocumentValidationException("The uploaded file is incomplete or exceeds the upload size limit.");
        }

        var bytes = memory.ToArray();
        var analysis = DocumentAnalyzer.Analyse(bytes, Path.GetExtension(originalFileName).ToLowerInvariant());
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var storageKey = $"{tenantId:N}/{documentId:N}{analysis.Extension}";
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

    /// <summary>
    /// Receives, encrypts, validates and atomically publishes a document without
    /// materialising the complete source or ciphertext in managed memory.
    /// </summary>
    public async Task<StoredDocument> ValidateAndStoreStreamingAsync(
        Guid tenantId,
        Guid documentId,
        string originalFileName,
        Stream input,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || documentId == Guid.Empty)
            throw new ArgumentException("A tenant and document identity are required.");
        ArgumentNullException.ThrowIfNull(input);
        if (declaredLength is <= 0 or > UploadLimits.MaxFileBytes)
            throw new DocumentValidationException("File size must be within the upload size limit.");

        var tenantDirectory = Path.Combine(ResolveStorageRoot(), tenantId.ToString("N"));
        Directory.CreateDirectory(tenantDirectory);
        var temporaryPath = Path.Combine(
            tenantDirectory,
            $".{documentId:N}.{Guid.NewGuid():N}.upload.tmp");
        var dataKey = RandomNumberGenerator.GetBytes(Qsp2DocumentFormat.DataKeySizeBytes);
        var envelope = CreateQsp2StorageEnvelope(tenantId, documentId, dataKey);

        try
        {
            Qsp2DocumentMetadata encryptedMetadata;
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(envelope, cancellationToken);
                encryptedMetadata = await Qsp2DocumentFormat.EncryptAsync(
                    input,
                    output,
                    tenantId,
                    documentId,
                    declaredLength,
                    dataKey,
                    cancellationToken: cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            DocumentAnalysis analysis;
            using (var encryptedFile = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.RandomAccess))
            using (var content = new OffsetReadStream(encryptedFile, Qsp2StorageEnvelopeSize, leaveOpen: true))
            using (var document = Qsp2DocumentFormat.OpenRead(
                content,
                dataKey,
                tenantId,
                documentId,
                UploadLimits.MaxFileBytes,
                encryptedMetadata.Sha256,
                leaveOpen: true))
            {
                analysis = DocumentAnalyzer.Analyse(
                    document,
                    Path.GetExtension(originalFileName).ToLowerInvariant());
            }

            var storageKey = $"{tenantId:N}/{documentId:N}{analysis.Extension}";
            var fullPath = ResolveStoragePath(storageKey);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            // The staging file is in the target tenant directory, so this same-volume
            // rename publishes the authenticated object as one atomic filesystem entry.
            File.Move(temporaryPath, fullPath);
            return new StoredDocument(storageKey, encryptedMetadata.Sha256, declaredLength, analysis);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    /// <summary>
    /// Opens QSE2/QSP2 documents as authenticated seekable streams. Legacy QSP1
    /// objects remain readable through the existing compatibility reader.
    /// </summary>
    public Stream OpenReadStream(
        string storageKey,
        Guid tenantId,
        Guid documentId,
        string expectedSha256)
    {
        if (tenantId == Guid.Empty || documentId == Guid.Empty)
            throw new ArgumentException("A tenant and document identity are required.");

        FileStream? input = new(
            ResolveStoragePath(storageKey),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);
        byte[]? envelope = null;
        byte[]? dataKey = null;

        try
        {
            Span<byte> magic = stackalloc byte[4];
            ReadExactly(input, magic);
            input.Position = 0;

            if (magic.SequenceEqual("QSP1"u8))
            {
                input.Dispose();
                input = null;
                var legacy = OpenRead(storageKey);
                try
                {
                    if (!string.IsNullOrWhiteSpace(expectedSha256))
                    {
                        var actualDigest = SHA256.HashData(legacy);
                        try
                        {
                            var expectedDigest = Convert.FromHexString(expectedSha256);
                            try
                            {
                                if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
                                    throw new InvalidDataException("The legacy document SHA-256 does not match its stored metadata.");
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(expectedDigest);
                            }
                        }
                        catch (FormatException exception)
                        {
                            throw new InvalidDataException("The stored document SHA-256 metadata is invalid.", exception);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(actualDigest);
                        }
                        legacy.Position = 0;
                    }
                    return legacy;
                }
                catch
                {
                    legacy.Dispose();
                    throw;
                }
            }

            if (!magic.SequenceEqual("QSE2"u8))
                throw new InvalidDataException("The stored document has an unsupported encrypted format.");

            envelope = new byte[Qsp2StorageEnvelopeSize];
            ReadExactly(input, envelope);
            dataKey = UnwrapQsp2StorageEnvelope(envelope, tenantId, documentId);
            var contentOffset = new OffsetReadStream(input, Qsp2StorageEnvelopeSize, leaveOpen: false);
            input = null; // The offset stream now owns the file stream.
            return Qsp2DocumentFormat.OpenRead(
                contentOffset,
                dataKey,
                tenantId,
                documentId,
                UploadLimits.MaxFileBytes,
                expectedSha256,
                leaveOpen: false);
        }
        catch
        {
            input?.Dispose();
            throw;
        }
        finally
        {
            if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
        }
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
            declaredLength is <= UploadLimits.PrivateRelayEncryptionOverheadBytes or > UploadLimits.MaxFileBytes + UploadLimits.PrivateRelayEncryptionOverheadBytes)
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

    private const int Qsp2StorageEnvelopeSize = 4 + 1 + 12 + 32 + 16;

    private byte[] CreateQsp2StorageEnvelope(Guid tenantId, Guid documentId, ReadOnlySpan<byte> dataKey)
    {
        var envelope = new byte[Qsp2StorageEnvelopeSize];
        "QSE2"u8.CopyTo(envelope);
        envelope[4] = 1;
        var aad = CreateQsp2StorageEnvelopeAad(tenantId, documentId);
        try
        {
            RandomNumberGenerator.Fill(envelope.AsSpan(5, 12));
            using var aes = new AesGcm(encryptionKey, 16);
            aes.Encrypt(
                envelope.AsSpan(5, 12),
                dataKey,
                envelope.AsSpan(17, 32),
                envelope.AsSpan(49, 16),
                aad);
            return envelope;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(envelope);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private byte[] UnwrapQsp2StorageEnvelope(byte[] envelope, Guid tenantId, Guid documentId)
    {
        if (envelope.Length != Qsp2StorageEnvelopeSize ||
            !envelope.AsSpan(0, 4).SequenceEqual("QSE2"u8) ||
            envelope[4] != 1)
            throw new InvalidDataException("The QSP2 cloud key envelope is invalid or unsupported.");

        var dataKey = new byte[Qsp2DocumentFormat.DataKeySizeBytes];
        var aad = CreateQsp2StorageEnvelopeAad(tenantId, documentId);
        try
        {
            using var aes = new AesGcm(encryptionKey, 16);
            aes.Decrypt(
                envelope.AsSpan(5, 12),
                envelope.AsSpan(17, 32),
                envelope.AsSpan(49, 16),
                dataKey,
                aad);
            return dataKey;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw new InvalidDataException("The QSP2 cloud data key could not be authenticated or unwrapped.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private static byte[] CreateQsp2StorageEnvelopeAad(Guid tenantId, Guid documentId)
    {
        var aad = new byte[4 + 1 + 16 + 16];
        "QSE2"u8.CopyTo(aad);
        aad[4] = 1;
        if (!tenantId.TryWriteBytes(aad.AsSpan(5, 16), bigEndian: true, out _) ||
            !documentId.TryWriteBytes(aad.AsSpan(21, 16), bigEndian: true, out _))
        {
            CryptographicOperations.ZeroMemory(aad);
            throw new InvalidOperationException("The QSP2 cloud storage identity could not be encoded.");
        }
        return aad;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0)
                throw new EndOfStreamException("The encrypted document ended before its envelope was complete.");
            offset += read;
        }
    }

    /// <summary>
    /// Presents a file region as a seekable stream without copying it. The wrapper
    /// maps positions relative to the QSE2 envelope and can own the base file stream.
    /// </summary>
    private sealed class OffsetReadStream(Stream inner, long offset, bool leaveOpen) : Stream
    {
        private bool disposed;

        public override bool CanRead => !disposed && inner.CanRead;
        public override bool CanSeek => !disposed && inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return inner.Length - offset;
            }
        }
        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return inner.Position - offset;
            }
            set
            {
                ThrowIfDisposed();
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                inner.Position = checked(offset + value);
            }
        }

        public override int Read(byte[] buffer, int bufferOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (bufferOffset > buffer.Length - count)
                throw new ArgumentException("The offset and count exceed the destination buffer.");
            ThrowIfDisposed();
            return inner.Read(buffer, bufferOffset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long seekOffset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            var target = origin switch
            {
                SeekOrigin.Begin => seekOffset,
                SeekOrigin.Current => checked(Position + seekOffset),
                SeekOrigin.End => checked(Length + seekOffset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0) throw new IOException("Cannot seek before the beginning of the encrypted document body.");
            Position = target;
            return target;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException("The encrypted document stream is read-only.");
        public override void Write(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException("The encrypted document stream is read-only.");

        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                if (disposing && !leaveOpen)
                    inner.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(OffsetReadStream));
        }
    }

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
