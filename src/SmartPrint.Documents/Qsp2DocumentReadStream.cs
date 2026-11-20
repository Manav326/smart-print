using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SmartPrint.Documents;

/// <summary>
/// Authenticated, seekable view over a QSP2 encrypted document.
/// It decrypts and authenticates only the records requested by the consumer and
/// keeps at most one plaintext record in memory.
/// </summary>
public sealed class Qsp2DocumentReadStream : Stream
{
    private readonly object sync = new();
    private readonly Stream encryptedInput;
    private readonly bool leaveOpen;
    private readonly long plaintextLength;
    private readonly int recordSizeBytes;
    private readonly ulong recordCount;
    private readonly long recordsOffset;
    private readonly byte[] headerHash = Array.Empty<byte>();
    private readonly byte[] noncePrefix = Array.Empty<byte>();
    private readonly byte[] recordKey = Array.Empty<byte>();
    private readonly byte[] ciphertextBuffer = Array.Empty<byte>();
    private readonly byte[] plaintextBuffer = Array.Empty<byte>();
    private AesGcm? aes;
    private long position;
    private ulong cachedRecordIndex = ulong.MaxValue;
    private int cachedRecordLength;
    private bool disposed;

    public string Sha256 { get; }

    internal Qsp2DocumentReadStream(
        Stream encryptedInput,
        ReadOnlyMemory<byte> dataKey,
        Guid expectedTenantId,
        Guid expectedDocumentId,
        long maximumPlaintextLength,
        string? expectedSha256,
        bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(encryptedInput);
        if (!encryptedInput.CanRead || !encryptedInput.CanSeek)
            throw new ArgumentException("QSP2 random-access reading requires a readable, seekable encrypted stream.", nameof(encryptedInput));
        if (dataKey.Length != Qsp2DocumentFormat.DataKeySizeBytes)
            throw new ArgumentException("A QSP2 data key must contain exactly 32 bytes.", nameof(dataKey));
        if (expectedTenantId == Guid.Empty || expectedDocumentId == Guid.Empty)
            throw new ArgumentException("Expected tenant and document identities are required.");
        if (maximumPlaintextLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPlaintextLength));

        this.encryptedInput = encryptedInput;
        this.leaveOpen = leaveOpen;

