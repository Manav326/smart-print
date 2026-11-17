using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Tests;

public sealed class PublicDownloadServiceTests
{
    private const string Repository = "Manav326/QRtoPrint";

    [Fact]
    public async Task StatusExposesOnlyExactAssetsFromLatestStableRelease()
    {
        var handler = new QueueMessageHandler(
            _ => JsonResponse("""
                {
                  "tag_name": "v1.2.3",
                  "assets": [
                    {
                      "name": "SmartPrint-Windows-Setup.exe",
                      "browser_download_url": "https://github.com/Manav326/QRtoPrint/releases/download/v1.2.3/SmartPrint-Windows-Setup.exe"
                    }
                  ]
                }
                """));

        var service = CreateService(handler);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal("v1.2.3", status.Version);
        Assert.True(status.WindowsAvailable);
        Assert.False(status.AndroidAvailable);
        Assert.Null(status.DownloadBaseUrl);
        Assert.Equal("https://api.github.com/repos/Manav326/QRtoPrint/releases/latest", handler.RequestedUris[0].AbsoluteUri);
    }

    [Fact]
    public async Task StatusExposesOnlyAValidatedHttpsDownloadOrigin()
    {
        var handler = new QueueMessageHandler(
            _ => JsonResponse("""
                {
                  "tag_name": "v1.2.3",
                  "assets": [
                    {
                      "name": "SmartPrint-Windows-Setup.exe",
                      "browser_download_url": "https://github.com/Manav326/QRtoPrint/releases/download/v1.2.3/SmartPrint-Windows-Setup.exe"
                    }
                  ]
                }
                """));

        var service = CreateService(handler, "https://downloads.smartprint.example");
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal("https://downloads.smartprint.example", status.DownloadBaseUrl);
    }

    [Theory]
    [InlineData("http://downloads.smartprint.example")]
    [InlineData("https://downloads.smartprint.example/path")]
    [InlineData("https://user:password@downloads.smartprint.example")]
    [InlineData("https://downloads.smartprint.example?token=secret")]
    public async Task StatusDisablesUnsafeConfiguredDownloadOriginsAndKeepsApiFallback(string downloadBaseUrl)
    {
        var handler = new QueueMessageHandler(
            _ => JsonResponse("""
                {
                  "tag_name": "v1.2.3",
                  "assets": []
                }
                """));

        var service = CreateService(handler, downloadBaseUrl);

        var status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Null(status.DownloadBaseUrl);
    }

