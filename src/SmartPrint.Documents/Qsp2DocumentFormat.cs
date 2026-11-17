using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SmartPrint.Documents;

/// <summary>
/// Streams documents into and out of the QSP2 authenticated record container.
/// The wrapped data key is managed by the caller so an authorised service can
/// re-wrap the key without decrypting or rewriting document records.
/// </summary>
public static class Qsp2DocumentFormat
{
    public const int DefaultRecordSizeBytes = 1024 * 1024;
    public const int MinimumRecordSizeBytes = 64 * 1024;
    public const int MaximumRecordSizeBytes = 8 * 1024 * 1024;
    public const int DataKeySizeBytes = 32;
    public const int AuthenticationTagSizeBytes = 16;
    public const int HeaderSizeBytes = 100;
    public const int HeaderMacSizeBytes = 32;
    public const int FooterSizeBytes = 68;

    private const byte CurrentVersion = 2;
    private const byte Aes256GcmCipherSuite = 1;
    internal const int SaltOffset = 32;
    internal const int SaltSize = 32;
    internal const int NoncePrefixOffset = 64;
    private const int TenantIdOffset = 68;
    private const int DocumentIdOffset = 84;
    internal const int FooterCoreSizeBytes = 52;

    internal static readonly byte[] HeaderKeyInfo = "SmartPrint.QSP2.HeaderKey.v1"u8.ToArray();
    internal static readonly byte[] RecordKeyInfo = "SmartPrint.QSP2.RecordKey.v1"u8.ToArray();

    /// <summary>
    /// Calculates the exact QSP2 object length for an exact plaintext length.
    /// Useful when a streaming HTTP body must declare a Content-Length without
    /// first materialising any of the encrypted document.
    /// </summary>
    public static long GetEncryptedLength(
        long plaintextLength,
        int recordSizeBytes = DefaultRecordSizeBytes)
    {
        ValidateRecordSize(recordSizeBytes);
        if (plaintextLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(plaintextLength));

        var recordCount = checked((plaintextLength - 1) / recordSizeBytes + 1);
        var perRecordOverhead = checked(recordCount *
            (sizeof(uint) + (long)AuthenticationTagSizeBytes));
        return checked(HeaderSizeBytes + HeaderMacSizeBytes +
            plaintextLength + perRecordOverhead + FooterSizeBytes);
    }

    /// <summary>
    /// Opens a seekable authenticated reader over a QSP2 encrypted object. Only the
    /// requested records are decrypted, with a one-record cache for random-access
    /// document parsers. The caller owns the input stream unless leaveOpen is false.
    /// </summary>
    public static Qsp2DocumentReadStream OpenRead(
        Stream encryptedInput,
        ReadOnlyMemory<byte> dataKey,
        Guid expectedTenantId,
        Guid expectedDocumentId,
        long maximumPlaintextLength,
        string? expectedSha256 = null,
        bool leaveOpen = true) =>
        new(
            encryptedInput,
            dataKey,
            expectedTenantId,
            expectedDocumentId,
            maximumPlaintextLength,
            expectedSha256,
            leaveOpen);

