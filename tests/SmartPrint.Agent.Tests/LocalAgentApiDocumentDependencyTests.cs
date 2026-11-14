using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class LocalAgentApiDocumentDependencyTests
{
    [Fact]
    public async Task LocalApiRouteResolvesThreatScannerAndAnalyzerFromItsOwnContainer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLocalAgentDocumentProcessingServices();

        await using var app = builder.Build();
        app.MapPost(
            "/api/v1/local/uploads/{ticketId:guid}",
            (
                Guid ticketId,
                HttpContext context,
                WindowsDefenderDocumentThreatScanner threatScanner,
                WindowsLocalDocumentAnalyzer documentAnalyzer,
                CancellationToken cancellationToken) =>
            {
                Assert.NotEqual(Guid.Empty, ticketId);
                Assert.NotNull(context);
                Assert.NotNull(threatScanner);
                Assert.NotNull(documentAnalyzer);
                Assert.False(cancellationToken.IsCancellationRequested);
                return Results.Ok();
            });

        await app.StartAsync();
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault();
            Assert.False(string.IsNullOrWhiteSpace(address));

            using var client = new HttpClient { BaseAddress = new Uri(address!) };
            using var response = await client.PostAsync(
                $"/api/v1/local/uploads/{Guid.NewGuid():D}",
                new StringContent(string.Empty));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
