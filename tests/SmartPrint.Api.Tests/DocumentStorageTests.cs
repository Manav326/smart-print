using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Storage;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class DocumentStorageTests
{
    private static readonly byte[] ValidPng =
    [
        137, 80, 78, 71, 13, 10, 26, 10,
        0, 0, 0, 13, 73, 72, 68, 82,
        0, 0, 0, 2, 0, 0, 0, 3
    ];

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
    public async Task RejectsMaliciousDocumentBeforeWritingAnyEncryptedBytes()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Documents:StorageRoot"] = root,
                ["Documents:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }).Build();
            using var storage = new DocumentStorage(
                configuration,
                new TestWebHostEnvironment(root, "Production"),
                new RejectDocumentThreatScanner());

            await using var input = new MemoryStream(ValidPng);
            var exception = await Assert.ThrowsAsync<DocumentThreatScanException>(() =>
                storage.ValidateAndStoreAsync(Guid.NewGuid(), Guid.NewGuid(), "infected.png", input, ValidPng.Length, CancellationToken.None));

            Assert.True(exception.ThreatDetected);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
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
    public async Task RejectsUnsupportedExtension()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var storage = CreateStorage(root);
            await using var input = new MemoryStream(ValidPng);
            await Assert.ThrowsAsync<DocumentValidationException>(() =>
                storage.ValidateAndStoreAsync(Guid.NewGuid(), Guid.NewGuid(), "invoice.exe", input, ValidPng.Length, CancellationToken.None));
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
                storage.ValidateAndStoreAsync(Guid.NewGuid(), Guid.NewGuid(), "large.png", input, 20L * 1024 * 1024 + 1, CancellationToken.None));
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
            Assert.Throws<InvalidOperationException>(() => new DocumentStorage(configuration, environment, new AllowDocumentThreatScanner()));
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
        return new DocumentStorage(configuration, new TestWebHostEnvironment(root, "Production"), new AllowDocumentThreatScanner());
    }

    private sealed class AllowDocumentThreatScanner : IDocumentThreatScanner
    {
        public Task ScanAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RejectDocumentThreatScanner : IDocumentThreatScanner
    {
        public Task ScanAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken) =>
            throw new DocumentThreatScanException("test threat", threatDetected: true);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "qrtoprint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
