using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Migrations;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api.Tests;

public sealed class PrinterDiagnosticEndpointIntegrationTests
{
    [Fact]
    public async Task OfflineIdempotentRetryReturnsOriginalAttemptAndConcurrentConfirmationsCommitOnlyOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var fixture = await TestFixture.CreateAsync(connectionString);
        var seeded = await fixture.SeedDiagnosticAsync();

        // The printer and its Agent are deliberately offline. The request must
        // return the previously committed diagnostic before checking readiness.
        var retry = await fixture.InvokeStartAsync(seeded.PrinterId, "diagnostic-retry-123");
        Assert.Equal(StatusCodes.Status200OK, retry.StatusCode);
        using (var retryJson = JsonDocument.Parse(retry.Body))
        {
            Assert.True(retryJson.RootElement.GetProperty("duplicateRequest").GetBoolean());
            Assert.Equal(seeded.AttemptId, retryJson.RootElement.GetProperty("id").GetGuid());
        }

        // Put the same attempt into a terminal, fully printed state. Two separate
        // request scopes then race with different owner decisions.
        await using (var db = new SmartPrintDbContext(fixture.DbOptions))
        {
            var job = await db.PrintJobs.SingleAsync(item => item.Id == seeded.JobId);
            var now = DateTimeOffset.UtcNow;
            job.Status = "PRINTING";
            job.LocalQueueReceivedUtc = now.AddSeconds(-30);
            job.PrintDispatchAttempts = 1;
            job.SpoolerJobId = 123;
            job.SpoolerJobName = "SmartPrint-DIAG-INTEGRATION";
            job.SpoolerStatus = "PRINTED";
            job.SpoolerSubmittedUtc = now.AddSeconds(-20);
            job.SpoolerObservedUtc = now;
            job.SpoolerPagesPrinted = 1;
            job.SpoolerTotalPages = 1;
            job.UpdatedUtc = now;
            await db.SaveChangesAsync();
        }

        var confirmations = await Task.WhenAll(
            fixture.InvokeConfirmationAsync(seeded.AttemptId, "PRINTED_CORRECTLY"),
            fixture.InvokeConfirmationAsync(seeded.AttemptId, "PRINTED_WITH_ISSUE"));

        Assert.Single(confirmations, result => result.StatusCode == StatusCodes.Status200OK);
        Assert.Single(confirmations, result => result.StatusCode == StatusCodes.Status409Conflict);

        await using var verification = new SmartPrintDbContext(fixture.DbOptions);
        var savedAttempt = await verification.PrinterDiagnosticAttempts.SingleAsync(
            item => item.Id == seeded.AttemptId);
        var savedJob = await verification.PrintJobs.SingleAsync(item => item.Id == seeded.JobId);
        Assert.NotNull(savedAttempt.ConfirmedUtc);
        Assert.True(savedAttempt.OwnerDecision is "PRINTED_CORRECTLY" or "PRINTED_WITH_ISSUE");
        Assert.Equal(
            savedAttempt.OwnerDecision == "PRINTED_CORRECTLY" ? "DIAGNOSTIC_VERIFIED" : "DIAGNOSTIC_ISSUE",
            savedJob.Status);
        Assert.Equal(1, await verification.PrintJobEvents.CountAsync(
            item => item.PrintJobId == seeded.JobId));
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly string rootConnectionString;
        private readonly string schemaName;
        private readonly WebApplication app;
        private readonly RouteEndpoint startEndpoint;
        private readonly RouteEndpoint confirmationEndpoint;
        private Guid currentTenantId;
        private Guid currentUserId;

        public DbContextOptions<SmartPrintDbContext> DbOptions { get; }

