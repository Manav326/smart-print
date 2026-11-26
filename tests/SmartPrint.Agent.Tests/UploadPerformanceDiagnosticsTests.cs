using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SmartPrint.Agent;
using SmartPrint.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using Xunit.Abstractions;

namespace SmartPrint.Agent.Tests;

public sealed class UploadPerformanceDiagnosticsTests(ITestOutputHelper output)
{
    private const int Mebibyte = 1024 * 1024;
    private const int Repetitions = 5;

    [Fact]
    [Trait("Category", "UploadPerformanceDiagnostic")]
    public async Task Qsp2CryptoMatrixMeasuresEncryptionAndDecryptionWithoutDiskIo()
    {
        var agentId = Guid.NewGuid();
        var recordSizes = new[] { 1, 2, 4, 8 }.Select(value => value * Mebibyte).ToArray();
        var documentSizes = new[] { 1, 10, 50 }.Select(value => value * Mebibyte).ToArray();

        foreach (var recordSize in recordSizes)
        {
            await RunCryptoSampleAsync(agentId, 1 * Mebibyte, recordSize);
        }

        foreach (var documentSize in documentSizes)
        {
            var plaintext = RandomNumberGenerator.GetBytes(documentSize);
            var expectedSha256 = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
            try
            {
                foreach (var recordSize in recordSizes)
                {
                    for (var repetition = 1; repetition <= 3; repetition++)
                    {
                        var documentId = Guid.NewGuid();
                        var dataKey = RandomNumberGenerator.GetBytes(Qsp2DocumentFormat.DataKeySizeBytes);
                        try
                        {
                            using var source = new MemoryStream(plaintext, writable: false);
                            using var encrypted = new MemoryStream(
                                checked((int)Qsp2DocumentFormat.GetEncryptedLength(documentSize, recordSize)));
                            Qsp2DocumentMetadata? encryptionMetadata = null;

                            await MeasureAsync(
                                "qsp2_encrypt_memory_to_memory",
                                $"size_mib={documentSize / Mebibyte};record_mib={recordSize / Mebibyte};sample={repetition}",
                                documentSize,
                                async () =>
                                {
                                    source.Position = 0;
                                    encryptionMetadata = await Qsp2DocumentFormat.EncryptAsync(
                                        source,
                                        encrypted,
                                        agentId,
                                        documentId,
                                        documentSize,
                                        dataKey,
                                        recordSize);
                                });

                            Assert.NotNull(encryptionMetadata);
                            Assert.Equal(expectedSha256, encryptionMetadata!.Sha256);

                            await MeasureAsync(
                                "qsp2_decrypt_memory_to_null",
                                $"size_mib={documentSize / Mebibyte};record_mib={recordSize / Mebibyte};sample={repetition}",
                                documentSize,
                                async () =>
                                {
                                    encrypted.Position = 0;
                                    var metadata = await Qsp2DocumentFormat.DecryptAsync(
                                        encrypted,
                                        Stream.Null,
                                        dataKey,
                                        agentId,
                                        documentId,
                                        documentSize);
                                    Assert.Equal(expectedSha256, metadata.Sha256);
                                });
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(dataKey);
                        }
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    [Fact]
    [Trait("Category", "UploadPerformanceDiagnostic")]
    public async Task AgentStagingQueueAnalysisAndSecureEraseDiskMatrix()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "smartprint-upload-performance-diagnostics",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var queueRoot = Path.Combine(root, "queue");
        var agentId = Guid.NewGuid();
        var sourcePath = Path.Combine(root, "source-50mib.pdf");

        try
        {
            await WritePaddedPdfAsync(sourcePath, 50L * Mebibyte);
            var sourceInfo = new FileInfo(sourcePath);
            await using var sourceForHash = File.OpenRead(sourcePath);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(sourceForHash)).ToLowerInvariant();

            for (var repetition = 1; repetition <= Repetitions; repetition++)
            {
                var writeThroughPath = Path.Combine(root, $"stage-writethrough-{repetition}.tmp");
                await MeasureAsync(
                    "staging_copy_write_through_flush",
                    $"size_mib=50;sample={repetition}",
                    sourceInfo.Length,
                    () => CopyAndFlushAsync(sourcePath, writeThroughPath, FileOptions.Asynchronous | FileOptions.WriteThrough));
                File.Delete(writeThroughPath);

                var sequentialPath = Path.Combine(root, $"stage-sequential-{repetition}.tmp");
                await MeasureAsync(
                    "staging_copy_sequential_flush",
                    $"size_mib=50;sample={repetition}",
                    sourceInfo.Length,
                    () => CopyAndFlushAsync(sourcePath, sequentialPath, FileOptions.Asynchronous | FileOptions.SequentialScan));
                File.Delete(sequentialPath);
            }

            for (var repetition = 1; repetition <= Repetitions; repetition++)
            {
                await MeasureAsync(
                    "document_analyzer_pdf_50mib",
                    $"size_mib=50;sample={repetition}",
                    sourceInfo.Length,
                    () =>
                    {
                        using var input = new FileStream(
                            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            64 * 1024, FileOptions.RandomAccess);
                        var analysis = DocumentAnalyzer.Analyse(input, ".pdf");
                        Assert.Equal(1, analysis.PageCount);
                        Assert.Equal("application/pdf", analysis.MediaType);
                        return Task.CompletedTask;
                    });
            }

            var queue = new AgentLocalQueueStore(queueRoot);
            await queue.InitializeAsync(CancellationToken.None);
            for (var repetition = 1; repetition <= Repetitions; repetition++)
            {
                var jobId = Guid.NewGuid();
                var job = new AgentClaimedJob(
                    jobId,
                    $"SP-UPLOAD-PERF-{repetition:D2}",
                    "LOCAL_UPLOAD_STAGED",
                    1,
                    1,
                    "Mono",
                    "A4",
                    false,
                    "INR",
                    0,
                    Guid.NewGuid(),
                    "SmartPrint QA Printer",
                    "SmartPrint QA Queue",
                    $"synthetic-50mib-{repetition:D2}.pdf",
                    "application/pdf",
                    sourceInfo.Length,
                    sha256,
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    StorageMode: "LOCAL_AGENT");

                await MeasureAsync(
                    "qlq2_dpapi_queue_persist_and_durable_flush",
                    $"size_mib=50;sample={repetition}",
                    sourceInfo.Length,
                    async () =>
                    {
                        await using var input = new FileStream(
                            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var entry = await queue.EnqueueLocalUploadStreamingAsync(
                            job, agentId, input, CancellationToken.None);
                        Assert.Equal("LOCAL_UPLOAD_STAGED", entry.State);
                        Assert.Equal(sha256, entry.Sha256);
                    });

                var stagedPath = await queue.CreatePrivateUploadStagingPathAsync(CancellationToken.None);
                await CopyAndFlushAsync(
                    sourcePath,
                    stagedPath,
                    FileOptions.Asynchronous | FileOptions.WriteThrough,
                    flushToDisk: false);
                await MeasureAsync(
                    "secure_plaintext_stage_overwrite_and_delete",
                    $"size_mib=50;sample={repetition}",
                    sourceInfo.Length,
                    () => queue.ErasePrivateUploadStagingFileAsync(stagedPath));
                Assert.False(File.Exists(stagedPath));
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private async Task RunCryptoSampleAsync(Guid agentId, int sizeBytes, int recordSizeBytes)
    {
        var plaintext = new byte[sizeBytes];
        var dataKey = RandomNumberGenerator.GetBytes(Qsp2DocumentFormat.DataKeySizeBytes);
        var documentId = Guid.NewGuid();
        try
        {
            using var source = new MemoryStream(plaintext, writable: false);
            using var encrypted = new MemoryStream(
                checked((int)Qsp2DocumentFormat.GetEncryptedLength(sizeBytes, recordSizeBytes)));
            await Qsp2DocumentFormat.EncryptAsync(
                source, encrypted, agentId, documentId, sizeBytes, dataKey, recordSizeBytes);
            encrypted.Position = 0;
            await Qsp2DocumentFormat.DecryptAsync(
                encrypted, Stream.Null, dataKey, agentId, documentId, sizeBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private async Task MeasureAsync(
        string operation,
        string dimensions,
        long bytes,
        Func<Task> action)
    {
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        process.Refresh();
        var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
        var throughput = stopwatch.Elapsed.TotalSeconds > 0
            ? bytes / 1024d / 1024d / stopwatch.Elapsed.TotalSeconds
            : 0;
        var scenario = operation.StartsWith("qsp2_", StringComparison.Ordinal) ? "A" : "B";
        output.WriteLine(
            $"SMARTPRINT_PERF,scenario={scenario},operation={operation},{dimensions},elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F2},cpu_ms={cpuMilliseconds:F2},allocated_bytes={allocatedBytes},throughput_mib_s={throughput:F2},working_set_bytes={process.WorkingSet64},peak_working_set_bytes={process.PeakWorkingSet64}");
    }

    private static async Task CopyAndFlushAsync(
        string sourcePath,
        string destinationPath,
        FileOptions options,
        bool flushToDisk = true)
    {
        await using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var outputStream = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, options);
        await input.CopyToAsync(outputStream, 1024 * 1024, CancellationToken.None);
        await outputStream.FlushAsync(CancellationToken.None);
        if (flushToDisk)
            outputStream.Flush(flushToDisk: true);
    }

    private static async Task WritePaddedPdfAsync(string path, long targetBytes)
    {
        const string pageContent = "BT /F1 18 Tf 72 720 Td (SmartPrint upload performance fixture) Tj ET\n";
        var pageContentLength = Encoding.ASCII.GetByteCount(pageContent);
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new long[7];

        void AddObject(int objectNumber, string body)
        {
            offsets[objectNumber] = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append(objectNumber).Append(" 0 obj\n")
                .Append(body).Append("\nendobj\n");
        }

        AddObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        AddObject(2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
        AddObject(3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
            "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        AddObject(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        AddObject(5,
            "<< /Length " + pageContentLength + " >>\nstream\n" +
            pageContent + "endstream");

        offsets[6] = Encoding.ASCII.GetByteCount(builder.ToString());
        var paddingBytes = targetBytes;
        var payloadPrefix = string.Empty;
        const string payloadSuffix = "\nendstream\nendobj\n";
        var crossReference = string.Empty;
        var trailer = string.Empty;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            payloadPrefix = "6 0 obj\n<< /Length " + paddingBytes + " >>\nstream\n";
            var xrefOffset = offsets[6] +
                Encoding.ASCII.GetByteCount(payloadPrefix) +
                paddingBytes +
                Encoding.ASCII.GetByteCount(payloadSuffix);
            var xrefBuilder = new StringBuilder("xref\n0 7\n0000000000 65535 f \n");
            for (var objectNumber = 1; objectNumber <= 6; objectNumber++)
            {
                xrefBuilder.Append(offsets[objectNumber].ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" 00000 n \n");
            }
            crossReference = xrefBuilder.ToString();
            trailer = "trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n" +
                xrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n%%EOF\n";
            var fixedBytes =
                Encoding.ASCII.GetByteCount(builder.ToString()) +
                Encoding.ASCII.GetByteCount(payloadPrefix) +
                Encoding.ASCII.GetByteCount(payloadSuffix) +
                Encoding.ASCII.GetByteCount(crossReference) +
                Encoding.ASCII.GetByteCount(trailer);
            var nextPaddingBytes = targetBytes - fixedBytes;
            if (nextPaddingBytes < 0)
                throw new InvalidOperationException("The requested PDF test payload is too small for its structure.");
            if (nextPaddingBytes == paddingBytes) break;
            paddingBytes = nextPaddingBytes;
        }

        payloadPrefix = "6 0 obj\n<< /Length " + paddingBytes + " >>\nstream\n";
        var finalXrefOffset = offsets[6] +
            Encoding.ASCII.GetByteCount(payloadPrefix) +
            paddingBytes +
            Encoding.ASCII.GetByteCount(payloadSuffix);
        var finalXref = new StringBuilder("xref\n0 7\n0000000000 65535 f \n");
        for (var objectNumber = 1; objectNumber <= 6; objectNumber++)
        {
            finalXref.Append(offsets[objectNumber].ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" 00000 n \n");
        }
        crossReference = finalXref.ToString();
        trailer = "trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n" +
            finalXrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n%%EOF\n";

        await using var outputStream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await outputStream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()));
        await outputStream.WriteAsync(Encoding.ASCII.GetBytes(payloadPrefix));
        var padding = new byte[1024 * 1024];
        Array.Fill(padding, (byte)0xA5);
        var remaining = paddingBytes;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, padding.Length);
            await outputStream.WriteAsync(padding.AsMemory(0, count));
            remaining -= count;
        }
        await outputStream.WriteAsync(Encoding.ASCII.GetBytes(payloadSuffix));
        await outputStream.WriteAsync(Encoding.ASCII.GetBytes(crossReference));
        await outputStream.WriteAsync(Encoding.ASCII.GetBytes(trailer));
        await outputStream.FlushAsync();

        if (outputStream.Length != targetBytes)
            throw new InvalidOperationException(
                $"The PDF performance fixture is {outputStream.Length} bytes; expected exactly {targetBytes} bytes.");
    }
}