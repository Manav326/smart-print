using System.Security.Cryptography;
using System.Text;
using SmartPrint.Documents;

const int maximumDocumentBytes = 20 * 1024 * 1024;
const string tokenHeader = "X-SmartPrint-Worker-Token";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maximumDocumentBytes;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
});

var tokenPath = builder.Configuration["DocumentWorker:TokenFile"] ?? "/run/secrets/document-worker-token";
if (!File.Exists(tokenPath))
{
    throw new InvalidOperationException("The internal document-worker authentication token file is missing.");
}

var token = File.ReadAllText(tokenPath).Trim();
if (token.Length < 64 || token.Any(character => !Uri.IsHexDigit(character)))
{
    throw new InvalidOperationException("The internal document-worker authentication token is invalid.");
}
var tokenBytes = Encoding.UTF8.GetBytes(token);

var app = builder.Build();
const string readyFile = "/tmp/smartprint-document-worker-ready";
app.Lifetime.ApplicationStarted.Register(() => File.WriteAllText(readyFile, "ready"));
app.Lifetime.ApplicationStopping.Register(() =>
{
    if (File.Exists(readyFile)) File.Delete(readyFile);
});

app.MapGet("/health/live", () => Results.Ok(new { service = "smartprint-document-worker", status = "live" }));
app.MapGet("/health/ready", () => File.Exists(readyFile)
    ? Results.Ok(new { service = "smartprint-document-worker", status = "ready" })
    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapPost("/analyze", async (HttpContext context, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";

    var suppliedToken = context.Request.Headers[tokenHeader].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(suppliedToken))
    {
        return Results.Unauthorized();
    }

    var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
    var authenticated = suppliedBytes.Length == tokenBytes.Length &&
        CryptographicOperations.FixedTimeEquals(suppliedBytes, tokenBytes);
    CryptographicOperations.ZeroMemory(suppliedBytes);
    if (!authenticated)
    {
        return Results.Unauthorized();
    }

    var extension = context.Request.Headers["X-Document-Extension"].FirstOrDefault()?.ToLowerInvariant();
    if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
    {
        return Results.BadRequest(new { code = "document_extension_unsupported", message = "The document extension is not supported." });
    }

    if (context.Request.ContentLength is <= 0 or > maximumDocumentBytes)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    await using var memory = new MemoryStream();
    var buffer = new byte[64 * 1024];
    try
    {
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, cancellationToken);
            if (read <= 0) break;
            if (memory.Length + read > maximumDocumentBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (memory.Length <= 0)
        {
            return Results.BadRequest(new { code = "document_empty", message = "The document body is empty." });
        }

        var document = memory.ToArray();
        try
        {
            var analysis = DocumentAnalyzer.Analyse(document, extension);
            return Results.Ok(analysis);
        }
        catch (DocumentValidationException exception)
        {
            return Results.UnprocessableEntity(new
            {
                code = "document_not_supported",
                message = exception.Message
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(document);
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(buffer);
    }
});

try
{
    await app.RunAsync();
}
finally
{
    CryptographicOperations.ZeroMemory(tokenBytes);
}
