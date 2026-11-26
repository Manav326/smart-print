using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartPrint.Agent;
using SmartPrint.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using Xunit.Abstractions;

namespace SmartPrint.Agent.Tests;

public sealed class LocalAgentUploadHttpIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task HostedWindowsAgentAcceptsAnExtensionlessPdfThroughItsRealMultipartHttpRoute()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartprint-agent-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var queueRoot = Path.Combine(root, "queue");
        var agentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var ticketId = Guid.NewGuid();
        var pdf = CreatePdf();
        var sha256 = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant();
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new FakeCloudHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(agentId.ToString(), request.Headers.GetValues("X-Print-Agent-Id").Single());
            Assert.Equal("agent-test-secret", request.Headers.GetValues("X-Print-Agent-Secret").Single());
            Assert.Equal("upload-grant-test", request.Headers.GetValues("X-Print-Upload-Grant").Single());

            if (request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
            {
                claimSeen.TrySetResult(true);
                var response = new
                {
                    alreadyCompleted = false,
                    id = ticketId,
                    printJobId = jobId,
                    jobNumber = "SP-EXTENSIONLESS",
                    agentDeviceId = agentId,
                    printerDeviceId = Guid.NewGuid(),
                    printerName = "SmartPrint QA Printer",
                    queueName = "SmartPrint QA Queue",
                    originalFileName = "certificate-without-extension",
                    fileExtension = ".pdf",
                    mediaType = "application/pdf",
                    sizeBytes = pdf.LongLength,
                    sha256,
                    colourMode = "Mono",
                    paperSize = "A4",
                    isDuplex = false,
                    copies = 1,
                    orientation = "AUTO",
                    privatePrinting = false,
                    expiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                    uploadResult = (object?)null,
                    printKind = "DOCUMENT",
                    outputMode = "PHYSICAL",
                    pagesPerSheet = 1,
                    attemptNumber = 1
                };
                return JsonResponse(response);
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                completion.TrySetResult(JsonDocument.Parse(body.RootElement.GetRawText()));
                var completed = new
                {
                    alreadyCompleted = false,
                    uploadResult = new
                    {
                        job = new
                        {
                            id = jobId,
                            jobNumber = "SP-EXTENSIONLESS",
                            status = "AWAITING_CONFIGURATION",
                            pageCount = 1,
                            copies = 1,
                            currency = "INR",
                            quotedAmountMinor = 0,
                            createdUtc = DateTimeOffset.UtcNow,
                            colourMode = "Mono",
                            paperSize = "A4",
                            isDuplex = false,
                            orientation = "AUTO",
                            pricingVersion = 1,
                            privatePrinting = false,
                            printKind = "DOCUMENT"
                        },
                        document = new
                        {
                            id = Guid.NewGuid(),
                            originalFileName = "certificate-without-extension",
                            mediaType = "application/pdf",
                            sizeBytes = pdf.LongLength,
                            sha256,
                            pageSizesJson = "[]",
                            storageMode = "LOCAL_AGENT"
                        },
                        duplicateRequest = false
                    }
                };
                return JsonResponse(completed);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var credentials = new AgentCredentialStore(Path.Combine(root, "agent.identity"));
        credentials.Save(new AgentCredentials(
            "http://127.0.0.1:8080",
            agentId,
            "agent-test-secret",
            DateTimeOffset.UtcNow));
        var documentEncryptionKeyStore = new AgentDocumentEncryptionKeyStore(Path.Combine(root, "document-key.dpapi"));

        var queue = new AgentLocalQueueStore(queueRoot);
        await queue.InitializeAsync(CancellationToken.None);
        var state = new LocalAgentApiState();
        var preferences = new LocalNetworkPreferenceStore(Path.Combine(root, "network.preference"));
        var networkManager = new LocalNetworkManager(preferences, state);
        var runtimeReadiness = new AgentRuntimeReadiness();

        var host = new HostBuilder()
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Agent:ApiBaseUrl"] = "http://127.0.0.1:8080",
                    ["Agent:LocalApi:Enabled"] = "true"
                }))
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<AgentCredentialStore>(credentials);
                services.AddSingleton<AgentLocalQueueStore>(queue);
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(cloud));
                services.AddSingleton<LocalAgentApiState>(state);
                services.AddSingleton<LocalNetworkManager>(networkManager);
                services.AddSingleton<AgentRuntimeReadiness>(runtimeReadiness);
                services.AddSingleton(documentEncryptionKeyStore);
                services.AddSingleton<LocalAgentApiHostedService>();
                services.AddSingleton<IHostedService>(provider =>
                    provider.GetRequiredService<LocalAgentApiHostedService>());
            })
            .Build();

        try
        {
            await host.StartAsync();
            var startupDeadline = DateTime.UtcNow.AddSeconds(20);
            while (!state.IsRunning && DateTime.UtcNow < startupDeadline)
                await Task.Delay(50);
            Assert.True(state.IsRunning, "The real Windows Agent local HTTP listener did not start.");

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var dataKey = RandomNumberGenerator.GetBytes(Qsp2DocumentFormat.DataKeySizeBytes);
            byte[] publicKeyBytes = Convert.FromBase64String(documentEncryptionKeyStore.GetOrCreatePublicKey());
            byte[] wrappedKey;
            try
            {
                using var publicRsa = RSA.Create();
                publicRsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
                wrappedKey = publicRsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKeyBytes);
            }

            using var plaintextSource = new MemoryStream(pdf, writable: false);
            using var encryptedBody = new MemoryStream();
            try
            {
                await Qsp2DocumentFormat.EncryptAsync(
                    plaintextSource, encryptedBody, agentId, jobId, pdf.LongLength, dataKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
            encryptedBody.Position = 0;
            using var encryptedContent = new StreamContent(encryptedBody);
            encryptedContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.smartprint.qsp2");

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://127.0.0.1:{LocalNetworkEndpointDiscovery.DefaultPort}/api/v1/local/uploads/{ticketId:D}")
            {
                Content = encryptedContent
            };
            request.Headers.Add("X-Print-Upload-Grant", "upload-grant-test");
            request.Headers.Add("X-Print-Encrypted-Format", "QSP2");
            request.Headers.Add("X-Print-Encrypted-Key", Convert.ToBase64String(wrappedKey));
            request.Headers.Add("X-Print-Encrypted-Size", encryptedBody.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            CryptographicOperations.ZeroMemory(wrappedKey);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.IsSuccessStatusCode,
                $"The extensionless PDF failed at the hosted Agent encrypted QSP2 endpoint: HTTP {(int)response.StatusCode}: {responseBody}");

            Assert.True(await claimSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            using var completedRequest = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(".pdf", completedRequest.RootElement.GetProperty("fileExtension").GetString());
            Assert.Equal("application/pdf", completedRequest.RootElement.GetProperty("mediaType").GetString());
            Assert.Equal(pdf.LongLength, completedRequest.RootElement.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(sha256, completedRequest.RootElement.GetProperty("sha256").GetString());
            Assert.Equal(1, completedRequest.RootElement.GetProperty("pageCount").GetInt32());

            var staged = queue.Find(jobId);
            Assert.NotNull(staged);
            Assert.Equal("LOCAL_UPLOAD_PENDING_PAYMENT", staged!.State);
            Assert.Equal("certificate-without-extension", staged.DocumentName);
            Assert.Equal("application/pdf", staged.MediaType);
            Assert.Equal(pdf.LongLength, staged.SizeBytes);
            Assert.Equal(sha256, staged.Sha256);

            using var stagedDocument = queue.OpenDocumentReadStream(jobId, agentId);
            var stagedAnalysis = DocumentAnalyzer.Analyse(stagedDocument, ".pdf");
            Assert.Equal("application/pdf", stagedAnalysis.MediaType);
            Assert.Equal(".pdf", stagedAnalysis.Extension);
            Assert.Equal(1, stagedAnalysis.PageCount);
            var stagedBytes = queue.ReadDocument(jobId);
            try
            {
                Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(stagedBytes)).ToLowerInvariant());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stagedBytes);
            }
        }
        finally
        {
            try
            {
                await host.StopAsync();
            }
            finally
            {
                host.Dispose();
                Directory.Delete(root, recursive: true);
                CryptographicOperations.ZeroMemory(pdf);
            }
        }
    }

    [Fact]
    [Trait("Category", "UploadConcurrencyDiagnostic")]
    public async Task HostedAgentStreamsTwentyFilesWithFiftyMiBMaximumAndFiveHundredMiBTotalThroughConfiguredConcurrentTransfers()
    {
        const int fileCount = 20;
        const long mebibyte = 1024L * 1024L;
        const long expectedBatchBytes = 500L * mebibyte;
        // Nine files reach the 50 MiB per-file ceiling, one is 40 MiB,
        // and ten are 1 MiB. Together these form exactly 20 files / 500 MiB.
        var targetFileSizes = Enumerable.Repeat(50L * mebibyte, 9)
            .Concat(new[] { 40L * mebibyte })
            .Concat(Enumerable.Repeat(1L * mebibyte, 10))
            .ToArray();
        var root = Path.Combine(Path.GetTempPath(), "smartprint-agent-http-batch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var queueRoot = Path.Combine(root, "queue");
        var agentId = Guid.NewGuid();
        var printerDeviceId = Guid.NewGuid();
        var fixtures = new ConcurrentDictionary<Guid, BatchUploadFixture>();
        var completionCounts = new ConcurrentDictionary<Guid, int>();
        var activeTransfers = 0;
        var maximumConcurrentTransfers = 0;
        var configuredConcurrency = Environment.GetEnvironmentVariable("SMARTPRINT_UPLOAD_TEST_CONCURRENCY");
        var concurrencyLimit = 3;
        if (!string.IsNullOrWhiteSpace(configuredConcurrency) &&
            (!int.TryParse(configuredConcurrency, out concurrencyLimit) || concurrencyLimit is < 1 or > 4))
            throw new InvalidOperationException("SMARTPRINT_UPLOAD_TEST_CONCURRENCY must be an integer from 1 through 4.");

        try
        {
            for (var index = 0; index < fileCount; index++)
            {
                var name = $"batch-{index + 1:D2}.pdf";
                var file = new FileInfo(Path.Combine(root, name));
                await WritePaddedPdfAsync(file.FullName, targetFileSizes[index]);
                file.Refresh();
                using var source = File.OpenRead(file.FullName);
                var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(source)).ToLowerInvariant();
                fixtures[Guid.NewGuid()] = new BatchUploadFixture(
                    Guid.NewGuid(), Guid.NewGuid(), file.FullName, name, file.Length, sha256);
            }

            Assert.Equal(expectedBatchBytes, fixtures.Values.Sum(item => item.SizeBytes));
            Assert.Equal(fileCount, fixtures.Count);
            Assert.Equal(9, fixtures.Values.Count(item => item.SizeBytes == 50L * mebibyte));
            Assert.All(fixtures.Values, fixture => Assert.InRange(fixture.SizeBytes, 1, 50L * mebibyte));

            var cloud = new FakeCloudHandler(async (request, cancellationToken) =>
            {
                Assert.Equal(agentId.ToString(), request.Headers.GetValues("X-Print-Agent-Id").Single());
                Assert.Equal("agent-test-secret", request.Headers.GetValues("X-Print-Agent-Secret").Single());
                Assert.Equal("upload-grant-test", request.Headers.GetValues("X-Print-Upload-Grant").Single());

                var pathSegments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var ticketId = Guid.Parse(pathSegments[^2]);
                var fixture = fixtures.Values.SingleOrDefault(item => item.TicketId == ticketId);
                Assert.NotNull(fixture);

                if (request.RequestUri.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
                {
                    var active = Interlocked.Increment(ref activeTransfers);
                    UpdateMaximum(ref maximumConcurrentTransfers, active);
                    await Task.Delay(20, cancellationToken);
                    return JsonResponse(new
                    {
                        alreadyCompleted = false,
                        id = fixture!.TicketId,
                        printJobId = fixture.JobId,
                        jobNumber = "SP-BATCH-" + fixture.Name[6..8],
                        agentDeviceId = agentId,
                        printerDeviceId,
                        printerName = "SmartPrint QA Printer",
                        queueName = "SmartPrint QA Queue",
                        originalFileName = fixture.Name,
                        fileExtension = ".pdf",
                        mediaType = "application/pdf",
                        sizeBytes = fixture.SizeBytes,
                        sha256 = fixture.Sha256,
                        colourMode = "Mono",
                        paperSize = "A4",
                        isDuplex = false,
                        copies = 1,
                        orientation = "AUTO",
                        privatePrinting = false,
                        expiresUtc = DateTimeOffset.UtcNow.AddMinutes(10),
                        uploadResult = (object?)null,
                        printKind = "DOCUMENT",
                        outputMode = "PHYSICAL",
                        pagesPerSheet = 1,
                        attemptNumber = 1
                    });
                }

                if (request.RequestUri.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.Equal(fixture!.SizeBytes, body.RootElement.GetProperty("sizeBytes").GetInt64());
                    Assert.Equal(fixture.Sha256, body.RootElement.GetProperty("sha256").GetString());
                    Assert.Equal(fixture.SizeBytes, body.RootElement.GetProperty("receivedBytes").GetInt64());
                    completionCounts.AddOrUpdate(ticketId, 1, (_, count) => count + 1);
                    Interlocked.Decrement(ref activeTransfers);

                    return JsonResponse(new
                    {
                        alreadyCompleted = false,
                        uploadResult = new
                        {
                            job = new
                            {
                                id = fixture.JobId,
                                jobNumber = "SP-BATCH-" + fixture.Name[..2],
                                status = "AWAITING_CONFIGURATION",
                                pageCount = 1,
                                copies = 1,
                                currency = "INR",
                                quotedAmountMinor = 0,
                                createdUtc = DateTimeOffset.UtcNow,
                                colourMode = "Mono",
                                paperSize = "A4",
                                isDuplex = false,
                                orientation = "AUTO",
                                pricingVersion = 1,
                                privatePrinting = false,
                                printKind = "DOCUMENT"
                            },
                            document = new
                            {
                                id = Guid.NewGuid(),
                                originalFileName = fixture.Name,
                                mediaType = "application/pdf",
                                sizeBytes = fixture.SizeBytes,
                                sha256 = fixture.Sha256,
                                pageSizesJson = "[]",
                                storageMode = "LOCAL_AGENT"
                            },
                            duplicateRequest = false
                        }
                    });
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var credentials = new AgentCredentialStore(Path.Combine(root, "agent.identity"));
            credentials.Save(new AgentCredentials(
                "http://127.0.0.1:8080",
                agentId,
                "agent-test-secret",
                DateTimeOffset.UtcNow));
            var documentEncryptionKeyStore = new AgentDocumentEncryptionKeyStore(Path.Combine(root, "document-key.dpapi"));
            var agentPublicKeyBytes = Convert.FromBase64String(documentEncryptionKeyStore.GetOrCreatePublicKey());

            var queue = new AgentLocalQueueStore(queueRoot);
            await queue.InitializeAsync(CancellationToken.None);
            var state = new LocalAgentApiState();
            var preferences = new LocalNetworkPreferenceStore(Path.Combine(root, "network.preference"));
            var networkManager = new LocalNetworkManager(preferences, state);
            var runtimeReadiness = new AgentRuntimeReadiness();

            var host = new HostBuilder()
                .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Agent:ApiBaseUrl"] = "http://127.0.0.1:8080",
                        ["Agent:LocalApi:Enabled"] = "true"
                    }))
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<AgentCredentialStore>(credentials);
                    services.AddSingleton<AgentLocalQueueStore>(queue);
                    services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(cloud));
                    services.AddSingleton<LocalAgentApiState>(state);
                    services.AddSingleton<LocalNetworkManager>(networkManager);
                    services.AddSingleton<AgentRuntimeReadiness>(runtimeReadiness);
                    services.AddSingleton(documentEncryptionKeyStore);
                    services.AddSingleton<LocalAgentApiHostedService>();
                    services.AddSingleton<IHostedService>(provider =>
                        provider.GetRequiredService<LocalAgentApiHostedService>());
                })
                .Build();

            try
            {
                await host.StartAsync();
                var startupDeadline = DateTime.UtcNow.AddSeconds(20);
                while (!state.IsRunning && DateTime.UtcNow < startupDeadline)
                    await Task.Delay(50);
                Assert.True(state.IsRunning, "The real Windows Agent local HTTP listener did not start.");

                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(8),
                    DefaultRequestVersion = new Version(1, 1),
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                using var concurrency = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);
                using var process = Process.GetCurrentProcess();
                var processCpuBefore = process.TotalProcessorTime;
                var allocatedBytesBefore = GC.GetTotalAllocatedBytes(precise: false);
                var batchStopwatch = Stopwatch.StartNew();
                var uploads = fixtures.Values.Select(async fixture =>
                {
                    await concurrency.WaitAsync();
                    try
                    {
                        await using var source = File.OpenRead(fixture.Path);
                        var dataKey = RandomNumberGenerator.GetBytes(Qsp2DocumentFormat.DataKeySizeBytes);
                        byte[] wrappedKey;
                        try
                        {
                            using var publicRsa = RSA.Create();
                            publicRsa.ImportSubjectPublicKeyInfo(agentPublicKeyBytes, out _);
                            wrappedKey = publicRsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
                        }
                        catch
                        {
                            CryptographicOperations.ZeroMemory(dataKey);
                            throw;
                        }

                        using var encryptedContent = new Qsp2HttpContent(
                            source, agentId, fixture.JobId, fixture.SizeBytes, dataKey);
                        using var request = new HttpRequestMessage(
                            HttpMethod.Post,
                            $"http://127.0.0.1:{LocalNetworkEndpointDiscovery.DefaultPort}/api/v1/local/uploads/{fixture.TicketId:D}")
                        {
                            Content = encryptedContent
                        };
                        request.Headers.Add("X-Print-Upload-Grant", "upload-grant-test");
                        request.Headers.Add("X-Print-Encrypted-Format", "QSP2");
                        request.Headers.Add("X-Print-Encrypted-Key", Convert.ToBase64String(wrappedKey));
                        request.Headers.Add("X-Print-Encrypted-Size", Qsp2DocumentFormat.GetEncryptedLength(fixture.SizeBytes).ToString(System.Globalization.CultureInfo.InvariantCulture));

                        try
                        {
                            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                            var responseBody = await response.Content.ReadAsStringAsync();
                            Assert.True(
                                response.IsSuccessStatusCode,
                                $"{fixture.Name} failed through the hosted Agent encrypted QSP2 endpoint: HTTP {(int)response.StatusCode}: {responseBody}");
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(dataKey);
                            CryptographicOperations.ZeroMemory(wrappedKey);
                        }
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                }).ToArray();

                await Task.WhenAll(uploads);
                batchStopwatch.Stop();
                process.Refresh();
                var processCpuMilliseconds = (process.TotalProcessorTime - processCpuBefore).TotalMilliseconds;
                var allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBytesBefore);
                output.WriteLine(
                    $"SMARTPRINT_PERF,scenario=C,configured_concurrency={concurrencyLimit},observed_concurrency={maximumConcurrentTransfers},batch_mib=500,files=20,elapsed_ms={batchStopwatch.Elapsed.TotalMilliseconds:F2},process_cpu_ms={processCpuMilliseconds:F2},allocated_bytes={allocatedBytes},working_set_bytes={process.WorkingSet64},peak_working_set_bytes={process.PeakWorkingSet64}");

                Assert.Equal(concurrencyLimit, maximumConcurrentTransfers);
                Assert.Equal(fileCount, completionCounts.Count);
                Assert.All(completionCounts.Values, count => Assert.Equal(1, count));

                foreach (var fixture in fixtures.Values)
                {
                    var staged = queue.Find(fixture.JobId);
                    Assert.NotNull(staged);
                    Assert.Equal("LOCAL_UPLOAD_PENDING_PAYMENT", staged!.State);
                    Assert.Equal(fixture.Name, staged.DocumentName);
                    Assert.Equal(fixture.SizeBytes, staged.SizeBytes);
                    Assert.Equal(fixture.Sha256, staged.Sha256);
                }
            }
            finally
            {
                try
                {
                    await host.StopAsync();
                }
                finally
                {
                    host.Dispose();
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    private sealed class Qsp2HttpContent : HttpContent
    {
        private readonly Stream plaintext;
        private readonly Guid agentId;
        private readonly Guid jobId;
        private readonly long plaintextLength;
        private readonly byte[] dataKey;

        public Qsp2HttpContent(Stream plaintext, Guid agentId, Guid jobId, long plaintextLength, byte[] dataKey)
        {
            this.plaintext = plaintext;
            this.agentId = agentId;
            this.jobId = jobId;
            this.plaintextLength = plaintextLength;
            this.dataKey = dataKey;
            Headers.ContentType = new MediaTypeHeaderValue("application/vnd.smartprint.qsp2");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await Qsp2DocumentFormat.EncryptAsync(
                plaintext, stream, agentId, jobId, plaintextLength, dataKey);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Qsp2DocumentFormat.GetEncryptedLength(plaintextLength);
            return true;
        }
    }

    private static async Task WritePaddedPdfAsync(string path, long targetBytes)
    {
        // Keep the 50 MiB payload inside one valid, unreferenced PDF stream object.
        // The page tree and its one small text page remain quick for PdfPig to analyse;
        // this avoids making the analyser scan tens of MiB of trailing whitespace just
        // to locate %%EOF, while the HTTP upload, hashing, queue encryption and disk I/O
        // still process the complete authorised payload.
        const string pageContent = "BT /F1 18 Tf 72 720 Td (SmartPrint 50 MiB upload acceptance) Tj ET\n";
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
        string payloadPrefix = string.Empty;
        const string payloadSuffix = "\nendstream\nendobj\n";
        string crossReference = string.Empty;
        string trailer = string.Empty;

        // The /Length and startxref values are decimal strings whose digit counts
        // affect the final size. Iterate to a stable exact target without buffering
        // the 50 MiB body in memory.
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

        // Rebuild the final xref/trailer using the converged stream length.
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

        await using var output = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()));
        await output.WriteAsync(Encoding.ASCII.GetBytes(payloadPrefix));

        var padding = new byte[1024 * 1024];
        Array.Fill(padding, (byte)0xA5);
        var remaining = paddingBytes;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, padding.Length);
            await output.WriteAsync(padding.AsMemory(0, count));
            remaining -= count;
        }

        await output.WriteAsync(Encoding.ASCII.GetBytes(payloadSuffix));
        await output.WriteAsync(Encoding.ASCII.GetBytes(crossReference));
        await output.WriteAsync(Encoding.ASCII.GetBytes(trailer));
        await output.FlushAsync();

        if (output.Length != targetBytes)
            throw new InvalidOperationException(
                $"The PDF test fixture is {output.Length} bytes; expected exactly {targetBytes} bytes.");
    }


    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    private static byte[] CreatePdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        return builder.Build();
    }

    private static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };

    private sealed record BatchUploadFixture(
        Guid TicketId,
        Guid JobId,
        string Path,
        string Name,
        long SizeBytes,
        string Sha256);

    private sealed class FakeCloudHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
