using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartPrint.Documents;

namespace SmartPrint.Api.Security;

/// <summary>
/// Sends document parsing to the private, resource-limited document-worker container.
/// Malware scanning is performed by ClamAV before this boundary is called.
/// </summary>
public sealed class IsolatedDocumentAnalyzer : IDocumentAnalyzer
{
    private const int MaximumDocumentBytes = 20 * 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly byte[] authenticationToken;

    public IsolatedDocumentAnalyzer(HttpClient httpClient, IConfiguration configuration)
    {
        this.httpClient = httpClient;
        var baseUrl = configuration["DocumentSecurity:AnalysisWorker:BaseUrl"] ?? "http://document-worker:8090/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
        {
            throw new InvalidOperationException(
                "The document analysis worker must be configured with a private HTTP service URL.");
        }

        this.httpClient.BaseAddress = new Uri(uri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        this.httpClient.Timeout = TimeSpan.FromSeconds(
            Math.Clamp(configuration.GetValue<int?>("DocumentSecurity:AnalysisWorker:TimeoutSeconds") ?? 90, 5, 180));

        var tokenPath = configuration["DocumentSecurity:AnalysisWorker:TokenFile"] ??
            "/run/secrets/document-worker-token";
        if (!File.Exists(tokenPath))
        {
            throw new DocumentProcessingException(
                "The isolated document processing service is not configured. Uploads are temporarily unavailable.");
        }

        var token = File.ReadAllText(tokenPath).Trim();
        if (token.Length < 64 || token.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("The isolated document worker authentication token is invalid.");
        }
        authenticationToken = Encoding.UTF8.GetBytes(token);
    }

    public async Task<DocumentAnalysis> AnalyzeAsync(
        ReadOnlyMemory<byte> document,
        string extension,
        CancellationToken cancellationToken)
    {
        if (document.IsEmpty || document.Length > MaximumDocumentBytes)
        {
            throw new DocumentValidationException("The document is outside the supported processing size range.");
        }

        if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
        {
            throw new DocumentValidationException("The document extension is not supported.");
        }

        var requestBytes = document.ToArray();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "analyze")
            {
                Content = new ByteArrayContent(requestBytes)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.Add("X-SmartPrint-Worker-Token", Encoding.UTF8.GetString(authenticationToken));
            request.Headers.Add("X-Document-Extension", extension);

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var failure = await response.Content.ReadFromJsonAsync<WorkerFailureResponse>(
                    cancellationToken: cancellationToken);
                throw new DocumentValidationException(
                    string.IsNullOrWhiteSpace(failure?.Message)
                        ? "The document could not be analysed safely."
                        : failure.Message);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DocumentProcessingException(
                    $"The isolated document processing service returned HTTP {(int)response.StatusCode}. Upload was blocked.");
            }

            var analysis = await response.Content.ReadFromJsonAsync<DocumentAnalysis>(
                cancellationToken: cancellationToken);
            return analysis ?? throw new DocumentProcessingException(
                "The isolated document processing service returned an invalid result. Upload was blocked.");
        }
        catch (DocumentValidationException)
        {
            throw;
        }
        catch (DocumentProcessingException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new DocumentProcessingException(
                "The isolated document processing service is unavailable or timed out. Upload was blocked.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestBytes);
        }
    }

    private sealed record WorkerFailureResponse(string? Code, string? Message);
}