    /// <summary>
    /// Encrypts an exact-length source stream into QSP2 using bounded, pooled buffers.
    /// The caller must write to a new temporary object and publish it only after this
    /// method succeeds. This method does not own or close either stream.
    /// </summary>
    public static async Task<Qsp2DocumentMetadata> EncryptAsync(
        Stream plaintext,
        Stream encryptedOutput,
        Guid tenantId,
        Guid documentId,
        long plaintextLength,
        ReadOnlyMemory<byte> dataKey,
        int recordSizeBytes = DefaultRecordSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(encryptedOutput);
        ValidateStreams(plaintext, encryptedOutput);
        ValidateIdentity(tenantId, documentId);
        ValidateDataKey(dataKey.Span);
        ValidateRecordSize(recordSizeBytes);

        if (plaintextLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(plaintextLength), "QSP2 documents must contain at least one byte.");

        var recordCount = checked((ulong)(((plaintextLength - 1) / recordSizeBytes) + 1));
        var header = CreateHeader(tenantId, documentId, plaintextLength, recordSizeBytes, recordCount);
        var headerMacKey = DeriveKey(dataKey.Span, header.AsSpan(SaltOffset, SaltSize), HeaderKeyInfo);
        var recordKey = DeriveKey(dataKey.Span, header.AsSpan(SaltOffset, SaltSize), RecordKeyInfo);
        var headerMac = HMACSHA256.HashData(headerMacKey, header);
        var headerHash = SHA256.HashData(header);
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(recordSizeBytes);
        var ciphertextBuffer = ArrayPool<byte>.Shared.Rent(recordSizeBytes);
        var recordLengthBytes = new byte[sizeof(uint)];
        var tag = new byte[AuthenticationTagSizeBytes];
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var aes = new AesGcm(recordKey, AuthenticationTagSizeBytes);

        try
        {
            await encryptedOutput.WriteAsync(header, cancellationToken);
            await encryptedOutput.WriteAsync(headerMac, cancellationToken);

            for (ulong recordIndex = 0; recordIndex < recordCount; recordIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recordLength = GetExpectedRecordLength(plaintextLength, recordSizeBytes, recordIndex, recordCount);
                await ReadExactlyAsync(plaintext, plaintextBuffer.AsMemory(0, recordLength), cancellationToken);

                fileHash.AppendData(plaintextBuffer, 0, recordLength);
                BinaryPrimitives.WriteUInt32BigEndian(recordLengthBytes, checked((uint)recordLength));

                var nonce = BuildNonce(header.AsSpan(NoncePrefixOffset, 4), recordIndex);
                var aad = BuildRecordAad(headerHash, recordIndex, recordLength);
                aes.Encrypt(
                    nonce,
                    plaintextBuffer.AsSpan(0, recordLength),
                    ciphertextBuffer.AsSpan(0, recordLength),
                    tag,
                    aad);

                await encryptedOutput.WriteAsync(recordLengthBytes, cancellationToken);
                await encryptedOutput.WriteAsync(ciphertextBuffer.AsMemory(0, recordLength), cancellationToken);
                await encryptedOutput.WriteAsync(tag, cancellationToken);

                CryptographicOperations.ZeroMemory(plaintextBuffer.AsSpan(0, recordLength));
                CryptographicOperations.ZeroMemory(ciphertextBuffer.AsSpan(0, recordLength));
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(aad);
            }

            var extraByte = new byte[1];
            if (await plaintext.ReadAsync(extraByte, cancellationToken) != 0)
                throw new InvalidDataException("The source stream contains more bytes than its declared length.");

            var digest = fileHash.GetHashAndReset();
            var digestHex = Convert.ToHexString(digest).ToLowerInvariant();
            var headerHashHex = Convert.ToHexString(headerHash).ToLowerInvariant();
            var footerCore = CreateFooterCore(recordCount, plaintextLength, digest);
            var footerNonce = BuildNonce(header.AsSpan(NoncePrefixOffset, 4), recordCount);
            var footerAad = BuildFooterAad(headerHash, footerCore);
            aes.Encrypt(
                footerNonce,
                ReadOnlySpan<byte>.Empty,
                Span<byte>.Empty,
                tag,
                footerAad);

            await encryptedOutput.WriteAsync(footerCore, cancellationToken);
            await encryptedOutput.WriteAsync(tag, cancellationToken);

            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(footerNonce);
            CryptographicOperations.ZeroMemory(footerAad);
            CryptographicOperations.ZeroMemory(tag);

            return new Qsp2DocumentMetadata(
                tenantId,
                documentId,
                plaintextLength,
                recordSizeBytes,
                recordCount,
                headerHashHex,
                digestHex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBuffer);
            CryptographicOperations.ZeroMemory(ciphertextBuffer);
            CryptographicOperations.ZeroMemory(headerMacKey);
            CryptographicOperations.ZeroMemory(recordKey);
            CryptographicOperations.ZeroMemory(headerMac);
            CryptographicOperations.ZeroMemory(headerHash);
            ArrayPool<byte>.Shared.Return(plaintextBuffer);
            ArrayPool<byte>.Shared.Return(ciphertextBuffer);
        }
    }

