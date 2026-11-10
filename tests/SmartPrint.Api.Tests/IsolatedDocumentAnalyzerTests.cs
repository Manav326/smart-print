using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Security;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class IsolatedDocumentAnalyzerTests
{
    [Fact]
    public async Task SendsDocumentOnlyToAuthenticatedWorkerAndDeserializesAnalysis()
    {
        var tokenPath = CreateTokenFile(out var directory);
        try
        {
            HttpRequestMessage? observed = null;
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                observed = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new DocumentAnalysis(1, "[]", "image/png", ".png"))
                });
            }));
            var analyzer = new IsolatedDocumentAnalyzer(client, Configuration(tokenPath));
            var bytes = "sample document"u8.ToArray();

            var result = await analyzer.AnalyzeAsync(bytes, ".png", CancellationToken.None);

            Assert.Equal(1, result.PageCount);
            Assert.NotNull(observed);
            Assert.Equal(HttpMethod.Post, observed.Method);
            Assert.Equal("http://document-worker:8090/analyze", observed.RequestUri!.ToString());
            Assert.Equal("unit-test-worker-token", observed.Headers.GetValues("X-SmartPrint-Worker-Token").Single());
            Assert.Equal(".png", observed.Headers.GetValues("X-Document-Extension").Single());
            Assert.Equal(bytes, await observed.Content!.ReadAsByteArrayAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ConvertsWorkerValidationFailureIntoDocumentValidationError()
    {
        var tokenPath = CreateTokenFile(out var directory);
        try
        {
            using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = JsonContent.Create(new { code = "document_not_supported", message = "Invalid PDF signature." })
                })));
            var analyzer = new IsolatedDocumentAnalyzer(client, Configuration(tokenPath));

            var exception = await Assert.ThrowsAsync<DocumentValidationException>(() =>
                analyzer.AnalyzeAsync("bad"u8.ToArray(), ".pdf", CancellationToken.None));

            Assert.Equal("Invalid PDF signature.", exception.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailsClosedWhenIsolatedWorkerIsUnavailable()
    {
        var tokenPath = CreateTokenFile(out var directory);
        try
        {
            using var client = new HttpClient(new StubHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
            var analyzer = new IsolatedDocumentAnalyzer(client, Configuration(tokenPath));

            var exception = await Assert.ThrowsAsync<DocumentProcessingException>(() =>
                analyzer.AnalyzeAsync("sample"u8.ToArray(), ".pdf", CancellationToken.None));

            Assert.Contains("Upload was blocked", exception.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RefusesToConstructWithoutTheWorkerToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "smartprint-worker-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var client = new HttpClient(new StubHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
            Assert.Throws<DocumentProcessingException>(() =>
                new IsolatedDocumentAnalyzer(client, Configuration(Path.Combine(directory, "missing-token"))));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static IConfiguration Configuration(string tokenPath) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DocumentSecurity:AnalysisWorker:BaseUrl"] = "http://document-worker:8090/",
            ["DocumentSecurity:AnalysisWorker:TokenFile"] = tokenPath,
            ["DocumentSecurity:AnalysisWorker:TimeoutSeconds"] = "15"
        }).Build();

    private static string CreateTokenFile(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "smartprint-worker-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "token");
        File.WriteAllText(path, "unit-test-worker-token");
        return path;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}