    [Fact]
    public async Task FollowsGithubPermanentRepositoryMoveAndValidatesAssetsAgainstCanonicalRepository()
    {
        var handler = new QueueMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.MovedPermanently)
            {
                Headers = { Location = new Uri("https://api.github.com/repositories/1439868781/releases/latest") }
            },
            _ => JsonResponse("""
                {
                  "url": "https://api.github.com/repos/Manav326/smart-print/releases/437206158",
                  "tag_name": "v0.1.34",
                  "assets": [
                    {
                      "name": "SmartPrint-Windows-Setup.exe",
                      "browser_download_url": "https://github.com/Manav326/smart-print/releases/download/v0.1.34/SmartPrint-Windows-Setup.exe"
                    },
                    {
                      "name": "SmartPrint-Android.apk",
                      "browser_download_url": "https://github.com/Manav326/smart-print/releases/download/v0.1.34/SmartPrint-Android.apk"
                    }
                  ]
                }
                """));

        var status = await CreateService(handler).GetStatusAsync(CancellationToken.None);

        Assert.Equal("v0.1.34", status.Version);
        Assert.True(status.WindowsAvailable);
        Assert.True(status.AndroidAvailable);
        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.Equal(
            "https://api.github.com/repositories/1439868781/releases/latest",
            handler.RequestedUris[1].AbsoluteUri);
    }

    [Fact]
    public async Task RejectsReleaseMetadataRedirectOutsideGithubApi()
    {
        var handler = new QueueMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.MovedPermanently)
            {
                Headers = { Location = new Uri("https://example.invalid/releases/latest") }
            });

        var service = CreateService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetStatusAsync(CancellationToken.None));
        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task IgnoresReleaseAssetsOutsideTheConfiguredGithubRepository()
    {
        var handler = new QueueMessageHandler(
            _ => JsonResponse("""
                {
                  "tag_name": "v1.2.3",
                  "assets": [
                    {
                      "name": "SmartPrint-Windows-Setup.exe",
                      "browser_download_url": "https://example.invalid/SmartPrint-Windows-Setup.exe"
                    }
                  ]
                }
                """));

        var status = await CreateService(handler).GetStatusAsync(CancellationToken.None);

        Assert.False(status.WindowsAvailable);
        Assert.Equal("v1.2.3", status.Version);
    }

    [Fact]
    public async Task VersionedDownloadUsesExactTagAndCachesAtCloudflareWithoutBrowserCaching()
    {
        var fileBytes = Encoding.UTF8.GetBytes("immutable-installer");
        var handler = new QueueMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.MovedPermanently)
            {
                Headers = { Location = new Uri("https://api.github.com/repositories/1439868781/releases/tags/v1.2.3") }
            },
            _ => JsonResponse("""
                {
                  "url": "https://api.github.com/repos/Manav326/QRtoPrint/releases/437206158",
                  "tag_name": "v1.2.3",
                  "assets": [
                    {
                      "name": "SmartPrint-Windows-Setup.exe",
                      "browser_download_url": "https://github.com/Manav326/QRtoPrint/releases/download/v1.2.3/SmartPrint-Windows-Setup.exe"
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://release-assets.githubusercontent.com/signed-installer") }
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fileBytes)
            });

        var service = CreateService(handler);
        var context = new DefaultHttpContext();
        await using var output = new MemoryStream();
        context.Response.Body = output;

        await service.StreamVersionedDownloadAsync(
            "v1.2.3", "SmartPrint-Windows-Setup.exe", context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("private, no-store", context.Response.Headers["Cache-Control"].ToString());
        Assert.Equal(
            "public, max-age=31536000, immutable",
            context.Response.Headers["Cloudflare-CDN-Cache-Control"].ToString());
        Assert.Contains("SmartPrint-Windows-Setup.exe", context.Response.Headers["Content-Disposition"].ToString(), StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers["Location"].ToString()));
        Assert.Equal(fileBytes, output.ToArray());
        Assert.Equal(
            "https://api.github.com/repos/Manav326/QRtoPrint/releases/tags/v1.2.3",
            handler.RequestedUris[0].AbsoluteUri);
        Assert.Equal(
            "https://api.github.com/repositories/1439868781/releases/tags/v1.2.3",
            handler.RequestedUris[1].AbsoluteUri);
        Assert.Equal(4, handler.RequestedUris.Count);
    }

    [Fact]
    public async Task VersionedDownloadRejectsUnknownAssetOrNonStableVersionBeforeNetworkAccess()
    {
        var handler = new QueueMessageHandler();
        var service = CreateService(handler);

        var invalidVersionContext = new DefaultHttpContext();
        await service.StreamVersionedDownloadAsync(
            "latest", "SmartPrint-Windows-Setup.exe", invalidVersionContext, CancellationToken.None);

        var invalidAssetContext = new DefaultHttpContext();
        await service.StreamVersionedDownloadAsync(
            "v1.2.3", "other.exe", invalidAssetContext, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, invalidVersionContext.Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, invalidAssetContext.Response.StatusCode);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task VersionedDownloadDoesNotServeDifferentTagThanRequested()
    {
        var handler = new QueueMessageHandler(
            _ => JsonResponse("""
                {
                  "url": "https://api.github.com/repos/Manav326/QRtoPrint/releases/437206159",
                  "tag_name": "v1.2.4",
                  "assets": [
                    {
                      "name": "SmartPrint-Windows-Setup.exe",
                      "browser_download_url": "https://github.com/Manav326/QRtoPrint/releases/download/v1.2.4/SmartPrint-Windows-Setup.exe"
                    }
                  ]
                }
                """));

        var service = CreateService(handler);
        var context = new DefaultHttpContext();
        await using var output = new MemoryStream();
        context.Response.Body = output;

        await service.StreamVersionedDownloadAsync(
            "v1.2.3", "SmartPrint-Windows-Setup.exe", context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Empty(output.ToArray());
        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task StreamsReleaseAssetOnTheApplicationResponseWithoutRedirectingTheBrowser()
    {
        var fileBytes = Encoding.UTF8.GetBytes("installer-binary");
        var handler = new QueueMessageHandler(
            _ => JsonResponse("""
                {
                  "tag_name": "v1.2.3",
                  "assets": [
                    {
                      "name": "SmartPrint-Windows-Setup.exe",
                      "browser_download_url": "https://github.com/Manav326/QRtoPrint/releases/download/v1.2.3/SmartPrint-Windows-Setup.exe"
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://release-assets.githubusercontent.com/signed-installer") }
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fileBytes)
            });

        var service = CreateService(handler);
        var context = new DefaultHttpContext();
        await using var output = new MemoryStream();
        context.Response.Body = output;

        await service.StreamDownloadAsync("windows", context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("attachment", context.Response.Headers["Content-Disposition"].ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SmartPrint-Windows-Setup.exe", context.Response.Headers["Content-Disposition"].ToString(), StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers["Location"].ToString()));
        Assert.Equal(fileBytes, output.ToArray());
        Assert.Equal(3, handler.RequestedUris.Count);
        Assert.Equal("https://release-assets.githubusercontent.com/signed-installer", handler.RequestedUris[2].AbsoluteUri);
    }

    [Fact]
    public async Task StreamsSingleByteRangeAndForwardsRangeHeaderToGithub()
    {
        var handler = new QueueMessageHandler(
            _ => JsonResponse("""
                {
                  "tag_name": "v1.2.3",
                  "assets": [
                    {
                      "name": "SmartPrint-Windows-Setup.exe",
                      "browser_download_url": "https://github.com/Manav326/QRtoPrint/releases/download/v1.2.3/SmartPrint-Windows-Setup.exe"
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("part"u8.ToArray())
                {
                    Headers = { ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 3, 16) }
                }
            });

        var service = CreateService(handler);
        var context = new DefaultHttpContext();
        context.Request.Headers.Range = "bytes=0-3";
        await using var output = new MemoryStream();
        context.Response.Body = output;

        await service.StreamDownloadAsync("windows", context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes=0-3", handler.RangeHeaders[1]);
        Assert.Equal("bytes 0-3/16", context.Response.Headers["Content-Range"].ToString());
        Assert.Equal("part", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task MissingLatestReleaseDoesNotProduceADownload()
    {
        var handler = new QueueMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = CreateService(handler);
        var context = new DefaultHttpContext();
        await using var output = new MemoryStream();
        context.Response.Body = output;

        await service.StreamDownloadAsync("windows", context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Empty(output.ToArray());
        Assert.True(string.IsNullOrEmpty(context.Response.Headers["Location"].ToString()));
    }

    private static PublicDownloadService CreateService(
        QueueMessageHandler handler,
        string? downloadBaseUrl = null)
    {
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicDownloads:GitHubRepository"] = Repository,
                ["PublicDownloads:PublicBaseUrl"] = downloadBaseUrl
            })
            .Build();
        return new PublicDownloadService(client, configuration, new MemoryCache(new MemoryCacheOptions()));
    }

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private sealed class QueueMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<Uri> RequestedUris { get; } = [];
        public List<string?> RangeHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            RangeHeaders.Add(request.Headers.Range?.ToString());
            if (_index >= responses.Length)
            {
                throw new InvalidOperationException("No queued HTTP response is available.");
            }

            return Task.FromResult(responses[_index++](request));
        }
    }
}
