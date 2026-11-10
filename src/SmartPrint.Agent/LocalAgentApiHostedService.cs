using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SmartPrint.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SmartPrint.Agent;

public sealed class LocalAgentApiState
{
    private readonly object sync = new();
    private HashSet<string> configuredOrigins = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> trustedPortalOrigins = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? phoneProbeHash;
    private string? phoneProbeNetworkId;
    private DateTimeOffset phoneProbeExpiresUtc;
    private string? phoneVerifiedNetworkId;
    private DateTimeOffset? phoneVerifiedUtc;

    public bool IsRunning { get; internal set; }

    public (string Token, DateTimeOffset ExpiresUtc) CreatePhoneProbe(string networkId)
    {
        if (string.IsNullOrWhiteSpace(networkId) || networkId.Length > 256)
            throw new ArgumentException("Select an available shop connection.", nameof(networkId));
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        CryptographicOperations.ZeroMemory(bytes);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var expiresUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        lock (sync)
        {
            ClearPhoneProbeCore();
            phoneVerifiedNetworkId = null;
            phoneVerifiedUtc = null;
            phoneProbeHash = hash;
            phoneProbeNetworkId = networkId;
            phoneProbeExpiresUtc = expiresUtc;
        }
        return (token, expiresUtc);
    }

    public bool TryCompletePhoneProbe(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return false;
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        try
        {
            lock (sync)
            {
                if (phoneProbeHash is null || phoneProbeNetworkId is null || DateTimeOffset.UtcNow > phoneProbeExpiresUtc)
                {
                    ClearPhoneProbeCore();
                    return false;
                }
                if (!CryptographicOperations.FixedTimeEquals(phoneProbeHash, candidateHash)) return false;
                phoneVerifiedNetworkId = phoneProbeNetworkId;
                phoneVerifiedUtc = DateTimeOffset.UtcNow;
                ClearPhoneProbeCore();
                return true;
            }
        }
        finally { CryptographicOperations.ZeroMemory(candidateHash); }
    }

    public DateTimeOffset? GetPhoneVerifiedUtc(string networkId, DateTimeOffset? now = null)
    {
        lock (sync)
        {
            if (!string.Equals(phoneVerifiedNetworkId, networkId, StringComparison.OrdinalIgnoreCase) ||
                phoneVerifiedUtc is null)
                return null;

            // A successful scan proves that the phone reached this exact network
            // recently. It is not a permanent "online" claim.
            if (phoneVerifiedUtc < (now ?? DateTimeOffset.UtcNow).AddMinutes(-10))
            {
                phoneVerifiedNetworkId = null;
                phoneVerifiedUtc = null;
                return null;
            }

            return phoneVerifiedUtc;
        }
    }

    public void ClearPhoneVerification()
    {
        lock (sync)
        {
            phoneVerifiedNetworkId = null;
            phoneVerifiedUtc = null;
            ClearPhoneProbeCore();
        }
    }

    private void ClearPhoneProbeCore()
    {
        if (phoneProbeHash is not null) CryptographicOperations.ZeroMemory(phoneProbeHash);
        phoneProbeHash = null;
        phoneProbeNetworkId = null;
        phoneProbeExpiresUtc = DateTimeOffset.MinValue;
    }

