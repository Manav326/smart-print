using System.Security.Cryptography;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class Qsp2DocumentFormatTests
{
    [Fact]
    public async Task EncryptsAndDecryptsMultipleRecordsUsingShortReads()
    {
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(Qsp2DocumentFormat.DataKeySizeBytes);
        var sourceBytes = RandomNumberGenerator.GetBytes(Qsp2DocumentFormat.DefaultRecordSizeBytes + 137);
        await using var source = new FragmentedReadStream(sourceBytes, maximumReadSize: 7919);
        await using var encrypted = new MemoryStream();

        var written = await Qsp2DocumentFormat.EncryptAsync(
            source,
            encrypted,
            tenantId,
            documentId,
            sourceBytes.LongLength,
            dataKey);

        Assert.Equal(2UL, written.RecordCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(), written.Sha256);
        Assert.Equal("QSP2", System.Text.Encoding.ASCII.GetString(encrypted.ToArray(), 0, 4));

        encrypted.Position = 0;
        await using var plaintext = new MemoryStream();
        var read = await Qsp2DocumentFormat.DecryptAsync(
            encrypted,
            plaintext,
            dataKey,
            tenantId,
            documentId,
            maximumPlaintextLength: 50L * 1024 * 1024);

        Assert.Equal(sourceBytes, plaintext.ToArray());
        Assert.Equal(written.Sha256, read.Sha256);
        Assert.Equal(written.HeaderSha256, read.HeaderSha256);
        Assert.Equal(tenantId, read.TenantId);
        Assert.Equal(documentId, read.DocumentId);
    }

    [Fact]
    public async Task RejectsWrongDataKeyBeforeWritingPlaintext()
    {
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var sourceBytes = RandomNumberGenerator.GetBytes(128 * 1024);
        var correctKey = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        await using var source = new MemoryStream(sourceBytes);
        await using var encrypted = new MemoryStream();
        await Qsp2DocumentFormat.EncryptAsync(source, encrypted, tenantId, documentId, sourceBytes.Length, correctKey);

        encrypted.Position = 0;
        await using var plaintext = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Qsp2DocumentFormat.DecryptAsync(encrypted, plaintext, wrongKey, tenantId, documentId, 1024 * 1024));
        Assert.Empty(plaintext.ToArray());
    }

    [Fact]
    public async Task RejectsModifiedRecordAuthenticationTag()
    {
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var sourceBytes = RandomNumberGenerator.GetBytes(128 * 1024);
        await using var source = new MemoryStream(sourceBytes);
        await using var encrypted = new MemoryStream();
        await Qsp2DocumentFormat.EncryptAsync(source, encrypted, tenantId, documentId, sourceBytes.Length, dataKey);

        var tampered = encrypted.ToArray();
        var firstRecordTagOffset = Qsp2DocumentFormat.HeaderSizeBytes +
            Qsp2DocumentFormat.HeaderMacSizeBytes +
            sizeof(uint) +
            sourceBytes.Length;
        tampered[firstRecordTagOffset] ^= 0x80;

        await using var tamperedInput = new MemoryStream(tampered);
        await using var plaintext = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Qsp2DocumentFormat.DecryptAsync(tamperedInput, plaintext, dataKey, tenantId, documentId, 1024 * 1024));
    }

    [Fact]
    public async Task RejectsWrongTenantOrDocumentIdentity()
    {
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var sourceBytes = RandomNumberGenerator.GetBytes(96 * 1024);
        await using var source = new MemoryStream(sourceBytes);
        await using var encrypted = new MemoryStream();
        await Qsp2DocumentFormat.EncryptAsync(source, encrypted, tenantId, documentId, sourceBytes.Length, dataKey);

        encrypted.Position = 0;
        await using var plaintext = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Qsp2DocumentFormat.DecryptAsync(encrypted, plaintext, dataKey, Guid.NewGuid(), documentId, 1024 * 1024));
        Assert.Empty(plaintext.ToArray());
    }

    [Fact]
    public async Task RejectsTruncatedFooterAndTrailingBytes()
    {
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var sourceBytes = RandomNumberGenerator.GetBytes(96 * 1024);
        await using var source = new MemoryStream(sourceBytes);
        await using var encrypted = new MemoryStream();
        await Qsp2DocumentFormat.EncryptAsync(source, encrypted, tenantId, documentId, sourceBytes.Length, dataKey);

        var complete = encrypted.ToArray();
        await using var truncatedInput = new MemoryStream(complete[..^1]);
        await using var truncatedPlaintext = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            Qsp2DocumentFormat.DecryptAsync(truncatedInput, truncatedPlaintext, dataKey, tenantId, documentId, 1024 * 1024));

        await using var trailingInput = new MemoryStream([.. complete, 0x42]);
        await using var trailingPlaintext = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Qsp2DocumentFormat.DecryptAsync(trailingInput, trailingPlaintext, dataKey, tenantId, documentId, 1024 * 1024));
    }

    [Fact]
    public async Task RejectsSourceLongerThanDeclaredLength()
    {
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        await using var source = new MemoryStream(RandomNumberGenerator.GetBytes(96 * 1024 + 1));
        await using var encrypted = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Qsp2DocumentFormat.EncryptAsync(source, encrypted, tenantId, documentId, 96 * 1024, dataKey));
    }

    [Fact]
    public async Task RejectsSourceShorterThanDeclaredLength()
    {
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        await using var source = new MemoryStream(RandomNumberGenerator.GetBytes(96 * 1024 - 1));
        await using var encrypted = new MemoryStream();

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            Qsp2DocumentFormat.EncryptAsync(source, encrypted, tenantId, documentId, 96 * 1024, dataKey));
    }

    [Fact]
    public async Task RejectsDeclaredLengthAboveCallerLimit()
    {
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var sourceBytes = RandomNumberGenerator.GetBytes(96 * 1024);
        await using var source = new MemoryStream(sourceBytes);
        await using var encrypted = new MemoryStream();
        await Qsp2DocumentFormat.EncryptAsync(source, encrypted, tenantId, documentId, sourceBytes.Length, dataKey);

        encrypted.Position = 0;
        await using var plaintext = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Qsp2DocumentFormat.DecryptAsync(encrypted, plaintext, dataKey, tenantId, documentId, sourceBytes.Length - 1));
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumReadSize) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadSize)], cancellationToken);
    }
}
