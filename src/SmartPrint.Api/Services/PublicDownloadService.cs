using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace SmartPrint.Api.Services;

public sealed record PublicDownloadStatus(string? Version, bool WindowsAvailable, bool AndroidAvailable, string? DownloadBaseUrl);

/// <summary>
/// Resolves stable release assets on the server and streams them through the
/// public application origin. GitHub asset redirects are followed server-side;
/// no GitHub URL or redirect is returned to a customer browser.
/// </summary>
public sealed class PublicDownloadService(HttpClient httpClient, IConfiguration configuration, IMemoryCache cache)
{
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan ReleaseCacheDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan VersionedReleaseCacheDuration = TimeSpan.FromDays(1);
    private const int StreamCopyBufferSize = 128 * 1024;

    private static readonly IReadOnlyDictionary<string, string> PlatformAssets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = "SmartPrint-Windows-Setup.exe",
            ["android"] = "SmartPrint-Android.apk"
        };

    private readonly string _repository = configuration["PublicDownloads:GitHubRepository"]?.Trim() ?? "";
    private readonly string _downloadBaseUrl = configuration["PublicDownloads:PublicBaseUrl"]?.Trim() ?? "";

    public async Task<PublicDownloadStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var release = await GetLatestReleaseAsync(cancellationToken);
        return new PublicDownloadStatus(
            release?.TagName,
            release?.Assets.ContainsKey(PlatformAssets["windows"]) == true,
            release?.Assets.ContainsKey(PlatformAssets["android"]) == true,
            GetValidatedDownloadBaseUrl());
    }

    private string? GetValidatedDownloadBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(_downloadBaseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(_downloadBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.AbsolutePath != "/" && !string.IsNullOrEmpty(uri.AbsolutePath)) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "PublicDownloads:PublicBaseUrl must be an HTTPS origin without a path, query, fragment, or credentials.");
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public Task StreamDownloadAsync(string platform, HttpContext context, CancellationToken cancellationToken) =>
        StreamDownloadCoreAsync(platform, null, context, cancellationToken);

    /// <summary>
    /// Streams an immutable, explicitly versioned release asset. The browser-facing
    /// response remains private/no-store; Cloudflare receives a separate one-year
    /// cache directive because both URL path components identify immutable bytes.
    /// </summary>
    public Task StreamVersionedDownloadAsync(
        string version,
        string assetName,
        HttpContext context,
        CancellationToken cancellationToken) =>
        StreamDownloadCoreAsync(assetName, version, context, cancellationToken);

    private async Task StreamDownloadCoreAsync(
        string platformOrAssetName,
        string? version,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        string assetName;
        ReleaseInfo? release;
        if (version is null)
        {
            if (!PlatformAssets.TryGetValue(platformOrAssetName, out assetName!))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.Headers["Cache-Control"] = "no-store";
                return;
            }

            release = await GetLatestReleaseAsync(cancellationToken);
        }
        else
        {
            // Only exact known artifact names and stable semantic versions are
            // accepted. This prevents arbitrary GitHub paths from entering the proxy.
            if (!Regex.IsMatch(version, @"^v[0-9]+\.[0-9]+\.[0-9]+$") ||
                !PlatformAssets.Values.Contains(platformOrAssetName, StringComparer.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.Headers["Cache-Control"] = "no-store";
                return;
            }

            assetName = platformOrAssetName;
            release = await GetReleaseByTagAsync(version, cancellationToken);
        }

        if (release is null || !release.Assets.TryGetValue(assetName, out var assetUri))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.Headers["Cache-Control"] = "no-store";
            return;
        }

        if (!TryGetRange(context.Request, out var range))
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers["Cache-Control"] = "no-store";
            return;
        }

        using var upstreamResponse = await SendAssetRequestAsync(
            assetUri, range, context.Request.Headers["If-Range"].ToString(), cancellationToken);

        if (upstreamResponse.StatusCode == HttpStatusCode.NotFound)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.Headers["Cache-Control"] = "no-store";
            return;
        }

        if (upstreamResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            CopyContentRange(upstreamResponse, context.Response);
            context.Response.Headers["Cache-Control"] = "no-store";
            return;
        }

        if (upstreamResponse.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            throw new HttpRequestException(
                $"The release asset provider returned HTTP {(int)upstreamResponse.StatusCode}.");
        }

        context.Response.StatusCode = (int)upstreamResponse.StatusCode;
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers["Content-Disposition"] = new ContentDispositionHeaderValue("attachment")
        {
            FileName = assetName,
            FileNameStar = assetName
        }.ToString();

        // Prevent browser and intermediary storage while allowing Cloudflare's
        // edge cache to retain only the version-pinned public release artifact.
        context.Response.Headers["Cache-Control"] = "private, no-store";
        if (version is not null)
        {
            context.Response.Headers["Cloudflare-CDN-Cache-Control"] = "public, max-age=31536000, immutable";
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Accept-Ranges"] =
            upstreamResponse.Headers.AcceptRanges.FirstOrDefault() ?? "bytes";

        if (upstreamResponse.Content.Headers.ContentLength is { } contentLength)
        {
            context.Response.ContentLength = contentLength;
        }

        CopyContentRange(upstreamResponse, context.Response);

        if (upstreamResponse.Content.Headers.LastModified is { } lastModified)
        {
            context.Response.Headers["Last-Modified"] = lastModified.ToString("R");
        }

        if (upstreamResponse.Headers.ETag is { } etag)
        {
            context.Response.Headers["ETag"] = etag.ToString();
        }

        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        await upstreamStream.CopyToAsync(context.Response.Body, StreamCopyBufferSize, cancellationToken);
    }

    private Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken) =>
        GetReleaseMetadataAsync(
            "latest",
            $"public-download-release:{_repository}:latest",
            ReleaseCacheDuration,
            expectedTagName: null,
            cancellationToken);

    private Task<ReleaseInfo?> GetReleaseByTagAsync(string version, CancellationToken cancellationToken) =>
        GetReleaseMetadataAsync(
            $"tags/{Uri.EscapeDataString(version)}",
            $"public-download-release:{_repository}:{version}",
            VersionedReleaseCacheDuration,
            expectedTagName: version,
            cancellationToken);

    private async Task<ReleaseInfo?> GetReleaseMetadataAsync(
        string releaseSelector,
        string cacheKey,
        TimeSpan cacheDuration,
        string? expectedTagName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_repository))
        {
            return null;
        }

        if (!Regex.IsMatch(_repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
        {
            throw new InvalidOperationException(
                "PublicDownloads:GitHubRepository must use the owner/repository format.");
        }

        if (cache.TryGetValue<CachedRelease>(cacheKey, out var cached))
        {
            return cached?.Release;
        }

        var apiUri = new Uri($"https://api.github.com/repos/{_repository}/releases/{releaseSelector}");
        using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        metadataTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await SendReleaseMetadataRequestAsync(apiUri, metadataTimeout.Token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            cache.Set(cacheKey, new CachedRelease(null), cacheDuration);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The release metadata provider returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(metadataTimeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: metadataTimeout.Token);
        var root = document.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagProperty)
            ? tagProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(tagName) ||
            !Regex.IsMatch(tagName, @"^v[0-9]+\.[0-9]+\.[0-9]+$") ||
            (expectedTagName is not null && !string.Equals(tagName, expectedTagName, StringComparison.Ordinal)))
        {
            cache.Set(cacheKey, new CachedRelease(null), cacheDuration);
            return null;
        }

        // GitHub may permanently move a renamed repository's /repos/{owner}/{repo}
        // API route to /repositories/{id}. The numeric endpoint's release URL is
        // authoritative for the canonical owner/repository used by download assets.
        var assetRepository = GetReleaseRepository(root, _repository);
        var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
        if (root.TryGetProperty("assets", out var assetsProperty) &&
            assetsProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsProperty.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameProperty) ||
                    nameProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameProperty.GetString();
                if (name is null || !PlatformAssets.Values.Contains(name, StringComparer.Ordinal) ||
                    !asset.TryGetProperty("browser_download_url", out var urlProperty) ||
                    urlProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (TryValidateAssetUri(urlProperty.GetString(), name, assetRepository, out var validatedUri))
                {
                    assets[name] = validatedUri;
                }
            }
        }

        var release = new ReleaseInfo(tagName, assets);
        cache.Set(cacheKey, new CachedRelease(release), cacheDuration);
        return release;
    }

    private async Task<HttpResponseMessage> SendReleaseMetadataRequestAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            if (!visited.Add(currentUri.AbsoluteUri))
            {
                throw new HttpRequestException("The release metadata redirect loop was detected.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is not (
                HttpStatusCode.MovedPermanently or
                HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();

            if (location is null ||
                !Uri.TryCreate(currentUri, location, out var nextUri) ||
                !IsAllowedMetadataRedirect(nextUri) ||
                redirectCount == MaximumRedirects)
            {
                throw new HttpRequestException(
                    "The release metadata redirect was invalid or exceeded its limit.");
            }

            currentUri = nextUri;
        }

        throw new HttpRequestException("The release metadata redirect limit was exceeded.");
    }

    private static bool IsAllowedMetadataRedirect(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IdnHost.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        Regex.IsMatch(uri.AbsolutePath, @"^/repositories/[0-9]+/releases/(?:latest|tags/v[0-9]+\.[0-9]+\.[0-9]+)$", RegexOptions.CultureInvariant);

    private static string GetReleaseRepository(JsonElement root, string fallback)
    {
        if (!root.TryGetProperty("url", out var urlProperty) ||
            urlProperty.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(urlProperty.GetString(), UriKind.Absolute, out var releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps ||
            !releaseUri.IdnHost.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
            !releaseUri.IsDefaultPort ||
            !string.IsNullOrEmpty(releaseUri.UserInfo) ||
            !string.IsNullOrEmpty(releaseUri.Query) ||
            !string.IsNullOrEmpty(releaseUri.Fragment))
        {
            return fallback;
        }

        var match = Regex.Match(
            releaseUri.AbsolutePath,
            @"^/repos/(?<repository>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)/releases/[0-9]+$",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["repository"].Value : fallback;
    }

    private bool TryValidateAssetUri(string? value, string assetName, string repository, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !candidate.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !candidate.IsDefaultPort ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        var path = Uri.UnescapeDataString(candidate.AbsolutePath);
        var expectedPrefix = $"/{repository}/releases/download/";
        if (!path.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            !path.EndsWith("/" + assetName, StringComparison.Ordinal))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private async Task<HttpResponseMessage> SendAssetRequestAsync(
        Uri initialUri,
        RangeHeaderValue? range,
        string ifRangeHeader,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (range is not null)
            {
                request.Headers.Range = range;
            }

            if (RangeConditionHeaderValue.TryParse(ifRangeHeader, out var ifRange))
            {
                request.Headers.IfRange = ifRange;
            }

            var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is not (
                HttpStatusCode.MovedPermanently or
                HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null ||
                !Uri.TryCreate(currentUri, location, out var nextUri) ||
                !IsAllowedRedirect(nextUri) ||
                redirectCount == MaximumRedirects)
            {
                throw new HttpRequestException("The release asset redirect was invalid or exceeded its limit.");
            }

            currentUri = nextUri;
        }

        throw new HttpRequestException("The release asset redirect limit was exceeded.");
    }

    private static bool IsAllowedRedirect(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool TryGetRange(HttpRequest request, out RangeHeaderValue? range)
    {
        range = null;
        var rangeHeader = request.Headers["Range"].ToString();
        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return true;
        }

        if (!RangeHeaderValue.TryParse(rangeHeader, out range) || range.Ranges.Count != 1)
        {
            return false;
        }

        return true;
    }

    private static void CopyContentRange(HttpResponseMessage upstream, HttpResponse response)
    {
        if (upstream.Content.Headers.ContentRange is { } contentRange)
        {
            response.Headers["Content-Range"] = contentRange.ToString();
        }
    }

    private sealed record ReleaseInfo(string TagName, IReadOnlyDictionary<string, Uri> Assets);
    private sealed record CachedRelease(ReleaseInfo? Release);
}