    public void SetConfiguredOrigins(IEnumerable<string> origins)
    {
        lock (sync)
        {
            configuredOrigins = origins
                .Select(NormalizeOrigin)
                .Where(origin => origin is not null)
                .Select(origin => origin!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetPublicPortalOrigin(string? origin) =>
        SetTrustedPortalOrigins(origin is null ? Array.Empty<string>() : [origin]);

    public void SetTrustedPortalOrigins(IEnumerable<string> origins)
    {
        lock (sync)
        {
            trustedPortalOrigins = origins
                .Select(NormalizeOrigin)
                .Where(origin => origin is not null)
                .Select(origin => origin!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsOriginAllowed(string origin)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized is null) return false;
        lock (sync)
        {
            return configuredOrigins.Contains(normalized) || trustedPortalOrigins.Contains(normalized);
        }
    }

    private static string? NormalizeOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme is not ("http" or "https")) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}

public sealed class LocalAgentApiHostedService(
    IConfiguration configuration,
    AgentCredentialStore credentialStore,
    AgentLocalQueueStore localQueueStore,
    IHttpClientFactory httpClientFactory,
    LocalAgentApiState state,
    ILogger<LocalAgentApiHostedService> logger,
    IHostApplicationLifetime hostLifetime,
    LocalNetworkManager localNetworkManager,
    AgentRuntimeReadiness runtimeReadiness) : IHostedService, IAsyncDisposable
{
    private WebApplication? application;
    private CancellationTokenSource? retryLifetime;
    private Task? retryTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (retryTask is not null)
            return Task.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();
        retryLifetime = CancellationTokenSource.CreateLinkedTokenSource(hostLifetime.ApplicationStopping);
        retryTask = StartWithRetryAsync(retryLifetime.Token);
        return Task.CompletedTask;
    }

    private async Task StartWithRetryAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Agent:LocalApi:Enabled", true))
        {
            logger.LogInformation("Local Agent API is disabled by configuration.");
            return;
        }

        var delay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StartApplicationAsync(cancellationToken);
                var runningApplication = application
                    ?? throw new InvalidOperationException("The local Agent listener did not retain its application host.");

                await runningApplication.WaitForShutdownAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    return;

                state.IsRunning = false;
                throw new InvalidOperationException("The local Agent listener stopped unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                state.IsRunning = false;
                if (application is not null)
                {
                    await application.DisposeAsync();
                    application = null;
                }

                logger.LogWarning(
                    exception,
                    "The local Agent listener could not stay online. Cloud printing remains available; retrying the LAN listener in {RetrySeconds} seconds.",
                    delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    private async Task StartApplicationAsync(CancellationToken cancellationToken)
    {
        const int port = LocalNetworkEndpointDiscovery.DefaultPort;

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Configuration.AddConfiguration(configuration);
            // The cloud advertises RFC1918 IPv4 endpoints only; do not also expose
            // the same unauthenticated local status route on IPv6 interfaces.
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, port));

            var allowedOrigins = configuration.GetSection("Agent:LocalApi:AllowedOrigins")
                .Get<string[]>()?
                .Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                                 (uri.Scheme is "http" or "https") &&
                                 uri.AbsolutePath == "/" &&
                                 string.IsNullOrEmpty(uri.Query) &&
                                 string.IsNullOrEmpty(uri.Fragment) &&
                                 string.IsNullOrEmpty(uri.UserInfo))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();

            builder.Services.Configure<FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = 21 * 1024 * 1024;
                options.MemoryBufferThreshold = 21 * 1024 * 1024;
            });

