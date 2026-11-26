using System.Diagnostics;
using System.Security.Cryptography;
using SmartPrint.Agent;
using SmartPrint.Documents;
using Xunit;
using Xunit.Abstractions;

namespace SmartPrint.Agent.Tests;

/// <summary>
/// Measurement-only diagnostics for the Standard QSP2 upload pipeline.
/// These tests do not change production behaviour or relax any integrity checks.
/// Results are written to the GitHub Actions test log.
/// </summary>
public sealed class UploadPipelinePerformanceDiagnosticsTests(ITestOutputHelper output)
{
    private const int MiB = 1024 * 1024;
    private const int PayloadMiB = 50;
    private const int MeasuredIterations = 3;

    [Fact]
    [Trait("Category", "UploadPerformanceDiagnostic")]
    public async Task MeasureQsp2RecordSizesDiskStagesQueuePersistenceAndCryptoConcurrency()
    {
        Assert.True(OperatingSystem.IsWindows(), "These diagnostics require the Windows Agent runner and DPAPI.");

        var root = Path.Combine(
            Path.GetTempPath(),
            "smartprint-upload-performance-diagnostics",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var payload = GC.AllocateUninitializedArray<byte>(PayloadMiB * MiB);
        RandomNumberGenerator.Fill(payload);
        var tenantId = Guid.NewGuid();
        var sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        try
        {
            output.WriteLine("SMARTPRINT_UPLOAD_PERFORMANCE_DIAGNOSTICS_V1");
            output.WriteLine($"OS={Environment.OSVersion}");
            output.WriteLine($"Runtime={Environment.Version}");
            output.WriteLine($"ProcessorCount={Environment.ProcessorCount}");
            output.WriteLine($"PayloadMiB={PayloadMiB}");
            output.WriteLine("All scenarios use the same 50 MiB in-memory source payload.");
            output.WriteLine("Crypto-only results exclude physical disk I/O; queue results include QLQ2 encryption and durable flush.");

            await MeasureCryptoRecordSizesAsync(payload, tenantId);
            await MeasureDiskStagesAndQueueAsync(payload, tenantId, sha256, root);
            await MeasureCryptoConcurrencyAsync(payload, tenantId);

            output.WriteLine("SMARTPRINT_UPLOAD_PERFORMANCE_DIAGNOSTICS_COMPLETE");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private async Task MeasureCryptoRecordSizesAsync(byte[] payload, Guid tenantId)
    {
        output.WriteLine("SCENARIO_A_CRYPTO_RECORD_SIZE_BEGIN");
        foreach (var recordSizeMiB in new[] { 1, 2, 4, 8 })
        {
            var recordSizeBytes = recordSizeMiB * MiB;
            var encryptSamples = new List<double>();
            var decryptSamples = new List<double>();

            // One unreported warm-up per record size prevents JIT/start-up work
            // from dominating the measured samples.
            await RunCryptoRoundTripAsync(payload, tenantId, recordSizeBytes);

            for (var iteration = 0; iteration < MeasuredIterations; iteration++)
            {
                var (encryptMs, decryptMs) =
                    await RunCryptoRoundTripAsync(payload, tenantId, recordSizeBytes);
                encryptSamples.Add(encryptMs);
                decryptSamples.Add(decryptMs);
            }

            WriteSamples(
                $"CRYPTO recordMiB={recordSizeMiB} encrypt",
                encryptSamples,
                PayloadMiB);
            WriteSamples(
                $"CRYPTO recordMiB={recordSizeMiB} decrypt",
                decryptSamples,
                PayloadMiB);
        }
        output.WriteLine("SCENARIO_A_CRYPTO_RECORD_SIZE_END");
    }

    private static async Task<(double EncryptMs, double DecryptMs)> RunCryptoRoundTripAsync(
        byte[] payload,
        Guid tenantId,
        int recordSizeBytes)
    {
        var documentId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(Qsp2DocumentFormat.DataKeySizeBytes);
        try
        {
            var encryptedCapacity = checked((int)Qsp2DocumentFormat.GetEncryptedLength(
                payload.LongLength,
                recordSizeBytes));
            using var plaintext = new MemoryStream(payload, writable: false);
            using var encrypted = new MemoryStream(encryptedCapacity);

            var encryptTimer = Stopwatch.StartNew();
            await Qsp2DocumentFormat.EncryptAsync(
                plaintext,
                encrypted,
                tenantId,
                documentId,
                payload.LongLength,
                dataKey,
                recordSizeBytes);
            encryptTimer.Stop();

            encrypted.Position = 0;
            var decryptTimer = Stopwatch.StartNew();
            await Qsp2DocumentFormat.DecryptAsync(
                encrypted,
                Stream.Null,
                dataKey,
                tenantId,
                documentId,
                payload.LongLength);
            decryptTimer.Stop();

            return (encryptTimer.Elapsed.TotalMilliseconds, decryptTimer.Elapsed.TotalMilliseconds);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private async Task MeasureDiskStagesAndQueueAsync(
        byte[] payload,
        Guid tenantId,
        string sha256,
        string root)
    {
        output.WriteLine("SCENARIO_B_DISK_AND_QUEUE_BEGIN");
        var queueRoot = Path.Combine(root, "queue");
        var queue = new AgentLocalQueueStore(queueRoot);
        await queue.InitializeAsync(CancellationToken.None);

        foreach (var writeThrough in new[] { false, true })
        {
            var samples = new List<double>();
            for (var iteration = 0; iteration < MeasuredIterations; iteration++)
            {
                var path = Path.Combine(root, $"staging-{writeThrough}-{iteration}.tmp");
                var options = FileOptions.Asynchronous | FileOptions.SequentialScan;
                if (writeThrough)
                    options |= FileOptions.WriteThrough;

                var timer = Stopwatch.StartNew();
                await using (var destination = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    MiB,
                    options))
                {
                    await WritePayloadAsync(destination, payload);
                    await destination.FlushAsync();
                    destination.Flush(flushToDisk: true);
                }
                timer.Stop();
                samples.Add(timer.Elapsed.TotalMilliseconds);
                File.Delete(path);
            }

            WriteSamples(
                $"DISK stagingWrite writeThrough={writeThrough} durableFlush=true",
                samples,
                PayloadMiB);
        }

        var eraseSamples = new List<double>();
        for (var iteration = 0; iteration < MeasuredIterations; iteration++)
        {
            var stagingPath = await queue.CreatePrivateUploadStagingPathAsync(CancellationToken.None);
            await using (var staging = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                MiB,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await WritePayloadAsync(staging, payload);
                await staging.FlushAsync();
                staging.Flush(flushToDisk: true);
            }

            var eraseTimer = Stopwatch.StartNew();
            await queue.ErasePrivateUploadStagingFileAsync(stagingPath);
            eraseTimer.Stop();
            eraseSamples.Add(eraseTimer.Elapsed.TotalMilliseconds);
            Assert.False(File.Exists(stagingPath));
        }

        WriteSamples("DISK secureStagingOverwriteAndDelete", eraseSamples, PayloadMiB);

        var queueSamples = new List<double>();
        for (var iteration = 0; iteration < MeasuredIterations; iteration++)
        {
            var jobId = Guid.NewGuid();
            var job = new AgentClaimedJob(
                jobId,
                $"SP-PERF-{iteration + 1:D2}",
                "AWAITING_CONFIGURATION",
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
                "performance-50MiB.pdf",
                "application/pdf",
                payload.LongLength,
                sha256,
                DateTimeOffset.UtcNow.AddMinutes(5),
                StorageMode: "LOCAL_AGENT");

            using var source = new MemoryStream(payload, writable: false);
            var timer = Stopwatch.StartNew();
            var entry = await queue.EnqueueLocalUploadStreamingAsync(
                job,
                tenantId,
                source,
                CancellationToken.None);
            timer.Stop();
            queueSamples.Add(timer.Elapsed.TotalMilliseconds);

            Assert.Equal("LOCAL_UPLOAD_STAGED", entry.State);
            Assert.Equal(payload.LongLength, entry.SizeBytes);
            Assert.Equal(sha256, entry.Sha256);

            // Verify the persisted QLQ2 object outside the timed write interval.
            using var queuedDocument = queue.OpenDocumentReadStream(jobId, tenantId);
            var persistedHash = Convert.ToHexString(
                await SHA256.HashDataAsync(queuedDocument)).ToLowerInvariant();
            Assert.Equal(sha256, persistedHash);
        }

        WriteSamples("QUEUE QLQ2EncryptAndDurableCommit", queueSamples, PayloadMiB);
        output.WriteLine("SCENARIO_B_DISK_AND_QUEUE_END");
    }

    private async Task MeasureCryptoConcurrencyAsync(byte[] payload, Guid tenantId)
    {
        output.WriteLine("SCENARIO_C_CRYPTO_CONCURRENCY_BEGIN");
        foreach (var concurrency in new[] { 1, 2, 3, 4 })
        {
            var samples = new List<double>();
            // Two measured repetitions at each level; run levels sequentially so
            // separate configurations do not compete with each other.
            for (var iteration = 0; iteration < 2; iteration++)
            {
                var timer = Stopwatch.StartNew();
                var workers = Enumerable.Range(0, concurrency)
                    .Select(_ => Task.Run(async () =>
                    {
                        await RunCryptoRoundTripAsync(
                            payload,
                            tenantId,
                            Qsp2DocumentFormat.DefaultRecordSizeBytes);
                    }))
                    .ToArray();
                await Task.WhenAll(workers);
                timer.Stop();
                samples.Add(timer.Elapsed.TotalMilliseconds);
            }

            WriteSamples(
                $"CONCURRENCY qsp2RoundTrips={concurrency}",
                samples,
                PayloadMiB * concurrency);
        }
        output.WriteLine("SCENARIO_C_CRYPTO_CONCURRENCY_END");
    }

    private static async Task WritePayloadAsync(Stream destination, byte[] payload)
    {
        for (var offset = 0; offset < payload.Length; offset += MiB)
        {
            var count = Math.Min(MiB, payload.Length - offset);
            await destination.WriteAsync(payload.AsMemory(offset, count));
        }
    }

    private void WriteSamples(string label, IReadOnlyList<double> samples, int totalMiB)
    {
        var sorted = samples.OrderBy(value => value).ToArray();
        var median = sorted[sorted.Length / 2];
        var p95 = sorted[^1]; // Nearest-rank p95 for this deliberately small sample set.
        var throughput = totalMiB / (median / 1000d);
        output.WriteLine(
            $"{label} samplesMs=[{string.Join(",", sorted.Select(value => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}] " +
            $"medianMs={median:F2} p95Ms={p95:F2} medianMiBPerSec={throughput:F2}");
    }
}
