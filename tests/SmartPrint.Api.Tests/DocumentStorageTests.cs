using System.Security.Cryptography;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Storage;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class DocumentStorageTests
{
    private static readonly byte[] ValidPng = CreateValidPng();

    private static byte[] CreateValidPng()
    {
        const int width = 2;
        const int height = 3;
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR", header);

        var rowBytes = width * 4;
        var raw = new byte[(rowBytes + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (rowBytes + 1)] = 0;
            for (var x = 0; x < width; x++)
            {
                var pixel = y * (rowBytes + 1) + 1 + x * 4;
                raw[pixel] = 24;
                raw[pixel + 1] = 96;
                raw[pixel + 2] = 160;
                raw[pixel + 3] = 255;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw);
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ComputePngCrc(crcInput));
        output.Write(crcBytes);
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    [Fact]
    public async Task StoresDocumentsEncryptedAndReadsTheOriginalBytesBack()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var storage = CreateStorage(root);
            var tenantId = Guid.NewGuid();
            var documentId = Guid.NewGuid();
            await using var input = new MemoryStream(ValidPng);
            var stored = await storage.ValidateAndStoreAsync(tenantId, documentId, "invoice.png", input, ValidPng.Length, CancellationToken.None);
            var persistedPath = Path.Combine(root, stored.StorageKey.Replace('/', Path.DirectorySeparatorChar));
            var persistedBytes = await File.ReadAllBytesAsync(persistedPath);

            Assert.NotEqual(ValidPng, persistedBytes);
            Assert.Equal("QSP1", System.Text.Encoding.ASCII.GetString(persistedBytes, 0, 4));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(ValidPng)).ToLowerInvariant(), stored.Sha256);
            Assert.Equal(1, stored.Analysis.PageCount);
            using var decrypted = storage.OpenRead(stored.StorageKey);
            using var result = new MemoryStream();
            await decrypted.CopyToAsync(result);
            Assert.Equal(ValidPng, result.ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DeletingAStoredDocumentRemovesItsPayloadFileAndReleasesItsStorageEntry()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var storage = CreateStorage(root);
            var stored = await StorePngAsync(storage);
            var path = Path.Combine(root, stored.StorageKey.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);

            await storage.DeleteAsync(stored.StorageKey);

            Assert.False(File.Exists(path));
            Assert.Throws<FileNotFoundException>(() => storage.OpenRead(stored.StorageKey));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DetectsTamperingOfEncryptedDocument()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var storage = CreateStorage(root);
            var stored = await StorePngAsync(storage);
            var path = Path.Combine(root, stored.StorageKey.Replace('/', Path.DirectorySeparatorChar));
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[^1] ^= 0x40;
            await File.WriteAllBytesAsync(path, bytes);
            Assert.Throws<InvalidDataException>(() => storage.OpenRead(stored.StorageKey));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ListsOnlyOldCanonicalPrivateRelayObjectsForOrphanCleanup()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var storage = CreateStorage(root);
            var tenantId = Guid.NewGuid();
            var ticketId = Guid.NewGuid();
            await using var firstInput = new MemoryStream("opaque encrypted payload"u8.ToArray());
            var staleKey = await storage.StoreOpaquePrivateRelayAsync(
                tenantId, ticketId, firstInput, firstInput.Length, CancellationToken.None);
            var stalePath = Path.Combine(root, staleKey.Replace('/', Path.DirectorySeparatorChar));
            File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(-2));

            await using var secondInput = new MemoryStream("another encrypted payload"u8.ToArray());
            var freshKey = await storage.StoreOpaquePrivateRelayAsync(
                tenantId, Guid.NewGuid(), secondInput, secondInput.Length, CancellationToken.None);

            var candidates = storage.GetStalePrivateRelayStorageKeys(TimeSpan.FromHours(1), 100);

            Assert.Contains(staleKey, candidates);
            Assert.DoesNotContain(freshKey, candidates);
            Assert.All(candidates, key =>
                Assert.StartsWith("private-relay/", key, StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StoresSupportedContentUsingDetectedExtensionEvenWhenFilenameIsUnsupported()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var storage = CreateStorage(root);
            await using var input = new MemoryStream(ValidPng);
            var stored = await storage.ValidateAndStoreAsync(
                Guid.NewGuid(), Guid.NewGuid(), "invoice.exe", input, ValidPng.Length, CancellationToken.None);

            Assert.Equal("image/png", stored.Analysis.MediaType);
            Assert.Equal(".png", stored.Analysis.Extension);
            Assert.EndsWith(".png", stored.StorageKey, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsInvalidPdfSignature()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var storage = CreateStorage(root);
            await using var input = new MemoryStream("not a PDF file"u8.ToArray());
            await Assert.ThrowsAsync<DocumentValidationException>(() =>
                storage.ValidateAndStoreAsync(Guid.NewGuid(), Guid.NewGuid(), "document.pdf", input, input.Length, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsFilesLargerThanTwentyMegabytesBeforeReading()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var storage = CreateStorage(root);
            await using var input = new MemoryStream();
            await Assert.ThrowsAsync<DocumentValidationException>(() =>
                storage.ValidateAndStoreAsync(Guid.NewGuid(), Guid.NewGuid(), "large.png", input, UploadLimits.MaxFileBytes + 1, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RequiresAProductionEncryptionKey()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            var environment = new TestWebHostEnvironment(root, "Production");
            Assert.Throws<InvalidOperationException>(() => new DocumentStorage(configuration, environment));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<StoredDocument> StorePngAsync(DocumentStorage storage)
    {
        await using var input = new MemoryStream(ValidPng);
        return await storage.ValidateAndStoreAsync(Guid.NewGuid(), Guid.NewGuid(), "test.png", input, ValidPng.Length, CancellationToken.None);
    }

    private static DocumentStorage CreateStorage(string root)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Documents:StorageRoot"] = root,
            ["Documents:EncryptionKey"] = key
        }).Build();
        return new DocumentStorage(configuration, new TestWebHostEnvironment(root, "Production"));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "qrtoprint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
