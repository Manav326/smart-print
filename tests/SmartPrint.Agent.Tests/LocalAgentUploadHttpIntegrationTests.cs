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

namespace SmartPrint.Agent.Tests;

public sealed class LocalAgentUploadHttpIntegrationTests
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

        var queue = new AgentLocalQueueStore(queueRoot);
        await queue.InitializeAsync(CancellationToken.None);
        var state = new LocalAgentApiState();
        var preferences = new LocalNetworkPreferenceStore(Path.Combine(root, "network.preference"));
        var networkManager = new LocalNetworkManager(preferences, state);
        var runtimeReadiness = new AgentRuntimeReadiness();

        var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
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
                services.AddSingleton(new AgentDocumentEncryptionKeyStore(Path.Combine(root, "document-key.dpapi")));
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
            using var multipart = new MultipartFormDataContent();
            using var fileContent = new ByteArrayContent(pdf);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            multipart.Add(fileContent, "file", "certificate-without-extension");

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://127.0.0.1:{LocalNetworkEndpointDiscovery.DefaultPort}/api/v1/local/uploads/{ticketId:D}")
            {
                Content = multipart
            };
            request.Headers.Add("X-Print-Upload-Grant", "upload-grant-test");

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.IsSuccessStatusCode,
                $"The extensionless PDF failed at the hosted Agent multipart endpoint: HTTP {(int)response.StatusCode}: {responseBody}");

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
            Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(queue.ReadDocument(jobId))).ToLowerInvariant());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            Directory.Delete(root, recursive: true);
            CryptographicOperations.ZeroMemory(pdf);
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