        byte[]? localHeaderMacKey = null;
        byte[]? localRecordKey = null;
        byte[]? localHeaderHash = null;
        byte[]? localNoncePrefix = null;
        var header = new byte[Qsp2DocumentFormat.HeaderSizeBytes];
        var suppliedHeaderMac = new byte[Qsp2DocumentFormat.HeaderMacSizeBytes];
        try
        {
            ReadExactly(encryptedInput, header);
            ReadExactly(encryptedInput, suppliedHeaderMac);

            Qsp2DocumentFormat.ValidateHeader(
                header,
                expectedTenantId,
                expectedDocumentId,
                maximumPlaintextLength);

            plaintextLength = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(12, 8));
            recordSizeBytes = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)));
            recordCount = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24, 8));
            recordsOffset = Qsp2DocumentFormat.HeaderSizeBytes + Qsp2DocumentFormat.HeaderMacSizeBytes;

            localHeaderMacKey = Qsp2DocumentFormat.DeriveKey(
                dataKey.Span,
                header.AsSpan(Qsp2DocumentFormat.SaltOffset, Qsp2DocumentFormat.SaltSize),
                Qsp2DocumentFormat.HeaderKeyInfo);
            localRecordKey = Qsp2DocumentFormat.DeriveKey(
                dataKey.Span,
                header.AsSpan(Qsp2DocumentFormat.SaltOffset, Qsp2DocumentFormat.SaltSize),
                Qsp2DocumentFormat.RecordKeyInfo);

            var expectedHeaderMac = HMACSHA256.HashData(localHeaderMacKey, header);
            if (!CryptographicOperations.FixedTimeEquals(suppliedHeaderMac, expectedHeaderMac))
            {
                CryptographicOperations.ZeroMemory(expectedHeaderMac);
                throw new InvalidDataException("The QSP2 header authentication failed.");
            }

            CryptographicOperations.ZeroMemory(expectedHeaderMac);
            localHeaderHash = SHA256.HashData(header);
            localNoncePrefix = header.AsSpan(Qsp2DocumentFormat.NoncePrefixOffset, 4).ToArray();

            var finalRecordLength = Qsp2DocumentFormat.GetExpectedRecordLength(
                plaintextLength, recordSizeBytes, recordCount - 1, recordCount);
            var fullRecordCount = checked((long)(recordCount - 1));
            var fullRecordsBytes = checked(fullRecordCount *
                (sizeof(uint) + (long)recordSizeBytes + Qsp2DocumentFormat.AuthenticationTagSizeBytes));
            var finalRecordBytes = checked(sizeof(uint) + (long)finalRecordLength + Qsp2DocumentFormat.AuthenticationTagSizeBytes);
            var footerOffset = checked(recordsOffset + fullRecordsBytes + finalRecordBytes);
            var expectedEncryptedLength = checked(footerOffset + Qsp2DocumentFormat.FooterSizeBytes);
            if (encryptedInput.Length != expectedEncryptedLength)
                throw new InvalidDataException("The QSP2 encrypted object has an invalid total length.");

            encryptedInput.Position = footerOffset;
            var footerCore = new byte[Qsp2DocumentFormat.FooterCoreSizeBytes];
            var footerTag = new byte[Qsp2DocumentFormat.AuthenticationTagSizeBytes];
            try
            {
                ReadExactly(encryptedInput, footerCore);
                ReadExactly(encryptedInput, footerTag);
                Qsp2DocumentFormat.ValidateFooterCore(footerCore, recordCount, plaintextLength);

                var footerNonce = Qsp2DocumentFormat.BuildNonce(localNoncePrefix, recordCount);
                var footerAad = Qsp2DocumentFormat.BuildFooterAad(localHeaderHash, footerCore);
                try
                {
                    using var footerAes = new AesGcm(localRecordKey, Qsp2DocumentFormat.AuthenticationTagSizeBytes);
                    footerAes.Decrypt(
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
                finally
                {
                    CryptographicOperations.ZeroMemory(footerNonce);
                    CryptographicOperations.ZeroMemory(footerAad);
                }

                var footerDigest = footerCore.AsSpan(20, 32);
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    byte[] expectedDigest;
                    try
                    {
                        if (expectedSha256.Length != 64)
                            throw new FormatException();
                        expectedDigest = Convert.FromHexString(expectedSha256);
                    }
                    catch (FormatException exception)
                    {
                        throw new ArgumentException(
                            "The expected SHA-256 digest must be a 64-character hexadecimal value.",
                            nameof(expectedSha256),
                            exception);
                    }

                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(footerDigest, expectedDigest))
                            throw new InvalidDataException("The QSP2 footer digest does not match the stored document metadata.");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(expectedDigest);
                    }
                }

                Sha256 = Convert.ToHexString(footerDigest).ToLowerInvariant();

                // Ownership of these key-derived values transfers to the stream.
                headerHash = localHeaderHash;
                noncePrefix = localNoncePrefix;
                recordKey = localRecordKey;
                localHeaderHash = null;
                localNoncePrefix = null;
                localRecordKey = null;

                ciphertextBuffer = ArrayPool<byte>.Shared.Rent(recordSizeBytes);
                plaintextBuffer = ArrayPool<byte>.Shared.Rent(recordSizeBytes);
                aes = new AesGcm(recordKey, Qsp2DocumentFormat.AuthenticationTagSizeBytes);
                encryptedInput.Position = recordsOffset;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(footerCore);
                CryptographicOperations.ZeroMemory(footerTag);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(suppliedHeaderMac);
            if (localHeaderMacKey is not null) CryptographicOperations.ZeroMemory(localHeaderMacKey);
            if (localRecordKey is not null) CryptographicOperations.ZeroMemory(localRecordKey);
            if (localHeaderHash is not null) CryptographicOperations.ZeroMemory(localHeaderHash);
            if (localNoncePrefix is not null) CryptographicOperations.ZeroMemory(localNoncePrefix);
        }
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length => plaintextLength;
    public override long Position
    {
        get
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return position;
            }
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The offset and count exceed the destination buffer.");
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (buffer.IsEmpty || position >= plaintextLength)
                return 0;

            var totalRead = 0;
            while (totalRead < buffer.Length && position < plaintextLength)
            {
                var recordIndex = checked((ulong)(position / recordSizeBytes));
                var recordOffset = checked((int)(position % recordSizeBytes));
                LoadRecord(recordIndex);

                var available = cachedRecordLength - recordOffset;
                if (available <= 0)
                    throw new InvalidDataException("The QSP2 record cache does not cover the requested document position.");

                var copyLength = Math.Min(available, buffer.Length - totalRead);
                plaintextBuffer.AsSpan(recordOffset, copyLength).CopyTo(buffer[totalRead..]);
                totalRead += copyLength;
                position += copyLength;
            }

            return totalRead;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(plaintextLength + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0)
                throw new IOException("Cannot seek before the beginning of a QSP2 document.");

            position = target;
            return position;
        }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException("QSP2 document streams are read-only.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("QSP2 document streams are read-only.");

    protected override void Dispose(bool disposing)
    {
        lock (sync)
        {
            if (disposed)
            {
                base.Dispose(disposing);
                return;
            }

            disposed = true;
            aes?.Dispose();
            CryptographicOperations.ZeroMemory(recordKey);
            CryptographicOperations.ZeroMemory(headerHash);
            CryptographicOperations.ZeroMemory(noncePrefix);
            if (ciphertextBuffer.Length > 0)
            {
                CryptographicOperations.ZeroMemory(ciphertextBuffer);
                ArrayPool<byte>.Shared.Return(ciphertextBuffer);
            }
            if (plaintextBuffer.Length > 0)
            {
                CryptographicOperations.ZeroMemory(plaintextBuffer);
                ArrayPool<byte>.Shared.Return(plaintextBuffer);
            }
            cachedRecordIndex = ulong.MaxValue;
            cachedRecordLength = 0;
            if (disposing && !leaveOpen)
                encryptedInput.Dispose();
        }

        base.Dispose(disposing);
    }

    private void LoadRecord(ulong recordIndex)
    {
        if (cachedRecordIndex == recordIndex)
            return;

        var expectedRecordLength = Qsp2DocumentFormat.GetExpectedRecordLength(
            plaintextLength, recordSizeBytes, recordIndex, recordCount);
        var recordOffset = checked(recordsOffset +
            checked((long)recordIndex *
            (sizeof(uint) + (long)recordSizeBytes + Qsp2DocumentFormat.AuthenticationTagSizeBytes)));

        // Invalidate the previous cache before touching untrusted bytes. A short
        // read or invalid length must not leave old plaintext eligible for reuse.
        cachedRecordIndex = ulong.MaxValue;
        cachedRecordLength = 0;
        CryptographicOperations.ZeroMemory(ciphertextBuffer);
        CryptographicOperations.ZeroMemory(plaintextBuffer);

        Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
        Span<byte> tag = stackalloc byte[Qsp2DocumentFormat.AuthenticationTagSizeBytes];
        byte[]? nonce = null;
        byte[]? aad = null;
        var authenticated = false;
        try
        {
            encryptedInput.Position = recordOffset;
            ReadExactly(encryptedInput, lengthBytes);
            var actualLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(lengthBytes));
            if (actualLength != expectedRecordLength)
                throw new InvalidDataException($"QSP2 record {recordIndex} has an unexpected length.");

            ReadExactly(encryptedInput, ciphertextBuffer.AsSpan(0, actualLength));
            ReadExactly(encryptedInput, tag);

            nonce = Qsp2DocumentFormat.BuildNonce(noncePrefix, recordIndex);
            aad = Qsp2DocumentFormat.BuildRecordAad(headerHash, recordIndex, actualLength);
            try
            {
                (aes ?? throw new ObjectDisposedException(nameof(Qsp2DocumentReadStream))).Decrypt(
                    nonce,
                    ciphertextBuffer.AsSpan(0, actualLength),
                    tag,
                    plaintextBuffer.AsSpan(0, actualLength),
                    aad);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException($"QSP2 record {recordIndex} failed authentication.", exception);
            }

            authenticated = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertextBuffer);
            CryptographicOperations.ZeroMemory(tag);
            if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
            if (aad is not null) CryptographicOperations.ZeroMemory(aad);
            if (!authenticated) CryptographicOperations.ZeroMemory(plaintextBuffer);
        }

        cachedRecordIndex = recordIndex;
        cachedRecordLength = expectedRecordLength;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0)
                throw new EndOfStreamException("The QSP2 encrypted object ended before its declared content was complete.");
            offset += read;
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(Qsp2DocumentReadStream));
    }
}