            // Retain loopback-only static origins for local development. The production
            // HTTPS origin is supplied dynamically by the authenticated API heartbeat.
            state.SetConfiguredOrigins(allowedOrigins.Where(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var configuredUri) &&
                (configuredUri.IsLoopback || string.Equals(configuredUri.Host, "tauri.localhost", StringComparison.OrdinalIgnoreCase))));
            builder.Services.AddCors(options => options.AddPolicy("CustomerPortal", policy =>
            {
                policy.SetIsOriginAllowed(state.IsOriginAllowed)
                    .WithMethods("GET", "POST")
                    .WithHeaders("Content-Type", "X-Print-Upload-Grant", "X-Print-Preview-Grant", "X-Print-Photo-Revision-Grant")
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
            }));

            var app = builder.Build();
            application = app;
            app.Use(async (context, next) =>
            {
                // Bind every remote request to the shop connection the owner selected.
                // This prevents a different local adapter/subnet from reaching the
                // listener merely because Kestrel is bound to all IPv4 interfaces.
                if (!localNetworkManager.IsRemoteClientOnSelectedNetwork(
                        context.Connection.RemoteIpAddress,
                        context.Connection.LocalIpAddress))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.Headers["Cache-Control"] = "no-store";
                    return;
                }

                if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                    state.IsOriginAllowed(origin.ToString()) &&
                    string.Equals(context.Request.Headers["Access-Control-Request-Private-Network"],
                        "true", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                }

                await next();
            });
            app.UseCors("CustomerPortal");
            app.MapGet("/api/v1/local/status", () =>
            {
                try
                {
                    var credentials = credentialStore.LoadForCloudRequests(configuration["Agent:ApiBaseUrl"]);
                    if (credentials is null)
                    {
                        return Results.Json(new
                        {
                            title = credentialStore.IsRevoked()
                                ? "This computer's SmartPrint pairing was revoked by its shop owner."
                                : "This computer is not paired with a print shop.",
                            apiBaseUrl = configuration["Agent:ApiBaseUrl"],
                            readiness = runtimeReadiness.GetSnapshot(),
                            localUploadReady = false
                        }, statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    var readiness = runtimeReadiness.GetSnapshot();
                    return Results.Ok(new
                    {
                        agentDeviceId = credentials.AgentDeviceId,
                        protocolVersion = 1,
                        status = "LOCAL_ENDPOINT_READY",
                        apiBaseUrl = credentials.ApiBaseUrl,
                        readiness,
                        localUploadReady = readiness.LocalUploadReady
                    });
                }
                catch (Exception exception) when (
                    exception is CryptographicException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning("SmartPrint local identity status is unavailable ({FailureType}).", exception.GetType().Name);
                    return Results.Json(new
                    {
                        title = exception is InvalidOperationException
                            ? "The saved SmartPrint API address must use HTTPS. Repair the Agent connection."
                            : "The local agent identity is unavailable.",
                        apiBaseUrl = configuration["Agent:ApiBaseUrl"],
                        readiness = runtimeReadiness.GetSnapshot(),
                        localUploadReady = false
                    }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }).RequireCors("CustomerPortal");

            app.MapGet("/api/v1/local/connection-check", (HttpContext context) =>
            {
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                if (!state.TryCompletePhoneProbe(context.Request.Query["token"].FirstOrDefault()))
                    return Results.Content(
                        "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>SmartPrint</title><body><main><h1>Test link expired</h1><p>Return to SmartPrint on the shop computer and start a new phone check.</p></main></body></html>",
                        "text/html; charset=utf-8", statusCode: StatusCodes.Status410Gone);

                return Results.Content(
                    "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>SmartPrint connection verified</title><style>body{font-family:system-ui,sans-serif;background:#f4f7fb;color:#17243b;display:grid;min-height:90vh;place-items:center;margin:0;padding:24px}main{background:white;border-radius:20px;padding:32px;max-width:420px;text-align:center;box-shadow:0 12px 40px #18233a18}h1{font-size:25px}p{line-height:1.55;color:#526178}.mark{width:56px;height:56px;border-radius:50%;background:#e4f7ef;color:#128052;display:grid;place-items:center;margin:0 auto 18px;font-size:30px}</style><main><div class=\"mark\">✓</div><h1>Connection verified</h1><p>This phone can reach your shop computer. You can close this page and return to SmartPrint.</p></main></html>",
                    "text/html; charset=utf-8");
            });

            app.MapPost("/api/v1/local/documents/{jobId:guid}/photo-revision", async (
                Guid jobId,
                HttpContext context,
                WindowsDefenderDocumentThreatScanner threatScanner,
                CancellationToken ct) =>
            {
                AgentCredentials? credentials;
                try
                {
                    credentials = credentialStore.LoadForCloudRequests(configuration["Agent:ApiBaseUrl"]);
                }
                catch (InvalidOperationException exception)
                {
                    logger.LogWarning("Blocked a local photo revision because the saved Agent API address is not secure ({FailureType}).", exception.GetType().Name);
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "The saved SmartPrint API address must use HTTPS. Repair the Agent connection.");
                }

                var grantToken = context.Request.Headers["X-Print-Photo-Revision-Grant"].FirstOrDefault();
                if (credentials is null || string.IsNullOrWhiteSpace(grantToken) || grantToken.Length > 4096)
                    return Results.Unauthorized();
                if (!context.Request.HasFormContentType)
                    return Results.BadRequest(new { code = "photo_revision_form_required", message = "Send one prepared photo-sheet PDF." });

                var form = await context.Request.ReadFormAsync(ct);
                if (form.Files.Count != 1)
                    return Results.BadRequest(new { code = "one_photo_sheet_required", message = "Send exactly one prepared photo-sheet PDF." });

                var file = form.Files[0];
                if (file.Length is <= 0 or > 20 * 1024 * 1024 ||
                    !string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                    return Results.UnprocessableEntity(new { code = "photo_sheet_invalid", message = "The prepared photo sheet must be a PDF between 1 byte and 20 MB." });

                var photoSizeCode = form["photoSizeCode"].FirstOrDefault() ?? string.Empty;
                if (!int.TryParse(form["photoCount"].FirstOrDefault(), out var photoCount) ||
                    photoCount is < 1 or > 100 ||
                    !int.TryParse(form["photosPerPage"].FirstOrDefault(), out var photosPerPage) ||
                    photosPerPage is < 1 or > 100 ||
                    photoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6"))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["photoDetails"] = ["Choose a supported ID photo size and a quantity from 1 to 100."]
                    });

                byte[] bytes;
                await using (var input = file.OpenReadStream())
                await using (var memory = new MemoryStream((int)file.Length))
                {
                    await input.CopyToAsync(memory, ct);
                    bytes = memory.ToArray();
                }

                try
                {
                    await threatScanner.ScanAsync(bytes, ".pdf", ct);
                    logger.LogInformation(
                        "Local photo revision passed Defender scanning. JobId={JobId} SizeBytes={SizeBytes}",
                        jobId, bytes.LongLength);
                }
                catch (DocumentThreatScanException exception)
                {
                    logger.LogWarning(
                        exception,
                        "Local photo revision rejected by document security scanning. JobId={JobId} ThreatDetected={ThreatDetected} SizeBytes={SizeBytes}",
                        jobId, exception.ThreatDetected, bytes.LongLength);
                    CryptographicOperations.ZeroMemory(bytes);
                    return exception.ThreatDetected
                        ? Results.UnprocessableEntity(new { code = "document_security_rejected", message = exception.Message })
                        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                            title: "The shop's malware scanner could not confirm that this document is safe. The upload was blocked.");
                }

                DocumentAnalysis analysis;
                try
                {
                    analysis = DocumentAnalyzer.Analyse(bytes, ".pdf");
                }
                catch (DocumentValidationException exception)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return Results.UnprocessableEntity(new { code = "photo_sheet_not_supported", message = exception.Message });
                }
                var expectedPageCount = checked((photoCount + photosPerPage - 1) / photosPerPage);
                if (analysis.PageCount != expectedPageCount)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return Results.UnprocessableEntity(new
                    {
                        code = "photo_sheet_page_count_mismatch",
                        message = $"The prepared PDF must contain exactly {expectedPageCount} A4 page(s) for this layout."
                    });
                }

                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var entry = localQueueStore.Find(jobId);
                if (entry is null ||
                    entry.State is not ("LOCAL_UPLOAD_STAGED" or "LOCAL_UPLOAD_PENDING_PAYMENT") ||
                    !File.Exists(entry.DocumentPath))
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return Results.Conflict(new { code = "local_photo_source_missing", message = "The original local photo is no longer eligible for enhancement." });
                }

                var revisionRequest = new LocalAgentPhotoRevisionRequest(
                    entry.Sha256,
                    entry.SizeBytes,
                    hash,
                    bytes.LongLength,
                    analysis.PageCount,
                    analysis.PageSizesJson,
                    photoSizeCode,
                    photoCount,
                    photosPerPage);

                var client = httpClientFactory.CreateClient("cloud");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");

                using var authorizeRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"api/v1/agent/local-document-photo-revisions/{jobId}/authorize")
                {
                    Content = JsonContent.Create(revisionRequest)
                };
                authorizeRequest.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
                authorizeRequest.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
                authorizeRequest.Headers.Add("X-Print-Photo-Revision-Grant", grantToken);

                using var authorizeResponse = await client.SendAsync(authorizeRequest, ct);
                if (!authorizeResponse.IsSuccessStatusCode)
                {
                    var detail = await authorizeResponse.Content.ReadAsStringAsync(ct);
                    CryptographicOperations.ZeroMemory(bytes);
                    logger.LogWarning("Local photo revision authorization for job {JobId} returned HTTP {StatusCode}.",
                        jobId, (int)authorizeResponse.StatusCode);
                    return Results.Problem(
                        statusCode: (int)authorizeResponse.StatusCode,
                        title: string.IsNullOrWhiteSpace(detail)
                            ? "The cloud service could not authorize this local photo revision."
                            : detail);
                }

                var authorization = await authorizeResponse.Content.ReadFromJsonAsync<LocalAgentPhotoRevisionAuthorizationResponse>(cancellationToken: ct);
                if (authorization is null ||
                    !string.Equals(authorization.OriginalSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    authorization.OriginalSizeBytes != entry.SizeBytes ||
                    !string.Equals(authorization.NewSha256, hash, StringComparison.OrdinalIgnoreCase) ||
                    authorization.NewSizeBytes != bytes.LongLength ||
                    authorization.PhotoSizeCode != photoSizeCode ||
                    authorization.PhotoCount != photoCount ||
                    authorization.PageCount != analysis.PageCount ||
                    authorization.PhotosPerPage != photosPerPage)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return Results.Conflict(new { code = "local_photo_revision_authorization_mismatch", message = "The cloud service authorization did not match this original and prepared photo sheet." });
                }

                var outputName = $"photo-sheet-{entry.JobNumber}.pdf";
                try
                {
                    await localQueueStore.ReplaceStagedPhotoDocumentAsync(
                        jobId,
                        entry.Sha256,
                        entry.SizeBytes,
                        outputName,
                        bytes,
                        analysis.PageCount,
                        authorization.QuotedAmountMinor,
                        async () =>
                        {
                            for (var attempt = 0; attempt < 2; attempt++)
                            {
                                using var completeRequest = new HttpRequestMessage(
                                    HttpMethod.Post,
                                    $"api/v1/agent/local-document-photo-revisions/{jobId}/complete")
                                {
                                    Content = JsonContent.Create(revisionRequest)
                                };
                                completeRequest.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
                                completeRequest.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
                                completeRequest.Headers.Add("X-Print-Photo-Revision-Grant", grantToken);
                                HttpResponseMessage completeResponse;
                                try
                                {
                                    completeResponse = await client.SendAsync(completeRequest, ct);
                                }
                                catch (Exception exception) when (
                                    attempt == 0 &&
                                    !ct.IsCancellationRequested &&
                                    exception is HttpRequestException or TaskCanceledException)
                                {
                                    logger.LogWarning("Local photo revision completion response was interrupted for job {JobId}; retrying the same idempotent request.", jobId);
                                    continue;
                                }

                                using (completeResponse)
                                {
                                    if (completeResponse.IsSuccessStatusCode) return;
                                    var detail = await completeResponse.Content.ReadAsStringAsync(ct);
                                    if (attempt == 0 && (int)completeResponse.StatusCode >= 500)
                                    {
                                        logger.LogWarning("Local photo revision completion for job {JobId} returned HTTP {StatusCode}; retrying once.", jobId, (int)completeResponse.StatusCode);
                                        continue;
                                    }
                                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                                        ? $"The cloud service rejected the local photo revision ({(int)completeResponse.StatusCode})."
                                        : detail);
                                }
                            }

                            throw new HttpRequestException("The cloud service did not confirm the local photo revision after a retry.");
                        },
                        ct);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Local photo revision for job {JobId} was rolled back ({FailureType}).", jobId, exception.GetType().Name);
                    CryptographicOperations.ZeroMemory(bytes);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: exception.Message);
                }

                CryptographicOperations.ZeroMemory(bytes);
                return Results.Ok(new
                {
                    JobId = jobId,
                    OriginalFileName = outputName,
                    MediaType = "application/pdf",
                    SizeBytes = revisionRequest.SizeBytes,
                    Sha256 = revisionRequest.Sha256,
                    revisionRequest.PhotoSizeCode,
                    revisionRequest.PhotoCount,
                    revisionRequest.PageCount,
                    revisionRequest.PhotosPerPage,
                    authorization.PhotoUnitPriceMinor,
                    authorization.QuotedAmountMinor,
                    authorization.PricingVersion
                });
            }).RequireCors("CustomerPortal");

            app.MapGet("/api/v1/local/documents/{jobId:guid}/preview", async (
                Guid jobId,
                HttpContext context,
                CancellationToken ct) =>
            {
                var variant = context.Request.Query["variant"].FirstOrDefault();
                var outputPreview = string.Equals(variant, "output", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(variant) && !outputPreview &&
                    !string.Equals(variant, "original", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { code = "local_preview_variant_invalid" });

                AgentCredentials? credentials;
                try
                {
                    credentials = credentialStore.LoadForCloudRequests(configuration["Agent:ApiBaseUrl"]);
                }
                catch (InvalidOperationException exception)
                {
                    logger.LogWarning("Blocked a local preview because the saved Agent API address is not secure ({FailureType}).", exception.GetType().Name);
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "The saved SmartPrint API address must use HTTPS. Repair the Agent connection.");
                }

                var grantToken = context.Request.Headers["X-Print-Preview-Grant"].FirstOrDefault();
                if (credentials is null || string.IsNullOrWhiteSpace(grantToken) || grantToken.Length > 4096)
                    return Results.Unauthorized();

                var client = httpClientFactory.CreateClient("cloud");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");
                var authorizePath = $"api/v1/agent/local-document-previews/{jobId}/authorize" +
                    (outputPreview ? "?variant=output" : string.Empty);
                using var authorizeRequest = new HttpRequestMessage(HttpMethod.Get, authorizePath);
                authorizeRequest.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
                authorizeRequest.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
                authorizeRequest.Headers.Add("X-Print-Preview-Grant", grantToken);

                using var authorizeResponse = await client.SendAsync(authorizeRequest, ct);
                if (!authorizeResponse.IsSuccessStatusCode)
                {
                    var rawDetail = await authorizeResponse.Content.ReadAsStringAsync(ct);
                    var detail = "";
                    if (!string.IsNullOrWhiteSpace(rawDetail))
                    {
                        try
                        {
                            using var parsed = JsonDocument.Parse(rawDetail);
                            if (parsed.RootElement.TryGetProperty("message", out var message) &&
                                message.ValueKind == JsonValueKind.String)
                                detail = message.GetString() ?? "";
                            else if (parsed.RootElement.TryGetProperty("title", out var title) &&
                                     title.ValueKind == JsonValueKind.String)
                                detail = title.GetString() ?? "";
                        }
                        catch (JsonException)
                        {
                            // Do not echo unstructured upstream content into the local UI.
                        }
                    }
                    logger.LogWarning("Local document preview authorization for job {JobId} returned HTTP {StatusCode}.",
                        jobId, (int)authorizeResponse.StatusCode);
                    return Results.Problem(
                        statusCode: (int)authorizeResponse.StatusCode,
                        title: string.IsNullOrWhiteSpace(detail)
                            ? "The cloud service could not authorize this local document preview."
                            : detail);
                }

                var authorizedDocument = await authorizeResponse.Content.ReadFromJsonAsync<LocalDocumentPreviewAuthorizationResponse>(cancellationToken: ct);
                if (authorizedDocument is null)
                    return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The cloud service returned invalid local preview metadata.");

                var entry = localQueueStore.Find(jobId);
                if (entry is null)
                    return Results.NotFound(new { code = "local_preview_document_missing", message = "This print job is no longer available on the shop computer." });

                byte[] bytes;
                string mediaType;
                if (outputPreview)
                {
                    if (!PdfExportPreviewRules.TryGetVerifiedReceipt(
                            entry,
                            authorizedDocument.OriginalFileName,
                            authorizedDocument.SizeBytes,
                            authorizedDocument.Sha256,
                            out var outputPath))
                        return Results.Conflict(new { code = "local_preview_output_mismatch", message = "The saved PDF does not match the export receipt recorded by the shop service." });

                    try
                    {
                        var info = new FileInfo(outputPath);
                        if (!info.Exists)
                            return Results.NotFound(new { code = "local_preview_output_missing", message = "The final PDF export is no longer present on this shop computer." });
                        if (info.Length != authorizedDocument.SizeBytes)
                            return Results.Conflict(new { code = "local_preview_output_mismatch", message = "The saved PDF file size no longer matches its verified export receipt." });
                        bytes = await File.ReadAllBytesAsync(outputPath, ct);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        logger.LogError(exception, "Saved PDF preview could not be read for job {JobId}.", jobId);
                        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                            title: "The saved PDF could not be read safely from the shop computer.");
                    }

                    if (!PdfExportPreviewRules.MatchesPdfBytes(
                            bytes, authorizedDocument.SizeBytes, authorizedDocument.Sha256))
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                        return Results.Conflict(new { code = "local_preview_output_integrity_failed", message = "The saved PDF failed its integrity check and has not been shown." });
                    }
                    mediaType = "application/pdf";
                }
                else
                {
                    if (!File.Exists(entry.DocumentPath))
                        return Results.NotFound(new { code = "local_preview_document_missing", message = "The original file is no longer present on this shop computer." });
                    if (entry.MediaType is not ("application/pdf" or "image/jpeg" or "image/png"))
                        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                    if (entry.SizeBytes != authorizedDocument.SizeBytes ||
                        !string.Equals(entry.Sha256, authorizedDocument.Sha256, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(entry.MediaType, authorizedDocument.MediaType, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(entry.DocumentName, authorizedDocument.OriginalFileName, StringComparison.Ordinal))
                        return Results.Conflict(new { code = "local_preview_document_mismatch", message = "The protected local file does not match the document registered by the shop API." });

                    try
                    {
                        bytes = localQueueStore.ReadDocument(jobId);
                    }
                    catch (Exception exception) when (exception is IOException or CryptographicException or InvalidDataException)
                    {
                        logger.LogError(exception, "Local document preview integrity check failed for job {JobId}.", jobId);
                        return Results.Problem(
                            statusCode: StatusCodes.Status500InternalServerError,
                            title: "The protected local document could not be read safely.");
                    }
                    mediaType = entry.MediaType;
                }

                context.Response.Headers["Cache-Control"] = "private, no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "sandbox";
                context.Response.Headers["Content-Disposition"] = "inline";
                context.Response.OnCompleted(() =>
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return Task.CompletedTask;
                });
                return Results.File(bytes, mediaType, enableRangeProcessing: false);
            }).RequireCors("CustomerPortal");

            app.MapPost("/api/v1/local/uploads/{ticketId:guid}", async (
                Guid ticketId,
                HttpContext context,
                WindowsDefenderDocumentThreatScanner threatScanner,
                CancellationToken ct) =>
            {
                AgentCredentials? credentials;
                try
                {
                    credentials = credentialStore.LoadForCloudRequests(configuration["Agent:ApiBaseUrl"]);
                }
                catch (InvalidOperationException exception)
                {
                    logger.LogWarning("Blocked a local upload because the saved Agent API address is not secure ({FailureType}).", exception.GetType().Name);
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "The saved SmartPrint API address must use HTTPS. Repair the Agent connection.");
                }
                var uploadGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
                if (credentials is null || string.IsNullOrWhiteSpace(uploadGrant))
                {
                    return Results.Unauthorized();
                }

                var client = httpClientFactory.CreateClient("cloud");
                client.Timeout = TimeSpan.FromMinutes(2);
                client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");
                using var claimRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"api/v1/agent/local-uploads/{ticketId}/claim")
                {
                    Content = JsonContent.Create(new { })
                };
                AddAgentHeaders(claimRequest, credentials, uploadGrant);
                using var claimResponse = await client.SendAsync(claimRequest, ct);
                if (!claimResponse.IsSuccessStatusCode)
                {
                    var detail = await claimResponse.Content.ReadAsStringAsync(ct);
                    return Results.Problem(
                        statusCode: (int)claimResponse.StatusCode,
                        title: "The shop could not authorise this local upload.",
                        detail: detail.Length > 500 ? detail[..500] : detail);
                }

                var claim = await claimResponse.Content.ReadFromJsonAsync<LocalUploadTicketClaimResponse>(cancellationToken: ct);
                if (claim is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The cloud API returned an invalid local upload grant.");
                }

                if (claim.AlreadyCompleted && claim.UploadResult is not null)
                {
                    var completedJobId = claim.PrintJobId != Guid.Empty
                        ? claim.PrintJobId
                        : claim.UploadResult.Job.Id;
                    var completedEntry = localQueueStore.Find(completedJobId);
                    if (completedEntry is null || !File.Exists(completedEntry.DocumentPath))
                    {
                        if (string.Equals(claim.UploadResult.Document.StorageMode, "CLOUD", StringComparison.OrdinalIgnoreCase))
                        {
                            // HTTPS recovery already stored this ticket's exact file
                            // in cloud storage. The normal paid-job claim can download
                            // it later; do not ask the customer to upload it again.
                            return Results.Ok(claim.UploadResult);
                        }
                        return Results.Conflict(new { code = "local_upload_document_missing", message = "The API has this local upload recorded, but the original document is missing from the protected shop queue. Ask the customer to upload again." });
                    }
                    await localQueueStore.MarkLocalUploadFinalizedAsync(completedJobId, ct);
                    return Results.Ok(claim.UploadResult);
                }

                if (claim.PrintJobId == Guid.Empty ||
                    claim.ExpiresUtc <= DateTimeOffset.UtcNow ||
                    claim.SizeBytes is <= 0 or > 20L * 1024 * 1024 ||
                    string.IsNullOrWhiteSpace(claim.Sha256) ||
                    string.IsNullOrWhiteSpace(claim.FileExtension))
                {
                    return Results.Conflict(new { code = "local_upload_ticket_invalid", message = "The cloud API returned invalid local upload metadata." });
                }

                byte[] bytes;
                var existing = localQueueStore.Find(claim.PrintJobId);
                if (existing is not null)
                {
                    if (!string.Equals(existing.Sha256, claim.Sha256, StringComparison.OrdinalIgnoreCase) ||
                        existing.SizeBytes != claim.SizeBytes ||
                        !File.Exists(existing.DocumentPath))
                    {
                        return Results.Conflict(new { code = "local_upload_recovery_conflict", message = "The local queue already contains different or incomplete data for this upload." });
                    }

                    if (existing.State == "LOCAL_UPLOAD_STAGED")
                    {
                        // A retried ticket may have received a new LAN lease.
                        // Refresh the staged manifest before the periodic expiry
                        // sweep can remove the same original protected document.
                        await localQueueStore.RefreshStagedLocalUploadLeaseAsync(
                            claim.PrintJobId, claim.Sha256, claim.SizeBytes, claim.ExpiresUtc, ct);
                    }
                    bytes = localQueueStore.ReadDocument(claim.PrintJobId);
                }
                else
                {
                    if (!context.Request.HasFormContentType)
                    {
                        return Results.BadRequest(new { code = "multipart_form_required", message = "Send the document as multipart/form-data." });
                    }

                    var form = await context.Request.ReadFormAsync(ct);
                    if (form.Files.Count != 1 || form.Files[0].Length != claim.SizeBytes)
                    {
                        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The received document size does not match the authorised upload.");
                    }

                    await using var memory = new MemoryStream((int)claim.SizeBytes);
                    await using (var input = form.Files[0].OpenReadStream())
                    {
                        await input.CopyToAsync(memory, ct);
                    }
                    if (memory.Length != claim.SizeBytes)
                    {
                        return Results.BadRequest(new { code = "local_upload_incomplete", message = "The document transfer was incomplete. Retry the upload." });
                    }
                    bytes = memory.ToArray();
                    CryptographicOperations.ZeroMemory(memory.GetBuffer().AsSpan(0, (int)memory.Length));
                }

                try
                {
                    var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (!string.Equals(actualHash, claim.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.UnprocessableEntity(new { code = "local_upload_integrity_mismatch", message = "The document hash does not match the cloud-authorised upload ticket." });
                    }

                    try
                    {
                        await threatScanner.ScanAsync(bytes, claim.FileExtension, ct);
                        logger.LogInformation(
                            "Local upload passed Defender scanning. TicketId={TicketId} SizeBytes={SizeBytes}",
                            ticketId, bytes.LongLength);
                    }
                    catch (DocumentThreatScanException exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Local upload rejected by document security scanning. TicketId={TicketId} ThreatDetected={ThreatDetected} SizeBytes={SizeBytes}",
                            ticketId, exception.ThreatDetected, bytes.LongLength);
                        return exception.ThreatDetected
                            ? Results.UnprocessableEntity(new { code = "document_security_rejected", message = exception.Message })
                            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                                title: "The shop's malware scanner could not confirm that this document is safe. The upload was blocked.");
                    }

                    DocumentAnalysis analysis;
                    try
                    {
                        analysis = DocumentAnalyzer.Analyse(bytes, claim.FileExtension);
                    }
                    catch (DocumentValidationException exception)
                    {
                        return Results.UnprocessableEntity(new { code = "document_not_supported", message = exception.Message });
                    }

                    if (existing is null)
                    {
                        var stagedJob = new AgentClaimedJob(
                            claim.PrintJobId,
                            claim.JobNumber,
                            "LOCAL_UPLOAD_STAGED",
                            analysis.PageCount,
                            claim.Copies,
                            claim.ColourMode,
                            claim.PaperSize,
                            claim.IsDuplex,
                            "INR",
                            0,
                            claim.PrinterDeviceId,
                            claim.PrinterName,
                            claim.QueueName,
                            claim.OriginalFileName,
                            analysis.MediaType,
                            claim.SizeBytes,
                            claim.Sha256,
                            claim.ExpiresUtc,
                            "LOCAL_AGENT",
                            Orientation: claim.Orientation);
                        await localQueueStore.EnqueueLocalUploadAsync(stagedJob, bytes, ct);
                    }

                    using var completionRequest = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"api/v1/agent/local-uploads/{ticketId}/complete")
                    {
                        Content = JsonContent.Create(new LocalPrintUploadCompletionRequest(
                            analysis.PageCount,
                            analysis.PageSizesJson,
                            analysis.Extension,
                            analysis.MediaType,
                            claim.SizeBytes,
                            claim.Sha256))
                    };
                    AddAgentHeaders(completionRequest, credentials, uploadGrant);
                    using var completionResponse = await client.SendAsync(completionRequest, ct);
                    if (!completionResponse.IsSuccessStatusCode)
                    {
                        var detail = await completionResponse.Content.ReadAsStringAsync(ct);
                        return Results.Problem(
                            statusCode: StatusCodes.Status503ServiceUnavailable,
                            title: "The document is safely stored on the shop computer, but the cloud job record could not be finalised. Retry this same upload.",
                            detail: detail.Length > 500 ? detail[..500] : detail);
                    }

                    var completed = await completionResponse.Content.ReadFromJsonAsync<LocalUploadCompletionResponse>(cancellationToken: ct);
                    if (completed?.UploadResult is null)
                    {
                        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The cloud API did not return the completed local upload.");
                    }

                    await localQueueStore.MarkLocalUploadFinalizedAsync(claim.PrintJobId, ct);
                    return Results.Ok(completed.UploadResult);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            })
            .WithMetadata(new RequestSizeLimitAttribute(21 * 1024 * 1024))
            .RequireCors("CustomerPortal");

            await application.StartAsync(cancellationToken);
            state.IsRunning = true;
            logger.LogInformation("Local Agent status endpoint is listening on port {Port}.", port);
        }
        catch
        {
            state.IsRunning = false;
            if (application is not null)
            {
                await application.DisposeAsync();
                application = null;
            }
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        state.IsRunning = false;
        retryLifetime?.Cancel();
        if (retryTask is not null)
            await retryTask;

        if (application is not null)
        {
            await application.StopAsync(cancellationToken);
            await application.DisposeAsync();
            application = null;
        }
    }


    private static void AddAgentHeaders(HttpRequestMessage request, AgentCredentials credentials, string uploadGrant)
    {
        request.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
        request.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
        request.Headers.Add("X-Print-Upload-Grant", uploadGrant);
    }

    private sealed record LocalUploadTicketClaimResponse(
        bool AlreadyCompleted,
        Guid Id,
        Guid PrintJobId,
        string JobNumber,
        Guid AgentDeviceId,
        Guid PrinterDeviceId,
        string PrinterName,
        string QueueName,
        string OriginalFileName,
        string FileExtension,
        string MediaType,
        long SizeBytes,
        string Sha256,
        string ColourMode,
        string PaperSize,
        bool IsDuplex,
        int Copies,
        string Orientation,
        DateTimeOffset ExpiresUtc,
        LocalUploadedPrintResult? UploadResult);

    private sealed record LocalUploadCompletionResponse(
        bool AlreadyCompleted,
        LocalUploadedPrintResult? UploadResult);

    private sealed record LocalUploadedPrintResult(
        LocalUploadedJob Job,
        LocalUploadedDocument Document,
        bool DuplicateRequest);

    private sealed record LocalUploadedJob(
        Guid Id, string JobNumber, string Status, int PageCount, int Copies,
        string Currency, long QuotedAmountMinor, DateTimeOffset CreatedUtc,
        string ColourMode = "Mono", string PaperSize = "A4", bool IsDuplex = false,
        string Orientation = "AUTO", int PricingVersion = 1);

    private sealed record LocalUploadedDocument(
        Guid Id, string OriginalFileName, string MediaType, long SizeBytes,
        string Sha256, string PageSizesJson, string? StorageMode = null);

    private sealed record LocalDocumentPreviewAuthorizationResponse(
        string MediaType, string OriginalFileName, long SizeBytes, string Sha256);

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        retryLifetime?.Dispose();
        retryLifetime = null;
        retryTask = null;
    }
}