    /// <summary>
    /// Authenticates and decrypts a QSP2 object into a caller-owned staging stream.
    /// The caller must discard that staging output if this method throws because
    /// the final whole-file digest is checked after all authenticated records arrive.
    /// </summary>
    public static async Task<Qsp2DocumentMetadata> DecryptAsync(
        Stream encryptedInput,
        Stream plaintextStagingOutput,
        ReadOnlyMemory<byte> dataKey,
        Guid expectedTenantId,
        Guid expectedDocumentId,
        long maximumPlaintextLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedInput);
        ArgumentNullException.ThrowIfNull(plaintextStagingOutput);
        ValidateStreams(encryptedInput, plaintextStagingOutput);
        ValidateIdentity(expectedTenantId, expectedDocumentId);
        ValidateDataKey(dataKey.Span);

        if (maximumPlaintextLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPlaintextLength));

        var header = new byte[HeaderSizeBytes];
        var suppliedHeaderMac = new byte[HeaderMacSizeBytes];
        await ReadExactlyAsync(encryptedInput, header, cancellationToken);
        await ReadExactlyAsync(encryptedInput, suppliedHeaderMac, cancellationToken);
        ValidateHeader(header, expectedTenantId, expectedDocumentId, maximumPlaintextLength);

        var plaintextLength = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(12, 8));
        var recordSizeBytes = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)));
        var recordCount = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24, 8));
        var headerMacKey = DeriveKey(dataKey.Span, header.AsSpan(SaltOffset, SaltSize), HeaderKeyInfo);
        var recordKey = DeriveKey(dataKey.Span, header.AsSpan(SaltOffset, SaltSize), RecordKeyInfo);
        var expectedHeaderMac = HMACSHA256.HashData(headerMacKey, header);
        var headerHash = SHA256.HashData(header);

        if (!CryptographicOperations.FixedTimeEquals(suppliedHeaderMac, expectedHeaderMac))
        {
            ZeroSensitive(headerMacKey, recordKey, expectedHeaderMac, headerHash, suppliedHeaderMac);
            throw new InvalidDataException("The QSP2 header authentication failed.");
        }

        var ciphertextBuffer = ArrayPool<byte>.Shared.Rent(recordSizeBytes);
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(recordSizeBytes);
        var recordLengthBytes = new byte[sizeof(uint)];
        var tag = new byte[AuthenticationTagSizeBytes];
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var aes = new AesGcm(recordKey, AuthenticationTagSizeBytes);

        try
        {
            for (ulong recordIndex = 0; recordIndex < recordCount; recordIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadExactlyAsync(encryptedInput, recordLengthBytes, cancellationToken);
                var recordLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(recordLengthBytes));
                var expectedRecordLength = GetExpectedRecordLength(plaintextLength, recordSizeBytes, recordIndex, recordCount);
                if (recordLength != expectedRecordLength)
                    throw new InvalidDataException("A QSP2 record has an unexpected plaintext length.");

                await ReadExactlyAsync(encryptedInput, ciphertextBuffer.AsMemory(0, recordLength), cancellationToken);
                await ReadExactlyAsync(encryptedInput, tag, cancellationToken);

                var nonce = BuildNonce(header.AsSpan(NoncePrefixOffset, 4), recordIndex);
                var aad = BuildRecordAad(headerHash, recordIndex, recordLength);
                try
                {
                    aes.Decrypt(
                        nonce,
                        ciphertextBuffer.AsSpan(0, recordLength),
                        tag,
                        plaintextBuffer.AsSpan(0, recordLength),
                        aad);
                }
                catch (CryptographicException exception)
                {
                    throw new InvalidDataException($"QSP2 record {recordIndex} failed authentication.", exception);
                }

                fileHash.AppendData(plaintextBuffer, 0, recordLength);
                await plaintextStagingOutput.WriteAsync(plaintextBuffer.AsMemory(0, recordLength), cancellationToken);

                CryptographicOperations.ZeroMemory(ciphertextBuffer.AsSpan(0, recordLength));
                CryptographicOperations.ZeroMemory(plaintextBuffer.AsSpan(0, recordLength));
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(aad);
            }

            var footerCore = new byte[FooterCoreSizeBytes];
            var footerTag = new byte[AuthenticationTagSizeBytes];
            await ReadExactlyAsync(encryptedInput, footerCore, cancellationToken);
            await ReadExactlyAsync(encryptedInput, footerTag, cancellationToken);
            ValidateFooterCore(footerCore, recordCount, plaintextLength);

            var digest = fileHash.GetHashAndReset();
            var footerNonce = BuildNonce(header.AsSpan(NoncePrefixOffset, 4), recordCount);
            var footerAad = BuildFooterAad(headerHash, footerCore);
            try
            {
                aes.Decrypt(
                    footerNonce,
                    ReadOnlySpan<byte>.Empty,
                    footerTag,
                    Span<byte>.Empty,
                    footerAad);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("The QSP2 final authentication record is invalid.", exception);
            }

            var expectedDigest = footerCore.AsSpan(20, 32);
            if (!CryptographicOperations.FixedTimeEquals(digest, expectedDigest))
                throw new InvalidDataException("The QSP2 whole-document SHA-256 digest does not match.");

            var trailingByte = new byte[1];
            if (await encryptedInput.ReadAsync(trailingByte, cancellationToken) != 0)
                throw new InvalidDataException("The QSP2 object contains unexpected trailing bytes.");

            return new Qsp2DocumentMetadata(
                expectedTenantId,
                expectedDocumentId,
                plaintextLength,
                recordSizeBytes,
                recordCount,
                Convert.ToHexString(headerHash).ToLowerInvariant(),
                Convert.ToHexString(digest).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertextBuffer);
            CryptographicOperations.ZeroMemory(plaintextBuffer);
            CryptographicOperations.ZeroMemory(headerMacKey);
            CryptographicOperations.ZeroMemory(recordKey);
            CryptographicOperations.ZeroMemory(expectedHeaderMac);
            CryptographicOperations.ZeroMemory(headerHash);
            CryptographicOperations.ZeroMemory(suppliedHeaderMac);
            ArrayPool<byte>.Shared.Return(ciphertextBuffer);
            ArrayPool<byte>.Shared.Return(plaintextBuffer);
        }
    }

    private static byte[] CreateHeader(
        Guid tenantId,
        Guid documentId,
        long plaintextLength,
        int recordSizeBytes,
        ulong recordCount)
    {
        var header = new byte[HeaderSizeBytes];
        "QSP2"u8.CopyTo(header);
        header[4] = CurrentVersion;
        header[5] = Aes256GcmCipherSuite;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), HeaderSizeBytes);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(12, 8), plaintextLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), checked((uint)recordSizeBytes));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(24, 8), recordCount);
        RandomNumberGenerator.Fill(header.AsSpan(SaltOffset, SaltSize));
        RandomNumberGenerator.Fill(header.AsSpan(NoncePrefixOffset, 4));
        if (!tenantId.TryWriteBytes(header.AsSpan(TenantIdOffset, 16), bigEndian: true, out _) ||
            !documentId.TryWriteBytes(header.AsSpan(DocumentIdOffset, 16), bigEndian: true, out _))
            throw new InvalidOperationException("The QSP2 document identity could not be encoded.");
        return header;
    }

    internal static void ValidateHeader(
        byte[] header,
        Guid expectedTenantId,
        Guid expectedDocumentId,
        long maximumPlaintextLength)
    {
        if (!header.AsSpan(0, 4).SequenceEqual("QSP2"u8) ||
            header[4] != CurrentVersion ||
            header[5] != Aes256GcmCipherSuite ||
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2)) != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)) != HeaderSizeBytes)
            throw new InvalidDataException("The encrypted document header is not a supported QSP2 format.");

        var length = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(12, 8));
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        var count = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24, 8));
        if (length <= 0 || length > maximumPlaintextLength ||
            recordSize is < MinimumRecordSizeBytes or > MaximumRecordSizeBytes ||
            count != checked((ulong)(((length - 1) / recordSize) + 1)))
            throw new InvalidDataException("The QSP2 document length or record layout is invalid.");

        var tenantId = new Guid(header.AsSpan(TenantIdOffset, 16), bigEndian: true);
        var documentId = new Guid(header.AsSpan(DocumentIdOffset, 16), bigEndian: true);
        if (tenantId != expectedTenantId || documentId != expectedDocumentId)
            throw new InvalidDataException("The QSP2 document identity does not match the authorised request.");
    }

    private static byte[] CreateFooterCore(ulong recordCount, long plaintextLength, byte[] digest)
    {
        var footer = new byte[FooterCoreSizeBytes];
        "QEND"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(4, 8), recordCount);
        BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(12, 8), plaintextLength);
        digest.CopyTo(footer, 20);
        return footer;
    }

    internal static void ValidateFooterCore(byte[] footer, ulong recordCount, long plaintextLength)
    {
        if (!footer.AsSpan(0, 4).SequenceEqual("QEND"u8) ||
            BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(4, 8)) != recordCount ||
            BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(12, 8)) != plaintextLength)
            throw new InvalidDataException("The QSP2 final record does not match the document header.");
    }

    internal static byte[] BuildRecordAad(byte[] headerHash, ulong recordIndex, int recordLength)
    {
        var aad = new byte[headerHash.Length + 1 + sizeof(ulong) + sizeof(uint)];
        headerHash.CopyTo(aad, 0);
        aad[headerHash.Length] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(headerHash.Length + 1, sizeof(ulong)), recordIndex);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(headerHash.Length + 1 + sizeof(ulong), sizeof(uint)), checked((uint)recordLength));
        return aad;
    }

    internal static byte[] BuildFooterAad(byte[] headerHash, byte[] footerCore)
    {
        var aad = new byte[headerHash.Length + 1 + footerCore.Length];
        headerHash.CopyTo(aad, 0);
        aad[headerHash.Length] = 2;
        footerCore.CopyTo(aad, headerHash.Length + 1);
        return aad;
    }

    internal static byte[] BuildNonce(ReadOnlySpan<byte> noncePrefix, ulong recordIndex)
    {
        var nonce = new byte[12];
        noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), recordIndex);
        return nonce;
    }

    internal static byte[] DeriveKey(
        ReadOnlySpan<byte> dataKey,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info)
    {
        // The .NET HKDF convenience overload accepts arrays. Keep these copies
        // limited to the small key-derivation inputs and clear them immediately.
        var inputKey = dataKey.ToArray();
        var saltBytes = salt.ToArray();
        var infoBytes = info.ToArray();
        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                inputKey,
                DataKeySizeBytes,
                saltBytes,
                infoBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputKey);
            CryptographicOperations.ZeroMemory(saltBytes);
            CryptographicOperations.ZeroMemory(infoBytes);
        }
    }

    internal static int GetExpectedRecordLength(
        long plaintextLength,
        int recordSizeBytes,
        ulong recordIndex,
        ulong recordCount)
    {
        if (recordIndex >= recordCount)
            throw new InvalidDataException("The QSP2 record index is outside the declared document.");
        if (recordIndex < recordCount - 1)
            return recordSizeBytes;
        return checked((int)(plaintextLength - checked((long)recordIndex * recordSizeBytes)));
    }

    private static async ValueTask ReadExactlyAsync(
        Stream source,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await source.ReadAsync(destination[offset..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("The QSP2 stream ended before the declared content was complete.");
            offset += read;
        }
    }

    private static void ValidateStreams(Stream input, Stream output)
    {
        if (!input.CanRead)
            throw new ArgumentException("The source stream must be readable.", nameof(input));
        if (!output.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(output));
    }

    private static void ValidateIdentity(Guid tenantId, Guid documentId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A tenant identity is required.", nameof(tenantId));
        if (documentId == Guid.Empty)
            throw new ArgumentException("A document identity is required.", nameof(documentId));
    }

    private static void ValidateDataKey(ReadOnlySpan<byte> dataKey)
    {
        if (dataKey.Length != DataKeySizeBytes)
            throw new ArgumentException("A QSP2 data key must contain exactly 32 bytes.", nameof(dataKey));
    }

    private static void ValidateRecordSize(int recordSizeBytes)
    {
        if (recordSizeBytes is < MinimumRecordSizeBytes or > MaximumRecordSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(recordSizeBytes),
                $"QSP2 records must be between {MinimumRecordSizeBytes} and {MaximumRecordSizeBytes} bytes.");
    }

    private static void ZeroSensitive(params byte[][] values)
    {
        foreach (var value in values)
            CryptographicOperations.ZeroMemory(value);
    }
}

/// <summary>
/// Authenticated metadata returned after a complete QSP2 write or read.
/// The wrapped data-key envelope is stored and managed separately.
/// </summary>
public sealed record Qsp2DocumentMetadata(
    Guid TenantId,
    Guid DocumentId,
    long PlaintextLength,
    int RecordSizeBytes,
    ulong RecordCount,
    string HeaderSha256,
    string Sha256);