        private TestFixture(
            string rootConnectionString,
            string schemaName,
            DbContextOptions<SmartPrintDbContext> dbOptions,
            WebApplication app,
            RouteEndpoint startEndpoint,
            RouteEndpoint confirmationEndpoint)
        {
            this.rootConnectionString = rootConnectionString;
            this.schemaName = schemaName;
            DbOptions = dbOptions;
            this.app = app;
            this.startEndpoint = startEndpoint;
            this.confirmationEndpoint = confirmationEndpoint;
        }

        public static async Task<TestFixture> CreateAsync(string rootConnectionString)
        {
            var schemaName = "printer_diag_" + Guid.NewGuid().ToString("N");
            await using (var root = new NpgsqlConnection(rootConnectionString))
            {
                await root.OpenAsync();
                await using var createSchema = root.CreateCommand();
                createSchema.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
                await createSchema.ExecuteNonQueryAsync();
            }

            var scopedConnection = new NpgsqlConnectionStringBuilder(rootConnectionString)
            {
                SearchPath = schemaName
            }.ConnectionString;
            var dbOptions = new DbContextOptionsBuilder<SmartPrintDbContext>()
                .UseNpgsql(scopedConnection, npgsql => npgsql.MigrationsAssembly(
                    typeof(InitialPlatform).Assembly.GetName().Name))
                .ReplaceService<IMigrationsIdGenerator, LegacyCompatibleMigrationsIdGenerator>()
                .Options;

            await using (var db = new SmartPrintDbContext(dbOptions))
                await db.Database.MigrateAsync();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDbContext<SmartPrintDbContext>(options =>
                options.UseNpgsql(scopedConnection, npgsql => npgsql.MigrationsAssembly(
                    typeof(InitialPlatform).Assembly.GetName().Name))
                    .ReplaceService<IMigrationsIdGenerator, LegacyCompatibleMigrationsIdGenerator>());
            builder.Services.AddSingleton<DocumentStorage>();

            var app = builder.Build();
            app.MapGroup("/api/v1/shop").MapPrinterDiagnosticEndpoints();
            await app.StartAsync();

            var endpoints = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .ToArray();
            static RouteEndpoint GetPostEndpoint(
                IEnumerable<RouteEndpoint> candidates,
                string routeSuffix)
            {
                var matches = candidates.Where(endpoint =>
                    endpoint.RoutePattern.RawText?.EndsWith(routeSuffix, StringComparison.Ordinal) == true &&
                    endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                        "POST", StringComparer.OrdinalIgnoreCase) == true).ToArray();
                return matches.Length == 1
                    ? matches[0]
                    : throw new InvalidOperationException(
                        $"Expected one POST endpoint ending in '{routeSuffix}', found {matches.Length}.");
            }

            var startEndpoint = GetPostEndpoint(
                endpoints, "/printers/{printerId:guid}/diagnostics");
            var confirmationEndpoint = GetPostEndpoint(
                endpoints, "/printer-diagnostics/{attemptId:guid}/confirm");

            return new TestFixture(
                rootConnectionString, schemaName, dbOptions, app,
                startEndpoint, confirmationEndpoint);
        }

        public async Task<(Guid TenantId, Guid UserId, Guid PrinterId, Guid JobId, Guid AttemptId)> SeedDiagnosticAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var tenant = new Tenant
            {
                Slug = "diagnostic-" + Guid.NewGuid().ToString("N")[..12],
                BusinessName = "Diagnostic integration shop"
            };
            var user = new AppUser
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UserName = "diagnostic-owner-" + Guid.NewGuid().ToString("N")[..8],
                NormalizedUserName = "DIAGNOSTIC-OWNER-" + Guid.NewGuid().ToString("N")[..8],
                DisplayName = "Diagnostic Shop Owner"
            };
            var agent = new AgentDevice
            {
                TenantId = tenant.Id,
                DeviceName = "Offline diagnostic Agent",
                FingerprintHash = new string('a', 64),
                SecretHash = new string('b', 64),
                Status = "Offline",
                LastSeenUtc = now.AddMinutes(-10)
            };
            var printer = new PrinterDevice
            {
                TenantId = tenant.Id,
                AgentDeviceId = agent.Id,
                DisplayName = "Offline diagnostic printer",
                Manufacturer = "Integration",
                Model = "Test Queue",
                ConnectionType = "WindowsQueue",
                OutputMode = PrinterTypePolicy.Physical,
                IdentityFingerprint = new string('c', 64),
                QueueName = "Diagnostic-Integration-Queue",
                CapabilitiesJson = "{}",
                Status = "Offline",
                IsActive = true,
                LastSeenUtc = now.AddMinutes(-10)
            };
            var job = new PrintJob
            {
                TenantId = tenant.Id,
                PrinterDeviceId = printer.Id,
                Origin = "PRINTER_DIAGNOSTIC",
                JobNumber = "D" + Guid.NewGuid().ToString("N")[..20],
                Status = "DIAGNOSTIC_QUEUED",
                IdempotencyKey = $"PD:{printer.Id:N}:diagnostic-retry-123",
                PageCount = 1,
                Copies = 1,
                ColourMode = "Mono",
                PaperSize = "A4",
                Currency = "INR",
                QuotedAmountMinor = 0,
                OutputType = PrinterTypePolicy.Physical,
                SpoolerStatus = "AWAITING_AGENT",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var attempt = new PrinterDiagnosticAttempt
            {
                TenantId = tenant.Id,
                PrinterDeviceId = printer.Id,
                AgentDeviceId = agent.Id,
                PrintJobId = job.Id,
                TemplateVersion = "SP-DIAG-1",
                CapabilitiesJson = "{}",
                CreatedUtc = now
            };

            await using var db = new SmartPrintDbContext(DbOptions);
            db.AddRange(tenant, user, agent, printer, job, attempt);
            await db.SaveChangesAsync();
            currentTenantId = tenant.Id;
            currentUserId = user.Id;
            return (tenant.Id, user.Id, printer.Id, job.Id, attempt.Id);
        }

        public Task<(int StatusCode, string Body)> InvokeStartAsync(Guid printerId, string idempotencyKey) =>
            InvokeAsync(
                startEndpoint,
                new Dictionary<string, string> { ["printerId"] = printerId.ToString("D") },
                new
                {
                    templateVersion = "SP-DIAG-1",
                    paperSize = "A4",
                    colourMode = "Mono",
                    isDuplex = false,
                    idempotencyKey
                });

        public Task<(int StatusCode, string Body)> InvokeConfirmationAsync(Guid attemptId, string decision) =>
            InvokeAsync(
                confirmationEndpoint,
                new Dictionary<string, string> { ["attemptId"] = attemptId.ToString("D") },
                new { decision, notes = (string?)null });

        private async Task<(int StatusCode, string Body)> InvokeAsync(
            RouteEndpoint endpoint,
            IReadOnlyDictionary<string, string> routeValues,
            object body)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext();
            context.RequestServices = scope.ServiceProvider;
            context.Request.Method = "POST";
            context.Request.ContentType = "application/json";
            context.Request.RouteValues = new RouteValueDictionary(
                routeValues.ToDictionary(pair => pair.Key, pair => (object?)pair.Value));
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
            context.Request.ContentLength = context.Request.Body.Length;
            context.Response.Body = new MemoryStream();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", currentTenantId.ToString("D")),
                new Claim(JwtRegisteredClaimNames.Sub, currentUserId.ToString("D")),
                new Claim("shop_role", "ShopOwner")
            ], "diagnostic-integration"));

            await (endpoint.RequestDelegate
                ?? throw new InvalidOperationException("The mapped diagnostic endpoint has no request delegate."))(context);
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            return ((int)context.Response.StatusCode, await reader.ReadToEndAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            await using var root = new NpgsqlConnection(rootConnectionString);
            await root.OpenAsync();
            await using var dropSchema = root.CreateCommand();
            dropSchema.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await dropSchema.ExecuteNonQueryAsync();
        }
    }
}
