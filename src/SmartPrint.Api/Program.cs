using SmartPrint.Api;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using static SmartPrint.Api.Security.EndpointIdentity;
using SmartPrint.Api.Storage;
using SmartPrint.Api.Services;
using SmartPrint.Documents;

const string bootstrapPlatformAdminArgument = "--bootstrap-platform-admin";
var bootstrapPlatformAdmin = args.Any(argument =>
    string.Equals(argument, bootstrapPlatformAdminArgument, StringComparison.OrdinalIgnoreCase));
var builderArguments = args
    .Where(argument => !string.Equals(argument, bootstrapPlatformAdminArgument, StringComparison.OrdinalIgnoreCase))
    .ToArray();
var builder = WebApplication.CreateBuilder(builderArguments);
var connectionString = builder.Configuration.GetConnectionString("SmartPrint")
    ?? (builder.Environment.IsDevelopment()
        ? "Host=localhost;Port=5432;Database=qrtoprint;Username=postgres;Password=postgres"
        : throw new InvalidOperationException("ConnectionStrings:SmartPrint is required outside Development."));
var signingKeyText = builder.Configuration["Authentication:SigningKey"]
    ?? (builder.Environment.IsDevelopment()
        ? "local-development-only-key-change-before-deploy-2026"
        : throw new InvalidOperationException("Authentication:SigningKey is required outside Development."));

if (Encoding.UTF8.GetByteCount(signingKeyText) < 32)
{
    throw new InvalidOperationException("Authentication:SigningKey must contain at least 32 UTF-8 bytes.");
}

var issuer = builder.Configuration["Authentication:Issuer"] ?? "QRtoPrint";
var audience = builder.Configuration["Authentication:Audience"] ?? "QRtoPrint.Clients";

var corsOrigins = builder.Services.AddApiServices(builder.Configuration, connectionString, signingKeyText, issuer, audience);

var app = builder.Build();

app.MapRazorpaySubscriptionWebhookEndpoint();
app.MapPublicAdvertisingEndpoints();

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
    await migrationDb.Database.MigrateAsync();
}

if (bootstrapPlatformAdmin)
{
    await PlatformAdminBootstrap.RunAsync(app.Services);
    return;
}

app.UseExceptionHandler();
app.UseCors("web");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Public downloads are proxied by the API and exposed only through the portal's
// same-origin /downloads routes. No upstream Location header reaches the browser.
app.MapGet("/downloads/status", async (
    PublicDownloadService downloads,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    try
    {
        return Results.Ok(await downloads.GetStatusAsync(cancellationToken));
    }
    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
        app.Logger.LogWarning(exception, "Public release availability lookup timed out.");
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Download availability is temporarily unavailable.");
    }
    catch (Exception exception) when (
        exception is HttpRequestException or JsonException or InvalidOperationException)
    {
        app.Logger.LogWarning(exception, "Public release availability could not be loaded.");
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Download availability is temporarily unavailable.");
    }
}).RequireRateLimiting("public-api");

app.MapGet("/downloads/{platform}", async (
    string platform,
    PublicDownloadService downloads,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        await downloads.StreamDownloadAsync(platform, context, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // The browser closed or cancelled the download.
    }
    catch (OperationCanceledException exception)
    {
        app.Logger.LogWarning(exception, "Public release lookup timed out for platform {Platform}.", platform);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Cache-Control"] = "no-store";
        }
        else
        {
            context.Abort();
        }
    }
    catch (Exception exception) when (
        exception is HttpRequestException or JsonException or InvalidOperationException)
    {
        app.Logger.LogWarning(exception, "Public download for platform {Platform} failed.", platform);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Cache-Control"] = "no-store";
        }
        else
        {
            context.Abort();
        }
    }
}).RequireRateLimiting("public-api");

app.MapGet("/health/live", () => Results.Ok(new
{
    service = "qrtoprint-api",
    status = "live",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0"
})).AllowAnonymous();

app.MapGet("/health/ready", async (SmartPrintDbContext db, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "ready", database = "connected" })
            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
    }
    catch
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
    }
}).AllowAnonymous();

app.MapGet("/", () => Results.Ok(new
{
    product = "QRtoPrint",
    name = "Smart Print API",
    apiVersion = "v1",
    health = "/health/live"
})).AllowAnonymous();

var platform = app.MapGroup("/api/v1/platform")
    .RequireAuthorization(PlatformAdminAuthorization.PolicyName);

platform.MapSubscriptionPlanEndpoints();
platform.MapPlatformSubscriptionEndpoints();
platform.MapPlatformOperationalSettingsEndpoints();
platform.MapPlatformAdvertisingEndpoints();

platform.MapPlatformOverviewEndpoints();
platform.MapPlatformTenantStatusEndpoints();

var auth = app.MapGroup("/api/v1/auth").RequireRateLimiting("authentication");
auth.MapAccountRegistrationEndpoints();
auth.MapAuthenticationSessionEndpoints();

app.MapCustomerEndpoints();
var shop = app.MapGroup("/api/v1/shop").RequireAuthorization()
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: true));

shop.MapShopSubscriptionPaymentEndpoints();

shop.MapGet("/subscription", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var subscription = await db.TenantSubscriptions.AsNoTracking()
        .Where(item => item.TenantId == tenantId)
        .OrderByDescending(item => item.CreatedUtc)
        .ThenByDescending(item => item.Id)
        .Select(item => new
        {
            item.Id,
            item.PlanCode,
            item.Status,
            item.BillingInterval,
            item.PeriodStartUtc,
            item.PeriodEndUtc,
            item.CreatedUtc,
            item.PlanVersionId
        })
        .FirstOrDefaultAsync(ct);
    if (subscription is null)
    {
        return Results.NotFound();
    }

    var plan = subscription.PlanVersionId is Guid planVersionId
        ? await (
            from version in db.SubscriptionPlanVersions.AsNoTracking()
            join definition in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals definition.Id
            where version.Id == planVersionId
            select new
            {
                version.Id,
                definition.Code,
                definition.Name,
                definition.Description,
                definition.IsActive,
                version.Version,
                version.Status,
                version.Currency,
                version.MonthlyPriceMinor,
                version.AnnualPriceMinor,
                version.MaxPrinters,
                version.MaxAgentDevices,
                version.MaxQrStations,
                version.MaxMonthlyPrintJobs,
                version.MaxDocumentBytes,
                version.CloudAssistedPrintingEnabled,
                version.WhiteLabelEnabled,
                version.AdvancedReportsEnabled,
                version.PublishedUtc
            })
            .SingleOrDefaultAsync(ct)
        : null;

    var entitlement = await entitlements.GetForTenantAsync(tenantId, ct);
    var now = DateTimeOffset.UtcNow;
    var periodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
    var periodEnd = periodStart.AddMonths(1);

    var printerCount = await db.PrinterDevices.AsNoTracking()
        .LongCountAsync(item => item.TenantId == tenantId, ct);
    var agentDeviceCount = await db.AgentDevices.AsNoTracking()
        .LongCountAsync(item => item.TenantId == tenantId &&
                                item.RevokedUtc == null && item.Status != "Revoked", ct);
    var qrStationCount = await db.QrStations.AsNoTracking()
        .LongCountAsync(item => item.TenantId == tenantId, ct);
    var monthlyPrintJobCount = await entitlements.GetMonthlyPrintJobUsageAsync(tenantId, ct);

    return Results.Ok(new
    {
        generatedUtc = now,
        subscription = new
        {
            subscription.Id,
            subscription.PlanCode,
            subscription.Status,
            subscription.BillingInterval,
            subscription.PeriodStartUtc,
            subscription.PeriodEndUtc,
            subscription.CreatedUtc
        },
        plan,
        entitlements = new
        {
            entitlement.MaxPrinters,
            entitlement.MaxAgentDevices,
            entitlement.MaxQrStations,
            entitlement.MaxMonthlyPrintJobs,
            entitlement.MaxDocumentBytes,
            entitlement.CloudAssistedPrintingEnabled,
            entitlement.WhiteLabelEnabled,
            entitlement.AdvancedReportsEnabled,
            entitlement.IsLegacyUnconfigured,
            entitlement.IsAccessActive
        },
        usage = new
        {
            printerCount,
            agentDeviceCount,
            qrStationCount,
            monthlyPrintJobCount,
            monthlyPeriodStartUtc = periodStart,
            monthlyPeriodEndUtc = periodEnd
        }
    });
});

shop.MapShopAccountEndpoints();

shop.MapGet("/printers", async (ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var onlineAfter = DateTimeOffset.UtcNow.AddMinutes(-2);
    var printers = await db.PrinterDevices.AsNoTracking()
        .Where(printer => printer.TenantId == tenantId)
        .OrderBy(printer => printer.DisplayName)
        .Select(printer => new
        {
            printer.Id,
            printer.DisplayName,
            printer.Manufacturer,
            printer.Model,
            printer.ConnectionType,
            printer.OutputMode,
            printer.ConnectionAddress,
            printer.QueueName,
            printer.IsDefault,
            printer.AgentDeviceId,
            printer.CapabilitiesJson,
            Status = printer.Status == "AgentRevoked"
                ? "AgentRevoked"
                : printer.AgentDeviceId != null &&
                  !db.AgentDevices.AsNoTracking().Any(agent =>
                      agent.Id == printer.AgentDeviceId && agent.TenantId == tenantId &&
                      agent.RevokedUtc == null && agent.Status != "Revoked" &&
                      agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter)
                    ? "AgentOffline"
                    : printer.AgentDeviceId != null && printer.OutputMode == "PDF_EXPORT"
                        ? "Ready"
                        : printer.AgentDeviceId != null &&
                          (printer.LastSeenUtc == null || printer.LastSeenUtc < onlineAfter)
                            ? "InventoryStale"
                            : printer.Status,
            printer.IsActive,
            printer.LastSeenUtc
        })
        .ToListAsync(ct);
    return Results.Ok(printers);
});

shop.MapPost("/printers", async (
    CreatePrinterRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var connectionTypes = new[] { "USB", "LAN", "IPP", "WindowsQueue" };
    if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 160 ||
        string.IsNullOrWhiteSpace(request.Manufacturer) || request.Manufacturer.Trim().Length > 80 ||
        string.IsNullOrWhiteSpace(request.Model) || request.Model.Trim().Length > 120 ||
        !connectionTypes.Contains(request.ConnectionType, StringComparer.OrdinalIgnoreCase))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printer"] = ["A name, manufacturer, model and supported connection type are required."]
        });
    }

    var capabilities = request.CapabilitiesJson ?? "{}";
    try
    {
        using var parsed = JsonDocument.Parse(capabilities);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object || capabilities.Length > 16000)
        {
            throw new JsonException();
        }
    }
    catch (JsonException)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["capabilitiesJson"] = ["Printer capabilities must be a valid JSON object under 16 KB."]
        });
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.Printer, ct);
    var quota = await entitlements.CheckCapacityAsync(tenantId, TenantQuotaResource.Printer, ct);
    if (!quota.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
    }

    var printer = new PrinterDevice
    {
        TenantId = tenantId,
        DisplayName = request.DisplayName.Trim(),
        Manufacturer = request.Manufacturer.Trim(),
        Model = request.Model.Trim(),
        ConnectionType = connectionTypes.Single(value => value.Equals(request.ConnectionType, StringComparison.OrdinalIgnoreCase)),
        ConnectionAddress = string.IsNullOrWhiteSpace(request.ConnectionAddress) ? null : request.ConnectionAddress.Trim(),
        QueueName = string.IsNullOrWhiteSpace(request.QueueName) ? null : request.QueueName.Trim(),
        CapabilitiesJson = capabilities,
        Status = "Unknown",
        IsActive = true
    };
    db.PrinterDevices.Add(printer);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Created($"/api/v1/shop/printers/{printer.Id}", new
    {
        printer.Id,
        printer.DisplayName,
        printer.Manufacturer,
        printer.Model,
        printer.ConnectionType,
        printer.OutputMode,
        printer.ConnectionAddress,
        printer.QueueName,
        printer.CapabilitiesJson,
        printer.Status,
        printer.IsActive
    });
});

shop.MapPatch("/printers/{printerId:guid}/active", async (
    Guid printerId,
    bool isActive,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var printer = await db.PrinterDevices.SingleOrDefaultAsync(x => x.Id == printerId && x.TenantId == tenantId, ct);
    if (printer is null)
    {
        return Results.NotFound();
    }

    if (!isActive)
    {
        var activeJobNumber = await db.PrintJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId &&
                          job.PrinterDeviceId == printerId &&
                          (job.Status == "LOCAL_QUEUE_CLAIMED" ||
                           job.Status == "LOCAL_QUEUE_READY" ||
                           job.Status == "PRINT_AUTHORISED" ||
                           job.Status == "PRINT_SUBMITTING" ||
                           job.Status == "SPOOLER_ACCEPTED" ||
                           job.Status == "PRINTING"))
            .OrderByDescending(job => job.CreatedUtc)
            .Select(job => job.JobNumber)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(activeJobNumber))
        {
            return Results.Conflict(new
            {
                code = "printer_has_active_output",
                message = $"Printer cannot be disabled while job {activeJobNumber} is active in the Windows Agent or spooler. Resolve that same job first; disabling it does not cancel Windows output."
            });
        }
    }

    printer.IsActive = isActive;
    printer.UpdatedUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { printer.Id, printer.IsActive, printer.UpdatedUtc });
});

shop.MapGet("/qr-stations", async (ClaimsPrincipal principal, SmartPrintDbContext db, IConfiguration configuration, CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var stations = await db.QrStations.AsNoTracking()
        .Where(station => station.TenantId == tenantId)
        .OrderBy(station => station.DisplayName)
        .Select(station => new
        {
            station.Id,
            station.PrinterDeviceId,
            station.DisplayName,
            station.PublicCodeHint,
            station.PublicCode,
            station.IsEnabled,
            station.CreatedUtc
        })
        .ToListAsync(ct);
    var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
    return Results.Ok(stations.Select(station => new
    {
        station.Id,
        station.PrinterDeviceId,
        station.DisplayName,
        station.PublicCodeHint,
        station.IsEnabled,
        station.CreatedUtc,
        PublicUrl = station.PublicCode is not null ? $"{portalBase}/s/{station.PublicCode}" : null
    }));
});

shop.MapPost("/qr-stations", async (
    CreateQrStationRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    IConfiguration configuration,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
    if (!Uri.TryCreate(portalBase, UriKind.Absolute, out var portalUri) ||
        portalUri.Scheme != Uri.UriSchemeHttps || portalUri.AbsolutePath != "/" ||
        !string.IsNullOrEmpty(portalUri.Query) || !string.IsNullOrEmpty(portalUri.Fragment) ||
        !string.IsNullOrEmpty(portalUri.UserInfo))
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The canonical public HTTPS URL is not configured.");
    }

    if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 120)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["displayName"] = ["A station name of up to 120 characters is required."]
        });
    }

    if (request.PrinterDeviceId is Guid printerId)
    {
        var selectedPrinterOutputMode = await db.PrinterDevices.AsNoTracking()
            .Where(printer => printer.Id == printerId && printer.TenantId == tenantId && printer.IsActive)
            .Select(printer => printer.OutputMode)
            .SingleOrDefaultAsync(ct);
        if (selectedPrinterOutputMode is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["printerDeviceId"] = ["Select an active printer that belongs to this shop."]
            });
        }
        if (selectedPrinterOutputMode is not ("PHYSICAL" or "PDF_EXPORT"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["printerDeviceId"] = ["This virtual/document driver is not supported for SmartPrint output. Select a physical printer or Microsoft Print to PDF for non-production testing."]
            });
        }
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.QrStation, ct);
    var quota = await entitlements.CheckCapacityAsync(tenantId, TenantQuotaResource.QrStation, ct);
    if (!quota.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
    }

    var publicCode = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
    var station = new QrStation
    {
        TenantId = tenantId,
        PrinterDeviceId = request.PrinterDeviceId,
        DisplayName = request.DisplayName.Trim(),
        PublicCodeHash = HashOpaqueToken(publicCode),
        PublicCode = publicCode,
        PublicCodeHint = publicCode[^4..]
    };
    db.QrStations.Add(station);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    return Results.Created($"/api/v1/shop/qr-stations/{station.Id}", new
    {
        station.Id,
        station.DisplayName,
        station.PrinterDeviceId,
        station.PublicCodeHint,
        station.IsEnabled,
        PublicUrl = $"{portalBase}/s/{publicCode}"
    });
});

shop.MapPost("/qr-stations/{stationId:guid}/rotate-code", async (
    Guid stationId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
    if (!Uri.TryCreate(portalBase, UriKind.Absolute, out var portalUri) ||
        portalUri.Scheme != Uri.UriSchemeHttps || portalUri.AbsolutePath != "/" ||
        !string.IsNullOrEmpty(portalUri.Query) || !string.IsNullOrEmpty(portalUri.Fragment) ||
        !string.IsNullOrEmpty(portalUri.UserInfo))
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The canonical public HTTPS URL is not configured.");
    }

    var station = await db.QrStations.SingleOrDefaultAsync(
        item => item.Id == stationId && item.TenantId == tenantId, ct);
    if (station is null)
    {
        return Results.NotFound();
    }

    var publicCode = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
    station.PublicCodeHash = HashOpaqueToken(publicCode);
    station.PublicCode = publicCode;
    station.PublicCodeHint = publicCode[^4..];
    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        station.Id,
        station.PublicCodeHint,
        PublicUrl = $"{portalBase}/s/{publicCode}"
    });
});

shop.MapPatch("/qr-stations/{stationId:guid}/enabled", async (
    Guid stationId,
    bool isEnabled,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var station = await db.QrStations.SingleOrDefaultAsync(
        item => item.Id == stationId && item.TenantId == tenantId, ct);
    if (station is null)
    {
        return Results.NotFound();
    }

    station.IsEnabled = isEnabled;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { station.Id, station.IsEnabled });
});


shop.MapGet("/pricing", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var pricing = await db.ShopPricingProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
    return pricing is null
        ? Results.NotFound()
        : Results.Ok(new
        {
            pricing.IsConfigured,
            pricing.MonoPerSideMinor,
            pricing.ColourPerSideMinor,
            pricing.MonoDuplexPerSheetMinor,
            pricing.ColourDuplexPerSheetMinor,
            pricing.MinimumChargeMinor,
            pricing.Photo35x45Minor,
            pricing.Photo25x35Minor,
            pricing.Photo2x2Minor,
            pricing.Photo4x6Minor,
            pricing.Version,
            pricing.UpdatedUtc
        });
});

shop.MapPut("/pricing", async (
    ShopPricingRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    const long maximumRateMinor = 100_000;
    const long maximumMinimumChargeMinor = 10_000_000;
    var rates = new[]
    {
        request.MonoPerSideMinor,
        request.ColourPerSideMinor,
        request.MonoDuplexPerSheetMinor,
        request.ColourDuplexPerSheetMinor,
        request.Photo35x45Minor,
        request.Photo25x35Minor,
        request.Photo2x2Minor,
        request.Photo4x6Minor
    };
    if (rates.Any(amount => amount < 0 || amount > maximumRateMinor) ||
        request.MinimumChargeMinor < 0 || request.MinimumChargeMinor > maximumMinimumChargeMinor ||
        request.MonoPerSideMinor == 0 || request.ColourPerSideMinor == 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["pricing"] = ["Set positive mono and colour per-side prices, non-negative duplex prices and a valid minimum charge. Prices use paise for INR."]
        });
    }

    var pricing = await db.ShopPricingProfiles.SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
    if (pricing is null)
    {
        return Results.NotFound();
    }

    pricing.MonoPerSideMinor = request.MonoPerSideMinor;
    pricing.ColourPerSideMinor = request.ColourPerSideMinor;
    pricing.MonoDuplexPerSheetMinor = request.MonoDuplexPerSheetMinor;
    pricing.ColourDuplexPerSheetMinor = request.ColourDuplexPerSheetMinor;
    pricing.MinimumChargeMinor = request.MinimumChargeMinor;
    pricing.Photo35x45Minor = request.Photo35x45Minor;
    pricing.Photo25x35Minor = request.Photo25x35Minor;
    pricing.Photo2x2Minor = request.Photo2x2Minor;
    pricing.Photo4x6Minor = request.Photo4x6Minor;
    pricing.IsConfigured = true;
    pricing.Version++;
    pricing.UpdatedUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        pricing.IsConfigured,
        pricing.MonoPerSideMinor,
        pricing.ColourPerSideMinor,
        pricing.MonoDuplexPerSheetMinor,
        pricing.ColourDuplexPerSheetMinor,
        pricing.MinimumChargeMinor,
            pricing.Photo35x45Minor,
            pricing.Photo25x35Minor,
            pricing.Photo2x2Minor,
            pricing.Photo4x6Minor,
        pricing.Version,
        pricing.UpdatedUtc
    });
});

shop.MapGet("/customers", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    string? search,
    int? page,
    int? pageSize,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    if (search is not null && search.Trim().Length > 100)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["search"] = ["Search must be 100 characters or fewer."]
        });
    }

    var pageNumber = Math.Clamp(page ?? 1, 1, 10_000);
    var boundedPageSize = Math.Clamp(pageSize ?? 20, 1, 50);
    var customerSessions =
        from session in db.CustomerPrintSessions.AsNoTracking()
        join user in db.Users.AsNoTracking()
            on session.CustomerUserId equals (Guid?)user.Id
        where session.TenantId == tenantId && user.TenantId == null
        select new
        {
            CustomerId = user.Id,
            user.DisplayName,
            user.Email,
            session.CreatedUtc
        };

    if (!string.IsNullOrWhiteSpace(search))
    {
        var pattern = $"%{search.Trim()}%";
        customerSessions = customerSessions.Where(item =>
            EF.Functions.ILike(item.DisplayName, pattern) ||
            (item.Email != null && EF.Functions.ILike(item.Email, pattern)));
    }

    var groupedCustomers = customerSessions
        .GroupBy(item => new { item.CustomerId, item.DisplayName, item.Email })
        .Select(group => new
        {
            group.Key.CustomerId,
            group.Key.DisplayName,
            group.Key.Email,
            FirstSeenUtc = group.Min(item => item.CreatedUtc),
            LastSeenUtc = group.Max(item => item.CreatedUtc),
            SessionCount = group.Count()
        });

    var totalCount = await groupedCustomers.CountAsync(ct);
    var customers = await groupedCustomers
        .OrderByDescending(item => item.LastSeenUtc)
        .ThenBy(item => item.DisplayName)
        .Skip((pageNumber - 1) * boundedPageSize)
        .Take(boundedPageSize)
        .ToListAsync(ct);

    var customerIds = customers.Select(item => item.CustomerId).ToArray();
    var jobStatistics = await (
        from job in db.PrintJobs.AsNoTracking()
        where job.TenantId == tenantId &&
              ((job.CustomerUserId != null && customerIds.Contains(job.CustomerUserId.Value)) ||
               (job.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                   session.TenantId == tenantId &&
                   session.Id == job.CustomerSessionId &&
                   session.CustomerUserId != null &&
                   customerIds.Contains(session.CustomerUserId.Value))))
        select new
        {
            CustomerId = job.CustomerUserId ?? db.CustomerPrintSessions
                .Where(session => session.TenantId == tenantId && session.Id == job.CustomerSessionId)
                .Select(session => session.CustomerUserId)
                .FirstOrDefault(),
            job.Status,
            job.OutputType,
            job.OutputCompletedUtc,
            job.OutputConfirmedUtc,
            job.CreatedUtc
        } into customerJob
        where customerJob.CustomerId != null
        group customerJob by customerJob.CustomerId!.Value into customerJobs
        select new
        {
            CustomerId = customerJobs.Key,
            PrintJobCount = customerJobs.Count(),
            CompletedJobCount = customerJobs.Count(job =>
                job.Status == "COMPLETED" &&
                    (job.OutputType == "PDF_EXPORT"
                        ? job.OutputCompletedUtc != null
                        : job.OutputConfirmedUtc != null)),
            LastPrintUtc = customerJobs.Max(job => job.CreatedUtc)
        }).ToListAsync(ct);

    var statisticsByCustomer = jobStatistics.ToDictionary(item => item.CustomerId);
    return Results.Ok(new
    {
        page = pageNumber,
        pageSize = boundedPageSize,
        totalCount,
        items = customers.Select(customer =>
        {
            statisticsByCustomer.TryGetValue(customer.CustomerId, out var statistics);
            return new
            {
                id = customer.CustomerId,
                customer.DisplayName,
                MaskedEmail = CustomerDirectoryRules.MaskEmail(customer.Email),
                customer.FirstSeenUtc,
                customer.LastSeenUtc,
                customer.SessionCount,
                PrintJobCount = statistics?.PrintJobCount ?? 0,
                CompletedJobCount = statistics?.CompletedJobCount ?? 0,
                LastPrintUtc = statistics?.LastPrintUtc
            };
        }).ToList()
    });
});

shop.MapGet("/customers/{customerId:guid}/jobs", async (
    Guid customerId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    int? take,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var customer = await db.Users.AsNoTracking()
        .Where(user => user.Id == customerId &&
                       user.TenantId == null &&
                       db.CustomerPrintSessions.Any(session =>
                           session.TenantId == tenantId && session.CustomerUserId == user.Id))
        .Select(user => new
        {
            user.Id,
            user.DisplayName,
            user.Email
        })
        .SingleOrDefaultAsync(ct);

    if (customer is null)
    {
        return Results.NotFound();
    }

    var boundedTake = Math.Clamp(take ?? 50, 1, 100);
    var customerJobs =
        from job in db.PrintJobs.AsNoTracking()
        join document in db.PrintDocuments.AsNoTracking()
            on new { job.TenantId, PrintJobId = job.Id }
            equals new { document.TenantId, document.PrintJobId }
        where job.TenantId == tenantId &&
              (job.CustomerUserId == customerId ||
               (job.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                   session.TenantId == tenantId &&
                   session.Id == job.CustomerSessionId &&
                   session.CustomerUserId == customerId)))
        select new
        {
            job.Id,
            job.PrivatePrinting,
            job.JobNumber,
            job.Status,
            job.CreatedUtc,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.PrintKind,
            job.PhotoSizeCode,
            job.PhotoCount,
        job.PhotosPerPage,
            job.PhotoUnitPriceMinorSnapshot,
            job.Currency,
            job.QuotedAmountMinor,
            job.RejectedPageNumbersJson,
            job.OutputType,
            job.OutputConfirmedUtc,
            job.OutputCompletedUtc,
            Document = new
            {
                OriginalFileName = PrivatePrintingRules.SafeDocumentLabel(job.PrivatePrinting, document.OriginalFileName),
                MediaType = job.PrivatePrinting ? "application/octet-stream" : document.MediaType,
                SizeBytes = job.PrivatePrinting ? 0 : document.SizeBytes
            },
            Payment = db.PrintPayments.AsNoTracking()
                .Where(payment => payment.TenantId == tenantId && payment.PrintJobId == job.Id)
                .OrderByDescending(payment => payment.CreatedUtc)
                .Select(payment => new
                {
                    payment.Status,
                    payment.Method,
                    payment.AmountMinor,
                    payment.RefundedAmountMinor,
                    payment.Currency,
                    payment.VerifiedUtc
                })
                .FirstOrDefault()
        };

    var totalJobs = await customerJobs.CountAsync(ct);
    var jobs = await customerJobs
        .OrderByDescending(item => item.CreatedUtc)
        .Take(boundedTake)
        .ToListAsync(ct);

    return Results.Ok(new
    {
        customer = new
        {
            customer.Id,
            customer.DisplayName,
            MaskedEmail = CustomerDirectoryRules.MaskEmail(customer.Email)
        },
        totalJobs,
        jobs
    });
});

shop.MapGet("/print-jobs", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    int? offset,
    int? take,
    string? search,
    Guid? batchId,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    // Keep paging bounded; the shop UI uses 100-row pages and an exact batch
    // lookup so a filtered queue can never silently omit sibling files.
    var boundedOffset = Math.Clamp(offset ?? 0, 0, 1_000_000);
    var boundedTake = Math.Clamp(take ?? 100, 1, 100);
    var searchTerm = search?.Trim();
    if (searchTerm is { Length: > 120 }) searchTerm = searchTerm[..120];

    var query =
        from job in db.PrintJobs.AsNoTracking()
        join document in db.PrintDocuments.AsNoTracking()
            on new { job.TenantId, PrintJobId = job.Id } equals new { document.TenantId, document.PrintJobId }
        where job.TenantId == tenantId
        orderby job.CreatedUtc descending
        select new
        {
            job.Id,
            job.BatchId,
            job.BatchGroupName,
            job.BatchName,
            job.RejectedPageNumbersJson,
            job.PdfBrandingEnabled,
            job.PrivatePrinting,
            job.Origin,
            job.CustomerUserId,
            job.RecipientName,
            job.RecipientPhone,
            job.JobNumber,
            job.Status,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.Orientation,
            job.PricingVersion,
            job.PrintKind,
            job.PhotoSizeCode,
            job.PhotoCount,
        job.PhotosPerPage,
            job.PhotoUnitPriceMinorSnapshot,
            job.Currency,
            job.QuotedAmountMinor,
            job.QuoteExpiresUtc,
            job.CreatedUtc,
            job.PrinterDeviceId,
            PrinterName = job.PrinterNameSnapshot ?? db.PrinterDevices
                .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                .Select(printer => printer.DisplayName)
                .FirstOrDefault(),
            QueueName = job.PrinterQueueNameSnapshot ?? db.PrinterDevices
                .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                .Select(printer => printer.QueueName)
                .FirstOrDefault(),
            PrinterAgentName = job.PrinterAgentNameSnapshot ?? db.AgentDevices
                .Where(agent => agent.TenantId == tenantId &&
                    agent.Id == db.PrinterDevices
                        .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                        .Select(printer => printer.AgentDeviceId)
                        .FirstOrDefault())
                .Select(agent => agent.DeviceName)
                .FirstOrDefault(),
            job.PrinterSnapshotUtc,
            job.ClaimedByAgentDeviceId,
            job.AgentLeaseExpiresUtc,
            job.LocalQueueReceivedUtc,
            job.PrintDispatchLeaseExpiresUtc,
            job.SpoolerJobId,
            job.SpoolerJobName,
            job.SpoolerStatus,
            job.SpoolerSubmittedUtc,
            job.SpoolerObservedUtc,
            job.OutputConfirmedUtc,
            OutputConfirmedByName = db.PrintJobEvents.AsNoTracking()
                .Where(item => item.TenantId == tenantId &&
                               item.PrintJobId == job.Id &&
                               item.ReasonCode == "SHOP_OPERATOR_CONFIRMED_PHYSICAL_OUTPUT")
                .OrderByDescending(item => item.CreatedUtc)
                .Select(item => db.Users
                    .Where(user => user.TenantId == tenantId &&
                                   item.ActorId != null &&
                                   user.Id == item.ActorId.Value)
                    .Select(user => user.DisplayName)
                    .FirstOrDefault())
                .FirstOrDefault(),
            job.FailureCode,
            OutputMode = db.PrinterDevices
                .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                .Select(printer => printer.OutputMode)
                .FirstOrDefault(),
            job.OutputType,
            job.OutputFilePath,
            job.OutputFileName,
            job.OutputSha256,
            job.OutputSizeBytes,
            job.OutputCompletedUtc,
            StorageMode = document.StorageMode,
            PhotoSourceAvailable = !job.PrivatePrinting &&
                (document.PhotoSourceMediaType == "image/jpeg" ||
                 document.PhotoSourceMediaType == "image/png" ||
                 (document.PhotoSourceMediaType == null &&
                  (document.MediaType == "image/jpeg" || document.MediaType == "image/png"))),
            PhotoEditorSettingsJson = job.PrivatePrinting ? null : document.PhotoEditorSettingsJson,
            DocumentAgentDeviceId = document.AgentDeviceId,
            DocumentAgentDeviceName = db.AgentDevices
                .Where(agent => agent.TenantId == tenantId && agent.Id == document.AgentDeviceId)
                .Select(agent => agent.DeviceName)
                .FirstOrDefault(),
            Payment = db.PrintPayments.AsNoTracking()
                .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id)
                .OrderByDescending(payment => payment.CreatedUtc)
                .Select(payment => new
                {
                    payment.Id,
                    payment.Status,
                    payment.AmountMinor,
                    payment.Currency,
                    payment.Method
                })
                .FirstOrDefault(),
            Document = new
            {
                OriginalFileName = PrivatePrintingRules.SafeDocumentLabel(job.PrivatePrinting, document.OriginalFileName),
                MediaType = job.PrivatePrinting ? "application/octet-stream" : document.MediaType,
                SizeBytes = job.PrivatePrinting ? 0 : document.SizeBytes
            }
        };

    if (batchId.HasValue)
    {
        query = query.Where(job => job.BatchId == batchId.Value);
    }

    if (!string.IsNullOrWhiteSpace(searchTerm))
    {
        var normalizedSearch = searchTerm.ToLowerInvariant();
        query = query.Where(job =>
            job.JobNumber.ToLower().Contains(normalizedSearch) ||
            (job.BatchName != null && job.BatchName.ToLower().Contains(normalizedSearch)) ||
            (job.BatchGroupName != null && job.BatchGroupName.ToLower().Contains(normalizedSearch)) ||
            (job.RecipientName != null && job.RecipientName.ToLower().Contains(normalizedSearch)) ||
            (job.RecipientPhone != null && job.RecipientPhone.ToLower().Contains(normalizedSearch)) ||
            (!job.PrivatePrinting && job.Document.OriginalFileName.ToLower().Contains(normalizedSearch)) ||
            (job.PrinterName != null && job.PrinterName.ToLower().Contains(normalizedSearch)));
    }

    var jobs = await query
        .OrderByDescending(job => job.CreatedUtc)
        .ThenByDescending(job => job.Id)
        .Skip(boundedOffset)
        .Take(boundedTake)
        .ToListAsync(ct);
    return Results.Ok(jobs);
});




shop.MapPost("/photo-jobs", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    DocumentStorage storage,
    TenantEntitlementService entitlements,
    HttpContext context,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
        return Results.Forbid();

    if (!context.Request.HasFormContentType)
        return Results.BadRequest(new { code = "photo_order_form_required", message = "Select a photo, prepare its A4 sheet and save the photo order." });

    var form = await context.Request.ReadFormAsync(ct);
    if (bool.TryParse(form["privatePrinting"].FirstOrDefault(), out var privatePrintingRequested) && privatePrintingRequested)
    {
        return Results.Conflict(new
        {
            code = "private_printing_requires_local_agent",
            message = "Private Printing must be transferred directly to the paired shop computer. Cloud document storage and cloud recovery are disabled for this request."
        });
    }
    if (form.Files.Count != 1)
        return Results.BadRequest(new { code = "one_photo_sheet_required", message = "Save exactly one prepared multi-page A4 photo PDF." });

    if (!Guid.TryParse(form["idempotencyKey"].FirstOrDefault(), out var requestKey))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["idempotencyKey"] = ["Refresh this form and retry with its original request key."]
        });
    var idempotencyKey = $"SHOP-PHOTO-{requestKey:N}";
    var paymentMethod = form["paymentMethod"].FirstOrDefault();
    if (paymentMethod is not ("UPI" or "CASH"))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["paymentMethod"] = ["Choose UPI or cash at counter."]
        });

    var photoSizeCode = form["photoSizeCode"].FirstOrDefault() ?? string.Empty;
    if (photoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6") ||
        !int.TryParse(form["photoCount"].FirstOrDefault(), out var photoCount) ||
        photoCount is < 1 or > UploadLimits.MaxPhotoCount ||
        !int.TryParse(form["photosPerPage"].FirstOrDefault(), out var photosPerPage) ||
        photosPerPage < 1 ||
        photosPerPage > PricingCalculator.MaximumPhotosPerA4Sheet(photoSizeCode))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["photoDetails"] = [$"Choose a supported photo size, 1–{UploadLimits.MaxPhotoCount} photos and a valid photos-per-page layout."]
        });
    }

    var recipientType = form["recipientType"].FirstOrDefault();
    Guid? customerUserId = null;
    string recipientName;
    string? recipientPhone = null;

    if (recipientType == "EXISTING_CUSTOMER")
    {
        if (!Guid.TryParse(form["customerUserId"].FirstOrDefault(), out var selectedCustomerId))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["customerUserId"] = ["Choose a customer from this shop's customer list."]
            });

        var customer = await db.Users.AsNoTracking()
            .Where(user => user.Id == selectedCustomerId &&
                           user.TenantId == null &&
                           db.CustomerPrintSessions.Any(session =>
                               session.TenantId == tenantId && session.CustomerUserId == user.Id))
            .Select(user => new { user.Id, user.DisplayName, user.PhoneNumber })
            .SingleOrDefaultAsync(ct);

        if (customer is null)
            return Results.NotFound(new { code = "shop_customer_not_found", message = "That customer is not associated with this shop. Refresh the customer list and choose again." });

        customerUserId = customer.Id;
        recipientName = customer.DisplayName.Trim();
        recipientPhone = string.IsNullOrWhiteSpace(customer.PhoneNumber) ? null : customer.PhoneNumber.Trim();
    }
    else if (recipientType == "WALK_IN")
    {
        recipientName = form["recipientName"].FirstOrDefault()?.Trim() ?? string.Empty;
        recipientPhone = form["recipientPhone"].FirstOrDefault()?.Trim();
        if (recipientName.Length is < 1 or > 160)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["recipientName"] = ["Enter the walk-in customer's name (up to 160 characters)."]
            });
        if (string.IsNullOrWhiteSpace(recipientPhone)) recipientPhone = null;
        if (recipientPhone is { Length: > 40 } ||
            recipientPhone is not null && recipientPhone.Any(character =>
                !char.IsDigit(character) && character is not ('+' or '(' or ')' or '-' or ' ')))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["recipientPhone"] = ["Enter a valid contact number using digits and common phone punctuation, or leave it blank."]
            });
    }
    else
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["recipientType"] = ["Choose an existing customer or a walk-in customer."]
        });
    }

    var upload = form.Files[0];
    if (upload.Length is <= 0 or > UploadLimits.MaxFileBytes)
    {
        return Results.UnprocessableEntity(new
        {
            code = "photo_sheet_invalid",
            message = $"The prepared photo sheet must be a PDF up to {UploadLimits.MaxFileBytes / (1024 * 1024)} MB."
        });
    }

    var expectedPageCount = PricingCalculator.PhotoPageCount(photoCount, photoSizeCode, photosPerPage);
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    StoredDocument? stored = null;

    async Task CleanupUncommittedStorageAsync()
    {
        if (stored is null) return;
        var storageKey = stored.StorageKey;
        bool referencedByCommittedDocument;
        try
        {
            referencedByCommittedDocument = await db.PrintDocuments.AsNoTracking()
                .AnyAsync(item => item.TenantId == tenantId && item.StorageKey == storageKey, ct);
        }
        catch
        {
            // A database outcome that cannot be checked is uncertain. Retain the
            // encrypted file rather than risk deleting a committed print document.
            return;
        }

        if (!referencedByCommittedDocument)
            await storage.DeleteAsync(storageKey);
        stored = null;
    }

    try
    {
        var now = DateTimeOffset.UtcNow;
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var jobNumber = $"SP-{now:yyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var outputName = $"photo-sheet-{jobNumber}.pdf";

        try
        {
            await using var input = upload.OpenReadStream();
            stored = await storage.ValidateAndStoreAsync(tenantId, documentId, outputName, input, upload.Length, ct);
        }
        catch (DocumentValidationException exception)
        {
            await transaction.RollbackAsync(ct);
            return Results.UnprocessableEntity(new { code = "photo_sheet_not_supported", message = exception.Message });
        }

        if (stored.Analysis.PageCount != expectedPageCount ||
            !PhotoSheetPdfRules.HasExactlyA4PortraitPages(stored.Analysis.PageSizesJson, expectedPageCount))
        {
            await storage.DeleteAsync(stored.StorageKey);
            stored = null;
            await transaction.RollbackAsync(ct);
            return Results.UnprocessableEntity(new
            {
                code = "photo_sheet_layout_invalid",
                message = $"The prepared file must contain exactly {expectedPageCount} portrait A4 page(s), each with one full-page 2480 × 3508 raster. Check the sheet preview and prepare it again."
            });
        }

        var existingJob = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.IdempotencyKey == idempotencyKey, ct);
        if (existingJob is not null)
        {
            var existingDocument = await db.PrintDocuments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.PrintJobId == existingJob.Id, ct);
            var existingPayment = await db.PrintPayments.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.PrintJobId == existingJob.Id)
                .OrderByDescending(item => item.CreatedUtc)
                .Select(item => new { item.Id, item.Method, item.Status, item.AmountMinor, item.Currency, item.UpiIdSnapshot, item.PayeeNameSnapshot })
                .FirstOrDefaultAsync(ct);
            var isSameRequest = existingDocument is not null && existingPayment is not null &&
                existingPayment.Method == paymentMethod &&
                existingJob.Origin == "SHOP_OWNER" &&
                existingJob.CustomerUserId == customerUserId &&
                existingJob.RecipientName == recipientName &&
                existingJob.RecipientPhone == recipientPhone &&
                existingJob.PrintKind == "PHOTO" &&
                existingJob.PhotoSizeCode == photoSizeCode &&
                existingJob.PhotoCount == photoCount &&
                existingJob.PhotosPerPage == photosPerPage &&
                string.Equals(existingDocument.Sha256, stored.Sha256, StringComparison.OrdinalIgnoreCase);

            await storage.DeleteAsync(stored.StorageKey);
            stored = null;
            await transaction.RollbackAsync(ct);

            if (!isSameRequest)
                return Results.Conflict(new { code = "idempotency_key_reused", message = "This request key already belongs to a different photo order. Start a new photo order instead of changing its original." });

            return Results.Ok(new
            {
                Job = new
                {
                    existingJob.Id,
                    existingJob.PrivatePrinting,
                    existingJob.JobNumber,
                    existingJob.Status,
                    existingJob.Origin,
                    existingJob.RecipientName,
                    existingJob.RecipientPhone,
                    existingJob.CustomerUserId,
                    existingJob.PrintKind,
                    existingJob.PhotoSizeCode,
                    existingJob.PhotoCount,
                    existingJob.PhotosPerPage,
                    existingJob.PageCount,
                    existingJob.PhotoUnitPriceMinorSnapshot,
                    existingJob.QuotedAmountMinor,
                    existingJob.Currency,
                    existingJob.QuoteExpiresUtc,
                    existingJob.CreatedUtc
                },
                Document = new
                {
                    existingDocument!.Id,
                    existingDocument.OriginalFileName,
                    existingDocument.MediaType,
                    existingDocument.SizeBytes,
                    existingDocument.Sha256,
                    existingDocument.PageSizesJson,
                    existingDocument.StorageMode
                },
                Payment = existingPayment,
                DuplicateRequest = true
            });
        }

        var pricing = await db.ShopPricingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.IsConfigured, ct);
        if (pricing is null)
        {
            await storage.DeleteAsync(stored.StorageKey);
            stored = null;
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new { code = "shop_pricing_not_configured", message = "Configure the shop's photo sizes and per-photo prices before creating a photo order." });
        }

        long unitPrice;
        long amount;
        try
        {
            unitPrice = PricingCalculator.PhotoUnitRate(photoSizeCode, pricing);
            amount = PricingCalculator.CalculatePhoto(photoCount, photoSizeCode, pricing);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await storage.DeleteAsync(stored.StorageKey);
            stored = null;
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new { code = "photo_pricing_unavailable", message = exception.Message });
        }

        var paymentSettings = await db.ShopPaymentProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
        if (paymentSettings is null ||
            (paymentMethod == "UPI" && (!paymentSettings.UpiEnabled || string.IsNullOrWhiteSpace(paymentSettings.UpiId))) ||
            (paymentMethod == "CASH" && !paymentSettings.CashEnabled))
        {
            await storage.DeleteAsync(stored.StorageKey);
            stored = null;
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new { code = "payment_method_unavailable", message = "The selected payment method is not enabled for this shop. Update Payment Settings and try again." });
        }

        await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.MonthlyPrintJob, ct);
        var quota = await entitlements.CheckCapacityAsync(tenantId, TenantQuotaResource.MonthlyPrintJob, ct);
        if (!quota.Allowed)
        {
            await storage.DeleteAsync(stored.StorageKey);
            stored = null;
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
        }

        var job = new PrintJob
        {
            Id = jobId,
            TenantId = tenantId,
            QrStationId = null,
            CustomerSessionId = null,
            CustomerUserId = customerUserId,
            Origin = "SHOP_OWNER",
            RecipientName = recipientName,
            RecipientPhone = recipientPhone,
            JobNumber = jobNumber,
            Status = paymentMethod == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT",
            IdempotencyKey = idempotencyKey,
            PageCount = expectedPageCount,
            Copies = 1,
            ColourMode = "Colour",
            PaperSize = "A4",
            IsDuplex = false,
            Orientation = "AUTO",
            PrintKind = "PHOTO",
            PhotoSizeCode = photoSizeCode,
            PhotoCount = photoCount,
            PhotosPerPage = photosPerPage,
            PhotoUnitPriceMinorSnapshot = unitPrice,
            PricingVersion = pricing.Version,
            QuoteExpiresUtc = now.AddMinutes(30),
            Currency = "INR",
            QuotedAmountMinor = amount,
            PageRefundAllocationsJson = JsonSerializer.Serialize(
                PricingCalculator.CalculatePhotoPageRefundAllocations(photoCount, photoSizeCode, photosPerPage, unitPrice)),
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var document = new PrintDocument
        {
            Id = documentId,
            TenantId = tenantId,
            PrintJobId = job.Id,
            OriginalFileName = outputName,
            MediaType = stored.Analysis.MediaType,
            FileExtension = stored.Analysis.Extension,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            StorageKey = stored.StorageKey,
            StorageMode = "CLOUD",
            PageSizesJson = stored.Analysis.PageSizesJson,
            CreatedUtc = now
        };

        db.PrintJobs.Add(job);
        db.PrintDocuments.Add(document);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = null,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = GetActorId(principal),
            ReasonCode = "SHOP_PHOTO_ORDER_CREATED_AWAITING_PAYMENT",
            CreatedUtc = now
        });
        var payment = new PrintPayment
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            Method = paymentMethod,
            Status = paymentMethod == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "CASH_AWAITING_PAYMENT",
            AmountMinor = amount,
            Currency = job.Currency,
            UpiIdSnapshot = paymentMethod == "UPI" ? paymentSettings.UpiId : null,
            PayeeNameSnapshot = paymentMethod == "UPI" ? paymentSettings.PayeeName : null,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        db.PrintPayments.Add(payment);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = job.Status,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = GetActorId(principal),
            ReasonCode = paymentMethod == "UPI" ? "SHOP_PHOTO_UPI_PAYMENT_PENDING" : "SHOP_PHOTO_CASH_PAYMENT_PENDING",
            CreatedUtc = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/v1/shop/print-jobs/{job.Id}", new
        {
            Job = new
            {
                job.Id,
                job.JobNumber,
                job.Status,
                job.Origin,
                job.RecipientName,
                job.RecipientPhone,
                job.CustomerUserId,
                job.PrintKind,
                job.PhotoSizeCode,
                job.PhotoCount,
                job.PhotosPerPage,
                job.PageCount,
                job.PhotoUnitPriceMinorSnapshot,
                job.QuotedAmountMinor,
                job.Currency,
                job.QuoteExpiresUtc,
                job.CreatedUtc
            },
            Payment = new { payment.Id, payment.Method, payment.Status, payment.AmountMinor, payment.Currency, payment.UpiIdSnapshot, payment.PayeeNameSnapshot },
            Document = new
            {
                document.Id,
                document.OriginalFileName,
                document.MediaType,
                document.SizeBytes,
                document.Sha256,
                document.PageSizesJson
            },
            DuplicateRequest = false
        });
    }
    catch (DbUpdateException)
    {
        await transaction.RollbackAsync(ct);
        await CleanupUncommittedStorageAsync();

        // A concurrent request may have won the unique (TenantId, IdempotencyKey)
        // insert. Do not create another job/payment or surface a generic 500.
        var concurrentJobExists = await db.PrintJobs.AsNoTracking().AnyAsync(
            item => item.TenantId == tenantId && item.IdempotencyKey == idempotencyKey, ct);
        if (concurrentJobExists)
            return Results.Conflict(new
            {
                code = "photo_order_created_concurrently",
                message = "This photo order was submitted at the same time in another window. Refresh the Print Queue; retry with the same request key to retrieve the created order."
            });
        throw;
    }
    catch
    {
        await transaction.RollbackAsync(ct);
        await CleanupUncommittedStorageAsync();
        throw;
    }
})
.WithMetadata(new RequestSizeLimitAttribute(UploadLimits.MaxMultipartRequestBytes))
.RequireRateLimiting("document-upload");

shop.MapGet("/reports/summary", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    int? days,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var plan = await entitlements.GetForTenantAsync(tenantId, ct);
    if (!plan.IsAccessActive)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "This shop's trial or subscription period is no longer active.");
    if (!plan.AdvancedReportsEnabled)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "Advanced reporting is not included in this shop's current plan.");

    var periodDays = days ?? 30;
    if (periodDays != 7 && periodDays != 30 && periodDays != 90)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["days"] = ["Choose a reporting period of 7, 30 or 90 days."]
        });
    }

    var toUtc = DateTimeOffset.UtcNow;
    var fromUtc = toUtc.AddDays(-periodDays);
    var jobs = db.PrintJobs.AsNoTracking().Where(job =>
        job.TenantId == tenantId &&
        job.CreatedUtc >= fromUtc &&
        job.CreatedUtc < toUtc);

    var totalJobs = await jobs.CountAsync(ct);
    var completedJobs = await jobs.CountAsync(job =>
        job.Status == "COMPLETED" && job.OutputConfirmedUtc != null, ct);
    var outputUnconfirmedJobs = await jobs.CountAsync(job =>
        job.Status == "OUTPUT_UNCONFIRMED", ct);
    var pagesRequested = await jobs.SumAsync(job =>
        (long?)(job.PageCount * (long)job.Copies), ct) ?? 0;

    var jobsByStatus = await jobs
        .GroupBy(job => job.Status)
        .Select(group => new { status = group.Key, count = group.Count() })
        .OrderByDescending(group => group.count)
        .ThenBy(group => group.status)
        .ToListAsync(ct);

    // File-level payment allocations remain the source of refund accounting,
    // but a batch checkout is one customer payment, not one payment per file.
    var verifiedIndividualCollections = await db.PrintPayments.AsNoTracking()
        .Where(payment => payment.TenantId == tenantId &&
                          payment.BatchPaymentId == null &&
                          payment.VerifiedUtc != null &&
                          payment.VerifiedUtc >= fromUtc &&
                          payment.VerifiedUtc < toUtc)
        .GroupBy(payment => new { payment.Method, payment.Currency })
        .Select(group => new
        {
            method = group.Key.Method,
            currency = group.Key.Currency,
            paymentCount = group.Count(),
            amountMinor = group.Sum(payment => payment.AmountMinor)
        })
        .ToListAsync(ct);

    var verifiedBatchCollections = await db.PrintBatchPayments.AsNoTracking()
        .Where(payment => payment.TenantId == tenantId &&
                          payment.Status == "VERIFIED" &&
                          payment.VerifiedUtc != null &&
                          payment.VerifiedUtc >= fromUtc &&
                          payment.VerifiedUtc < toUtc)
        .GroupBy(payment => new { payment.Method, payment.Currency })
        .Select(group => new
        {
            method = group.Key.Method,
            currency = group.Key.Currency,
            paymentCount = group.Count(),
            amountMinor = group.Sum(payment => payment.AmountMinor)
        })
        .ToListAsync(ct);

    var verifiedCollectionsByMethod = verifiedIndividualCollections
        .Concat(verifiedBatchCollections)
        .GroupBy(item => new { item.method, item.currency })
        .Select(group => new
        {
            method = group.Key.method,
            currency = group.Key.currency,
            paymentCount = group.Sum(item => item.paymentCount),
            amountMinor = group.Sum(item => item.amountMinor)
        })
        .OrderBy(group => group.currency)
        .ThenBy(group => group.method)
        .ToList();

    var refundsRecordedByMethod = await db.PrintRefunds.AsNoTracking()
        .Where(refund => refund.TenantId == tenantId &&
                         refund.Status == "RECORDED_AS_SENT" &&
                         refund.RefundedUtc >= fromUtc &&
                         refund.RefundedUtc < toUtc)
        .GroupBy(refund => new { refund.Method, refund.Currency })
        .Select(group => new
        {
            method = group.Key.Method,
            currency = group.Key.Currency,
            refundCount = group.Count(),
            amountMinor = group.Sum(refund => refund.AmountMinor)
        })
        .OrderBy(group => group.currency)
        .ThenBy(group => group.method)
        .ToListAsync(ct);

    var pendingIndividualPaymentCount = await db.PrintPayments.AsNoTracking().CountAsync(payment =>
        payment.TenantId == tenantId &&
        payment.BatchPaymentId == null &&
        (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
         payment.Status == "CUSTOMER_REPORTED" ||
         payment.Status == "CASH_AWAITING_PAYMENT"), ct);
    var pendingBatchPaymentCount = await db.PrintBatchPayments.AsNoTracking().CountAsync(payment =>
        payment.TenantId == tenantId &&
        (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
         payment.Status == "CUSTOMER_REPORTED" ||
         payment.Status == "CASH_AWAITING_PAYMENT"), ct);
    var pendingPaymentCount = pendingIndividualPaymentCount + pendingBatchPaymentCount;

    return Results.Ok(new
    {
        periodDays,
        fromUtc,
        toUtc,
        generatedUtc = DateTimeOffset.UtcNow,
        totalJobs,
        completedJobs,
        outputUnconfirmedJobs,
        pagesRequested,
        pendingPaymentCount,
        jobsByStatus,
        verifiedCollectionsByMethod,
        refundsRecordedByMethod
    });
});

shop.MapPatch("/print-jobs/{jobId:guid}/printer", async (
    Guid jobId,
    AssignPrintJobPrinterRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
        return Results.Forbid();

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var batchInfo = await db.PrintJobs.AsNoTracking()
        .Where(item => item.Id == jobId && item.TenantId == tenantId)
        .Select(item => new { item.BatchId })
        .SingleOrDefaultAsync(ct);
    if (batchInfo is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    List<PrintJob> batchJobs;
    if (batchInfo.BatchId is Guid batchId)
    {
        batchJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
            .ToListAsync(ct);
    }
    else
    {
        batchJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {jobId} FOR UPDATE")
            .ToListAsync(ct);
    }
    var selectedJob = batchJobs.SingleOrDefault(item => item.Id == jobId);
    if (selectedJob is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    var now = DateTimeOffset.UtcNow;
    var hasVerifiedPayment = await db.PrintPayments.AnyAsync(
        payment => payment.TenantId == tenantId &&
                   payment.PrintJobId == selectedJob.Id &&
                   (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"), ct);
    var expiredAgentLease = selectedJob.Status == "LOCAL_QUEUE_CLAIMED" &&
        (selectedJob.AgentLeaseExpiresUtc == null || selectedJob.AgentLeaseExpiresUtc <= now);
    var readyQueueCanBeReassigned = selectedJob.Status == "LOCAL_QUEUE_READY" &&
        selectedJob.LocalQueueReceivedUtc != null &&
        selectedJob.SpoolerJobId == null &&
        string.IsNullOrWhiteSpace(selectedJob.SpoolerJobName) &&
        selectedJob.OutputConfirmedUtc == null;
    var canAssign = hasVerifiedPayment &&
        (selectedJob.Status == "PAID_AUTHORISED" || expiredAgentLease || readyQueueCanBeReassigned);
    if (!canAssign)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "job_not_assignable",
            message = "A printer can be assigned after payment is verified and before print output starts. Once printing has started, the whole batch stays on that printer."
        });
    }

    var onlineAfter = now.AddMinutes(-2);
    var printer = await db.PrinterDevices.AsNoTracking()
        .Where(item => item.Id == request.PrinterDeviceId &&
                       item.TenantId == tenantId &&
                       item.IsActive &&
                       item.AgentDeviceId != null &&
                       (item.OutputMode == "PHYSICAL" || item.OutputMode == "PDF_EXPORT") &&
                       ((item.OutputMode == "PDF_EXPORT" && !selectedJob.PrivatePrinting) ||
                        (item.LastSeenUtc != null && item.LastSeenUtc >= onlineAfter &&
                         (item.Status == "Ready" || item.Status == "Busy"))) &&
                       db.AgentDevices.Any(agent =>
                           agent.Id == item.AgentDeviceId &&
                           agent.TenantId == tenantId &&
                           agent.RevokedUtc == null &&
                           agent.LastSeenUtc != null &&
                           agent.LastSeenUtc >= onlineAfter))
        .Select(item => new
        {
            item.Id,
            item.DisplayName,
            item.QueueName,
            item.AgentDeviceId,
            item.OutputMode
        })
        .SingleOrDefaultAsync(ct);

    if (printer is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "printer_not_available",
            message = "Select a printer on a paired, online computer with a recent heartbeat and a ready or busy Windows queue."
        });
    }
    var allowPrivateVirtualOutput = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct);
    if (selectedJob.PrivatePrinting &&
        !PrivatePrintingOutputPolicy.IsEligible(allowPrivateVirtualOutput, environment.IsProduction(), printer.OutputMode))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "private_printing_physical_output_required",
            message = "Private Printing requires a connected physical printer. Virtual printers are available only when the Company Main Admin enables non-production test output."
        });
    }

    var activeJobs = batchJobs
        .Where(item => item.Status is not ("REJECTED" or "REFUNDED"))
        .ToList();
    var activeJobIds = activeJobs.Select(item => item.Id).ToArray();
    var documents = await db.PrintDocuments.AsNoTracking()
        .Where(item => item.TenantId == tenantId && activeJobIds.Contains(item.PrintJobId))
        .Select(item => new { item.PrintJobId, item.StorageMode, item.AgentDeviceId })
        .ToListAsync(ct);
    var documentsByJob = documents.ToDictionary(item => item.PrintJobId);
    var states = activeJobs.Select(item =>
    {
        documentsByJob.TryGetValue(item.Id, out var document);
        var outputLocked = item.Status is "PRINT_AUTHORISED" or "PRINT_SUBMITTING" or
            "SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED" or "COMPLETED";
        var activeAgentLease = item.Status == "LOCAL_QUEUE_CLAIMED" &&
            item.AgentLeaseExpiresUtc is DateTimeOffset lease && lease > now;
        return new BatchPrinterAssignmentState(
            item.PrinterDeviceId,
            document?.AgentDeviceId,
            document?.StorageMode == "LOCAL_AGENT",
            outputLocked,
            activeAgentLease);
    }).ToArray();

    var assignmentConflict = BatchPrinterAssignmentRules.FindConflict(
        states, printer.Id, printer.AgentDeviceId!.Value);
    if (assignmentConflict == "local_document_agent_locked")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "local_document_agent_locked",
            message = "A file in this batch is stored on a different shop computer. All files must stay on that same computer; upload the batch again to move it."
        });
    }
    if (assignmentConflict == "batch_printer_locked")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_printer_locked",
            message = "A file in this batch has already started printing. The whole batch is locked to that printer."
        });
    }
    if (assignmentConflict is not null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = assignmentConflict,
            message = "This batch cannot be assigned to the selected printer. Refresh the queue and try again."
        });
    }

    var assigned = 0;
    foreach (var batchJob in activeJobs)
    {
        var needsQueueReset =
            (batchJob.Status == "LOCAL_QUEUE_READY" && batchJob.PrinterDeviceId != printer.Id) ||
            (batchJob.Status == "LOCAL_QUEUE_CLAIMED" &&
             (batchJob.AgentLeaseExpiresUtc == null || batchJob.AgentLeaseExpiresUtc <= now));
        var printerChanged = batchJob.PrinterDeviceId != printer.Id;
        if (!printerChanged && !needsQueueReset)
        {
            assigned++;
            continue;
        }

        var previousStatus = batchJob.Status;
        batchJob.PrinterDeviceId = printer.Id;
        if (needsQueueReset)
        {
            batchJob.Status = "PAID_AUTHORISED";
            batchJob.ClaimedByAgentDeviceId = null;
            batchJob.AgentLeaseExpiresUtc = null;
            batchJob.AgentClaimedUtc = null;
            batchJob.LocalQueueReceivedUtc = null;
            batchJob.FailureCode = null;
            batchJob.SpoolerStatus = null;
        }
        batchJob.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = batchJob.Id,
            PreviousStatus = previousStatus,
            NewStatus = batchJob.Status,
            ActorType = "ShopUser",
            ActorId = GetActorId(principal),
            ReasonCode = selectedJob.BatchId is null ? "PRINTER_ASSIGNED" : "BATCH_PRINTER_ASSIGNED",
            CreatedUtc = now
        });
        assigned++;
    }

    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new
    {
        selectedJob.Id,
        selectedJob.BatchId,
        Status = selectedJob.Status,
        PrinterDeviceId = printer.Id,
        PrinterName = printer.DisplayName,
        QueueName = printer.QueueName,
        AgentDeviceId = printer.AgentDeviceId,
        AssignedJobCount = assigned,
        UpdatedUtc = now
    });
});

shop.MapPatch("/print-jobs/{jobId:guid}/settings", async (
    Guid jobId,
    UpdatePrintJobSettingsRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();

    var colourMode = request.ColourMode?.Trim();
    var paperSize = request.PaperSize?.Trim().ToUpperInvariant();
    var orientation = request.Orientation?.Trim().ToUpperInvariant();
    if (colourMode is not ("Mono" or "Colour") ||
        paperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
        request.Copies is < 1 or > 100 ||
        orientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE"))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printOptions"] = ["Choose supported paper, orientation, colour, sides and 1–100 copies."]
        });

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (job.PrintKind != "DOCUMENT" ||
        job.Status is not ("AWAITING_CONFIGURATION" or "AWAITING_CUSTOMER_APPROVAL" or
            "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "QUOTE_EXPIRED" or "PAYMENT_REJECTED"))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "print_settings_locked",
            message = "Print options can only be adjusted before payment begins and before the job is handed to the Agent."
        });
    }

    var hasStartedPayment = await db.PrintPayments.AsNoTracking().AnyAsync(payment =>
        payment.TenantId == tenantId && payment.PrintJobId == jobId && payment.Status != "REJECTED", ct);
    var batchPaymentStarted = job.BatchId is Guid batchId &&
        await db.PrintBatchPayments.AsNoTracking().AnyAsync(payment =>
            payment.TenantId == tenantId && payment.BatchId == batchId && payment.Status != "REJECTED", ct);
    if (hasStartedPayment || batchPaymentStarted)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "payment_already_started",
            message = "This job or its batch already has a payment attempt. Do not change its quoted settings; resolve the payment or refund first."
        });
    }

    var pricing = await db.ShopPricingProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.IsConfigured, ct);
    if (pricing is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "shop_pricing_not_configured", message = "Configure shop print prices before revising this quote." });
    }

    long amount;
    long[] pageRefundAllocations;
    try
    {
        amount = PricingCalculator.Calculate(job.PageCount, request.Copies, colourMode, request.IsDuplex, pricing);
        pageRefundAllocations = PricingCalculator.CalculatePageRefundAllocations(
            job.PageCount, request.Copies, colourMode, request.IsDuplex, pricing);
    }
    catch (DuplexPricingNotConfiguredException exception)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "duplex_pricing_not_configured", message = exception.Message });
    }

    var now = DateTimeOffset.UtcNow;
    var previousStatus = job.Status;
    job.ColourMode = colourMode;
    job.PaperSize = paperSize;
    job.IsDuplex = request.IsDuplex;
    job.Copies = request.Copies;
    job.Orientation = orientation;
    job.Currency = "INR";
    job.QuotedAmountMinor = amount;
    job.PageRefundAllocationsJson = JsonSerializer.Serialize(pageRefundAllocations);
    job.PricingVersion = pricing.Version;
    job.QuoteExpiresUtc = now.AddMinutes(10);
    job.Status = "AWAITING_CUSTOMER_APPROVAL";
    job.FailureCode = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = tenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = GetActorId(principal),
        ReasonCode = "SHOP_REVISED_PRINT_SETTINGS_CUSTOMER_APPROVAL_REQUIRED",
        CreatedUtc = now
    });

    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new
    {
        job.Id, job.JobNumber, job.Status, job.PageCount, job.Copies,
        job.ColourMode, job.PaperSize, job.IsDuplex, job.Orientation,
        job.Currency, job.QuotedAmountMinor, AmountMajor = amount / 100m,
        job.PricingVersion, job.QuoteExpiresUtc, CustomerApprovalRequired = true
    });
});

shop.MapPost("/print-jobs/{jobId:guid}/dispatch", async (Guid jobId, ClaimsPrincipal principal, SmartPrintDbContext db, IHostEnvironment environment, CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var batchInfo = await db.PrintJobs.AsNoTracking()
        .Where(item => item.Id == jobId && item.TenantId == tenantId)
        .Select(item => new { item.BatchId })
        .SingleOrDefaultAsync(ct);
    if (batchInfo is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    List<PrintJob> batchJobs;
    if (batchInfo.BatchId is Guid batchId)
    {
        batchJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
            .ToListAsync(ct);
    }
    else
    {
        batchJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {jobId} FOR UPDATE")
            .ToListAsync(ct);
    }
    var job = batchJobs.SingleOrDefault(item => item.Id == jobId);
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }
    if (job.Status == "PRINT_AUTHORISED")
        return Results.Ok(new { job.Id, job.Status, job.SpoolerJobName, DuplicateRequest = true });

    if (job.Status != "LOCAL_QUEUE_READY" || job.LocalQueueReceivedUtc is null ||
        job.PrinterDeviceId is null || job.ClaimedByAgentDeviceId is null)
        return Results.Conflict(new { code = "job_not_ready_for_print_dispatch", message = "The paid document must be secured by its assigned agent before printing can start." });

    if (batchJobs.Any(other =>
        other.Id != job.Id &&
        other.Status != "REJECTED" &&
        other.Status != "REFUNDED" &&
        other.PrinterDeviceId != job.PrinterDeviceId))
    {
        return Results.Conflict(new
        {
            code = "batch_printer_mismatch",
            message = "Every accepted file in this batch must use the same printer. Assign one printer to the whole batch before starting output."
        });
    }

    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    var ready = await (
        from printer in db.PrinterDevices.AsNoTracking()
        join agent in db.AgentDevices.AsNoTracking()
            on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
            equals new { agent.TenantId, AgentId = agent.Id }
        where printer.Id == job.PrinterDeviceId && printer.TenantId == tenantId &&
              printer.AgentDeviceId == job.ClaimedByAgentDeviceId && printer.IsActive &&
              printer.QueueName != null && printer.QueueName != "" &&
              (printer.OutputMode == "PHYSICAL" || printer.OutputMode == "PDF_EXPORT") &&
              ((printer.OutputMode == "PDF_EXPORT" && !job.PrivatePrinting) ||
               (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                (printer.Status == "Ready" || printer.Status == "Busy"))) &&
              agent.RevokedUtc == null && agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter
        select new { printer.DisplayName, printer.QueueName, printer.OutputMode, AgentId = agent.Id, AgentName = agent.DeviceName }
    ).SingleOrDefaultAsync(ct);
    var verified = await db.PrintPayments.AnyAsync(x => x.TenantId == tenantId && x.PrintJobId == job.Id &&
        (x.Status == "VERIFIED" || x.Status == "PARTIALLY_REFUNDED"), ct);
    if (ready is null || !verified)
        return Results.Conflict(new { code = "print_prerequisites_not_met", message = "Verified payment and the same paired computer's live printer queue are required." });
    var allowPrivateVirtualOutput = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct);
    if (job.PrivatePrinting &&
        !PrivatePrintingOutputPolicy.IsEligible(allowPrivateVirtualOutput, environment.IsProduction(), ready.OutputMode))
        return Results.Conflict(new
        {
            code = "private_printing_physical_output_required",
            message = "Private Printing requires a connected physical printer. Virtual printers are available only for enabled non-production test output."
        });

    var isPrivateTestOutput = job.PrivatePrinting && ready.OutputMode == "PDF_EXPORT";
    var previous = job.Status;
    job.Status = "PRINT_AUTHORISED";
    job.OutputType = isPrivateTestOutput
        ? PrivatePrintingOutputPolicy.TestVirtual
        : ready.OutputMode;
    job.PrinterNameSnapshot = ready.DisplayName;
    job.PrinterQueueNameSnapshot = ready.QueueName;
    job.PrinterAgentNameSnapshot = ready.AgentName;
    job.PrinterSnapshotUtc = now;
    job.SpoolerJobName = ready.OutputMode == "PDF_EXPORT"
        ? $"{(isPrivateTestOutput ? "PRIVATE-TEST-PDF" : "PDF-EXPORT")}-{job.JobNumber}-{job.Id:N}"
        : $"SmartPrint-{job.JobNumber}-{job.Id:N}";
    job.SpoolerJobId = null;
    job.SpoolerStatus = "AWAITING_AGENT";
    job.PrintDispatchLeaseExpiresUtc = null;
    job.FailureCode = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = tenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "ShopUser", ActorId = GetActorId(principal),
        ReasonCode = isPrivateTestOutput
            ? "SHOP_EXPLICITLY_AUTHORISED_PRIVATE_TEST_VIRTUAL_OUTPUT"
            : ready.OutputMode == "PDF_EXPORT"
                ? "SHOP_EXPLICITLY_AUTHORISED_PDF_EXPORT"
                : "SHOP_EXPLICITLY_AUTHORISED_PHYSICAL_PRINT",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new
    {
        job.Id, job.JobNumber, job.Status, PrinterName = ready.DisplayName,
        ready.QueueName, ready.OutputMode, ready.AgentId, job.SpoolerJobName
    });
});

shop.MapPost("/print-jobs/{jobId:guid}/retry-pdf-export", async (
    Guid jobId, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
    var job = await db.PrintJobs.SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == tenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.PrivatePrinting)
        return Results.Conflict(new { code = "private_printing_physical_output_required", message = "Private Printing source documents cannot be exported or retried as managed PDFs." });

    var printerIsPdfExport = job.PrinterDeviceId is Guid assignedPrinterId &&
        await db.PrinterDevices.AnyAsync(printer =>
            printer.Id == assignedPrinterId && printer.TenantId == tenantId &&
            printer.OutputMode == "PDF_EXPORT", ct);
    if (job.Status != "OUTPUT_UNCONFIRMED" ||
        (job.OutputType != "PDF_EXPORT" && !printerIsPdfExport))
        return Results.Conflict(new { code = "pdf_export_not_recoverable", message = "Only an output-unconfirmed job assigned to a PDF export destination can be retried." });
    if (job.PrinterDeviceId is not Guid printerId || job.ClaimedByAgentDeviceId is not Guid agentId ||
        job.LocalQueueReceivedUtc is null)
        return Results.Conflict(new { code = "pdf_export_local_copy_missing", message = "The original protected local document and assigned Agent are required." });

    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    var output = await (
        from printer in db.PrinterDevices.AsNoTracking()
        join agent in db.AgentDevices.AsNoTracking()
            on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
            equals new { agent.TenantId, AgentId = agent.Id }
        where printer.Id == printerId && printer.TenantId == tenantId &&
              printer.AgentDeviceId == agentId && printer.OutputMode == "PDF_EXPORT" &&
              printer.IsActive &&
              agent.RevokedUtc == null && agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter
        select new { printer.DisplayName }
    ).SingleOrDefaultAsync(ct);
    var verified = await db.PrintPayments.AnyAsync(payment =>
        payment.TenantId == tenantId && payment.PrintJobId == job.Id && (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"), ct);
    if (output is null || !verified)
        return Results.Conflict(new { code = "pdf_export_prerequisites_not_met", message = "Verified payment and the same paired computer's live PDF export destination are required." });

    var previous = job.Status;
    job.Status = "LOCAL_QUEUE_READY";
    job.OutputType = "PDF_EXPORT";
    job.OutputFilePath = null;
    job.OutputFileName = null;
    job.OutputSha256 = null;
    job.OutputSizeBytes = null;
    job.OutputCompletedUtc = null;
    job.OutputConfirmedUtc = null;
    job.SpoolerJobId = null;
    job.SpoolerJobName = null;
    job.SpoolerSubmittedUtc = null;
    job.SpoolerObservedUtc = null;
    job.PrintDispatchLeaseExpiresUtc = null;
    job.FailureCode = null;
    job.SpoolerStatus = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = tenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "ShopUser", ActorId = GetActorId(principal),
        ReasonCode = "SHOP_CONFIRMED_NO_PDF_FILE_AND_RETRY_REQUESTED", CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.JobNumber, job.Status, OutputMode = "PDF_EXPORT", PrinterName = output.DisplayName });
});


shop.MapPost("/print-jobs/{jobId:guid}/retry-private-test-output", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {jobId} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    var allowPrivateVirtualOutput = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct);
    if (environment.IsProduction() || !allowPrivateVirtualOutput ||
        !job.PrivatePrinting || job.OutputType != PrivatePrintingOutputPolicy.TestVirtual ||
        job.Status != "OUTPUT_UNCONFIRMED")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "private_test_output_not_recoverable",
            message = "Only an output-unconfirmed Private Printing test job can be retried while non-production virtual output is enabled."
        });
    }

    if (job.PrinterDeviceId is not Guid printerId ||
        job.ClaimedByAgentDeviceId is not Guid agentId ||
        job.LocalQueueReceivedUtc is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "private_test_local_copy_missing",
            message = "The original protected local copy and its paired Agent are required. No cloud copy can be used for this retry."
        });
    }

    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    var destination = await (
        from printer in db.PrinterDevices.AsNoTracking()
        join agent in db.AgentDevices.AsNoTracking()
            on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
            equals new { agent.TenantId, AgentId = agent.Id }
        where printer.Id == printerId && printer.TenantId == tenantId &&
              printer.AgentDeviceId == agentId && printer.IsActive &&
              printer.OutputMode == "PDF_EXPORT" &&
              !string.IsNullOrWhiteSpace(printer.QueueName) &&
              printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
              (printer.Status == "Ready" || printer.Status == "Busy") &&
              agent.RevokedUtc == null && agent.Status != "Revoked" &&
              agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter
        select new { printer.DisplayName, printer.QueueName }
    ).SingleOrDefaultAsync(ct);
    var verifiedPayment = await db.PrintPayments.AnyAsync(payment =>
        payment.TenantId == tenantId && payment.PrintJobId == job.Id &&
        (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"), ct);
    if (destination is null || !verifiedPayment)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "private_test_output_prerequisites_not_met",
            message = "The same paired Agent, its ready virtual test queue and verified payment are required before retrying."
        });
    }

    var previous = job.Status;
    job.Status = "LOCAL_QUEUE_READY";
    job.OutputType = PrivatePrintingOutputPolicy.TestVirtual;
    job.OutputFilePath = null;
    job.OutputFileName = null;
    job.OutputSha256 = null;
    job.OutputSizeBytes = null;
    job.OutputCompletedUtc = null;
    job.OutputConfirmedUtc = null;
    job.SpoolerJobId = null;
    job.SpoolerJobName = null;
    job.SpoolerSubmittedUtc = null;
    job.SpoolerObservedUtc = null;
    job.PrintDispatchLeaseExpiresUtc = null;
    job.FailureCode = null;
    job.SpoolerStatus = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = tenantId,
        PrintJobId = job.Id,
        PreviousStatus = previous,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = GetActorId(principal),
        ReasonCode = "SHOP_CONFIRMED_NO_PRIVATE_TEST_PDF_AND_RETRY_REQUESTED",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new
    {
        job.Id,
        job.JobNumber,
        job.Status,
        OutputMode = "PDF_EXPORT",
        OutputType = PrivatePrintingOutputPolicy.TestVirtual,
        PrinterName = destination.DisplayName,
        QueueName = destination.QueueName,
        PhysicalOutputConfirmed = false
    });
});

shop.MapPost("/print-jobs/{jobId:guid}/confirm-output", async (Guid jobId, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
    var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == tenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.OutputType is "PDF_EXPORT" or "TEST_VIRTUAL")
        return Results.Conflict(new
        {
            code = job.OutputType == "TEST_VIRTUAL" ? "test_virtual_output_not_physical" : "pdf_export_not_physical_output",
            message = job.OutputType == "TEST_VIRTUAL"
                ? "The test PDF was generated locally. It is not a physical print and cannot be marked as physically collected."
                : "PDF exports are confirmed when the local PDF file is saved and verified. Physical paper-output confirmation does not apply."
        });
    if (job.Status == "COMPLETED" && job.OutputConfirmedUtc != null)
        return Results.Ok(new { job.Id, job.Status, job.OutputConfirmedUtc, DuplicateRequest = true });
    if (job.Status is not ("SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED"))
        return Results.Conflict(new { code = "output_not_ready_for_confirmation", message = "Only a submitted or output-unconfirmed job can be completed after checking the physical pages." });

    if (job.PrivatePrinting && (job.OutputType != "PHYSICAL" || job.ClaimedByAgentDeviceId is null))
        return Results.Conflict(new
        {
            code = "private_output_confirmation_invalid",
            message = "Private Printing can be completed only after confirmed physical output from its assigned shop computer."
        });

    var now = DateTimeOffset.UtcNow;
    var previous = job.Status;
    job.Status = "COMPLETED";
    job.OutputConfirmedUtc = now;
    if (job.PrivatePrinting)
        job.PrivateDocumentPurgeRequestedUtc ??= now;
    job.SpoolerObservedUtc = now;
    job.SpoolerStatus = "SHOP_CONFIRMED_OUTPUT";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = tenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "ShopUser", ActorId = GetActorId(principal),
        ReasonCode = "SHOP_OPERATOR_CONFIRMED_PHYSICAL_OUTPUT", CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.OutputConfirmedUtc });
});

shop.MapGet("/print-jobs/{jobId:guid}/events", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    if (!await db.PrintJobs.AsNoTracking().AnyAsync(job => job.Id == jobId && job.TenantId == tenantId, ct))
    {
        return Results.NotFound();
    }

    var events = await db.PrintJobEvents.AsNoTracking()
        .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
        .OrderBy(item => item.CreatedUtc)
        .Select(item => new
        {
            item.Id,
            item.PreviousStatus,
            item.NewStatus,
            item.ActorType,
            item.ReasonCode,
            item.CreatedUtc
        })
        .ToListAsync(ct);
    return Results.Ok(events);
});

shop.MapPost("/print-jobs/{jobId:guid}/photo-revision-grant", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
        return Results.Forbid();

    var job = await db.PrintJobs.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == tenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.PrivatePrinting)
        return Results.Conflict(new { code = "private_printing_source_locked", message = "Private Printing documents cannot be opened or revised by the shop. Ask the customer to submit a new document when a change is needed." });

    if (job.Status != "AWAITING_SHOP_APPROVAL" ||
        job.LocalQueueReceivedUtc is not null ||
        job.ClaimedByAgentDeviceId is not null)
        return Results.Conflict(new
        {
            code = "photo_revision_not_allowed",
            message = "Local photo enhancement is available only before payment and before the job is handed to a printer."
        });

    var document = await db.PrintDocuments.AsNoTracking()
        .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
        .Select(item => new { item.StorageMode, item.AgentDeviceId, item.MediaType, item.PhotoSourceMediaType })
        .SingleOrDefaultAsync(ct);
    if (document is null) return Results.NotFound();
    if (document.StorageMode != "LOCAL_AGENT" || document.AgentDeviceId is not Guid agentId)
        return Results.Conflict(new { code = "photo_revision_not_local", message = "This document is not stored on the paired local computer." });
    var photoSourceMediaType = document.PhotoSourceMediaType ?? document.MediaType;
    if (photoSourceMediaType is not ("image/jpeg" or "image/png"))
        return Results.Conflict(new { code = "photo_revision_source_not_image", message = "The shop editor requires a retained original JPG or PNG image." });

    var hasPayment = await db.PrintPayments.AnyAsync(item =>
        item.TenantId == tenantId && item.PrintJobId == jobId &&
        item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
    var hasBatchPayment = job.BatchId is Guid batchId && await db.PrintBatchPayments.AnyAsync(item =>
        item.TenantId == tenantId && item.BatchId == batchId &&
        item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
    if (hasPayment || hasBatchPayment)
        return Results.Conflict(new
        {
            code = "photo_revision_payment_started",
            message = "A payment attempt already exists for this file or batch. Photo editing is locked."
        });

    var now = DateTimeOffset.UtcNow;
    var agent = await db.AgentDevices.AsNoTracking()
        .Where(item => item.Id == agentId && item.TenantId == tenantId &&
                       item.RevokedUtc == null && item.Status != "Revoked" &&
                       item.LastSeenUtc != null && item.LastSeenUtc >= now.AddMinutes(-2))
        .Select(item => new { item.Id, item.LocalEndpointsJson })
        .SingleOrDefaultAsync(ct);
    if (agent is null)
        return Results.Conflict(new
        {
            code = "photo_revision_agent_offline",
            message = "The paired shop computer is offline. Reconnect it before saving a local photo enhancement."
        });

    var endpoints = ReadLocalAgentEndpoints(agent.LocalEndpointsJson);
    if (endpoints.Length == 0)
        return Results.Conflict(new
        {
            code = "photo_revision_endpoint_missing",
            message = "The paired shop computer has not published a reachable local endpoint."
        });

    var expiresUtc = now.AddMinutes(2);
    var grantToken = LocalDocumentPhotoRevisionGrant.Issue(
        signingKeyText, issuer, audience, tenantId, jobId, agent.Id, GetActorId(principal), now, expiresUtc);
    return Results.Ok(new { GrantToken = grantToken, Endpoints = endpoints, ExpiresUtc = expiresUtc });
});

shop.MapPost("/print-jobs/{jobId:guid}/photo-revision", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    DocumentStorage storage,
    HttpContext context,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
        return Results.Forbid();

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }
    if (job.PrivatePrinting)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "private_printing_source_locked",
            message = "Private Printing never transfers the original photo or editor settings to the shop. Ask the customer to submit a new document if a change is needed."
        });
    }

    if (job.Status != "AWAITING_SHOP_APPROVAL" ||
        job.LocalQueueReceivedUtc is not null ||
        job.ClaimedByAgentDeviceId is not null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "photo_revision_not_allowed",
            message = "Photo enhancements can only be saved while this job is awaiting shop review and before any payment or printer handoff."
        });
    }

    var document = await db.PrintDocuments.SingleOrDefaultAsync(
        item => item.TenantId == tenantId && item.PrintJobId == jobId, ct);
    if (document is null || document.PurgedUtc is not null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (!string.Equals(document.StorageMode, "CLOUD", StringComparison.Ordinal))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "photo_revision_local_storage",
            message = "This original is stored only on the paired shop computer. It has not been copied to cloud storage; local-only photo editing is not available in this release."
        });
    }

    var photoSourceMediaType = document.PhotoSourceMediaType ?? document.MediaType;
    if (photoSourceMediaType is not ("image/jpeg" or "image/png"))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "photo_revision_source_not_image",
            message = "The shop editor can enhance an original JPG or PNG photo. This job has no retained editable photo source."
        });
    }

    var hasPayment = await db.PrintPayments.AnyAsync(item =>
        item.TenantId == tenantId && item.PrintJobId == jobId &&
        item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
    var hasBatchPayment = job.BatchId is Guid batchId && await db.PrintBatchPayments.AnyAsync(item =>
        item.TenantId == tenantId && item.BatchId == batchId &&
        item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
    if (hasPayment || hasBatchPayment)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "photo_revision_payment_started",
            message = "This file or its batch already has a payment attempt. The original is locked against editing; use the existing refund/review workflow."
        });
    }

    if (!context.Request.HasFormContentType)
    {
        await transaction.RollbackAsync(ct);
        return Results.BadRequest(new { code = "photo_revision_form_required", message = "Send the prepared photo sheet as a multipart file." });
    }

    var form = await context.Request.ReadFormAsync(ct);
    if (form.Files.Count != 1)
    {
        await transaction.RollbackAsync(ct);
        return Results.BadRequest(new { code = "one_photo_sheet_required", message = "Save exactly one prepared photo-sheet PDF." });
    }

    var file = form.Files[0];
    if (file.Length is <= 0 or > UploadLimits.MaxFileBytes)
    {
        await transaction.RollbackAsync(ct);
        return Results.UnprocessableEntity(new
        {
            code = "photo_sheet_invalid",
            message = $"The prepared photo sheet must be a PDF up to {UploadLimits.MaxFileBytes / (1024 * 1024)} MB."
        });
    }

    var photoSizeCode = form["photoSizeCode"].FirstOrDefault() ?? string.Empty;
    if (photoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6") ||
        !int.TryParse(form["photoCount"].FirstOrDefault(), out var photoCount) ||
        photoCount is < 1 or > UploadLimits.MaxPhotoCount ||
        !int.TryParse(form["photosPerPage"].FirstOrDefault(), out var photosPerPage) ||
        photosPerPage < 1 || photosPerPage > PricingCalculator.MaximumPhotosPerA4Sheet(photoSizeCode))
    {
        await transaction.RollbackAsync(ct);
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["photoDetails"] = [$"Choose a supported ID photo size and a quantity from 1 to {UploadLimits.MaxPhotoCount}."]
        });
    }

    var expectedPageCount = PricingCalculator.PhotoPageCount(photoCount, photoSizeCode, photosPerPage);
    if (!PhotoStudioEditorSettingsRules.TryNormalizeJson(
            form["editorSettingsJson"].FirstOrDefault(), out var editorSettingsJson))
    {
        await transaction.RollbackAsync(ct);
        return Results.BadRequest(new
        {
            code = "photo_editor_settings_invalid",
            message = "The saved Photo Studio editing settings are invalid. Reopen the editor and save again."
        });
    }

    var pricing = await db.ShopPricingProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.IsConfigured, ct);
    if (pricing is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "shop_pricing_not_configured", message = "Configure photo prices before saving an enhanced photo job." });
    }

    long amount;
    long unitPrice;
    try
    {
        unitPrice = PricingCalculator.PhotoUnitRate(photoSizeCode, pricing);
        amount = PricingCalculator.CalculatePhoto(photoCount, photoSizeCode, pricing);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "photo_pricing_unavailable", message = exception.Message });
    }

    var newDocumentId = Guid.NewGuid();
    var outputName = $"photo-sheet-{job.JobNumber}.pdf";
    StoredDocument stored;
    try
    {
        await using var input = file.OpenReadStream();
        stored = await storage.ValidateAndStoreAsync(tenantId, newDocumentId, outputName, input, file.Length, ct);
    }
    catch (DocumentValidationException exception)
    {
        await transaction.RollbackAsync(ct);
        return Results.UnprocessableEntity(new { code = "photo_sheet_not_supported", message = exception.Message });
    }

    if (!string.Equals(stored.Analysis.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
        stored.Analysis.PageCount != expectedPageCount ||
        !PhotoSheetPdfRules.HasExactlyA4PortraitPages(stored.Analysis.PageSizesJson, expectedPageCount))
    {
        await storage.DeleteAsync(stored.StorageKey);
        await transaction.RollbackAsync(ct);
        return Results.UnprocessableEntity(new
        {
            code = "photo_sheet_layout_invalid",
            message = $"The replacement Photo Studio PDF must contain exactly {expectedPageCount} portrait A4 page(s), each with one full-page 300-DPI photo raster."
        });
    }

    var previousStorageKey = document.StorageKey;
    var now = DateTimeOffset.UtcNow;
    if (document.PhotoSourceOriginalFileName is null)
    {
        // Preserve the first uploaded photo as the immutable editing source.
        // Every replacement PDF gets a fresh key; later edits always start from
        // this retained source instead of progressively recompressing a PDF.
        document.PhotoSourceStorageKey = document.StorageKey;
        document.PhotoSourceOriginalFileName = document.OriginalFileName;
        document.PhotoSourceMediaType = document.MediaType;
        document.PhotoSourceSha256 = document.Sha256;
        document.PhotoSourceSizeBytes = document.SizeBytes;
    }
    document.OriginalFileName = outputName;
    document.MediaType = stored.Analysis.MediaType;
    document.FileExtension = stored.Analysis.Extension;
    document.SizeBytes = stored.SizeBytes;
    document.Sha256 = stored.Sha256;
    document.StorageKey = stored.StorageKey;
    document.StorageMode = "CLOUD";
    document.AgentDeviceId = null;
    document.PageSizesJson = stored.Analysis.PageSizesJson;
    document.PhotoEditorSettingsJson = editorSettingsJson;
    document.PurgedUtc = null;

    job.PageCount = expectedPageCount;
    job.Copies = 1;
    job.ColourMode = "Colour";
    job.PaperSize = "A4";
    job.IsDuplex = false;
    job.Orientation = "AUTO";
    job.PrintKind = "PHOTO";
    job.PhotoSizeCode = photoSizeCode;
    job.PhotoCount = photoCount;
    job.PhotosPerPage = photosPerPage;
    job.PhotoUnitPriceMinorSnapshot = unitPrice;
    job.Currency = "INR";
    job.QuotedAmountMinor = amount;
    job.PageRefundAllocationsJson = JsonSerializer.Serialize(
        PricingCalculator.CalculatePhotoPageRefundAllocations(photoCount, photoSizeCode, photosPerPage, unitPrice));
    job.PricingVersion = pricing.Version;
    job.QuoteExpiresUtc = now.AddMinutes(10);
    job.UpdatedUtc = now;

    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = tenantId,
        PrintJobId = job.Id,
        PreviousStatus = job.Status,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = GetActorId(principal),
        ReasonCode = "SHOP_PHOTO_ENHANCED",
        CreatedUtc = now
    });

    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch
    {
        await transaction.RollbackAsync(ct);
        await storage.DeleteAsync(stored.StorageKey);
        throw;
    }

    if (!string.Equals(previousStorageKey, document.PhotoSourceStorageKey, StringComparison.Ordinal))
    {
        try
        {
            await storage.DeleteAsync(previousStorageKey);
        }
        catch (IOException)
        {
            // The database now points to the new revision. A failed old-file cleanup
            // must not turn a successful customer-visible revision into an error.
        }
    }

    return Results.Ok(new
    {
        job.Id,
        job.Status,
        job.PageCount,
        job.PrintKind,
        job.PhotoSizeCode,
        job.PhotoCount,
        job.PhotosPerPage,
        job.PhotoUnitPriceMinorSnapshot,
        job.QuotedAmountMinor,
        job.PricingVersion,
        job.QuoteExpiresUtc,
        document.OriginalFileName,
        document.MediaType,
        document.SizeBytes,
        document.PhotoEditorSettingsJson
    });
});

shop.MapGet("/print-jobs/{jobId:guid}/preview-source", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    HttpContext context,
    CancellationToken ct) =>
{
    // Preview grants are bearer credentials; never let browsers or proxies cache them.
    context.Response.Headers.CacheControl = "private, no-store";
    context.Response.Headers.Pragma = "no-cache";

    if (!TryGetTenantId(principal, out var tenantId))
        return Results.Forbid();

    if (await db.PrintJobs.AsNoTracking().AnyAsync(job =>
        job.Id == jobId && job.TenantId == tenantId && job.PrivatePrinting, ct))
        return Results.Conflict(new { code = "private_printing_source_locked", message = "The customer selected Private Printing. The shop cannot preview or retrieve the original document." });

    var variant = context.Request.Query["variant"].FirstOrDefault();
    var outputPreview = string.Equals(variant, "output", StringComparison.OrdinalIgnoreCase);
    var photoSourcePreview = string.Equals(variant, "photo-source", StringComparison.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(variant) && !outputPreview && !photoSourcePreview &&
        !string.Equals(variant, "original", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { code = "local_preview_variant_invalid" });

    Guid agentId;
    if (outputPreview)
    {
        var output = await db.PrintJobs.AsNoTracking()
            .Where(job => job.Id == jobId && job.TenantId == tenantId)
            .Select(job => new
            {
                job.Status,
                job.OutputType,
                job.OutputFileName,
                job.OutputSha256,
                job.OutputSizeBytes,
                job.OutputCompletedUtc,
                job.ClaimedByAgentDeviceId
            })
            .SingleOrDefaultAsync(ct);

        if (output is null)
            return Results.NotFound();
        if (output.Status != "COMPLETED" || output.OutputType != "PDF_EXPORT" ||
            output.OutputCompletedUtc is null ||
            string.IsNullOrWhiteSpace(output.OutputFileName) ||
            output.OutputFileName.Length > 255 ||
            output.OutputFileName.Contains('/') || output.OutputFileName.Contains('\\') ||
            !string.Equals(Path.GetExtension(output.OutputFileName), ".pdf", StringComparison.OrdinalIgnoreCase) ||
            output.OutputSizeBytes is not long outputSize || outputSize <= 0 || outputSize > 100L * 1024 * 1024 ||
            output.OutputSha256 is not { Length: 64 } || !output.OutputSha256.All(Uri.IsHexDigit) ||
            output.ClaimedByAgentDeviceId is not Guid outputAgentId)
        {
            return Results.Conflict(new
            {
                code = "pdf_export_preview_unavailable",
                message = "A verified, completed PDF export is not available for this job yet."
            });
        }

        agentId = outputAgentId;
    }
    else
    {
        var document = await db.PrintDocuments.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
            .Select(item => new
            {
                item.StorageMode,
                item.AgentDeviceId,
                item.MediaType,
                item.OriginalFileName,
                item.SizeBytes,
                item.Sha256,
                item.PhotoSourceMediaType,
                item.PhotoSourceOriginalFileName,
                item.PhotoSourceSha256,
                item.PhotoSourceSizeBytes
            })
            .SingleOrDefaultAsync(ct);
        if (document is null)
            return Results.NotFound();
        if (photoSourcePreview)
        {
            var sourceMediaType = document.PhotoSourceMediaType ?? document.MediaType;
            var sourceName = document.PhotoSourceOriginalFileName ?? document.OriginalFileName;
            var sourceSize = document.PhotoSourceSizeBytes ?? document.SizeBytes;
            var sourceHash = document.PhotoSourceSha256 ?? document.Sha256;
            if (sourceMediaType is not ("image/jpeg" or "image/png") ||
                string.IsNullOrWhiteSpace(sourceName) || sourceName.Length > 255 ||
                sourceSize <= 0 ||
                string.IsNullOrWhiteSpace(sourceHash) || sourceHash.Length != 64 || !sourceHash.All(Uri.IsHexDigit))
                return Results.Conflict(new { code = "photo_source_unavailable", message = "The original JPG or PNG photo is not available for editing." });
        }
        if (!string.Equals(document.StorageMode, "LOCAL_AGENT", StringComparison.Ordinal))
            return Results.Ok(new { Mode = "CLOUD" });
        if (document.AgentDeviceId is not Guid documentAgentId)
        {
            return Results.Conflict(new
            {
                code = "local_preview_agent_missing",
                message = "This local-only document is not associated with a paired print computer."
            });
        }
        agentId = documentAgentId;
    }

    var now = DateTimeOffset.UtcNow;
    var agent = await db.AgentDevices.AsNoTracking()
        .Where(item => item.Id == agentId && item.TenantId == tenantId &&
                       item.RevokedUtc == null && item.Status != "Revoked" &&
                       item.LastSeenUtc != null && item.LastSeenUtc >= now.AddMinutes(-2))
        .Select(item => new { item.Id, item.LocalEndpointsJson })
        .SingleOrDefaultAsync(ct);
    if (agent is null)
    {
        return Results.Conflict(new
        {
            code = "local_preview_agent_offline",
            message = "The paired Windows Print Agent is offline. Reconnect it before previewing this shop-local document."
        });
    }

    var endpoints = ReadLocalAgentEndpoints(agent.LocalEndpointsJson);
    if (endpoints.Length == 0)
    {
        return Results.Conflict(new
        {
            code = "local_preview_endpoint_missing",
            message = "The paired computer has not published a reachable local preview endpoint."
        });
    }

    var expiresUtc = now.AddMinutes(2);
    var grantToken = LocalDocumentPreviewGrant.Issue(
        signingKeyText, issuer, audience, tenantId, jobId, agent.Id, now, expiresUtc);
    return Results.Ok(new
    {
        Mode = "LOCAL_AGENT",
        GrantToken = grantToken,
        Endpoints = endpoints,
        ExpiresUtc = expiresUtc
    });
});

shop.MapGet("/print-jobs/{jobId:guid}/document", async (
    Guid jobId,
    ClaimsPrincipal principal,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var privatePrinting = await db.PrintJobs.AsNoTracking()
        .Where(job => job.TenantId == tenantId && job.Id == jobId)
        .Select(job => (bool?)job.PrivatePrinting)
        .SingleOrDefaultAsync(ct);
    if (privatePrinting is null) return Results.NotFound();
    if (privatePrinting.Value)
        return Results.Conflict(new { code = "private_printing_source_locked", message = "The customer selected Private Printing. The original source document is not available in the shop portal." });

    var variant = context.Request.Query["variant"].FirstOrDefault();
    var photoSourcePreview = string.Equals(variant, "photo-source", StringComparison.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(variant) && !photoSourcePreview)
        return Results.BadRequest(new { code = "document_variant_invalid" });

    var document = await db.PrintDocuments.AsNoTracking()
        .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
        .Select(item => new
        {
            item.StorageKey,
            item.StorageMode,
            item.MediaType,
            item.OriginalFileName,
            item.SizeBytes,
            item.Sha256,
            item.PurgedUtc,
            item.PhotoSourceStorageKey,
            item.PhotoSourceOriginalFileName,
            item.PhotoSourceMediaType,
            item.PhotoSourceSha256,
            item.PhotoSourceSizeBytes
        })
        .SingleOrDefaultAsync(ct);
    if (document is null)
    {
        return Results.NotFound();
    }

    if (document.StorageMode == "LOCAL_AGENT")
    {
        return Results.Conflict(new { code = "document_stored_on_local_agent", message = "This document is stored only on the shop's Windows Print Agent; cloud preview is not available." });
    }
    if (document.PurgedUtc is not null)
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    var storageKey = document.StorageKey;
    var mediaType = document.MediaType;
    if (photoSourcePreview)
    {
        mediaType = document.PhotoSourceMediaType ?? document.MediaType;
        storageKey = document.PhotoSourceStorageKey ?? document.StorageKey;
        var sourceHash = document.PhotoSourceSha256 ?? document.Sha256;
        var sourceSize = document.PhotoSourceSizeBytes ?? document.SizeBytes;
        if (mediaType is not ("image/jpeg" or "image/png") ||
            sourceSize <= 0 ||
            string.IsNullOrWhiteSpace(sourceHash) ||
            sourceHash.Length != 64 || !sourceHash.All(Uri.IsHexDigit))
            return Results.Conflict(new { code = "photo_source_unavailable", message = "The original JPG or PNG photo is not available for editing." });
    }

    MemoryStream content;
    try
    {
        content = storage.OpenRead(storageKey);
    }
    catch (IOException)
    {
        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The stored document could not be opened.");
    }

    context.Response.Headers["Cache-Control"] = "private, no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "sandbox";
    return Results.File(content, mediaType, enableRangeProcessing: false);
});

shop.MapPost("/print-batches/{batchId:guid}/review", async (
    Guid batchId,
    ApplyPrintBatchReviewRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
        return Results.Forbid();

    var reviewedIds = request.ReviewedJobIds?.Distinct().ToArray() ?? [];
    var acceptedIds = request.AcceptedJobIds?.Distinct().ToArray() ?? [];
    if (reviewedIds.Length is < 1 or > UploadLimits.MaxBatchFiles ||
        acceptedIds.Length > reviewedIds.Length ||
        acceptedIds.Except(reviewedIds).Any())
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["review"] = [$"Review between 1 and {UploadLimits.MaxBatchFiles} files. Accepted file IDs must be part of the reviewed batch."]
        });

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var allBatchJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
        .ToListAsync(ct);
    if (allBatchJobs.Select(job => job.CustomerSessionId).Distinct().Count() > 1)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_customer_session_mismatch",
            message = "This batch contains files from more than one customer session. No file decisions were changed; refresh the queue and contact support."
        });
    }

    var lockedJobs = allBatchJobs
        .Where(job => job.Status is "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED" or "REJECTED")
        .ToList();
    var lockedJobIds = lockedJobs.Select(job => job.Id).ToHashSet();
    if (lockedJobs.Count != reviewedIds.Length || reviewedIds.Any(id => !lockedJobIds.Contains(id)))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_review_changed",
            message = "The files awaiting review changed. Refresh the full batch before applying these decisions."
        });
    }

    var batchPaymentStarted = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
        item.TenantId == tenantId && item.BatchId == batchId && item.Status != "REJECTED", ct);
    if (batchPaymentStarted)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_already_started",
            message = "Checkout has already started for this batch. Do not change file decisions here; use the file-level refund workflow after payment verification."
        });
    }

    var paymentProtectedJobIds = await db.PrintPayments.AsNoTracking()
        .Where(item => item.TenantId == tenantId &&
                       reviewedIds.Contains(item.PrintJobId) &&
                       (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                        item.Status == "CUSTOMER_REPORTED" ||
                        item.Status == "CASH_AWAITING_PAYMENT" ||
                        item.Status == "VERIFIED" ||
                        item.Status == "PARTIALLY_REFUNDED"))
        .Select(item => item.PrintJobId)
        .Distinct()
        .ToListAsync(ct);
    var protectedJobIds = paymentProtectedJobIds.ToHashSet();

    if (request.PdfBrandingEnabled && lockedJobs.Any(job => job.PrintKind == "ID_DOCUMENT"))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "id_document_branding_not_allowed",
            message = "ID card sheets cannot be modified with SmartPrint PDF branding."
        });
    }

    var accepted = acceptedIds.ToHashSet();
    var now = DateTimeOffset.UtcNow;
    foreach (var job in lockedJobs)
    {
        if (job.PdfBrandingEnabled == request.PdfBrandingEnabled)
            continue;

        job.PdfBrandingEnabled = request.PdfBrandingEnabled;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = job.Status,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = GetActorId(principal),
            ReasonCode = request.PdfBrandingEnabled
                ? "SHOP_BATCH_PDF_BRANDING_ENABLED"
                : "SHOP_BATCH_PDF_BRANDING_DISABLED",
            CreatedUtc = now
        });
    }
    var acceptedCount = 0;
    var rejectedCount = 0;
    var quoteRefreshRequiredCount = 0;
    foreach (var job in lockedJobs)
    {
        var shouldAccept = accepted.Contains(job.Id);
        if (shouldAccept)
        {
            acceptedCount++;
            if (job.Status == "REJECTED")
            {
                if (protectedJobIds.Contains(job.Id))
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        code = "batch_file_has_payment_history",
                        jobId = job.Id,
                        jobNumber = job.JobNumber,
                        message = "This rejected file already has an active or verified payment. Use payment verification/refund review instead of restoring it to checkout."
                    });
                }

                var previousStatus = job.Status;
                var quoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
                job.Status = quoteExpired ? "QUOTE_EXPIRED" : "AWAITING_PAYMENT";
                if (quoteExpired) quoteRefreshRequiredCount++;
                job.FailureCode = null;
                job.UpdatedUtc = now;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = tenantId,
                    PrintJobId = job.Id,
                    PreviousStatus = previousStatus,
                    NewStatus = job.Status,
                    ActorType = "ShopUser",
                    ActorId = GetActorId(principal),
                    ReasonCode = quoteExpired
                        ? "SHOP_RESTORED_REJECTED_BATCH_FILE_REQUOTE_REQUIRED"
                        : "SHOP_RESTORED_REJECTED_BATCH_FILE",
                    CreatedUtc = now
                });
            }
            else if (job.Status == "AWAITING_SHOP_APPROVAL")
            {
                var previousStatus = job.Status;
                var quoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
                job.Status = quoteExpired ? "QUOTE_EXPIRED" : "AWAITING_PAYMENT";
                if (quoteExpired) quoteRefreshRequiredCount++;
                job.FailureCode = null;
                job.UpdatedUtc = now;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = tenantId,
                    PrintJobId = job.Id,
                    PreviousStatus = previousStatus,
                    NewStatus = job.Status,
                    ActorType = "ShopUser",
                    ActorId = GetActorId(principal),
                    ReasonCode = quoteExpired
                        ? "SHOP_REVIEW_FOUND_EXPIRED_BATCH_QUOTE"
                        : "SHOP_APPROVED_BATCH_AWAITING_PAYMENT",
                    CreatedUtc = now
                });
            }
            else if (job.Status is "AWAITING_PAYMENT" or "PAYMENT_REJECTED")
            {
                if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
                {
                    var previousStatus = job.Status;
                    job.Status = "QUOTE_EXPIRED";
                    job.FailureCode = null;
                    job.UpdatedUtc = now;
                    quoteRefreshRequiredCount++;
                    db.PrintJobEvents.Add(new PrintJobEvent
                    {
                        TenantId = tenantId,
                        PrintJobId = job.Id,
                        PreviousStatus = previousStatus,
                        NewStatus = job.Status,
                        ActorType = "ShopUser",
                        ActorId = GetActorId(principal),
                        ReasonCode = "SHOP_REVIEW_FOUND_EXPIRED_BATCH_QUOTE",
                        CreatedUtc = now
                    });
                }
            }
            else
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_file_not_reviewable",
                    jobId = job.Id,
                    jobNumber = job.JobNumber,
                    message = "A file has already moved beyond the pre-print review stage. Refresh the batch before changing its decision."
                });
            }
        }
        else
        {
            rejectedCount++;
            if (job.Status == "REJECTED")
                continue;

            if (protectedJobIds.Contains(job.Id))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_file_has_payment_attempt",
                    jobId = job.Id,
                    jobNumber = job.JobNumber,
                    message = "This file already has an active or verified individual payment. Resolve that payment first, then refresh the batch review."
                });
            }

            if (job.Status is not ("AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED"))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_file_requires_refund_review",
                    jobId = job.Id,
                    jobNumber = job.JobNumber,
                    message = "A paid or handed-off file cannot be rejected through pre-payment review. Use its file-level refund review before printer handoff."
                });
            }

            var previousStatus = job.Status;
            job.Status = "REJECTED";
            job.FailureCode = "SHOP_REJECTED";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "ShopUser",
                ActorId = GetActorId(principal),
                ReasonCode = "SHOP_REJECTED_BATCH_FILE",
                CreatedUtc = now
            });
        }
    }

    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_review_changed_concurrently",
            message = "The batch changed while the review was being applied. Refresh the batch and confirm the current file decisions."
        });
    }

    return Results.Ok(new
    {
        BatchId = batchId,
        ReviewedCount = lockedJobs.Count,
        AcceptedCount = acceptedCount,
        RejectedCount = rejectedCount,
        QuoteRefreshRequiredCount = quoteRefreshRequiredCount,
        UpdatedUtc = now,
        NextStep = quoteRefreshRequiredCount > 0
            ? $"The batch review was applied. {quoteRefreshRequiredCount} accepted file(s) have expired quotes and must be re-quoted before checkout; rejected files remain excluded."
            : "The batch review was applied together. Accepted files can proceed to one batch checkout; rejected files are excluded from printing."
    });
});

shop.MapPost("/print-jobs/{jobId:guid}/approve", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (job.Status == "AWAITING_PAYMENT")
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc, DuplicateRequest = true });
    }

    if (job.Status != "AWAITING_SHOP_APPROVAL")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "job_not_ready_for_shop_approval",
            message = "The customer must accept a valid quote before the shop can approve this job."
        });
    }

    var now = DateTimeOffset.UtcNow;
    if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
    {
        var expiredFromStatus = job.Status;
        job.Status = "QUOTE_EXPIRED";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = expiredFromStatus,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = GetActorId(principal),
            ReasonCode = "QUOTE_EXPIRED_BEFORE_SHOP_APPROVAL",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Conflict(new { code = "quote_expired", message = "The customer must request a new quote before this job can proceed." });
    }

    var previousStatus = job.Status;
    job.Status = "AWAITING_PAYMENT";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = GetActorId(principal),
        ReasonCode = "SHOP_APPROVED_AWAITING_PAYMENT",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    var paymentSettings = await db.ShopPaymentProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
    var hasConfiguredPaymentMethod = paymentSettings?.CashEnabled == true ||
        (paymentSettings?.UpiEnabled == true &&
         !string.IsNullOrWhiteSpace(paymentSettings.UpiId) &&
         !string.IsNullOrWhiteSpace(paymentSettings.PayeeName));

    return Results.Ok(new
    {
        job.Id,
        job.JobNumber,
        job.Status,
        job.Currency,
        job.QuotedAmountMinor,
        job.UpdatedUtc,
        NextStep = hasConfiguredPaymentMethod
            ? "Quote approved. The customer can now choose an enabled shop payment method. Verify payment before assigning a printer."
            : "Quote approved, but no shop payment method is enabled. Configure UPI or cash at counter in Payments before the customer can proceed."
    });
});

shop.MapPost("/print-jobs/{jobId:guid}/reject", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (job.Status == "REJECTED")
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, DuplicateRequest = true });
    }

    if (job.Status is not ("AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT"))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "job_not_ready_for_rejection",
            message = "Only a file awaiting approval or checkout can be rejected here. Once batch payment starts, use the file-level refund review after payment verification."
        });
    }

    var previousStatus = job.Status;
    var now = DateTimeOffset.UtcNow;
    job.Status = "REJECTED";
    job.FailureCode = "SHOP_REJECTED";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = GetActorId(principal),
        ReasonCode = "SHOP_REJECTED",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc });
});

shop.MapPost("/print-jobs/{jobId:guid}/refund-preview", async (
    Guid jobId, PrintRefundPreviewRequest request, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
    var job = await db.PrintJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == tenantId, ct);
    if (job is null) return Results.NotFound();
    if (!PrintRefundRules.CanRefundBeforePrintHandoff(job))
        return Results.Conflict(new { code = "file_not_refundable_after_print_handoff", message = "Page review and refunds must be finalized before the file is handed to the shop computer." });
    var payment = await db.PrintPayments.AsNoTracking().SingleOrDefaultAsync(
        x => x.TenantId == tenantId && x.PrintJobId == job.Id && (x.Status == "VERIFIED" || x.Status == "PARTIALLY_REFUNDED"), ct);
    if (!PrintRefundRules.HasRefundableVerifiedPayment(job, payment))
        return Results.Conflict(new { code = "verified_payment_not_found", message = "This file has no remaining verified payment to refund." });
    try
    {
        var previous = JsonSerializer.Deserialize<int[]>(job.RejectedPageNumbersJson) ?? [];
        var selected = PrintRefundRules.NormalizeRejectedPages(job.PageCount, request.RejectedPageNumbers, previous);
        var allocations = PrintRefundRules.NormalizePageAllocations(payment!.AmountMinor, job.PageCount,
            JsonSerializer.Deserialize<long[]>(job.PageRefundAllocationsJson));
        var additional = PrintRefundRules.CalculateAdditionalRefundAmount(payment.AmountMinor, payment.RefundedAmountMinor, selected, allocations);
        var cumulative = checked(payment.RefundedAmountMinor + additional);
        return Results.Ok(new { job.Id, job.PageCount, RejectedPageNumbers = selected, RejectedPageCount = selected.Length,
            AcceptedPageCount = job.PageCount - selected.Length, OriginalAmountMinor = payment.AmountMinor,
            PreviouslyRefundedAmountMinor = payment.RefundedAmountMinor, AdditionalRefundAmountMinor = additional,
            CumulativeRefundAmountMinor = cumulative, RemainingPaidAmountMinor = payment.AmountMinor - cumulative, payment.Currency });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or JsonException)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pages"] = [ex.Message] });
    }
});

shop.MapPost("/print-jobs/{jobId:guid}/refund", async (
    Guid jobId, RecordPrintRefundRequest request, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
    var reason = request.Reason?.Trim();
    var method = request.RefundMethod?.Trim().ToUpperInvariant();
    var reference = request.RefundReference?.Trim();
    var key = request.IdempotencyKey?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 3 or > 300 ||
        method is not ("UPI" or "CASH") ||
        (method == "UPI" && (string.IsNullOrWhiteSpace(reference) || reference.Length is < 4 or > 100)) ||
        reference?.Length > 100 || string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 100 || !request.ConfirmedSent)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["refund"] = ["Confirm the refund was sent, choose UPI or cash, provide a reason and unique key; UPI refunds need a transaction reference."] });

    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var duplicate = await db.PrintRefunds.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key, ct);
    if (duplicate is not null)
    {
        await tx.RollbackAsync(ct);
        if (duplicate.PrintJobId != jobId)
            return Results.Conflict(new { code = "refund_idempotency_key_reused", message = "This refund request key is already associated with another print file." });
        return Results.Ok(new { duplicate.Id, duplicate.PrintJobId, duplicate.PrintPaymentId, duplicate.AmountMinor, duplicate.Currency, duplicate.Method, duplicate.Status, duplicate.Reference, duplicate.Reason, duplicate.RefundedUtc, DuplicateRequest = true });
    }
    // Serialize refund recording with the Agent's atomic claim UPDATE.
    // The first transaction to lock the row wins; the second reads the committed state.
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"""SELECT * FROM "PrintJobs" WHERE "Id" = {jobId} AND "TenantId" = {tenantId} FOR UPDATE""")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null) { await tx.RollbackAsync(ct); return Results.NotFound(); }
    if (!PrintRefundRules.CanRefundBeforePrintHandoff(job))
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { code = "file_not_refundable_after_print_handoff", message = "Page rejection and refunds must be finalized before local computer handoff or printing." });
    }
    var payment = await db.PrintPayments.SingleOrDefaultAsync(
        x => x.TenantId == tenantId && x.PrintJobId == job.Id && (x.Status == "VERIFIED" || x.Status == "PARTIALLY_REFUNDED"), ct);
    if (!PrintRefundRules.HasRefundableVerifiedPayment(job, payment))
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { code = "verified_payment_not_found", message = "This file has no remaining verified payment to refund." });
    }

    int[] rejected;
    long additional;
    long cumulative;
    try
    {
        var previous = JsonSerializer.Deserialize<int[]>(job.RejectedPageNumbersJson) ?? [];
        var selected = request.RejectedPageNumbers ?? Enumerable.Range(1, job.PageCount).ToArray();
        rejected = PrintRefundRules.NormalizeRejectedPages(job.PageCount, selected, previous);
        if (rejected.Length == previous.Length)
        {
            await tx.RollbackAsync(ct);
            return Results.Conflict(new { code = "no_new_pages_rejected", message = "Select at least one additional page to reject." });
        }
        var allocations = PrintRefundRules.NormalizePageAllocations(payment!.AmountMinor, job.PageCount,
            JsonSerializer.Deserialize<long[]>(job.PageRefundAllocationsJson));
        additional = PrintRefundRules.CalculateAdditionalRefundAmount(payment.AmountMinor, payment.RefundedAmountMinor, rejected, allocations);
        cumulative = checked(payment.RefundedAmountMinor + additional);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or JsonException)
    {
        await tx.RollbackAsync(ct);
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pages"] = [ex.Message] });
    }
    if (additional <= 0)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { code = "refund_amount_below_one_minor_unit", message = "The selected pages do not produce an additional refund under the original quote." });
    }

    var refundActorId = GetActorId(principal);
    if (refundActorId is not Guid actorId)
    {
        await tx.RollbackAsync(ct);
        return Results.Forbid();
    }

    var now = DateTimeOffset.UtcNow;
    var previousJobStatus = job.Status;
    var fullyRefunded = cumulative >= payment!.AmountMinor || rejected.Length == job.PageCount;
    var refund = new PrintRefund { TenantId = tenantId, PrintJobId = job.Id, PrintPaymentId = payment.Id, IdempotencyKey = key!,
        AmountMinor = additional, Currency = payment.Currency, Method = method!, Status = "RECORDED_AS_SENT",
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference, Reason = reason!,
        RefundedByUserId = actorId, RefundedUtc = now, CreatedUtc = now };
    job.RejectedPageNumbersJson = JsonSerializer.Serialize(rejected);
    job.Status = fullyRefunded ? "REFUNDED" : "PAID_AUTHORISED";
    job.FailureCode = fullyRefunded ? "SHOP_REJECTED_FILE_FULLY_REFUNDED" : "SHOP_REJECTED_PAGES_PARTIALLY_REFUNDED";
    job.UpdatedUtc = now;
    payment.RefundedAmountMinor = cumulative;
    payment.Status = fullyRefunded ? "REFUNDED" : "PARTIALLY_REFUNDED";
    payment.UpdatedUtc = now;
    db.PrintRefunds.Add(refund);
    db.PrintJobEvents.Add(new PrintJobEvent { TenantId = tenantId, PrintJobId = job.Id, PreviousStatus = previousJobStatus,
        NewStatus = job.Status, ActorType = "ShopUser", ActorId = actorId,
        ReasonCode = fullyRefunded ? "SHOP_FILE_FULLY_REFUNDED" : "SHOP_PAGES_REJECTED_PARTIAL_REFUND_RECORDED", CreatedUtc = now });

    try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
    catch (DbUpdateConcurrencyException)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { code = "refund_recorded_concurrently", message = "The payment changed while this refund was recorded. Refresh and preview pages again." });
    }
    catch (DbUpdateException)
    {
        await tx.RollbackAsync(ct);
        var raced = await db.PrintRefunds.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key, ct);
        if (raced is not null)
        {
            if (raced.PrintJobId != jobId)
                return Results.Conflict(new
                {
                    code = "refund_idempotency_key_reused",
                    message = "This refund request key is already associated with another print file."
                });
            return Results.Ok(new { raced.Id, raced.PrintJobId, raced.PrintPaymentId, raced.AmountMinor, raced.Currency, raced.Method, raced.Status, raced.Reference, raced.Reason, raced.RefundedUtc, DuplicateRequest = true });
        }
        throw;
    }
    return Results.Created($"/api/v1/shop/refunds/{refund.Id}", new { refund.Id, refund.PrintJobId, refund.PrintPaymentId, job.JobNumber,
        refund.AmountMinor, refund.Currency, refund.Method, refund.Status, refund.Reference, refund.Reason, refund.RefundedUtc,
        refund.RefundedByUserId, RejectedPageNumbers = rejected, CumulativeRefundAmountMinor = cumulative,
        RemainingPaidAmountMinor = payment.AmountMinor - cumulative });
});

var shopAgents = app.MapGroup("/api/v1/shop").RequireAuthorization()
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));

shopAgents.MapPost("/agent-enrollment-codes", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var now = DateTimeOffset.UtcNow;
    var code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
    var enrollment = new AgentEnrollmentCode
    {
        TenantId = tenantId,
        CodeHash = HashOpaqueToken(code),
        CreatedUtc = now,
        ExpiresUtc = now.AddMinutes(10),
        CreatedByUserId = GetActorId(principal)
    };

    db.AgentEnrollmentCodes.Add(enrollment);
    await db.SaveChangesAsync(ct);
    return Results.Created("/api/v1/shop/agent-devices", new CreateAgentEnrollmentCodeResponse(code, enrollment.ExpiresUtc));
});

shopAgents.MapGet("/agent-devices", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    var devices = await db.AgentDevices.AsNoTracking()
        .Where(device => device.TenantId == tenantId)
        .OrderByDescending(device => device.LastSeenUtc)
        .Select(device => new
        {
            device.Id,
            device.DeviceName,
            Status = device.RevokedUtc != null ? "Revoked" :
                device.LastSeenUtc != null && device.LastSeenUtc >= onlineAfter ? "Online" : "Offline",
            device.CreatedUtc,
            device.LastSeenUtc,
            device.RevokedUtc,
            device.LocalEndpointsJson,
            PrinterCount = db.PrinterDevices.Count(printer =>
                printer.TenantId == tenantId && printer.AgentDeviceId == device.Id)
        })
        .ToListAsync(ct);
    return Results.Ok(devices.Select(device => new
    {
        device.Id, device.DeviceName, device.Status, device.CreatedUtc,
        device.LastSeenUtc, device.RevokedUtc, device.PrinterCount,
        LocalEndpoints = ReadLocalAgentEndpoints(device.LocalEndpointsJson)
    }));
});

shopAgents.MapPatch("/agent-devices/{agentId:guid}/revoke", async (
    Guid agentId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var device = await db.AgentDevices.SingleOrDefaultAsync(
        item => item.Id == agentId && item.TenantId == tenantId, ct);
    if (device is null)
    {
        return Results.NotFound();
    }

    if (device.RevokedUtc is null)
    {
        device.RevokedUtc = DateTimeOffset.UtcNow;
        device.Status = "Revoked";
        await db.PrinterDevices.Where(printer => printer.TenantId == tenantId && printer.AgentDeviceId == agentId)
            .ExecuteUpdateAsync(update => update.SetProperty(printer => printer.Status, "AgentRevoked"), ct);
        await db.SaveChangesAsync(ct);
    }

    return Results.Ok(new { device.Id, device.Status, device.RevokedUtc });
});

var agentApi = app.MapGroup("/api/v1/agent").AllowAnonymous()
    .RequireRateLimiting("agent-api")
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));

agentApi.MapPost("/enroll", async (
    AgentEnrollmentRequest request,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 100 ||
        string.IsNullOrWhiteSpace(request.Fingerprint) ||
        request.Fingerprint.Length > 256 ||
        string.IsNullOrWhiteSpace(request.DeviceName) ||
        request.DeviceName.Trim().Length > 160)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["enrollment"] = ["A valid enrollment code, device fingerprint and device name are required."]
        });
    }

    var now = DateTimeOffset.UtcNow;
    var codeHash = HashOpaqueToken(request.Code.Trim());
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var enrollment = await db.AgentEnrollmentCodes.SingleOrDefaultAsync(
        item => item.CodeHash == codeHash && item.UsedUtc == null && item.ExpiresUtc > now, ct);
    if (enrollment is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Unauthorized();
    }

    var fingerprintHash = HashOpaqueToken(request.Fingerprint);

    // Serialize enrollments for the same physical Windows identity across tenants.
    // This prevents two shops from claiming the same PC during concurrent activation.
    await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtextextended({fingerprintHash}, 0))", ct);

    if (await db.AgentDevices.AnyAsync(
        device => device.FingerprintHash == fingerprintHash && device.RevokedUtc == null, ct))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "agent_already_enrolled",
            message = "This computer is still paired with an active SmartPrint shop. Ask the owner of that shop to revoke its pairing before reconnecting it."
        });
    }

    await entitlements.AcquireQuotaLockAsync(enrollment.TenantId, TenantQuotaResource.AgentDevice, ct);
    var quota = await entitlements.CheckCapacityAsync(enrollment.TenantId, TenantQuotaResource.AgentDevice, ct);
    if (!quota.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
    }

    var claimed = await db.AgentEnrollmentCodes
        .Where(item => item.Id == enrollment.Id && item.UsedUtc == null && item.ExpiresUtc > now)
        .ExecuteUpdateAsync(update => update.SetProperty(item => item.UsedUtc, now), ct);
    if (claimed != 1)
    {
        await transaction.RollbackAsync(ct);
        return Results.Unauthorized();
    }

    var rawSecret = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    var device = new AgentDevice
    {
        TenantId = enrollment.TenantId,
        DeviceName = request.DeviceName.Trim(),
        FingerprintHash = fingerprintHash,
        SecretHash = HashOpaqueToken(rawSecret),
        Status = "Online",
        CreatedUtc = now,
        LastSeenUtc = now
    };
    db.AgentDevices.Add(device);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Created("/api/v1/agent/heartbeat",
        new AgentEnrollmentResponse(device.Id, rawSecret, device.CreatedUtc));
});


agentApi.MapGet("/jobs/private-purge", async (
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var jobIds = await db.PrintJobs.AsNoTracking()
        .Where(job => job.TenantId == device.TenantId &&
                      job.ClaimedByAgentDeviceId == device.Id &&
                      job.PrivatePrinting &&
                      ((job.Status == "COMPLETED" &&
                        job.OutputType == "PHYSICAL" &&
                        job.OutputConfirmedUtc != null) ||
                       (job.Status == "TEST_OUTPUT_READY" &&
                        job.OutputType == PrivatePrintingOutputPolicy.TestVirtual &&
                        job.OutputCompletedUtc != null)) &&
                      job.PrivateDocumentPurgeRequestedUtc != null &&
                      job.PrivateDocumentPurgedUtc == null)
        .OrderBy(job => job.PrivateDocumentPurgeRequestedUtc)
        .Select(job => job.Id)
        .Take(100)
        .ToListAsync(ct);

    return Results.Ok(new { JobIds = jobIds });
});

agentApi.MapPost("/jobs/{jobId:guid}/private-document-purged", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == jobId && item.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.PrivateDocumentPurgedUtc is not null)
        return Results.Ok(new { job.Id, job.PrivateDocumentPurgedUtc, DuplicateRequest = true });

    var physicalOutputConfirmed = job.Status == "COMPLETED" &&
        job.OutputType == "PHYSICAL" && job.OutputConfirmedUtc is not null;
    var testOutputVerified = job.Status == "TEST_OUTPUT_READY" &&
        job.OutputType == PrivatePrintingOutputPolicy.TestVirtual && job.OutputCompletedUtc is not null;
    if (!job.PrivatePrinting ||
        (!physicalOutputConfirmed && !testOutputVerified) ||
        job.PrivateDocumentPurgeRequestedUtc is null ||
        job.ClaimedByAgentDeviceId != device.Id)
    {
        return Results.Conflict(new
        {
            code = "private_document_purge_not_authorized",
            message = "The private source can be purged only after verified physical output or a separately recorded non-production test output. The source must remain available for reconciliation."
        });
    }

    var document = await db.PrintDocuments.SingleOrDefaultAsync(
        item => item.TenantId == device.TenantId && item.PrintJobId == jobId, ct);
    if (document is null)
        return Results.Conflict(new { code = "private_document_record_missing", message = "The private document record is unavailable; the purge cannot be acknowledged safely." });
    if (document.StorageMode != "LOCAL_AGENT" || document.AgentDeviceId != device.Id)
        return Results.Conflict(new { code = "private_document_storage_mismatch", message = "This private source is not assigned to the acknowledging Agent." });

    var now = DateTimeOffset.UtcNow;
    document.OriginalFileName = "Private document";
    document.MediaType = string.Empty;
    document.FileExtension = string.Empty;
    document.SizeBytes = 0;
    document.Sha256 = string.Empty;
    document.StorageKey = string.Empty;
    document.PageSizesJson = "[]";
    document.AgentDeviceId = null;
    document.PurgedUtc = now;
    job.PrivateDocumentPurgedUtc = now;
    job.UpdatedUtc = now;
    var uploadTicket = await db.LocalPrintUploadTickets.SingleOrDefaultAsync(item =>
        item.TenantId == device.TenantId && item.PrintJobId == jobId && item.PrivatePrinting, ct);
    if (uploadTicket is not null)
    {
        uploadTicket.OriginalFileName = "Private document";
        uploadTicket.MediaType = "application/octet-stream";
        uploadTicket.FileExtension = ".private";
        uploadTicket.SizeBytes = 0;
        uploadTicket.Sha256 = string.Empty;
        // Keep only the one-way grant digest so the customer can reconcile a
        // lost upload response. It cannot authorize document retrieval or
        // cloud recovery after the ticket has completed.
        uploadTicket.BatchGroupName = "Private documents";
        uploadTicket.BatchName = null;
    }
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = job.Status,
        NewStatus = job.Status,
        ActorType = "PrintAgent",
        ActorId = device.Id,
        ReasonCode = "PRIVATE_SOURCE_DOCUMENT_PURGED_AND_METADATA_REDACTED",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.PrivateDocumentPurgedUtc, DuplicateRequest = false });
});

agentApi.MapGet("/private-relays/pending", async (
    HttpContext context,
    SmartPrintDbContext db,
    PrivateRelayWakeupService relayWakeups,
    IHostEnvironment environment,
    int? waitSeconds,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    var allowPrivateVirtualOutput = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct);

    async Task<List<AgentPrivateRelayItem>> ReadPendingItemsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        return await (
            from ticket in db.LocalPrintUploadTickets.AsNoTracking()
            join printer in db.PrinterDevices.AsNoTracking()
                on new { ticket.TenantId, PrinterId = ticket.PrinterDeviceId }
                equals new { printer.TenantId, PrinterId = printer.Id }
            where ticket.TenantId == device.TenantId &&
                  ticket.AgentDeviceId == device.Id &&
                  ticket.PrivatePrinting &&
                  ticket.PrivateRelayUploadedUtc != null &&
                  ticket.PrivateRelayStorageKey != null &&
                  ticket.CompletedUtc == null &&
                  ticket.PrivateRelayFailedUtc == null &&
                  ticket.ExpiresUtc > now &&
                  printer.AgentDeviceId == device.Id &&
                  printer.IsActive &&
                  (printer.OutputMode == "PHYSICAL" ||
                   (allowPrivateVirtualOutput && printer.OutputMode == "PDF_EXPORT")) &&
                  printer.LastSeenUtc != null && printer.LastSeenUtc >= now.AddMinutes(-2) &&
                  (printer.Status == "Ready" || printer.Status == "Busy") &&
                  printer.QueueName != null && printer.QueueName != ""
            orderby ticket.PrivateRelayUploadedUtc
            select new AgentPrivateRelayItem(
                ticket.Id, ticket.PrintJobId, ticket.JobNumber, ticket.PrinterDeviceId,
                printer.DisplayName, printer.QueueName!, ticket.FileExtension, ticket.MediaType,
                ticket.SizeBytes, ticket.Sha256, ticket.ColourMode, ticket.PaperSize,
                ticket.IsDuplex, ticket.Copies, ticket.Orientation, ticket.ExpiresUtc,
                ticket.PrintKind, printer.OutputMode))
            .Take(50)
            .ToListAsync(ct);
    }

    // Subscribe before reading the durable queue. This closes the race where a relay
    // arrives between the empty query and the wait. Notifications merely wake us;
    // PostgreSQL remains the source of truth and the bounded timeout is the fallback.
    var signal = relayWakeups.Subscribe(device.Id);
    var items = await ReadPendingItemsAsync();
    if (items.Count == 0)
    {
        // Keep fallback pickup responsive if LISTEN is unavailable behind a
        // database proxy. With a live listener, notifications normally return
        // immediately and the longer timeout avoids needless idle DB queries.
        var maximumWaitSeconds = relayWakeups.IsListening ? 25 : 10;
        var boundedWaitSeconds = Math.Clamp(waitSeconds ?? maximumWaitSeconds, 0, maximumWaitSeconds);
        await relayWakeups.WaitForSignalAsync(
            signal, TimeSpan.FromSeconds(boundedWaitSeconds), ct);
        items = await ReadPendingItemsAsync();
    }

    return Results.Ok(new AgentPrivateRelayPendingResponse(items));
}).RequireRateLimiting("agent-api");

agentApi.MapGet("/private-relays/{ticketId:guid}/content", async (
    Guid ticketId,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    var relay = await db.LocalPrintUploadTickets.AsNoTracking()
        .Where(item => item.Id == ticketId &&
                       item.TenantId == device.TenantId &&
                       item.AgentDeviceId == device.Id &&
                       item.PrivatePrinting &&
                       item.CompletedUtc == null &&
                       item.PrivateRelayFailedUtc == null &&
                       item.PrivateRelayUploadedUtc != null &&
                       item.ExpiresUtc > DateTimeOffset.UtcNow)
        .Select(item => new
        {
            item.PrivateRelayStorageKey,
            item.PrivateRelayWrappedKey,
            item.PrivateRelayIv,
            item.PrinterDeviceId
        })
        .SingleOrDefaultAsync(ct);
    if (relay is null || string.IsNullOrWhiteSpace(relay.PrivateRelayStorageKey) ||
        string.IsNullOrWhiteSpace(relay.PrivateRelayWrappedKey) ||
        string.IsNullOrWhiteSpace(relay.PrivateRelayIv))
        return Results.NotFound();
    if (!await IsPrivatePrintingOutputQueueReadyAsync(
            db, environment, device.TenantId, device.Id, relay.PrinterDeviceId, ct))
        return Results.Conflict(new
        {
            code = "private_printing_output_policy_changed",
            message = "The shop's Private Printing output policy or printer readiness changed before the encrypted document could be delivered. No payload was downloaded."
        });

    MemoryStream ciphertext;
    try { ciphertext = storage.OpenRead(relay.PrivateRelayStorageKey); }
    catch (IOException) { return Results.NotFound(); }
    context.Response.Headers.CacheControl = "private, no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Print-Encrypted-Key"] = relay.PrivateRelayWrappedKey;
    context.Response.Headers["X-Print-Encrypted-Iv"] = relay.PrivateRelayIv;
    return Results.File(ciphertext, "application/octet-stream", enableRangeProcessing: false);
}).RequireRateLimiting("agent-api");

agentApi.MapPost("/private-relays/{ticketId:guid}/complete", async (
    Guid ticketId,
    LocalPrintUploadCompletionRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    TenantEntitlementService entitlements,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var ticket = await db.LocalPrintUploadTickets.SingleOrDefaultAsync(item =>
        item.Id == ticketId &&
        item.TenantId == device.TenantId &&
        item.AgentDeviceId == device.Id &&
        item.PrivatePrinting, ct);
    if (ticket is null) return Results.NotFound();

    async Task<IResult> ExistingResultAsync(bool duplicate)
    {
        var job = await db.PrintJobs.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        var document = await db.PrintDocuments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        if (job is null || document is null)
            return Results.Conflict(new { code = "private_relay_completion_inconsistent", message = "The shop's secure upload is being reconciled. Retry the same file." });

        return Results.Ok(new
        {
            AlreadyCompleted = duplicate,
            UploadResult = new
            {
                Job = new
                {
                    job.Id, job.PrivatePrinting, job.PrintKind, job.JobNumber, job.Status, job.PageCount,
                    job.Copies, job.ColourMode, job.PaperSize, job.IsDuplex, job.Orientation,
                    job.PhotoSizeCode, job.PhotoCount, job.PhotosPerPage,
                    job.PricingVersion, job.Currency, job.QuotedAmountMinor, job.CreatedUtc
                },
                Document = new
                {
                    document.Id, OriginalFileName = "Private document", document.MediaType,
                    document.SizeBytes, document.Sha256, document.PageSizesJson,
                    document.StorageMode
                },
                DuplicateRequest = duplicate
            }
        });
    }

    if (ticket.CompletedUtc is not null)
        return await ExistingResultAsync(true);

    if (!await IsPrivatePrintingOutputQueueReadyAsync(
            db, environment, ticket.TenantId, device.Id, ticket.PrinterDeviceId, ct))
        return Results.Conflict(new
        {
            code = "private_printing_output_policy_changed",
            message = "The shop's Private Printing output policy or printer readiness changed before the encrypted handoff completed. The protected local copy is retained for safe retry."
        });

    if (ticket.PrivateRelayFailedUtc is not null ||
        string.IsNullOrWhiteSpace(ticket.PrivateRelayStorageKey) ||
        ticket.PrivateRelayUploadedUtc is null ||
        ticket.ExpiresUtc <= DateTimeOffset.UtcNow)
        return Results.Conflict(new { code = "private_relay_not_available", message = "The encrypted relay is no longer available for this upload." });

    var sha = request.Sha256?.Trim().ToLowerInvariant();
    if (request.PageCount is < 1 or > 500 ||
        string.IsNullOrWhiteSpace(request.PageSizesJson) ||
        request.PageSizesJson.Length is < 2 or > 200_000 ||
        !string.Equals(request.FileExtension, ticket.FileExtension, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(request.MediaType, ticket.MediaType, StringComparison.OrdinalIgnoreCase) ||
        request.SizeBytes != ticket.SizeBytes ||
        !string.Equals(sha, ticket.Sha256, StringComparison.OrdinalIgnoreCase))
        return Results.UnprocessableEntity(new { code = "private_document_validation_mismatch", message = "The shop computer's document check did not match the original upload." });

    try
    {
        using var parsedSizes = JsonDocument.Parse(request.PageSizesJson);
        if (parsedSizes.RootElement.ValueKind != JsonValueKind.Array ||
            parsedSizes.RootElement.GetArrayLength() != request.PageCount)
            return Results.UnprocessableEntity(new { code = "private_document_page_analysis_invalid", message = "The shop computer returned invalid page information." });
    }
    catch (JsonException)
    {
        return Results.UnprocessableEntity(new { code = "private_document_page_analysis_invalid", message = "The shop computer returned invalid page information." });
    }

    if (ticket.PrintKind == "ID_DOCUMENT" &&
        (request.PageCount != 1 ||
         !string.Equals(ticket.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
         !IdCardPdfRules.HasExactlyA4PortraitPage(request.PageSizesJson)))
        return Results.UnprocessableEntity(new
        {
            code = "id_document_pdf_invalid",
            message = "The ID card sheet must be a valid, single-page A4 portrait PDF. Recheck the prepared sheet before sending it."
        });

    if (ticket.PrintKind == "PHOTO")
    {
        if (!string.Equals(ticket.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            ticket.PhotoSizeCode is not string photoSizeCode ||
            ticket.PhotoCount is not int photoCount ||
            ticket.PhotosPerPage is not int photosPerPage)
            return Results.UnprocessableEntity(new
            {
                code = "photo_metadata_invalid",
                message = "The photo sheet must be a PDF with complete photo size, quantity and A4 layout details. Select it again from the photo library."
            });
        var expectedPageCount = PricingCalculator.PhotoPageCount(photoCount, photoSizeCode, photosPerPage);
        if (request.PageCount != expectedPageCount ||
            !PhotoSheetPdfRules.HasExactlyA4PortraitPages(request.PageSizesJson, expectedPageCount))
            return Results.UnprocessableEntity(new
            {
                code = "photo_sheet_layout_invalid",
                message = $"The saved Photo Studio sheet must contain exactly {expectedPageCount} portrait A4 page(s), each with one full-page photo raster."
            });
    }

    var subscription = await entitlements.GetForTenantAsync(device.TenantId, ct);
    if (!subscription.IsAccessActive)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This shop's trial or subscription period is no longer active.");
    if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, ticket.SizeBytes))
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "This document exceeds the maximum size configured for this shop's plan.");

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedTickets = await db.LocalPrintUploadTickets.FromSqlInterpolated(
        $"SELECT * FROM \"LocalPrintUploadTickets\" WHERE \"Id\" = {ticket.Id} AND \"TenantId\" = {device.TenantId} AND \"AgentDeviceId\" = {device.Id} FOR UPDATE")
        .ToListAsync(ct);
    var locked = lockedTickets.SingleOrDefault();
    if (locked is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }
    if (locked.CompletedUtc is not null)
    {
        await transaction.CommitAsync(ct);
        return await ExistingResultAsync(true);
    }
    if (locked.PrivateRelayFailedUtc is not null ||
        string.IsNullOrWhiteSpace(locked.PrivateRelayStorageKey) ||
        locked.PrivateRelayUploadedUtc is null ||
        locked.ExpiresUtc <= DateTimeOffset.UtcNow)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "private_relay_not_available", message = "The encrypted relay is no longer available for this upload." });
    }

    await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    var quota = await entitlements.CheckCapacityAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    if (!quota.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
    }

    var now = DateTimeOffset.UtcNow;
    locked.CompletedUtc = now;
    var job = new PrintJob
    {
        Id = locked.PrintJobId,
        TenantId = locked.TenantId,
        BatchId = locked.BatchId,
        BatchGroupName = locked.BatchGroupName,
        BatchName = locked.BatchName,
        QrStationId = locked.QrStationId,
        PrinterDeviceId = locked.PrinterDeviceId,
        CustomerSessionId = locked.CustomerSessionId,
        JobNumber = locked.JobNumber,
        Status = "AWAITING_CONFIGURATION",
        IdempotencyKey = $"LOCAL-{locked.Id:N}",
        PageCount = request.PageCount,
        Copies = locked.Copies,
        ColourMode = locked.ColourMode,
        PaperSize = locked.PaperSize,
        IsDuplex = locked.IsDuplex,
        Orientation = locked.Orientation,
        PrivatePrinting = true,
        PrintKind = locked.PrintKind,
        PhotoSizeCode = locked.PhotoSizeCode,
        PhotoCount = locked.PhotoCount ?? 0,
        PhotosPerPage = locked.PhotosPerPage,
        Currency = "INR",
        QuotedAmountMinor = 0,
        CreatedUtc = now,
        UpdatedUtc = now
    };
    var document = new PrintDocument
    {
        TenantId = locked.TenantId,
        PrintJobId = job.Id,
        OriginalFileName = "Private document",
        MediaType = locked.MediaType,
        FileExtension = locked.FileExtension,
        SizeBytes = locked.SizeBytes,
        Sha256 = locked.Sha256,
        StorageKey = string.Empty,
        StorageMode = "LOCAL_AGENT",
        AgentDeviceId = device.Id,
        PageSizesJson = request.PageSizesJson,
        CreatedUtc = now
    };
    db.PrintJobs.Add(job);
    db.PrintDocuments.Add(document);
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = null,
        NewStatus = job.Status,
        ActorType = "Customer",
        ReasonCode = "PRIVATE_DOCUMENT_VALIDATED_ON_SHOP_AGENT",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    // Agent has already persisted a DPAPI-protected local copy. Delete the
    // opaque cloud relay as soon as that handoff and job record are committed.
    try
    {
        await storage.DeleteAsync(locked.PrivateRelayStorageKey);
        await db.LocalPrintUploadTickets
            .Where(item => item.Id == locked.Id && item.TenantId == device.TenantId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.PrivateRelayStorageKey, (string?)null)
                .SetProperty(item => item.PrivateRelayWrappedKey, (string?)null)
                .SetProperty(item => item.PrivateRelayIv, (string?)null)
                .SetProperty(item => item.PrivateRelayUploadedUtc, (DateTimeOffset?)null), ct);
    }
    catch (IOException)
    {
        // The five-minute private-relay retention worker retries the purge.
    }

    return Results.Ok(new
    {
        AlreadyCompleted = false,
        UploadResult = new
        {
            Job = new
            {
                job.Id, job.PrivatePrinting, job.PrintKind, job.JobNumber, job.Status, job.PageCount,
                job.Copies, job.ColourMode, job.PaperSize, job.IsDuplex, job.Orientation,
                job.PricingVersion, job.Currency, job.QuotedAmountMinor, job.CreatedUtc
            },
            Document = new
            {
                document.Id, OriginalFileName = "Private document", document.MediaType,
                document.SizeBytes, document.Sha256, document.PageSizesJson,
                document.StorageMode
            },
            DuplicateRequest = false
        }
    });
}).RequireRateLimiting("agent-api");

agentApi.MapPost("/private-relays/{ticketId:guid}/reject", async (
    Guid ticketId,
    AgentPrivateRelayRejectRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    if (request.Code is not ("document_not_supported" or "private_document_integrity_failed" or "private_document_decryption_failed") ||
        string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 400)
        return Results.BadRequest(new { code = "private_relay_rejection_invalid" });

    var ticket = await db.LocalPrintUploadTickets.SingleOrDefaultAsync(item =>
        item.Id == ticketId && item.TenantId == device.TenantId &&
        item.AgentDeviceId == device.Id && item.PrivatePrinting, ct);
    if (ticket is null) return Results.NotFound();
    if (ticket.CompletedUtc is not null)
        return Results.Conflict(new { code = "private_relay_already_completed" });
    if (ticket.PrivateRelayFailedUtc is not null)
        return Results.Ok(new { ticket.Id, ticket.PrivateRelayFailedUtc, DuplicateRequest = true });

    if (!string.IsNullOrWhiteSpace(ticket.PrivateRelayStorageKey))
        await storage.DeleteAsync(ticket.PrivateRelayStorageKey);
    ticket.PrivateRelayStorageKey = null;
    ticket.PrivateRelayWrappedKey = null;
    ticket.PrivateRelayIv = null;
    ticket.PrivateRelayUploadedUtc = null;
    ticket.PrivateRelayFailedUtc = DateTimeOffset.UtcNow;
    ticket.PrivateRelayFailureCode = request.Code;
    ticket.PrivateRelayFailureMessage = request.Message.Trim();
    ticket.ExpiresUtc = ticket.PrivateRelayFailedUtc.Value;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { ticket.Id, ticket.PrivateRelayFailedUtc, DuplicateRequest = false });
}).RequireRateLimiting("agent-api");

agentApi.MapPost("/jobs/claim", async (
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    // Queue staging secures a paid document on the paired shop computer; it
    // must not require the physical printer to be Ready. The owner explicitly
    // releases output later, and the dispatch route rechecks live hardware.
    var availablePrinterIds = await db.PrinterDevices.AsNoTracking()
        .Where(printer => printer.TenantId == device.TenantId &&
                          printer.AgentDeviceId == device.Id &&
                          printer.IsActive &&
                          printer.QueueName != null && printer.QueueName != "" &&
                          db.AgentDevices.Any(agent =>
                              agent.Id == device.Id && agent.TenantId == device.TenantId &&
                              agent.RevokedUtc == null && agent.Status != "Revoked" &&
                              agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter))
        .Select(printer => printer.Id)
        .ToListAsync(ct);

    if (availablePrinterIds.Count == 0)
    {
        return Results.Ok(new { Jobs = Array.Empty<object>(), ClaimedUtc = now });
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var claimAccess = await GetAgentClaimAccessAsync(device.TenantId, db, ct);
    if (!claimAccess.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Forbid();
    }
    var suspendedAtUtc = claimAccess.SuspendedAtUtc;
    var candidates = await (
        from job in db.PrintJobs.AsNoTracking()
        join document in db.PrintDocuments.AsNoTracking()
            on new { job.TenantId, PrintJobId = job.Id }
            equals new { document.TenantId, document.PrintJobId }
        join printer in db.PrinterDevices.AsNoTracking()
            on new { job.TenantId, PrinterId = job.PrinterDeviceId!.Value }
            equals new { printer.TenantId, PrinterId = printer.Id }
        where job.TenantId == device.TenantId &&
              (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
              db.PrintPayments.Any(payment =>
                  payment.TenantId == job.TenantId &&
                  payment.PrintJobId == job.Id &&
                  (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                  (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value))) &&
              availablePrinterIds.Contains(printer.Id) &&
              (!job.PrivatePrinting || (document.StorageMode == "LOCAL_AGENT" && document.AgentDeviceId == device.Id)) &&
              (job.BatchId == null || !db.PrintJobs.Any(other =>
                  other.TenantId == job.TenantId &&
                  other.BatchId == job.BatchId &&
                  other.Id != job.Id &&
                  other.Status != "REJECTED" &&
                  other.Status != "REFUNDED" &&
                  other.PrinterDeviceId != job.PrinterDeviceId)) &&
              (job.Status == "PAID_AUTHORISED" ||
               (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                (job.AgentLeaseExpiresUtc == null || job.AgentLeaseExpiresUtc <= now)))
        orderby job.CreatedUtc
        select new
        {
            job.Id,
            job.Status,
            job.JobNumber,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.Orientation,
            job.PdfBrandingEnabled,
            job.PrivatePrinting,
            job.PrintKind,
            job.Currency,
            job.QuotedAmountMinor,
            job.PrinterDeviceId,
            PrinterName = printer.DisplayName,
            QueueName = printer.QueueName,
            OutputMode = printer.OutputMode,
            DocumentName = document.OriginalFileName,
            document.MediaType,
            document.SizeBytes,
            document.Sha256,
            document.StorageMode
        })
        .Take(5)
        .ToListAsync(ct);

    var claimedJobs = new List<object>();
    foreach (var candidate in candidates)
    {
        var leaseExpiresUtc = now.AddMinutes(3);
        var updated = await db.PrintJobs
            .Where(job => job.Id == candidate.Id &&
                          job.TenantId == device.TenantId &&
                          (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                          job.PrinterDeviceId == candidate.PrinterDeviceId &&
                          (job.BatchId == null || !db.PrintJobs.Any(other =>
                              other.TenantId == job.TenantId &&
                              other.BatchId == job.BatchId &&
                              other.Id != job.Id &&
                              other.Status != "REJECTED" &&
                              other.Status != "REFUNDED" &&
                              other.PrinterDeviceId != job.PrinterDeviceId)) &&
                          db.PrinterDevices.Any(printer =>
                              printer.Id == job.PrinterDeviceId &&
                              printer.TenantId == device.TenantId &&
                              printer.AgentDeviceId == device.Id &&
                              printer.IsActive &&
                              printer.QueueName != null && printer.QueueName != "") &&
                          db.AgentDevices.Any(agent =>
                              agent.Id == device.Id &&
                              agent.TenantId == device.TenantId &&
                              agent.RevokedUtc == null && agent.Status != "Revoked" &&
                              agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter) &&
                          db.PrintPayments.Any(payment =>
                              payment.TenantId == job.TenantId &&
                              payment.PrintJobId == job.Id &&
                              (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                  (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value))) &&
                          (job.Status == "PAID_AUTHORISED" ||
                           (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                            (job.AgentLeaseExpiresUtc == null || job.AgentLeaseExpiresUtc <= now))))
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, "LOCAL_QUEUE_CLAIMED")
                .SetProperty(job => job.ClaimedByAgentDeviceId, device.Id)
                .SetProperty(job => job.AgentClaimedUtc, now)
                .SetProperty(job => job.AgentLeaseExpiresUtc, leaseExpiresUtc)
                .SetProperty(job => job.AgentDispatchAttempts, job => job.AgentDispatchAttempts + 1)
                .SetProperty(job => job.UpdatedUtc, now), ct);

        if (updated != 1)
        {
            continue;
        }

        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = device.TenantId,
            PrintJobId = candidate.Id,
            PreviousStatus = candidate.Status,
            NewStatus = "LOCAL_QUEUE_CLAIMED",
            ActorType = "PrintAgent",
            ActorId = device.Id,
            ReasonCode = "AGENT_CLAIMED_PAID_JOB",
            CreatedUtc = now
        });

        claimedJobs.Add(new
        {
            candidate.Id,
            candidate.JobNumber,
            Status = "LOCAL_QUEUE_CLAIMED",
            candidate.PageCount,
            candidate.Copies,
            candidate.ColourMode,
            candidate.PaperSize,
            candidate.IsDuplex,
            candidate.Orientation,
            candidate.PdfBrandingEnabled,
            candidate.PrivatePrinting,
            candidate.PrintKind,
            candidate.Currency,
            candidate.QuotedAmountMinor,
            candidate.PrinterDeviceId,
            candidate.PrinterName,
            candidate.QueueName,
            candidate.DocumentName,
            candidate.MediaType,
            candidate.SizeBytes,
            candidate.Sha256,
            candidate.StorageMode,
            candidate.OutputMode,
            LeaseExpiresUtc = leaseExpiresUtc
        });
    }

    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { Jobs = claimedJobs, ClaimedUtc = now });
});

agentApi.MapGet("/jobs/{jobId:guid}/document", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var document = await (
        from job in db.PrintJobs.AsNoTracking()
        join printDocument in db.PrintDocuments.AsNoTracking()
            on new { job.TenantId, PrintJobId = job.Id }
            equals new { printDocument.TenantId, printDocument.PrintJobId }
        where job.Id == jobId &&
              job.TenantId == device.TenantId &&
              job.Status == "LOCAL_QUEUE_CLAIMED" &&
              job.ClaimedByAgentDeviceId == device.Id &&
              job.AgentLeaseExpiresUtc != null &&
              job.AgentLeaseExpiresUtc > now
        select new
        {
            job.PrivatePrinting,
            printDocument.StorageKey,
            printDocument.StorageMode,
            printDocument.MediaType,
            printDocument.Sha256,
            printDocument.SizeBytes,
            printDocument.OriginalFileName
        })
        .SingleOrDefaultAsync(ct);

    if (document is null)
    {
        return Results.NotFound();
    }

    if (document.PrivatePrinting || document.StorageMode == "LOCAL_AGENT")
    {
        return Results.Conflict(new
        {
            code = document.PrivatePrinting ? "private_printing_cloud_download_blocked" : "document_stored_on_local_agent",
            message = document.PrivatePrinting
                ? "Private Printing documents are never downloaded from cloud storage."
                : "This document was uploaded directly to the shop's local agent and is not stored in cloud document storage."
        });
    }

    MemoryStream content;
    try
    {
        content = storage.OpenRead(document.StorageKey);
    }
    catch (IOException)
    {
        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The stored document could not be opened.");
    }

    context.Response.Headers["Cache-Control"] = "private, no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Document-SHA256"] = document.Sha256;
    context.Response.Headers["X-Document-Size"] = document.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
    context.Response.Headers["X-Document-Name"] = Uri.EscapeDataString(document.OriginalFileName);
    return Results.File(content, document.MediaType, enableRangeProcessing: false);
});

agentApi.MapPost("/jobs/{jobId:guid}/received", async (
    Guid jobId,
    AgentJobReceivedRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    var sha = request.Sha256?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64 ||
        sha.Any(character => !Uri.IsHexDigit(character)) ||
        request.SizeBytes <= 0 || request.SizeBytes > UploadLimits.MaxFileBytes)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["document"] = ["A valid SHA-256 hash and document size are required."]
        });
    }

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == jobId && item.TenantId == device.TenantId, ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    var document = await db.PrintDocuments.AsNoTracking()
        .Where(item => item.TenantId == device.TenantId && item.PrintJobId == jobId)
        .Select(item => new { item.Sha256, item.SizeBytes })
        .SingleOrDefaultAsync(ct);
    if (document is null ||
        !string.Equals(document.Sha256, sha, StringComparison.OrdinalIgnoreCase) ||
        document.SizeBytes != request.SizeBytes)
    {
        return Results.Conflict(new
        {
            code = "local_document_integrity_mismatch",
            message = "The agent's locally stored document does not match the shop's original upload."
        });
    }

    if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == device.Id)
    {
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.LocalQueueReceivedUtc, DuplicateRequest = true });
    }

    var now = DateTimeOffset.UtcNow;
    if (job.Status != "LOCAL_QUEUE_CLAIMED" ||
        job.ClaimedByAgentDeviceId != device.Id ||
        job.AgentLeaseExpiresUtc == null ||
        job.AgentLeaseExpiresUtc <= now)
    {
        return Results.Conflict(new
        {
            code = "agent_job_lease_expired",
            message = "This job lease has expired or was reassigned. Claim it again before acknowledging local receipt."
        });
    }

    var previousStatus = job.Status;
    job.Status = "LOCAL_QUEUE_READY";
    job.LocalQueueReceivedUtc = now;
    job.AgentLeaseExpiresUtc = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "PrintAgent",
        ActorId = device.Id,
        ReasonCode = "AGENT_CONFIRMED_LOCAL_QUEUE_RECEIPT",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);

    return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.LocalQueueReceivedUtc, DuplicateRequest = false });
});


agentApi.MapPost("/local-uploads/{ticketId:guid}/claim", async (
    Guid ticketId,
    HttpContext context,
    SmartPrintDbContext db,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawGrant) || rawGrant.Length is < 40 or > 128)
    {
        return Results.Unauthorized();
    }

    var ticket = await db.LocalPrintUploadTickets.SingleOrDefaultAsync(
        item => item.Id == ticketId &&
                item.TenantId == device.TenantId &&
                item.AgentDeviceId == device.Id,
        ct);
    if (ticket is null) return Results.NotFound();

    byte[] suppliedGrantHash;
    byte[] storedGrantHash;
    try
    {
        suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
        storedGrantHash = Convert.FromHexString(ticket.GrantHash);
    }
    catch (FormatException)
    {
        return Results.Unauthorized();
    }
    if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, storedGrantHash))
    {
        return Results.Unauthorized();
    }

    if (ticket.CompletedUtc is not null)
    {
        var completedJob = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        var completedDocument = await db.PrintDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        if (completedJob is null || completedDocument is null) return Results.Conflict();

        return Results.Ok(new
        {
            AlreadyCompleted = true,
            PrivatePrinting = ticket.PrivatePrinting,
            UploadResult = new
            {
                Job = new
                {
                    completedJob.Id,
                    completedJob.PrivatePrinting,
                    completedJob.PrintKind,
                    completedJob.JobNumber,
                    completedJob.Status,
                    completedJob.PageCount,
                    completedJob.Copies,
                    completedJob.ColourMode,
                    completedJob.PaperSize,
                    completedJob.IsDuplex,
                    completedJob.Orientation,
                    completedJob.PhotoSizeCode,
                    completedJob.PhotoCount,
                    completedJob.PhotosPerPage,
                    completedJob.PricingVersion,
                    completedJob.Currency,
                    completedJob.QuotedAmountMinor,
                    completedJob.CreatedUtc
                },
                Document = new
                {
                    completedDocument.Id,
                    completedDocument.OriginalFileName,
                    completedDocument.MediaType,
                    completedDocument.SizeBytes,
                    completedDocument.Sha256,
                    completedDocument.PageSizesJson,
                    completedDocument.StorageMode
                },
                DuplicateRequest = true
            }
        });
    }

    var now = DateTimeOffset.UtcNow;
    if (ticket.ExpiresUtc <= now)
    {
        return Results.Conflict(new { code = "local_upload_ticket_expired", message = "The upload ticket expired. Ask the customer to start a new upload." });
    }

    var stationPrinter = await db.PrinterDevices.AsNoTracking()
        .Where(item => item.Id == ticket.PrinterDeviceId &&
                       item.TenantId == device.TenantId &&
                       item.AgentDeviceId == device.Id)
        .Select(item => new { item.Id, item.DisplayName, QueueName = item.QueueName ?? string.Empty, item.OutputMode })
        .SingleOrDefaultAsync(ct);
    if (stationPrinter is null)
    {
        return Results.Conflict(new
        {
            code = "local_upload_printer_assignment_missing",
            message = "The original printer assignment changed. SmartPrint will keep this request in the secure queue for the shop owner to reassign."
        });
    }
    if (ticket.PrivatePrinting &&
        !await IsPrivatePrintingOutputQueueReadyAsync(
            db, environment, ticket.TenantId, device.Id, ticket.PrinterDeviceId, ct))
        return Results.Conflict(new
        {
            code = "private_printing_output_policy_changed",
            message = "The shop's Private Printing output policy or printer readiness changed before the encrypted document was transferred. Reconnect and retry the same upload."
        });

    ticket.ClaimedUtc ??= now;
    ticket.ClaimLeaseExpiresUtc = ticket.ExpiresUtc;
    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        AlreadyCompleted = false,
        ticket.Id,
        ticket.PrintJobId,
        ticket.BatchGroupName,
        ticket.JobNumber,
        ticket.AgentDeviceId,
        PrinterDeviceId = stationPrinter.Id,
        PrinterName = stationPrinter.DisplayName,
        QueueName = stationPrinter.QueueName,
        OutputMode = stationPrinter.OutputMode,
        ticket.OriginalFileName,
        ticket.FileExtension,
        ticket.PrivatePrinting,
        ticket.PrintKind,
        ticket.MediaType,
        ticket.SizeBytes,
        ticket.Sha256,
        ticket.ColourMode,
        ticket.PaperSize,
        ticket.IsDuplex,
        ticket.Copies,
        ticket.Orientation,
        ticket.ExpiresUtc
    });
});

agentApi.MapPost("/local-uploads/{ticketId:guid}/complete", async (
    Guid ticketId,
    LocalPrintUploadCompletionRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    TenantEntitlementService entitlements,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawGrant) || rawGrant.Length is < 40 or > 128)
    {
        return Results.Unauthorized();
    }

    var ticket = await db.LocalPrintUploadTickets
        .SingleOrDefaultAsync(item =>
            item.Id == ticketId &&
            item.TenantId == device.TenantId &&
            item.AgentDeviceId == device.Id, ct);
    if (ticket is null) return Results.NotFound();

    byte[] suppliedGrantHash;
    byte[] storedGrantHash;
    try
    {
        suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
        storedGrantHash = Convert.FromHexString(ticket.GrantHash);
    }
    catch (FormatException)
    {
        return Results.Unauthorized();
    }
    if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, storedGrantHash))
    {
        return Results.Unauthorized();
    }

    if (ticket.CompletedUtc is not null)
    {
        var completedJob = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        var completedDocument = await db.PrintDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        if (completedJob is null || completedDocument is null) return Results.Conflict();

        if (!string.IsNullOrWhiteSpace(ticket.PrivateRelayStorageKey))
        {
            try
            {
                await storage.DeleteAsync(ticket.PrivateRelayStorageKey);
                ticket.PrivateRelayStorageKey = null;
                ticket.PrivateRelayWrappedKey = null;
                ticket.PrivateRelayIv = null;
                ticket.PrivateRelayUploadedUtc = null;
                await db.SaveChangesAsync(ct);
            }
            catch (IOException)
            {
                // The Agent already has the DPAPI-protected source. Keep the
                // pointer so the periodic cleanup sweep can retry deletion.
            }
        }
        return Results.Ok(new
        {
            AlreadyCompleted = true,
            PrivatePrinting = ticket.PrivatePrinting,
            UploadResult = new
            {
                Job = new
                {
                    completedJob.Id,
                    completedJob.PrivatePrinting,
                    completedJob.PrintKind,
                    completedJob.JobNumber,
                    completedJob.Status,
                    completedJob.PageCount,
                    completedJob.Copies,
                    completedJob.ColourMode,
                    completedJob.PaperSize,
                    completedJob.IsDuplex,
                    completedJob.Orientation,
                    completedJob.PricingVersion,
                    completedJob.Currency,
                    completedJob.QuotedAmountMinor,
                    completedJob.CreatedUtc
                },
                Document = new
                {
                    completedDocument.Id,
                    completedDocument.OriginalFileName,
                    completedDocument.MediaType,
                    completedDocument.SizeBytes,
                    completedDocument.Sha256,
                    completedDocument.PageSizesJson,
                    completedDocument.StorageMode
                },
                DuplicateRequest = true
            }
        });
    }

    if (ticket.PrivatePrinting &&
        !await IsPrivatePrintingOutputQueueReadyAsync(
            db, environment, ticket.TenantId, device.Id, ticket.PrinterDeviceId, ct))
        return Results.Conflict(new
        {
            code = "private_printing_output_policy_changed",
            message = "The shop's Private Printing output policy or printer readiness changed before the upload could be finalised. Keep the protected local copy and retry the same request after the shop is ready."
        });

    var sha = request.Sha256?.Trim().ToLowerInvariant();
    if (request.PageCount is < 1 or > 500 ||
        request.PageSizesJson is null ||
        request.PageSizesJson.Length is < 2 or > 200_000 ||
        !string.Equals(request.FileExtension, ticket.FileExtension, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(request.MediaType, ticket.MediaType, StringComparison.OrdinalIgnoreCase) ||
        request.SizeBytes != ticket.SizeBytes ||
        !string.Equals(sha, ticket.Sha256, StringComparison.OrdinalIgnoreCase))
    {
        return Results.UnprocessableEntity(new { code = "local_document_validation_mismatch", message = "The local agent's file analysis does not match the document the customer authorised for upload." });
    }

    try
    {
        using var parsedSizes = JsonDocument.Parse(request.PageSizesJson);
        if (parsedSizes.RootElement.ValueKind != JsonValueKind.Array ||
            parsedSizes.RootElement.GetArrayLength() != request.PageCount)
        {
            return Results.UnprocessableEntity(new { code = "local_document_page_analysis_invalid", message = "The local agent returned invalid document page analysis." });
        }
    }
    catch (JsonException)
    {
        return Results.UnprocessableEntity(new { code = "local_document_page_analysis_invalid", message = "The local agent returned invalid document page analysis." });
    }

    if (ticket.PrintKind == "ID_DOCUMENT" &&
        (!ticket.PrivatePrinting ||
         request.PageCount != 1 ||
         !string.Equals(ticket.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
         !IdCardPdfRules.HasExactlyA4PortraitPage(request.PageSizesJson)))
        return Results.UnprocessableEntity(new
        {
            code = "id_document_pdf_invalid",
            message = "The ID card sheet must be a valid, single-page A4 portrait PDF. Recheck the prepared sheet before sending it."
        });

    if (ticket.PrintKind == "PHOTO")
    {
        if (!string.Equals(ticket.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            ticket.PhotoSizeCode is not string photoSizeCode ||
            ticket.PhotoCount is not int photoCount ||
            ticket.PhotosPerPage is not int photosPerPage)
            return Results.UnprocessableEntity(new
            {
                code = "photo_metadata_invalid",
                message = "The photo sheet must be a PDF with complete photo size, quantity and A4 layout details."
            });
        var expectedPageCount = PricingCalculator.PhotoPageCount(photoCount, photoSizeCode, photosPerPage);
        if (request.PageCount != expectedPageCount ||
            !PhotoSheetPdfRules.HasExactlyA4PortraitPages(request.PageSizesJson, expectedPageCount))
            return Results.UnprocessableEntity(new
            {
                code = "photo_sheet_layout_invalid",
                message = $"The Photo Studio PDF must contain exactly {expectedPageCount} portrait A4 page(s), each with one full-page photo raster."
            });
    }

    var now = DateTimeOffset.UtcNow;
    var privateRelayClaim = ticket.PrivatePrinting &&
        ticket.PrivateRelayUploadedUtc is not null &&
        !string.IsNullOrWhiteSpace(ticket.PrivateRelayStorageKey);
    if (ticket.ExpiresUtc <= now ||
        (!privateRelayClaim && (ticket.ClaimLeaseExpiresUtc is null || ticket.ClaimLeaseExpiresUtc <= now)))
    {
        return Results.Conflict(new { code = "local_upload_claim_expired", message = "The local upload grant expired before the document could be finalised." });
    }

    var subscription = await entitlements.GetForTenantAsync(device.TenantId, ct);
    if (!subscription.IsAccessActive)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This shop's trial or subscription period is no longer active.");
    }
    if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, ticket.SizeBytes))
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "This document exceeds the maximum size configured for this shop's plan.");
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var completed = await db.LocalPrintUploadTickets
        .Where(item => item.Id == ticket.Id &&
                       item.TenantId == device.TenantId &&
                       item.AgentDeviceId == device.Id &&
                       item.CompletedUtc == null &&
                       item.ExpiresUtc > now &&
                       (privateRelayClaim || item.ClaimLeaseExpiresUtc > now))
        .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedUtc, now), ct);

    if (completed != 1)
    {
        await transaction.RollbackAsync(ct);
        var existingJob = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        var existingDocument = await db.PrintDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        if (existingJob is null || existingDocument is null)
        {
            return Results.Conflict(new { code = "local_upload_ticket_not_claimed", message = "The upload ticket is no longer claimable by this Agent." });
        }

        return Results.Ok(new
        {
            AlreadyCompleted = true,
            UploadResult = new
            {
                Job = new
                {
                    existingJob.Id,
                    existingJob.PrivatePrinting,
                    existingJob.PrintKind,
                    existingJob.JobNumber,
                    existingJob.Status,
                    existingJob.PageCount,
                    existingJob.Copies,
                    existingJob.ColourMode,
                    existingJob.PaperSize,
                    existingJob.IsDuplex,
                    existingJob.Orientation,
                    existingJob.PhotoSizeCode,
                    existingJob.PhotoCount,
                    existingJob.PhotosPerPage,
                    existingJob.PricingVersion,
                    existingJob.Currency,
                    existingJob.QuotedAmountMinor,
                    existingJob.CreatedUtc
                },
                Document = new
                {
                    existingDocument.Id,
                    existingDocument.OriginalFileName,
                    existingDocument.MediaType,
                    existingDocument.SizeBytes,
                    existingDocument.Sha256,
                    existingDocument.PageSizesJson
                },
                DuplicateRequest = true
            }
        });
    }

    await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    var quota = await entitlements.CheckCapacityAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    if (!quota.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
    }

    var job = new PrintJob
    {
        Id = ticket.PrintJobId,
        TenantId = ticket.TenantId,
        BatchId = ticket.BatchId,
        BatchGroupName = ticket.BatchGroupName,
        BatchName = ticket.BatchName,
        QrStationId = ticket.QrStationId,
        PrinterDeviceId = ticket.PrinterDeviceId,
        CustomerSessionId = ticket.CustomerSessionId,
        JobNumber = ticket.JobNumber,
        Status = "AWAITING_CONFIGURATION",
        IdempotencyKey = $"LOCAL-{ticket.Id:N}",
        PageCount = request.PageCount,
        Copies = ticket.Copies,
        ColourMode = ticket.ColourMode,
        PaperSize = ticket.PaperSize,
        IsDuplex = ticket.IsDuplex,
        Orientation = ticket.Orientation,
        PrivatePrinting = ticket.PrivatePrinting,
        PrintKind = ticket.PrintKind,
        PhotoSizeCode = ticket.PhotoSizeCode,
        PhotoCount = ticket.PhotoCount ?? 0,
        PhotosPerPage = ticket.PhotosPerPage,
        Currency = "INR",
        QuotedAmountMinor = 0,
        CreatedUtc = now,
        UpdatedUtc = now
    };
    var printDocument = new PrintDocument
    {
        TenantId = ticket.TenantId,
        PrintJobId = job.Id,
        OriginalFileName = ticket.PrivatePrinting ? "Private document" : ticket.OriginalFileName,
        MediaType = ticket.MediaType,
        FileExtension = ticket.FileExtension,
        SizeBytes = ticket.SizeBytes,
        Sha256 = ticket.Sha256,
        StorageKey = string.Empty,
        StorageMode = "LOCAL_AGENT",
        AgentDeviceId = device.Id,
        PageSizesJson = request.PageSizesJson,
        CreatedUtc = now
    };
    db.PrintJobs.Add(job);
    db.PrintDocuments.Add(printDocument);
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = null,
        NewStatus = job.Status,
        ActorType = "Customer",
        ReasonCode = "DOCUMENT_UPLOADED_TO_LOCAL_AGENT",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    if (privateRelayClaim && !string.IsNullOrWhiteSpace(ticket.PrivateRelayStorageKey))
    {
        try
        {
            await storage.DeleteAsync(ticket.PrivateRelayStorageKey);
            await db.LocalPrintUploadTickets
                .Where(item => item.Id == ticket.Id && item.TenantId == device.TenantId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.PrivateRelayStorageKey, (string?)null)
                    .SetProperty(item => item.PrivateRelayWrappedKey, (string?)null)
                    .SetProperty(item => item.PrivateRelayIv, (string?)null)
                    .SetProperty(item => item.PrivateRelayUploadedUtc, (DateTimeOffset?)null), ct);
        }
        catch (IOException)
        {
            // A committed local copy is authoritative. Retain the opaque relay
            // pointer for the retention worker to retry deleting the ciphertext.
        }
    }

    return Results.Ok(new
    {
        AlreadyCompleted = false,
        UploadResult = new
        {
            Job = new
            {
                job.Id,
                job.PrintKind,
                job.BatchGroupName,
                job.BatchName,
                job.BatchId,
                job.JobNumber,
                job.Status,
                job.PageCount,
                job.Copies,
                job.ColourMode,
                job.PaperSize,
                job.IsDuplex,
                job.Orientation,
                job.PricingVersion,
                job.Currency,
                job.QuotedAmountMinor,
                job.CreatedUtc
            },
            Document = new
            {
                printDocument.Id,
                printDocument.OriginalFileName,
                printDocument.MediaType,
                printDocument.SizeBytes,
                printDocument.Sha256,
                printDocument.PageSizesJson
            },
            DuplicateRequest = false
        }
    });
});

agentApi.MapGet("/local-uploads/{jobId:guid}/status", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
        .Where(item => item.PrintJobId == jobId &&
                       item.TenantId == device.TenantId &&
                       item.AgentDeviceId == device.Id)
        .Select(item => new { item.CompletedUtc, item.ExpiresUtc, item.Sha256, item.SizeBytes, item.PrivatePrinting })
        .SingleOrDefaultAsync(ct);
    if (ticket is null) return Results.NotFound();

    var documentExists = await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
        item.TenantId == device.TenantId &&
        item.PrintJobId == jobId &&
        item.SizeBytes == ticket.SizeBytes &&
        item.Sha256 == ticket.Sha256 &&
        (item.StorageMode == "CLOUD" ||
         (item.StorageMode == "LOCAL_AGENT" && item.AgentDeviceId == device.Id)), ct);

    var safeToDelete = false;
    DateTimeOffset? deleteAfterUtc = null;
    if (ticket.CompletedUtc != null && documentExists)
    {
        var job = await db.PrintJobs.AsNoTracking()
            .Where(item => item.TenantId == device.TenantId && item.Id == jobId)
            .Select(item => new { item.Status, item.UpdatedUtc, item.OutputConfirmedUtc, item.OutputCompletedUtc })
            .SingleOrDefaultAsync(ct);
        if (job is not null &&
            (job.Status is "REJECTED" or "PAYMENT_REJECTED" or "QUOTE_EXPIRED" or "REFUNDED") &&
            job.OutputConfirmedUtc == null &&
            job.OutputCompletedUtc == null)
        {
            var hasFinancialHold = await db.PrintPayments.AsNoTracking().AnyAsync(payment =>
                payment.TenantId == device.TenantId && payment.PrintJobId == jobId &&
                (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 payment.Status == "CUSTOMER_REPORTED" ||
                 payment.Status == "CASH_AWAITING_PAYMENT" ||
                 ((payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                  payment.RefundedAmountMinor < payment.AmountMinor)), ct);
            if (!hasFinancialHold)
            {
                var retentionDays = await db.PlatformOperationalSettings.AsNoTracking()
                    .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
                    .Select(item => (int?)item.PendingLocalUploadRetentionDays)
                    .SingleOrDefaultAsync(ct) ?? 30;
                safeToDelete = true;
                // A private source is no longer needed once the job is terminal
                // without output or a financial hold. Do not keep it for the
                // standard abandoned-upload retention window.
                deleteAfterUtc = ticket.PrivatePrinting
                    ? DateTimeOffset.UtcNow
                    : job.UpdatedUtc.AddDays(retentionDays);
            }
        }
    }

    return Results.Ok(new
    {
        Completed = ticket.CompletedUtc != null && documentExists,
        ticket.ExpiresUtc,
        SafeToDelete = safeToDelete,
        DeleteAfterUtc = deleteAfterUtc
    });
});

agentApi.MapPost("/jobs/print-claim", async (AgentPrintClaimRequest request, HttpContext context, SmartPrintDbContext db, IHostEnvironment environment, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    var allowPrivateVirtualOutput = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct);
    if (request.LocalJobs is null || request.LocalJobs.Count is < 1 or > 100)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["localJobs"] = ["Provide between 1 and 100 local queue proofs."] });
    var proofs = request.LocalJobs
        .Where(x => x is not null && x.JobId != Guid.Empty && x.SizeBytes is > 0 and <= UploadLimits.MaxFileBytes &&
                    x.Sha256 is { Length: 64 } && x.Sha256.All(Uri.IsHexDigit))
        .GroupBy(x => x.JobId).Select(g => g.First()).ToList();
    if (proofs.Count != request.LocalJobs.Count)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["localJobs"] = ["Each proof must have a unique job ID, SHA-256 and valid size."] });

    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    var claimed = new List<object>();
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var claimAccess = await GetAgentClaimAccessAsync(device.TenantId, db, ct);
    if (!claimAccess.Allowed)
    {
        await tx.RollbackAsync(ct);
        return Results.Forbid();
    }
    var suspendedAtUtc = claimAccess.SuspendedAtUtc;
    foreach (var proof in proofs)
    {
        var candidate = await (
            from job in db.PrintJobs.AsNoTracking()
            join document in db.PrintDocuments.AsNoTracking()
                on new { job.TenantId, PrintJobId = job.Id } equals new { document.TenantId, document.PrintJobId }
            join printer in db.PrinterDevices.AsNoTracking()
                on new { job.TenantId, PrinterId = job.PrinterDeviceId!.Value } equals new { printer.TenantId, PrinterId = printer.Id }
            where job.Id == proof.JobId && job.TenantId == device.TenantId &&
                   (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                   (job.Status == "PRINT_AUTHORISED" ||
                   (job.Status == "PRINT_SUBMITTING" &&
                    job.PrintDispatchLeaseExpiresUtc != null &&
                    job.PrintDispatchLeaseExpiresUtc <= now)) &&
                  job.ClaimedByAgentDeviceId == device.Id && job.LocalQueueReceivedUtc != null &&
                  document.Sha256 == proof.Sha256 && document.SizeBytes == proof.SizeBytes &&
                  (job.BatchId == null || !db.PrintJobs.Any(other =>
                      other.TenantId == job.TenantId &&
                      other.BatchId == job.BatchId &&
                      other.Id != job.Id &&
                      other.Status != "REJECTED" &&
                      other.Status != "REFUNDED" &&
                      other.PrinterDeviceId != job.PrinterDeviceId)) &&
                  printer.AgentDeviceId == device.Id && printer.IsActive &&
                  printer.QueueName != null && printer.QueueName != "" &&
                  (printer.OutputMode == "PHYSICAL" || printer.OutputMode == "PDF_EXPORT") &&
                  (!job.PrivatePrinting || printer.OutputMode == "PHYSICAL" ||
                   (allowPrivateVirtualOutput && printer.OutputMode == "PDF_EXPORT")) &&
                  ((printer.OutputMode == "PDF_EXPORT" && !job.PrivatePrinting) ||
                   (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                    (printer.Status == "Ready" || printer.Status == "Busy"))) &&
                  db.AgentDevices.Any(a => a.Id == device.Id && a.TenantId == device.TenantId &&
                      a.RevokedUtc == null && a.Status != "Revoked" &&
                      a.LastSeenUtc != null && a.LastSeenUtc >= onlineAfter) &&
                  db.PrintPayments.Any(pay => pay.TenantId == job.TenantId && pay.PrintJobId == job.Id && (pay.Status == "VERIFIED" || pay.Status == "PARTIALLY_REFUNDED") &&
                      (!suspendedAtUtc.HasValue || (pay.VerifiedUtc != null && pay.VerifiedUtc <= suspendedAtUtc.Value)))
            select new { job.Id, job.JobNumber, job.Status, job.SpoolerJobName, job.RejectedPageNumbersJson, QueueName = printer.QueueName, OutputMode = printer.OutputMode }
        ).SingleOrDefaultAsync(ct);
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.SpoolerJobName)) continue;

        var lease = now.AddMinutes(5);
        var updated = await db.PrintJobs.Where(job =>
            job.Id == candidate.Id && job.TenantId == device.TenantId &&
            (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
            (job.Status == "PRINT_AUTHORISED" ||
                           (job.Status == "PRINT_SUBMITTING" && job.PrintDispatchLeaseExpiresUtc != null && job.PrintDispatchLeaseExpiresUtc <= now)) &&
                          job.ClaimedByAgentDeviceId == device.Id &&
            job.LocalQueueReceivedUtc != null &&
            (job.BatchId == null || !db.PrintJobs.Any(other =>
                other.TenantId == job.TenantId &&
                other.BatchId == job.BatchId &&
                other.Id != job.Id &&
                other.Status != "REJECTED" &&
                other.Status != "REFUNDED" &&
                other.PrinterDeviceId != job.PrinterDeviceId)) &&
            db.PrinterDevices.Any(printer => printer.Id == job.PrinterDeviceId && printer.TenantId == device.TenantId &&
                printer.AgentDeviceId == device.Id && printer.IsActive && printer.QueueName != null && printer.QueueName != "" &&
                (printer.OutputMode == "PHYSICAL" || printer.OutputMode == "PDF_EXPORT") &&
                (!job.PrivatePrinting || printer.OutputMode == "PHYSICAL" ||
                 (allowPrivateVirtualOutput && printer.OutputMode == "PDF_EXPORT")) &&
                ((printer.OutputMode == "PDF_EXPORT" && !job.PrivatePrinting) ||
                 (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                  (printer.Status == "Ready" || printer.Status == "Busy")))) &&
            db.AgentDevices.Any(agent => agent.Id == device.Id && agent.TenantId == device.TenantId &&
                agent.RevokedUtc == null && agent.Status != "Revoked" &&
                agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter) &&
            db.PrintPayments.Any(pay => pay.TenantId == job.TenantId && pay.PrintJobId == job.Id && (pay.Status == "VERIFIED" || pay.Status == "PARTIALLY_REFUNDED") &&
                      (!suspendedAtUtc.HasValue || (pay.VerifiedUtc != null && pay.VerifiedUtc <= suspendedAtUtc.Value))))
            .ExecuteUpdateAsync(u => u
                .SetProperty(job => job.Status, "PRINT_SUBMITTING")
                .SetProperty(job => job.PrintDispatchLeaseExpiresUtc, lease)
                .SetProperty(job => job.PrintDispatchAttempts, job => job.PrintDispatchAttempts + 1)
                .SetProperty(job => job.SpoolerStatus, "SUBMISSION_STARTED")
                .SetProperty(job => job.UpdatedUtc, now), ct);
        if (updated != 1) continue;

        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = device.TenantId, PrintJobId = candidate.Id, PreviousStatus = candidate.Status,
            NewStatus = "PRINT_SUBMITTING", ActorType = "PrintAgent", ActorId = device.Id,
            ReasonCode = candidate.Status == "PRINT_AUTHORISED"
                ? "AGENT_CLAIMED_EXPLICIT_PRINT_DISPATCH"
                : "AGENT_RECLAIMED_EXPIRED_PRINT_LEASE", CreatedUtc = now
        });
        claimed.Add(new
        {
            candidate.Id, candidate.JobNumber, Status = "PRINT_SUBMITTING",
            candidate.QueueName, candidate.SpoolerJobName, candidate.OutputMode,
            RejectedPageNumbers = JsonSerializer.Deserialize<int[]>(candidate.RejectedPageNumbersJson) ?? [],
            LeaseExpiresUtc = lease
        });
    }
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new { Jobs = claimed, ClaimedUtc = now });
});

agentApi.MapPost("/jobs/{jobId:guid}/pdf-exported", async (
    Guid jobId, AgentPdfExportedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255 ||
        request.FileName != Path.GetFileName(request.FileName) ||
        string.IsNullOrWhiteSpace(request.OutputPath) || request.OutputPath.Length > 1024 ||
        request.OutputPath.Any(char.IsControl) ||
        request.SizeBytes is <= 0 or > 1_000_000_000 ||
        request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit))
        return Results.BadRequest(new { code = "invalid_pdf_export_receipt" });

    var job = await db.PrintJobs.SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status == "COMPLETED" && job.OutputType == "PDF_EXPORT" &&
        job.ClaimedByAgentDeviceId == device.Id &&
        string.Equals(job.OutputSha256, request.Sha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(job.OutputFilePath, request.OutputPath, StringComparison.OrdinalIgnoreCase))
        return Results.Ok(new { job.Id, job.Status, job.OutputFilePath, DuplicateRequest = true });

    if (job.PrivatePrinting ||
        job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id ||
        job.PrinterDeviceId is not Guid printerId ||
        !await db.PrinterDevices.AnyAsync(printer =>
            printer.Id == printerId && printer.TenantId == device.TenantId &&
            printer.AgentDeviceId == device.Id && printer.OutputMode == "PDF_EXPORT", ct))
        return Results.Conflict(new { code = "pdf_export_dispatch_lease_invalid", message = "This Agent does not own an active standard PDF export dispatch for the selected destination." });

    var now = DateTimeOffset.UtcNow;
    var previous = job.Status;
    job.Status = "COMPLETED";
    job.OutputType = "PDF_EXPORT";
    job.OutputFilePath = request.OutputPath;
    job.OutputFileName = request.FileName;
    job.OutputSha256 = request.Sha256.ToLowerInvariant();
    job.OutputSizeBytes = request.SizeBytes;
    job.OutputCompletedUtc = now;
    job.OutputConfirmedUtc = null;
    job.PrintDispatchLeaseExpiresUtc = null;
    job.SpoolerStatus = "PDF_FILE_SAVED";
    job.SpoolerObservedUtc = now;
    job.FailureCode = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "LOCAL_PDF_EXPORT_CREATED_AND_VERIFIED", CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.OutputFilePath, job.OutputFileName, job.OutputSha256, job.OutputSizeBytes, job.OutputCompletedUtc });
});


agentApi.MapPost("/jobs/{jobId:guid}/private-test-output", async (
    Guid jobId,
    AgentPrivateTestOutputRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255 ||
        request.FileName != Path.GetFileName(request.FileName) ||
        !request.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
        request.FileName.Any(char.IsControl) ||
        request.SizeBytes is <= 0 or > 1_000_000_000 ||
        request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit))
        return Results.BadRequest(new { code = "invalid_private_test_output_receipt" });

    var job = await db.PrintJobs.SingleOrDefaultAsync(item =>
        item.Id == jobId && item.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status == "TEST_OUTPUT_READY" &&
        job.OutputType == PrivatePrintingOutputPolicy.TestVirtual &&
        job.ClaimedByAgentDeviceId == device.Id &&
        string.Equals(job.OutputSha256, request.Sha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(job.OutputFileName, request.FileName, StringComparison.Ordinal))
        return Results.Ok(new { job.Id, job.Status, job.OutputType, DuplicateRequest = true, PhysicalOutputConfirmed = false });

    // The central policy and current printer readiness were validated when the
    // server authorised this job for TEST_VIRTUAL at dispatch. Do not recheck
    // the toggle here: an administrator may turn testing off while an already
    // authorised Agent is reporting its completed file. The receipt is required
    // to finish source deletion safely, not to grant another output attempt.
    var now = DateTimeOffset.UtcNow;
    var paymentVerified = await db.PrintPayments.AnyAsync(payment =>
        payment.TenantId == device.TenantId && payment.PrintJobId == job.Id &&
        (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"), ct);
    if (environment.IsProduction() || !paymentVerified ||
        !job.PrivatePrinting || job.PrinterDeviceId is null ||
        job.OutputType != PrivatePrintingOutputPolicy.TestVirtual ||
        job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id ||
        job.LocalQueueReceivedUtc is null)
        return Results.Conflict(new
        {
            code = "private_test_output_not_authorised",
            message = "This private test output is no longer authorised. Keep the protected source and retry only after the Company Main Admin policy, Agent and test printer are ready."
        });

    var previous = job.Status;
    job.Status = "TEST_OUTPUT_READY";
    job.OutputType = PrivatePrintingOutputPolicy.TestVirtual;
    job.OutputFilePath = null;
    job.OutputFileName = request.FileName;
    job.OutputSha256 = request.Sha256.ToLowerInvariant();
    job.OutputSizeBytes = request.SizeBytes;
    job.OutputCompletedUtc = now;
    job.OutputConfirmedUtc = null;
    job.PrivateDocumentPurgeRequestedUtc ??= now;
    job.PrintDispatchLeaseExpiresUtc = null;
    job.SpoolerStatus = "TEST_VIRTUAL_FILE_SAVED";
    job.SpoolerObservedUtc = now;
    job.FailureCode = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previous,
        NewStatus = job.Status,
        ActorType = "PrintAgent",
        ActorId = device.Id,
        ReasonCode = "PRIVATE_TEST_VIRTUAL_OUTPUT_CREATED_NOT_PHYSICALLY_PRINTED",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new
    {
        job.Id,
        job.JobNumber,
        job.Status,
        job.OutputType,
        job.OutputFileName,
        job.OutputSha256,
        job.OutputSizeBytes,
        job.OutputCompletedUtc,
        PhysicalOutputConfirmed = false
    });
});
agentApi.MapPost("/jobs/{jobId:guid}/spooler-accepted", async (Guid jobId, AgentSpoolerAcceptedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    if (request.SpoolerJobId <= 0 || string.IsNullOrWhiteSpace(request.SpoolerJobName) || request.SpoolerJobName.Length > 180 ||
        string.IsNullOrWhiteSpace(request.SpoolerStatus) || request.SpoolerStatus.Length > 80)
        return Results.BadRequest(new { code = "invalid_spooler_receipt" });

    var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if ((job.Status is "SPOOLER_ACCEPTED" or "PRINTING" or "COMPLETED") &&
        job.SpoolerJobId == request.SpoolerJobId && job.SpoolerJobName == request.SpoolerJobName)
        return Results.Ok(new { job.Id, job.Status, job.SpoolerJobId, DuplicateRequest = true });

    var now = DateTimeOffset.UtcNow;
    if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id ||
        job.SpoolerJobName != request.SpoolerJobName)
        return Results.Conflict(new { code = "print_dispatch_lease_expired", message = "Do not submit this print again; the active submission lease is not valid." });

    var previous = job.Status;
    job.Status = "SPOOLER_ACCEPTED";
    job.SpoolerJobId = request.SpoolerJobId;
    job.SpoolerSubmittedUtc = now;
    job.SpoolerObservedUtc = now;
    job.SpoolerStatus = request.SpoolerStatus;
    job.PrintDispatchLeaseExpiresUtc = null;
    job.FailureCode = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "WINDOWS_SPOOLER_ACCEPTED", CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.SpoolerJobId, job.SpoolerSubmittedUtc });
});

agentApi.MapPost("/jobs/{jobId:guid}/print-blocked", async (Guid jobId, AgentPrintBlockedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    var reason = request.ReasonCode?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) return Results.BadRequest();
    var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == device.Id)
        return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
    if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id || job.SpoolerJobId != null)
        return Results.Conflict(new { code = "print_dispatch_state_changed", message = "Reconcile the spooler before attempting another print." });

    var previous = job.Status;
    job.Status = "LOCAL_QUEUE_READY";
    job.PrintDispatchLeaseExpiresUtc = null;
    // This failure is reported before StartDoc/StartPage. Remove the attempted
    // spooler name and dispatch snapshot so the owner can safely reassign the
    // same protected local document to another compatible queue.
    job.SpoolerJobName = null;
    job.SpoolerJobId = null;
    job.SpoolerSubmittedUtc = null;
    job.SpoolerObservedUtc = null;
    job.PrinterNameSnapshot = null;
    job.PrinterQueueNameSnapshot = null;
    job.PrinterAgentNameSnapshot = null;
    job.PrinterSnapshotUtc = null;
    job.FailureCode = reason;
    job.SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION";
    job.UpdatedUtc = DateTimeOffset.UtcNow;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = reason, CreatedUtc = DateTimeOffset.UtcNow
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.FailureCode });
});

agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed", async (Guid jobId, AgentPrintBlockedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    var reason = request.ReasonCode?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) return Results.BadRequest();
    var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
    {
        if (job.Status == "OUTPUT_UNCONFIRMED" && job.ClaimedByAgentDeviceId == device.Id &&
            job.PrinterDeviceId is Guid existingPrinterId &&
            await db.PrinterDevices.AnyAsync(printer =>
                printer.Id == existingPrinterId && printer.TenantId == device.TenantId &&
                printer.OutputMode == "PDF_EXPORT", ct))
        {
            job.OutputType = "PDF_EXPORT";
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { job.Id, job.Status, job.FailureCode, job.OutputType, DuplicateRequest = true });
    }
    if (job.Status is not ("PRINT_SUBMITTING" or "SPOOLER_ACCEPTED" or "PRINTING") || job.ClaimedByAgentDeviceId != device.Id)
        return Results.Conflict(new { code = "job_not_reconcilable" });

    var previous = job.Status;
    if (job.PrinterDeviceId is Guid assignedOutputId &&
        await db.PrinterDevices.AnyAsync(printer =>
            printer.Id == assignedOutputId && printer.TenantId == device.TenantId &&
            printer.OutputMode == "PDF_EXPORT", ct))
        job.OutputType = "PDF_EXPORT";
    job.Status = "OUTPUT_UNCONFIRMED";
    job.FailureCode = reason;
    job.SpoolerStatus = "OUTPUT_UNCONFIRMED";
    job.SpoolerObservedUtc = DateTimeOffset.UtcNow;
    job.PrintDispatchLeaseExpiresUtc = null;
    job.UpdatedUtc = DateTimeOffset.UtcNow;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = reason, CreatedUtc = DateTimeOffset.UtcNow
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.FailureCode });
});

agentApi.MapPost("/jobs/{jobId:guid}/spooler-status", async (Guid jobId, AgentSpoolerStatusRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    if (request.SpoolerJobId <= 0 || string.IsNullOrWhiteSpace(request.SpoolerStatus) || request.SpoolerStatus.Length > 80 ||
        request.PagesPrinted < 0 || request.TotalPages < 0 || (request.TotalPages > 0 && request.PagesPrinted > request.TotalPages))
        return Results.BadRequest(new { code = "invalid_spooler_status" });

    var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
        return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
    if (job.Status is not ("SPOOLER_ACCEPTED" or "PRINTING") || job.ClaimedByAgentDeviceId != device.Id || job.SpoolerJobId != request.SpoolerJobId)
        return Results.Conflict(new { code = "spooler_job_identity_mismatch" });

    var previous = job.Status;
    job.SpoolerStatus = request.SpoolerStatus;
    job.SpoolerObservedUtc = DateTimeOffset.UtcNow;
    job.Status = request.SpoolerStatus == "PRINTING" ? "PRINTING" : "SPOOLER_ACCEPTED";
    job.UpdatedUtc = DateTimeOffset.UtcNow;
    if (previous != job.Status)
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
            ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "WINDOWS_SPOOLER_STATUS_UPDATED", CreatedUtc = DateTimeOffset.UtcNow
        });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.SpoolerStatus, job.SpoolerObservedUtc });
});

agentApi.MapPost("/local-document-photo-revisions/{jobId:guid}/authorize", async (
    Guid jobId,
    HttpContext context,
    AgentLocalPhotoRevisionRequest request,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    if (!PhotoStudioEditorSettingsRules.TryNormalizeJson(request.EditorSettingsJson, out _))
        return Results.BadRequest(new { code = "photo_editor_settings_invalid", message = "The Photo Studio editing settings are invalid. Reopen the editor and save again." });

    var grantToken = context.Request.Headers["X-Print-Photo-Revision-Grant"].FirstOrDefault();
    if (!LocalDocumentPhotoRevisionGrant.TryValidate(
            grantToken, signingKeyText, issuer, audience, DateTimeOffset.UtcNow, out var grant) ||
        !LocalDocumentPhotoRevisionGrant.IsFor(grant, device.TenantId, jobId, device.Id))
        return Results.Unauthorized();

    var job = await db.PrintJobs.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.PrivatePrinting)
        return Results.Conflict(new
        {
            code = "private_printing_source_locked",
            message = "Private Printing never transfers original-photo or Photo Studio editor metadata to the shop Agent."
        });

    var document = await db.PrintDocuments.AsNoTracking()
        .Where(item => item.TenantId == device.TenantId && item.PrintJobId == jobId)
        .Select(item => new
        {
            item.StorageMode,
            item.AgentDeviceId,
            item.Sha256,
            item.SizeBytes,
            item.MediaType,
            item.PhotoSourceSha256,
            item.PhotoSourceSizeBytes,
            item.PhotoSourceMediaType
        })
        .SingleOrDefaultAsync(ct);
    if (document is null) return Results.NotFound();

    if (job.Status != "AWAITING_SHOP_APPROVAL" ||
        job.LocalQueueReceivedUtc is not null || job.ClaimedByAgentDeviceId is not null ||
        document.StorageMode != "LOCAL_AGENT" || document.AgentDeviceId != device.Id)
        return Results.Conflict(new { code = "photo_revision_not_allowed", message = "This local document is no longer eligible for photo enhancement." });

    var sourceSha256 = document.PhotoSourceSha256 ?? document.Sha256;
    var sourceSizeBytes = document.PhotoSourceSizeBytes ?? document.SizeBytes;
    var sourceMediaType = document.PhotoSourceMediaType ?? document.MediaType;
    if (sourceMediaType is not ("image/jpeg" or "image/png") ||
        !string.Equals(sourceSha256, request.ExpectedOriginalSha256, StringComparison.OrdinalIgnoreCase) ||
        sourceSizeBytes != request.ExpectedOriginalSizeBytes)
        return Results.Conflict(new { code = "photo_revision_original_changed", message = "The local original no longer matches the document registered by the shop service. Reopen its preview." });

    if (request.PageCount is < 1 or > 100 || request.SizeBytes is <= 0 or > UploadLimits.MaxFileBytes ||
        string.IsNullOrWhiteSpace(request.Sha256) || request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit) ||
        string.IsNullOrWhiteSpace(request.PageSizesJson) || request.PageSizesJson.Length > 64_000 || !IsValidPageSizesJson(request.PageSizesJson))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["photoSheet"] = ["The prepared local photo sheet metadata is invalid."]
        });

    if (!PhotoSheetPdfRules.HasExactlyA4PortraitPages(request.PageSizesJson, request.PageCount))
        return Results.UnprocessableEntity(new
        {
            code = "photo_sheet_layout_invalid",
            message = "The revised local photo PDF must contain only portrait A4 pages with one full-page 300-DPI photo raster on each page."
        });

    var hasPayment = await db.PrintPayments.AnyAsync(item =>
        item.TenantId == device.TenantId && item.PrintJobId == jobId &&
        item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
    var hasBatchPayment = job.BatchId is Guid batchId && await db.PrintBatchPayments.AnyAsync(item =>
        item.TenantId == device.TenantId && item.BatchId == batchId &&
        item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
    if (hasPayment || hasBatchPayment)
        return Results.Conflict(new { code = "photo_revision_payment_started", message = "A payment attempt already exists. This local photo is locked." });

    ShopPricingProfile? pricing = await db.ShopPricingProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == device.TenantId && item.IsConfigured, ct);
    if (pricing is null)
        return Results.Conflict(new { code = "shop_pricing_not_configured", message = "Configure photo prices before saving an enhanced photo job." });

    long amount;
    long unitPrice;
    try
    {
        var expectedPageCount = PricingCalculator.PhotoPageCount(request.PhotoCount, request.PhotoSizeCode, request.PhotosPerPage);
        if (request.PageCount != expectedPageCount)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["pageCount"] = [$"The prepared PDF must contain exactly {expectedPageCount} A4 page(s) for this photo quantity."]
            });
        unitPrice = PricingCalculator.PhotoUnitRate(request.PhotoSizeCode, pricing);
        amount = PricingCalculator.CalculatePhoto(request.PhotoCount, request.PhotoSizeCode, pricing);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
    {
        return Results.Conflict(new { code = "photo_pricing_unavailable", message = exception.Message });
    }

    return Results.Ok(new
    {
        job.Id,
        OriginalSha256 = sourceSha256,
        OriginalSizeBytes = sourceSizeBytes,
        NewSha256 = request.Sha256,
        NewSizeBytes = request.SizeBytes,
        request.PhotoSizeCode,
        request.PhotoCount,
        request.PageCount,
        request.PhotosPerPage,
        PhotoUnitPriceMinor = unitPrice,
        QuotedAmountMinor = amount,
        PricingVersion = pricing.Version
    });
});

agentApi.MapPost("/local-document-photo-revisions/{jobId:guid}/complete", async (
    Guid jobId,
    HttpContext context,
    AgentLocalPhotoRevisionRequest request,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    if (!PhotoStudioEditorSettingsRules.TryNormalizeJson(request.EditorSettingsJson, out var editorSettingsJson))
        return Results.BadRequest(new { code = "photo_editor_settings_invalid", message = "The Photo Studio editing settings are invalid. Reopen the editor and save again." });

    if (request.PageCount is < 1 or > 100 ||
        request.PhotoCount is < 1 or > UploadLimits.MaxPhotoCount ||
        request.PhotosPerPage is < 1 or > 100 ||
        request.PhotoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6"))
        return Results.BadRequest(new { code = "photo_revision_output_invalid", message = "The prepared photo page metadata is invalid." });

    if (request.PhotosPerPage > PricingCalculator.MaximumPhotosPerA4Sheet(request.PhotoSizeCode) ||
        request.PageCount != PricingCalculator.PhotoPageCount(request.PhotoCount, request.PhotoSizeCode, request.PhotosPerPage))
        return Results.BadRequest(new
        {
            code = "photo_revision_page_count_invalid",
            message = "The local photo PDF page count must match its selected photos-per-page layout."
        });

    var grantToken = context.Request.Headers["X-Print-Photo-Revision-Grant"].FirstOrDefault();
    if (!LocalDocumentPhotoRevisionGrant.TryValidate(
            grantToken, signingKeyText, issuer, audience, DateTimeOffset.UtcNow, out var grant) ||
        !LocalDocumentPhotoRevisionGrant.IsFor(grant, device.TenantId, jobId, device.Id))
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(request.PageSizesJson) || request.PageSizesJson.Length > 64_000 ||
        !PhotoSheetPdfRules.HasExactlyA4PortraitPages(request.PageSizesJson, request.PageCount))
        return Results.UnprocessableEntity(new
        {
            code = "photo_sheet_layout_invalid",
            message = "The completed local photo PDF must contain only portrait A4 pages with one full-page 300-DPI photo raster on each page."
        });

    if (request.ExpectedQuotedAmountMinor is not long expectedQuotedAmountMinor ||
        expectedQuotedAmountMinor <= 0 ||
        request.ExpectedPricingVersion is not int expectedPricingVersion ||
        expectedPricingVersion <= 0)
        return Results.BadRequest(new
        {
            code = "photo_revision_price_snapshot_missing",
            message = "The local photo revision must include the price and pricing version returned by its authorization."
        });

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {device.TenantId} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    var document = await db.PrintDocuments.SingleOrDefaultAsync(
        item => item.TenantId == device.TenantId && item.PrintJobId == jobId, ct);
    if (document is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }
    if (job.PrivatePrinting)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "private_printing_source_locked",
            message = "Private Printing documents cannot be modified by the shop Agent."
        });
    }

    // Idempotent retry: the previous completion may have committed while its
    // HTTP response was lost. Recognise the exact same prepared sheet before
    // requiring the original image hash again.
    if (job.Status == "AWAITING_SHOP_APPROVAL" &&
        job.LocalQueueReceivedUtc is null &&
        job.ClaimedByAgentDeviceId is null &&
        document.StorageMode == "LOCAL_AGENT" &&
        document.AgentDeviceId == device.Id &&
        document.MediaType == "application/pdf" &&
        string.Equals(document.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase) &&
        document.SizeBytes == request.SizeBytes &&
        job.PrintKind == "PHOTO" &&
        job.PageCount == request.PageCount &&
        job.PhotoSizeCode == request.PhotoSizeCode &&
        job.PhotoCount == request.PhotoCount &&
        job.PhotosPerPage == request.PhotosPerPage &&
        job.QuotedAmountMinor == request.ExpectedQuotedAmountMinor &&
        job.PricingVersion == request.ExpectedPricingVersion &&
        string.Equals(
            job.PageRefundAllocationsJson,
            JsonSerializer.Serialize(PricingCalculator.CalculatePhotoPageRefundAllocations(
                request.PhotoCount, request.PhotoSizeCode, request.PhotosPerPage, job.PhotoUnitPriceMinorSnapshot)),
            StringComparison.Ordinal) &&
        string.Equals(document.PageSizesJson, request.PageSizesJson, StringComparison.Ordinal) &&
        string.Equals(document.PhotoEditorSettingsJson, editorSettingsJson, StringComparison.Ordinal))
    {
        var duplicateHasPayment = await db.PrintPayments.AnyAsync(item =>
            item.TenantId == device.TenantId && item.PrintJobId == jobId &&
            item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
        var duplicateHasBatchPayment = job.BatchId is Guid duplicateBatchId &&
            await db.PrintBatchPayments.AnyAsync(item =>
                item.TenantId == device.TenantId && item.BatchId == duplicateBatchId &&
                item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
        if (!duplicateHasPayment && !duplicateHasBatchPayment)
        {
            await transaction.RollbackAsync(ct);
            return Results.Ok(new
            {
                job.Id,
                job.Status,
                job.PageCount,
                job.PrintKind,
                job.PhotoSizeCode,
                job.PhotoCount,
        job.PhotosPerPage,
                job.PhotoUnitPriceMinorSnapshot,
                job.QuotedAmountMinor,
                job.PricingVersion,
                job.QuoteExpiresUtc,
                document.OriginalFileName,
                document.MediaType,
                document.SizeBytes,
                DuplicateRequest = true
            });
        }
    }

    var retainedSourceSha256 = document.PhotoSourceSha256 ?? document.Sha256;
    var retainedSourceSizeBytes = document.PhotoSourceSizeBytes ?? document.SizeBytes;
    var retainedSourceMediaType = document.PhotoSourceMediaType ?? document.MediaType;
    if (job.Status != "AWAITING_SHOP_APPROVAL" ||
        job.LocalQueueReceivedUtc is not null || job.ClaimedByAgentDeviceId is not null ||
        document.StorageMode != "LOCAL_AGENT" || document.AgentDeviceId != device.Id ||
        retainedSourceMediaType is not ("image/jpeg" or "image/png") ||
        !string.Equals(retainedSourceSha256, request.ExpectedOriginalSha256, StringComparison.OrdinalIgnoreCase) ||
        retainedSourceSizeBytes != request.ExpectedOriginalSizeBytes)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "photo_revision_original_changed", message = "The local original or job state changed before the revision was committed. The original file has been restored on the shop computer." });
    }

    var hasPayment = await db.PrintPayments.AnyAsync(item =>
        item.TenantId == device.TenantId && item.PrintJobId == jobId &&
        item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
    var hasBatchPayment = job.BatchId is Guid batchId && await db.PrintBatchPayments.AnyAsync(item =>
        item.TenantId == device.TenantId && item.BatchId == batchId &&
        item.Status != "REJECTED" && item.Status != "REFUNDED", ct);
    if (hasPayment || hasBatchPayment)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "photo_revision_payment_started", message = "A payment attempt started before the revision could be saved. The local original has been restored." });
    }

    if (request.PageCount is < 1 or > 100 || request.SizeBytes is <= 0 or > UploadLimits.MaxFileBytes ||
        request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit) ||
        string.IsNullOrWhiteSpace(request.PageSizesJson) || request.PageSizesJson.Length > 64_000)
    {
        await transaction.RollbackAsync(ct);
        return Results.BadRequest(new { code = "photo_revision_output_invalid", message = "The local Agent supplied invalid prepared-sheet metadata." });
    }

    ShopPricingProfile? pricing = await db.ShopPricingProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == device.TenantId && item.IsConfigured, ct);
    if (pricing is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "shop_pricing_not_configured", message = "Photo pricing is no longer configured." });
    }

    long amount;
    long unitPrice;
    try
    {
        var expectedPageCount = PricingCalculator.PhotoPageCount(request.PhotoCount, request.PhotoSizeCode, request.PhotosPerPage);
        if (request.PageCount != expectedPageCount)
            throw new ArgumentException($"The prepared PDF must contain exactly {expectedPageCount} A4 page(s) for this photo quantity.", nameof(request.PageCount));
        unitPrice = PricingCalculator.PhotoUnitRate(request.PhotoSizeCode, pricing);
        amount = PricingCalculator.CalculatePhoto(request.PhotoCount, request.PhotoSizeCode, pricing);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "photo_pricing_unavailable", message = exception.Message });
    }

    if (pricing.Version != expectedPricingVersion || amount != expectedQuotedAmountMinor)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "photo_revision_pricing_changed",
            message = "Photo pricing changed while this sheet was being saved. The original photo is unchanged; reopen Photo Studio to review the current price."
        });
    }

    var now = DateTimeOffset.UtcNow;
    var previousStorageMode = document.StorageMode;
    var outputName = $"photo-sheet-{job.JobNumber}.pdf";
    if (document.PhotoSourceOriginalFileName is null)
    {
        document.PhotoSourceOriginalFileName = document.OriginalFileName;
        document.PhotoSourceMediaType = document.MediaType;
        document.PhotoSourceSha256 = document.Sha256;
        document.PhotoSourceSizeBytes = document.SizeBytes;
    }
    document.OriginalFileName = outputName;
    document.MediaType = "application/pdf";
    document.FileExtension = ".pdf";
    document.SizeBytes = request.SizeBytes;
    document.Sha256 = request.Sha256.ToLowerInvariant();
    document.PageSizesJson = request.PageSizesJson;
    document.PhotoEditorSettingsJson = editorSettingsJson;
    document.PurgedUtc = null;
    document.StorageMode = previousStorageMode;
    document.AgentDeviceId = device.Id;

    job.PageCount = request.PageCount;
    job.Copies = 1;
    job.ColourMode = "Colour";
    job.PaperSize = "A4";
    job.IsDuplex = false;
    job.Orientation = "AUTO";
    job.PrintKind = "PHOTO";
    job.PhotoSizeCode = request.PhotoSizeCode;
    job.PhotoCount = request.PhotoCount;
    job.PhotosPerPage = request.PhotosPerPage;
    job.PhotoUnitPriceMinorSnapshot = unitPrice;
    job.Currency = "INR";
    job.QuotedAmountMinor = amount;
    job.PageRefundAllocationsJson = JsonSerializer.Serialize(
        PricingCalculator.CalculatePhotoPageRefundAllocations(
            request.PhotoCount, request.PhotoSizeCode, request.PhotosPerPage, unitPrice));
    job.PricingVersion = pricing.Version;
    job.QuoteExpiresUtc = now.AddMinutes(10);
    job.UpdatedUtc = now;

    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = job.Status,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = grant.ActorUserId,
        ReasonCode = "SHOP_PHOTO_ENHANCED_LOCAL",
        CreatedUtc = now
    });

    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new
    {
        job.Id,
        job.Status,
        job.PageCount,
        job.PrintKind,
        job.PhotoSizeCode,
        job.PhotoCount,
        job.PhotosPerPage,
        job.PhotoUnitPriceMinorSnapshot,
        job.QuotedAmountMinor,
        job.PricingVersion,
        job.QuoteExpiresUtc,
        document.OriginalFileName,
        document.MediaType,
        document.SizeBytes,
        document.PhotoEditorSettingsJson
    });
});

agentApi.MapGet("/local-document-previews/{jobId:guid}/authorize", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Json(new
        {
            code = "agent_identity_rejected",
            message = "The paired Print Agent identity was rejected by the shop service. Repair or re-pair this computer, then reopen the document preview."
        }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (await db.PrintJobs.AsNoTracking().AnyAsync(job =>
        job.Id == jobId && job.TenantId == device.TenantId && job.PrivatePrinting, ct))
        return Results.Conflict(new
        {
            code = "private_printing_source_locked",
            message = "The customer selected Private Printing. The shop Agent cannot preview or retrieve the original source document."
        });

    var grantToken = context.Request.Headers["X-Print-Preview-Grant"].FirstOrDefault();
    if (!LocalDocumentPreviewGrant.TryValidate(
            grantToken, signingKeyText, issuer, audience, DateTimeOffset.UtcNow, out var grant) ||
        !LocalDocumentPreviewGrant.IsFor(grant, device.TenantId, jobId, device.Id))
        return Results.Json(new
        {
            code = "preview_grant_invalid_or_expired",
            message = "The local preview permission expired or does not match this print job. Close the preview and reopen it."
        }, statusCode: StatusCodes.Status401Unauthorized);

    var variant = context.Request.Query["variant"].FirstOrDefault();
    var outputPreview = string.Equals(variant, "output", StringComparison.OrdinalIgnoreCase);
    var photoSourcePreview = string.Equals(variant, "photo-source", StringComparison.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(variant) && !outputPreview && !photoSourcePreview &&
        !string.Equals(variant, "original", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { code = "local_preview_variant_invalid" });

    if (outputPreview)
    {
        var output = await db.PrintJobs.AsNoTracking()
            .Where(job => job.Id == jobId && job.TenantId == device.TenantId &&
                          job.ClaimedByAgentDeviceId == device.Id)
            .Select(job => new
            {
                job.Status,
                job.OutputType,
                job.OutputFileName,
                job.OutputSizeBytes,
                job.OutputSha256,
                job.OutputCompletedUtc
            })
            .SingleOrDefaultAsync(ct);

        if (output is null)
            return Results.NotFound();
        if (output.Status != "COMPLETED" || output.OutputType != "PDF_EXPORT" ||
            output.OutputCompletedUtc is null ||
            string.IsNullOrWhiteSpace(output.OutputFileName) ||
            output.OutputFileName.Length > 255 ||
            output.OutputFileName.Contains('/') || output.OutputFileName.Contains('\\') ||
            !string.Equals(Path.GetExtension(output.OutputFileName), ".pdf", StringComparison.OrdinalIgnoreCase) ||
            output.OutputSizeBytes is not long outputSize || outputSize <= 0 || outputSize > 100L * 1024 * 1024 ||
            output.OutputSha256 is not { Length: 64 } || !output.OutputSha256.All(Uri.IsHexDigit))
            return Results.Conflict(new { code = "pdf_export_preview_unavailable" });

        return Results.Ok(new
        {
            MediaType = "application/pdf",
            OriginalFileName = output.OutputFileName,
            SizeBytes = outputSize,
            Sha256 = output.OutputSha256
        });
    }

    var document = await (
        from job in db.PrintJobs.AsNoTracking()
        join item in db.PrintDocuments.AsNoTracking()
            on new { job.TenantId, PrintJobId = job.Id } equals new { item.TenantId, item.PrintJobId }
        where job.Id == jobId && job.TenantId == device.TenantId &&
              item.StorageMode == "LOCAL_AGENT" && item.AgentDeviceId == device.Id
        select new
        {
            item.MediaType,
            item.OriginalFileName,
            item.SizeBytes,
            item.Sha256,
            item.PhotoSourceOriginalFileName,
            item.PhotoSourceMediaType,
            item.PhotoSourceSha256,
            item.PhotoSourceSizeBytes
        }
    ).SingleOrDefaultAsync(ct);

    if (document is null) return Results.NotFound();
    if (photoSourcePreview)
    {
        var mediaType = document.PhotoSourceMediaType ?? document.MediaType;
        var originalFileName = document.PhotoSourceOriginalFileName ?? document.OriginalFileName;
        var sizeBytes = document.PhotoSourceSizeBytes ?? document.SizeBytes;
        var sha256 = document.PhotoSourceSha256 ?? document.Sha256;
        if (mediaType is not ("image/jpeg" or "image/png") ||
            sizeBytes <= 0 ||
            string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Length > 255)
            return Results.Conflict(new { code = "local_photo_source_unavailable", message = "The original JPG or PNG photo is not available for editing." });
        return Results.Ok(new { MediaType = mediaType, OriginalFileName = originalFileName, SizeBytes = sizeBytes, Sha256 = sha256 });
    }
    return Results.Ok(new
    {
        document.MediaType,
        document.OriginalFileName,
        document.SizeBytes,
        document.Sha256
    });
});

agentApi.MapPost("/heartbeat", async (
    HttpContext context,
    SmartPrintDbContext db,
    IConfiguration configuration,
    AgentHeartbeatRequest request,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var localEndpoints = AgentLocalEndpointRules.Normalize(request.LocalEndpoints);
    device.LocalEndpointsJson = JsonSerializer.Serialize(localEndpoints);
    if (!string.IsNullOrWhiteSpace(request.DocumentEncryptionPublicKey))
    {
        try
        {
            var publicKeyBytes = Convert.FromBase64String(request.DocumentEncryptionPublicKey);
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
                if (rsa.KeySize < 2048 || rsa.KeySize > 4096)
                    return Results.BadRequest(new { code = "agent_document_key_invalid", message = "The Agent document-encryption key must be RSA-2048 through RSA-4096." });
                device.DocumentEncryptionPublicKey = request.DocumentEncryptionPublicKey;
            }
            finally { CryptographicOperations.ZeroMemory(publicKeyBytes); }
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return Results.BadRequest(new { code = "agent_document_key_invalid", message = "The Agent document-encryption public key is invalid." });
        }
    }
    else
    {
        // A heartbeat without a usable key disables Private Printing rather
        // than advertising an obsolete public key after local key-store failure.
        device.DocumentEncryptionPublicKey = null;
    }
    device.Status = "Online";
    device.LastSeenUtc = now;
    await db.SaveChangesAsync(ct);
    var publicBaseUrl = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
    var operationalSettings = await db.PlatformOperationalSettings.AsNoTracking()
        .SingleAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, ct);
    return Results.Ok(new
    {
        device.Id,
        device.Status,
        device.LastSeenUtc,
        AgentVersion = request.Version,
        LocalEndpoints = localEndpoints,
        PublicBaseUrl = publicBaseUrl,
        AllowedPortalOrigins = corsOrigins,
        AgentCompletedDocumentRetentionDays = operationalSettings.AgentCompletedDocumentRetentionDays,
        AgentRetentionSweepHours = operationalSettings.AgentRetentionSweepHours
    });
});

agentApi.MapPost("/printers/sync", async (
    HttpContext context,
    SmartPrintDbContext db,
    AgentPrinterSyncRequest request,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    if (request.Printers is null || request.Printers.Count > 100)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printers"] = ["A maximum of 100 discovered printer queues can be synchronized."]
        });
    }

    var invalidCapabilitiesJson = request.Printers.Any(printer =>
    {
        if (printer is null) return true;
        var json = string.IsNullOrWhiteSpace(printer.CapabilitiesJson) ? "{}" : printer.CapabilitiesJson;
        if (json.Length > 16000) return true;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.ValueKind != JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return true;
        }
    });
    if (invalidCapabilitiesJson)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["capabilitiesJson"] = ["Each printer capability report must be a valid JSON object under 16 KB."]
        });
    }

    var validStatuses = new[] { "Ready", "Busy", "Offline", "Paused", "Error", "Unknown" };
    var normalized = request.Printers
        .Where(printer => printer is not null &&
                          !string.IsNullOrWhiteSpace(printer.QueueName) &&
                          printer.QueueName.Length <= 160 &&
                          !string.IsNullOrWhiteSpace(printer.DriverName) &&
                          printer.DriverName.Length <= 160 &&
                          (printer.OutputMode is "PHYSICAL" or "PDF_EXPORT" or "UNSUPPORTED_VIRTUAL") &&
                          (printer.PortName?.Length ?? 0) <= 255 &&
                          (printer.CapabilitiesJson?.Length ?? 0) <= 16000 && validStatuses.Contains(printer.Status, StringComparer.Ordinal))
        .GroupBy(printer => printer.QueueName.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();

    if (normalized.Count != request.Printers.Count)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printers"] = ["Each discovered queue must have a unique name, supported status and valid driver/port metadata."]
        });
    }

    var now = DateTimeOffset.UtcNow;
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.Printer, ct);

    var tenantPrinters = await db.PrinterDevices.AsNoTracking()
        .Where(printer => printer.TenantId == device.TenantId)
        .Select(printer => new { printer.QueueName, printer.AgentDeviceId })
        .ToListAsync(ct);
    var revokedAgentIds = await db.AgentDevices.AsNoTracking()
        .Where(agent => agent.TenantId == device.TenantId && agent.RevokedUtc != null)
        .Select(agent => agent.Id)
        .ToListAsync(ct);
    var additionalPrinters = normalized.LongCount(discovered =>
        !tenantPrinters.Any(printer =>
            string.Equals(printer.QueueName, discovered.QueueName, StringComparison.OrdinalIgnoreCase) &&
            (printer.AgentDeviceId == device.Id ||
             printer.AgentDeviceId == null ||
             (printer.AgentDeviceId is Guid previousAgentId && revokedAgentIds.Contains(previousAgentId)))));
    if (additionalPrinters > 0)
    {
        var quota = await entitlements.CheckCapacityAsync(
            device.TenantId, TenantQuotaResource.Printer, ct, additionalPrinters);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
        }
    }

    var existing = await db.PrinterDevices
        .Where(printer => printer.TenantId == device.TenantId &&
                          printer.AgentDeviceId == device.Id)
        .ToListAsync(ct);
    var seenQueues = normalized.Select(printer => printer.QueueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var created = 0;
    var updated = 0;

    foreach (var discovered in normalized)
    {
        var printer = await db.PrinterDevices.FirstOrDefaultAsync(item =>
            item.TenantId == device.TenantId &&
            item.AgentDeviceId == device.Id &&
            item.QueueName == discovered.QueueName, ct);

        printer ??= await db.PrinterDevices.FirstOrDefaultAsync(item =>
            item.TenantId == device.TenantId &&
            item.QueueName == discovered.QueueName &&
            (item.AgentDeviceId == null ||
             db.AgentDevices.Any(previousAgent =>
                 previousAgent.Id == item.AgentDeviceId &&
                 previousAgent.TenantId == device.TenantId &&
                 previousAgent.RevokedUtc != null)), ct);

        if (printer is null)
        {
            printer = new PrinterDevice
            {
                TenantId = device.TenantId,
                AgentDeviceId = device.Id,
                DisplayName = discovered.QueueName.Trim(),
                Manufacturer = "Windows",
                Model = discovered.DriverName.Trim(),
                ConnectionType = "WindowsQueue",
                OutputMode = discovered.OutputMode,
                ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim(),
                QueueName = discovered.QueueName.Trim(),
                IsDefault = discovered.IsDefault,
                CapabilitiesJson = string.IsNullOrWhiteSpace(discovered.CapabilitiesJson) ? "{}" : discovered.CapabilitiesJson,
                IsActive = true,
                CreatedUtc = now
            };
            db.PrinterDevices.Add(printer);
            created++;
        }
        else
        {
            printer.AgentDeviceId = device.Id;
            printer.DisplayName = discovered.QueueName.Trim();
            printer.Manufacturer = "Windows";
            printer.Model = discovered.DriverName.Trim();
            printer.ConnectionType = "WindowsQueue";
            printer.OutputMode = discovered.OutputMode;
            printer.ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim();
            printer.IsDefault = discovered.IsDefault;
            printer.CapabilitiesJson = string.IsNullOrWhiteSpace(discovered.CapabilitiesJson) ? "{}" : discovered.CapabilitiesJson;
            updated++;
        }

        printer.Status = discovered.Status;
        printer.LastSeenUtc = now;
        printer.UpdatedUtc = now;
    }

    foreach (var printer in existing.Where(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)))
    {
        printer.Status = "Unavailable";
        printer.UpdatedUtc = now;
    }

    device.Status = "Online";
    device.LastSeenUtc = now;
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { device.Id, Discovered = normalized.Count, Created = created, Updated = updated, Unavailable = existing.Count(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)), SyncedUtc = now });
});


shop.MapGet("/payment-settings", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var settings = await db.ShopPaymentProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
    if (settings is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        settings.UpiEnabled,
        settings.UpiId,
        settings.PayeeName,
        settings.CashEnabled,
        settings.InstantPaymentEnabled,
        settings.UpdatedUtc
    });
});

shop.MapPut("/payment-settings", async (
    ShopPaymentSettingsRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var upiId = request.UpiId?.Trim();
    var payeeName = request.PayeeName?.Trim();
    if (request.UpiEnabled &&
        (!IsValidUpiId(upiId) || string.IsNullOrWhiteSpace(payeeName) || payeeName.Length > 120))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["upi"] = ["Enter a valid UPI ID and payee/business name before enabling UPI."]
        });
    }

    if (upiId is { Length: > 160 })
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["upiId"] = ["UPI ID cannot exceed 160 characters."]
        });
    }

    if (!request.UpiEnabled && !request.CashEnabled)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["methods"] = ["Enable at least one payment method so customers can pay for approved print jobs."]
        });
    }

    var settings = await db.ShopPaymentProfiles.SingleOrDefaultAsync(
        item => item.TenantId == tenantId, ct);
    if (settings is null)
    {
        return Results.NotFound();
    }

    settings.UpiEnabled = request.UpiEnabled;
    settings.UpiId = request.UpiEnabled ? upiId : null;
    settings.PayeeName = request.UpiEnabled ? payeeName : null;
    settings.CashEnabled = request.CashEnabled;
    settings.InstantPaymentEnabled = request.InstantPaymentEnabled;
    settings.UpdatedUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        settings.UpiEnabled,
        settings.UpiId,
        settings.PayeeName,
        settings.CashEnabled,
        settings.InstantPaymentEnabled,
        settings.UpdatedUtc
    });
});

shop.MapGet("/payments", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId)) return Results.Forbid();

    var individualPayments = await (
        from payment in db.PrintPayments.AsNoTracking()
        join job in db.PrintJobs.AsNoTracking()
            on new { payment.TenantId, PrintJobId = payment.PrintJobId }
            equals new { job.TenantId, PrintJobId = job.Id }
        join document in db.PrintDocuments.AsNoTracking()
            on new { payment.TenantId, payment.PrintJobId }
            equals new { document.TenantId, document.PrintJobId }
        where payment.TenantId == tenantId && payment.BatchPaymentId == null
        orderby payment.CreatedUtc descending
        select new
        {
            payment.Id,
            payment.PrintJobId,
            job.JobNumber,
            DocumentName = document.OriginalFileName,
            job.Origin,
            job.RecipientName,
            job.RecipientPhone,
            BatchPaymentId = (Guid?)null,
            BatchFileCount = (int?)null,
            payment.Method,
            payment.Status,
            payment.AmountMinor,
            payment.RefundedAmountMinor,
            payment.Currency,
            payment.PaymentReference,
            payment.UpiIdSnapshot,
            payment.PayeeNameSnapshot,
            payment.RejectionReason,
            payment.CreatedUtc,
            payment.UpdatedUtc,
            payment.VerifiedUtc
        })
        .Take(100)
        .ToListAsync(ct);

    var recentBatchPayments = db.PrintBatchPayments.AsNoTracking()
        .Where(item => item.TenantId == tenantId)
        .OrderByDescending(item => item.CreatedUtc)
        .Take(100);

    var batchPaymentRows = await (
        from batchPayment in recentBatchPayments
        join payment in db.PrintPayments.AsNoTracking()
            on new { batchPayment.TenantId, BatchPaymentId = (Guid?)batchPayment.Id }
            equals new { payment.TenantId, payment.BatchPaymentId }
        join job in db.PrintJobs.AsNoTracking()
            on new { payment.TenantId, PrintJobId = payment.PrintJobId }
            equals new { job.TenantId, PrintJobId = job.Id }
        join document in db.PrintDocuments.AsNoTracking()
            on new { payment.TenantId, payment.PrintJobId }
            equals new { document.TenantId, document.PrintJobId }
        where batchPayment.TenantId == tenantId
        select new
        {
            batchPayment.Id,
            payment.PrintJobId,
            job.JobNumber,
            DocumentName = document.OriginalFileName,
            job.Origin,
            job.RecipientName,
            job.RecipientPhone,
            BatchPaymentId = (Guid?)batchPayment.Id,
            BatchFileCount = (int?)null,
            batchPayment.Method,
            batchPayment.Status,
            batchPayment.AmountMinor,
            payment.RefundedAmountMinor,
            batchPayment.Currency,
            batchPayment.PaymentReference,
            batchPayment.UpiIdSnapshot,
            batchPayment.PayeeNameSnapshot,
            batchPayment.RejectionReason,
            batchPayment.CreatedUtc,
            batchPayment.UpdatedUtc,
            batchPayment.VerifiedUtc
        })
        .ToListAsync(ct);

    var groupedBatchPayments = batchPaymentRows
        .GroupBy(item => item.Id)
        .Select(group =>
        {
            var first = group.First();
            var names = group.Select(item => item.DocumentName).Distinct().ToArray();
            var documentSummary = names.Length <= 3
                ? string.Join(", ", names)
                : string.Join(", ", names.Take(3)) + $" +{names.Length - 3} more";
            return new
            {
                first.Id,
                first.PrintJobId,
                JobNumber = "Batch · " + first.JobNumber,
                DocumentName = documentSummary,
                first.Origin,
                first.RecipientName,
                first.RecipientPhone,
                first.BatchPaymentId,
                BatchFileCount = (int?)group.Count(),
                first.Method,
                first.Status,
                first.AmountMinor,
                RefundedAmountMinor = group.Sum(item => item.RefundedAmountMinor),
                first.Currency,
                first.PaymentReference,
                first.UpiIdSnapshot,
                first.PayeeNameSnapshot,
                first.RejectionReason,
                first.CreatedUtc,
                first.UpdatedUtc,
                first.VerifiedUtc
            };
        });

    var result = individualPayments.Select(item => (item.CreatedUtc, (object)item))
        .Concat(groupedBatchPayments.Select(item => (item.CreatedUtc, (object)item)))
        .OrderByDescending(item => item.CreatedUtc)
        .Take(100)
        .Select(item => item.Item2)
        .ToArray();

    return Results.Ok(result);
});

shop.MapGet("/refunds", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var refunds = await (
        from refund in db.PrintRefunds.AsNoTracking()
        join job in db.PrintJobs.AsNoTracking()
            on new { refund.TenantId, PrintJobId = refund.PrintJobId } equals new { job.TenantId, PrintJobId = job.Id }
        join document in db.PrintDocuments.AsNoTracking()
            on new { refund.TenantId, refund.PrintJobId } equals new { document.TenantId, document.PrintJobId }
        join user in db.Users.AsNoTracking()
            on refund.RefundedByUserId equals user.Id
        where refund.TenantId == tenantId
        orderby refund.RefundedUtc descending
        select new
        {
            refund.Id,
            refund.PrintJobId,
            refund.PrintPaymentId,
            job.JobNumber,
            DocumentName = document.OriginalFileName,
            refund.AmountMinor,
            refund.Currency,
            refund.Method,
            refund.Status,
            refund.Reference,
            refund.Reason,
            refund.RefundedUtc,
            refund.RefundedByUserId,
            RefundedByName = user.DisplayName
        })
        .Take(100)
        .ToListAsync(ct);

    return Results.Ok(refunds);
});

shop.MapPost("/payments/{paymentId:guid}/verify", async (
    Guid paymentId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedBatchPayments = await db.PrintBatchPayments.FromSqlInterpolated(
        $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {tenantId} FOR UPDATE")
        .ToListAsync(ct);
    var batchPayment = lockedBatchPayments.SingleOrDefault();
    if (batchPayment is not null)
    {
        if (batchPayment.Status == "VERIFIED")
        {
            await transaction.RollbackAsync(ct);
            return Results.Ok(new { batchPayment.Id, batchPayment.Status, batchPayment.VerifiedUtc, DuplicateRequest = true });
        }

        var batchCanVerify = batchPayment.Method == "UPI"
            ? batchPayment.Status is "AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED"
            : batchPayment.Method == "CASH" && batchPayment.Status == "CASH_AWAITING_PAYMENT";
        if (!batchCanVerify)
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new
            {
                code = "batch_payment_not_ready_for_manual_verification",
                message = batchPayment.Method == "UPI"
                    ? "Only an active UPI batch payment can be verified. Check the full batch amount in your bank/UPI app first."
                    : "Cash batch payment can be marked received only after the full amount has been physically received."
            });
        }

        var batchFilePayments = await db.PrintPayments
            .Where(item => item.TenantId == tenantId && item.BatchPaymentId == batchPayment.Id)
            .ToListAsync(ct);
        var batchJobIds = batchFilePayments.Select(item => item.PrintJobId).ToArray();
        var batchJobs = await db.PrintJobs
            .Where(item => item.TenantId == tenantId && batchJobIds.Contains(item.Id))
            .ToListAsync(ct);
        if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, batchFilePayments, batchJobs))
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new
            {
                code = "batch_payment_allocations_mismatch",
                message = "The batch total and per-file payment allocations do not match. Nothing was authorised or rejected; contact support before retrying."
            });
        }

        var actorId = GetActorId(principal);
        var batchVerifiedUtc = DateTimeOffset.UtcNow;
        batchPayment.Status = "VERIFIED";
        batchPayment.VerifiedByUserId = actorId;
        batchPayment.VerifiedUtc = batchVerifiedUtc;
        batchPayment.UpdatedUtc = batchVerifiedUtc;
        var jobsById = batchJobs.ToDictionary(item => item.Id);
        foreach (var filePayment in batchFilePayments)
        {
            filePayment.Status = "VERIFIED";
            filePayment.VerifiedByUserId = actorId;
            filePayment.VerifiedUtc = batchVerifiedUtc;
            filePayment.UpdatedUtc = batchVerifiedUtc;
            var batchJob = jobsById[filePayment.PrintJobId];
            var batchPreviousStatus = batchJob.Status;
            var batchFileWasRejected = batchJob.Status is "REJECTED" or "REFUNDED";
            if (!batchFileWasRejected)
            {
                batchJob.Status = "PAID_AUTHORISED";
                batchJob.UpdatedUtc = batchVerifiedUtc;
            }
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = batchJob.Id,
                PreviousStatus = batchPreviousStatus,
                NewStatus = batchJob.Status,
                ActorType = "ShopUser",
                ActorId = actorId,
                ReasonCode = batchFileWasRejected
                    ? "BATCH_PAYMENT_VERIFIED_FOR_REJECTED_FILE_REFUND"
                    : batchPayment.Method == "UPI"
                        ? "BATCH_UPI_PAYMENT_MANUALLY_VERIFIED"
                        : "BATCH_CASH_RECEIVED_AT_COUNTER",
                CreatedUtc = batchVerifiedUtc
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new
            {
                code = "batch_payment_changed_concurrently",
                message = "Another shop action changed this batch payment. Refresh the payment list before trying again."
            });
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(new
        {
            batchPayment.Id,
            batchPayment.Status,
            batchPayment.VerifiedUtc,
            batchPayment.AmountMinor,
            batchPayment.Currency,
            FileCount = batchFilePayments.Count,
            NextStep = "The batch payment is verified. Each accepted file is now authorised and can be assigned to a live printer."
        });
    }

    var payment = await db.PrintPayments.SingleOrDefaultAsync(
        item => item.Id == paymentId && item.TenantId == tenantId && item.BatchPaymentId == null, ct);
    if (payment is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if ((payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"))
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new { payment.Id, payment.Status, payment.VerifiedUtc, DuplicateRequest = true });
    }

    var canVerify = payment.Method == "UPI"
        ? payment.Status is "AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED"
        : payment.Method == "CASH" && payment.Status == "CASH_AWAITING_PAYMENT";
    if (!canVerify)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "payment_not_ready_for_manual_verification",
            message = payment.Method == "UPI"
                ? "Only an active UPI payment attempt can be verified. Check the actual credit in your bank/UPI app first."
                : "Cash can be marked received only after the shop has physically received it."
        });
    }

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == payment.PrintJobId && item.TenantId == tenantId, ct);
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    var now = DateTimeOffset.UtcNow;
    payment.Status = "VERIFIED";
    payment.VerifiedByUserId = GetActorId(principal);
    payment.VerifiedUtc = now;
    payment.UpdatedUtc = now;
    var previousStatus = job.Status;
    var singleFileWasRejected = job.Status is "REJECTED" or "REFUNDED";
    if (!singleFileWasRejected)
        job.Status = "PAID_AUTHORISED";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = tenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = GetActorId(principal),
        ReasonCode = singleFileWasRejected
            ? "PAYMENT_VERIFIED_FOR_REJECTED_FILE_REFUND"
            : payment.Method == "UPI"
                ? "UPI_PAYMENT_MANUALLY_VERIFIED"
                : "CASH_RECEIVED_AT_COUNTER",
        CreatedUtc = now
    });
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "payment_changed_concurrently",
            message = "Another shop action changed this payment. Refresh the payment list before trying again."
        });
    }

    await transaction.CommitAsync(ct);

    return Results.Ok(new
    {
        payment.Id,
        payment.Status,
        payment.VerifiedUtc,
        JobId = job.Id,
        JobNumber = job.JobNumber,
        JobStatus = job.Status,
        NextStep = "Payment is verified. Assign this job to a live printer on its paired computer. The agent will secure the document locally before you explicitly start printing."
    });
});

shop.MapPost("/payments/{paymentId:guid}/reject", async (
    Guid paymentId,
    RejectPrintPaymentRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var reason = request.Reason?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 3 or > 300)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["reason"] = ["Provide a reason between 3 and 300 characters."]
        });
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedBatchPayments = await db.PrintBatchPayments.FromSqlInterpolated(
        $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {tenantId} FOR UPDATE")
        .ToListAsync(ct);
    var batchPayment = lockedBatchPayments.SingleOrDefault();
    if (batchPayment is not null)
    {
        if (batchPayment.Status == "REJECTED")
        {
            await transaction.RollbackAsync(ct);
            return Results.Ok(new { batchPayment.Id, batchPayment.Status, batchPayment.RejectionReason, DuplicateRequest = true });
        }

        if (batchPayment.Status is not ("AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED" or "CASH_AWAITING_PAYMENT"))
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new
            {
                code = "batch_payment_not_rejectable",
                message = "Only a batch payment that is still awaiting payment or verification can be rejected."
            });
        }

        var batchFilePayments = await db.PrintPayments
            .Where(item => item.TenantId == tenantId && item.BatchPaymentId == batchPayment.Id)
            .ToListAsync(ct);
        var batchJobIds = batchFilePayments.Select(item => item.PrintJobId).ToArray();
        var batchJobs = await db.PrintJobs
            .Where(item => item.TenantId == tenantId && batchJobIds.Contains(item.Id))
            .ToListAsync(ct);
        if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, batchFilePayments, batchJobs))
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new
            {
                code = "batch_payment_allocations_mismatch",
                message = "The batch total and per-file payment allocations do not match. Nothing was authorised or rejected; contact support before retrying."
            });
        }

        var actorId = GetActorId(principal);
        var batchRejectedUtc = DateTimeOffset.UtcNow;
        batchPayment.Status = "REJECTED";
        batchPayment.RejectionReason = reason;
        batchPayment.UpdatedUtc = batchRejectedUtc;
        var jobsById = batchJobs.ToDictionary(item => item.Id);
        foreach (var filePayment in batchFilePayments)
        {
            filePayment.Status = "REJECTED";
            filePayment.RejectionReason = reason;
            filePayment.UpdatedUtc = batchRejectedUtc;
            var batchJob = jobsById[filePayment.PrintJobId];
            if (batchJob.Status is not ("REJECTED" or "REFUNDED"))
            {
                var batchPreviousStatus = batchJob.Status;
                batchJob.Status = "PAYMENT_REJECTED";
                batchJob.UpdatedUtc = batchRejectedUtc;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = tenantId,
                    PrintJobId = batchJob.Id,
                    PreviousStatus = batchPreviousStatus,
                    NewStatus = batchJob.Status,
                    ActorType = "ShopUser",
                    ActorId = actorId,
                    ReasonCode = "SHOP_REJECTED_BATCH_PAYMENT_REPORT",
                    CreatedUtc = batchRejectedUtc
                });
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new
            {
                code = "batch_payment_changed_concurrently",
                message = "Another shop action changed this batch payment. Refresh the payment list before trying again."
            });
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(new
        {
            batchPayment.Id,
            batchPayment.Status,
            batchPayment.RejectionReason,
            batchPayment.UpdatedUtc,
            FileCount = batchFilePayments.Count
        });
    }

    var payment = await db.PrintPayments.SingleOrDefaultAsync(
        item => item.Id == paymentId && item.TenantId == tenantId && item.BatchPaymentId == null, ct);
    if (payment is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (payment.Status == "REJECTED")
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new { payment.Id, payment.Status, payment.RejectionReason, DuplicateRequest = true });
    }

    if (payment.Status is not ("AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED" or "CASH_AWAITING_PAYMENT"))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "payment_not_rejectable",
            message = "Only a payment that is still awaiting payment or verification can be rejected."
        });
    }

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == payment.PrintJobId && item.TenantId == tenantId, ct);
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    var now = DateTimeOffset.UtcNow;
    payment.Status = "REJECTED";
    payment.RejectionReason = reason;
    payment.UpdatedUtc = now;
    var previousStatus = job.Status;
    job.Status = "PAYMENT_REJECTED";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = tenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = GetActorId(principal),
        ReasonCode = "SHOP_REJECTED_PAYMENT_REPORT",
        CreatedUtc = now
    });
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "payment_changed_concurrently",
            message = "Another shop action changed this payment. Refresh the payment list before trying again."
        });
    }

    await transaction.CommitAsync(ct);

    return Results.Ok(new
    {
        payment.Id,
        payment.Status,
        payment.RejectionReason,
        JobId = job.Id,
        JobStatus = job.Status,
        job.UpdatedUtc
    });
});

var publicApi = app.MapGroup("/api/v1/public").AllowAnonymous()
    .RequireRateLimiting("public-api")
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));

publicApi.MapPost("/sessions/current/jobs", async (
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) ||
        string.IsNullOrWhiteSpace(idempotencyKey) ||
        idempotencyKey.Length > 100)
    {
        return Results.BadRequest(new
        {
            code = "session_and_idempotency_required",
            message = "A valid print session and Idempotency-Key are required."
        });
    }

    var rawBatchId = context.Request.Headers["X-Print-Batch-Id"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(rawBatchId) && !Guid.TryParse(rawBatchId, out _))
    {
        return Results.BadRequest(new { code = "invalid_batch_id", message = "The upload batch identifier is invalid." });
    }
    var batchId = string.IsNullOrWhiteSpace(rawBatchId) ? Guid.NewGuid() : Guid.Parse(rawBatchId);
    var tokenHash = HashOpaqueToken(rawToken);
    var now = DateTimeOffset.UtcNow;
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId, item.QrStationId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var privateBatchAlreadyExists = await db.PrintJobs.AsNoTracking().AnyAsync(job =>
        job.TenantId == session.TenantId && job.CustomerSessionId == session.Id &&
        job.BatchId == batchId && job.PrivatePrinting, ct) ||
        await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(ticket =>
            ticket.TenantId == session.TenantId && ticket.CustomerSessionId == session.Id &&
            ticket.BatchId == batchId && ticket.PrivatePrinting, ct);
    if (privateBatchAlreadyExists)
        return Results.Conflict(new
        {
            code = "batch_privacy_mode_mismatch",
            message = "This batch was started in Private Printing. Continue on the paired shop computer or start a new batch for Standard Printing."
        });

    if (!context.Request.HasFormContentType)
    {
        return Results.BadRequest(new { code = "multipart_form_required", message = "Upload the document using multipart/form-data." });
    }

    var form = await context.Request.ReadFormAsync(ct);
    if (bool.TryParse(form["privatePrinting"].FirstOrDefault(), out var privatePrintingRequested) && privatePrintingRequested)
    {
        return Results.Conflict(new
        {
            code = "private_printing_requires_local_agent",
            message = "Private Printing is available only through encrypted direct transfer to the paired shop computer. Cloud document storage is disabled."
        });
    }
    if (form.Files.Count != 1)
    {
        return Results.BadRequest(new { code = "one_file_required", message = "Upload one document per print job." });
    }

    var file = form.Files[0];
    var requestedColourMode = form["colourMode"].FirstOrDefault() ?? "Mono";
    var requestedPaperSize = form["paperSize"].FirstOrDefault() ?? "A4";
    var requestedDuplexText = form["isDuplex"].FirstOrDefault() ?? "false";
    var requestedCopiesText = form["copies"].FirstOrDefault() ?? "1";
    var requestedOrientation = form["orientation"].FirstOrDefault() ?? "AUTO";
    var requestedPrintKind = form["printKind"].FirstOrDefault() ?? "DOCUMENT";
    if (requestedColourMode is not ("Mono" or "Colour") ||
        requestedPaperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
        !bool.TryParse(requestedDuplexText, out var requestedDuplex) ||
        !int.TryParse(requestedCopiesText, out var requestedCopies) ||
        requestedCopies is < 1 or > 100 ||
        requestedOrientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE"))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printOptions"] = ["Choose a supported paper size, portrait/landscape orientation, Mono/Colour, single/double-sided and 1–100 copies."]
        });
    }
    string batchGroupName;
    try
    {
        batchGroupName = PrintBatchGroupRules.Normalize(form["batchGroupName"].FirstOrDefault());
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { code = "invalid_batch_group", message = exception.Message });
    }

    string? requestedBatchName;
    try
    {
        requestedBatchName = PrintBatchNameRules.Normalize(form["batchName"].FirstOrDefault());
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { code = "invalid_batch_name", message = exception.Message });
    }
    if (file.Length is <= 0 or > UploadLimits.MaxFileBytes)
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: $"File size must be between 1 byte and {UploadLimits.MaxFileBytes / (1024 * 1024)} MB.");
    }

    var originalName = file.FileName.Split('/', '\\').LastOrDefault() ?? string.Empty;
    originalName = new string(originalName.Where(character => !char.IsControl(character)).ToArray()).Trim();
    if (string.IsNullOrWhiteSpace(originalName))
        return Results.BadRequest(new { code = "invalid_file_name", message = "The uploaded file name is invalid." });
    originalName = originalName[..Math.Min(originalName.Length, 255)];

    var photoSizeCode = form["photoSizeCode"].FirstOrDefault();
    var photoCountText = form["photoCount"].FirstOrDefault();
    var photosPerPageText = form["photosPerPage"].FirstOrDefault();
    var hasPhotoMetadata =
        !string.IsNullOrWhiteSpace(photoSizeCode) ||
        !string.IsNullOrWhiteSpace(photoCountText) ||
        !string.IsNullOrWhiteSpace(photosPerPageText);
    int? photoCount = int.TryParse(photoCountText, out var parsedPhotoCount) ? parsedPhotoCount : null;
    int? photosPerPage = int.TryParse(photosPerPageText, out var parsedPhotosPerPage) ? parsedPhotosPerPage : null;
    var validPhotoMetadata =
        requestedPrintKind == "PHOTO" &&
        photoSizeCode is string supportedPhotoSizeCode &&
        supportedPhotoSizeCode is "35x45" or "25x35" or "2x2" or "4x6" &&
        photoCount is >= 1 and <= UploadLimits.MaxPhotoCount &&
        photosPerPage is int requestedPhotosPerPage &&
        requestedPhotosPerPage >= 1 &&
        requestedPhotosPerPage <= PricingCalculator.MaximumPhotosPerA4Sheet(supportedPhotoSizeCode);
    var isPhotoUpload = requestedPrintKind == "PHOTO";
    var invalidPhotoUpload =
        isPhotoUpload &&
        (!validPhotoMetadata ||
         requestedColourMode != "Colour" ||
         requestedPaperSize != "A4" ||
         requestedDuplex ||
         requestedCopies != 1 ||
         requestedOrientation != "AUTO");
    if (requestedPrintKind is not ("DOCUMENT" or "PHOTO") ||
        (hasPhotoMetadata && !validPhotoMetadata) ||
        invalidPhotoUpload)
    {
        return Results.Conflict(new
        {
            code = requestedPrintKind == "ID_DOCUMENT" ? "id_document_private_printing_required" : "photo_metadata_invalid",
            message = requestedPrintKind == "ID_DOCUMENT"
                ? "ID Studio documents require Private Printing through the paired shop computer."
                : "Photo Studio sheets must be a valid A4 colour PDF with a supported photo size, quantity and per-page layout. No document was uploaded."
        });
    }

    // A retry must be the exact original document and the exact original
    // settings. Do not return an existing job just because its key matches;
    // verify the body before treating this request as idempotent.
    var existing = await db.PrintJobs.AsNoTracking()
        .Where(job => job.TenantId == session.TenantId && job.IdempotencyKey == idempotencyKey)
        .Select(job => new
        {
            job.Id, job.BatchId, job.BatchGroupName, job.BatchName, job.CustomerSessionId,
            job.JobNumber, job.Status, job.PageCount, job.Copies, job.ColourMode,
            job.PaperSize, job.IsDuplex, job.Orientation, job.PrintKind,
            job.PhotoSizeCode, job.PhotoCount, job.PhotosPerPage, job.PricingVersion,
            job.Currency, job.QuotedAmountMinor, job.CreatedUtc
        })
        .SingleOrDefaultAsync(ct);
    if (existing is not null)
    {
        if (existing.CustomerSessionId != session.Id)
            return Results.Conflict(new { code = "idempotency_key_reused", message = "This request key has already been used for another customer session." });

        var existingDocument = await db.PrintDocuments.AsNoTracking()
            .Where(document => document.TenantId == session.TenantId && document.PrintJobId == existing.Id)
            .Select(document => new
            {
                document.OriginalFileName, document.MediaType, document.SizeBytes,
                document.Sha256, document.PageSizesJson
            })
            .SingleOrDefaultAsync(ct);
        if (existingDocument is null)
            return Results.Conflict(new { code = "idempotency_state_missing", message = "The original document record is unavailable. Refresh the request before retrying." });

        string suppliedSha;
        await using (var existingInput = file.OpenReadStream())
        {
            suppliedSha = Convert.ToHexString(await SHA256.HashDataAsync(existingInput, ct)).ToLowerInvariant();
        }

        if ((!string.IsNullOrWhiteSpace(rawBatchId) && existing.BatchId != batchId) ||
            PrintBatchGroupRules.Normalize(existing.BatchGroupName) != batchGroupName ||
            existing.Copies != requestedCopies ||
            existing.ColourMode != requestedColourMode ||
            existing.PaperSize != requestedPaperSize ||
            existing.IsDuplex != requestedDuplex ||
            existing.Orientation != requestedOrientation ||
            existing.PrintKind != requestedPrintKind ||
            existing.PhotoSizeCode != photoSizeCode ||
            existing.PhotoCount != (photoCount ?? 0) ||
            existing.PhotosPerPage != photosPerPage ||
            (requestedBatchName is not null && existing.BatchName != requestedBatchName) ||
            existingDocument.OriginalFileName != originalName ||
            existingDocument.SizeBytes != file.Length ||
            !string.Equals(existingDocument.Sha256, suppliedSha, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new
            {
                code = "idempotency_key_reused",
                message = "This upload key already belongs to a different file, batch or set of print options. Keep the original request unchanged or start a new upload."
            });
        }

        return Results.Ok(new { Job = existing, Document = existingDocument, DuplicateRequest = true });
    }

    var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
        item.TenantId == session.TenantId &&
        item.CustomerSessionId == session.Id &&
        item.BatchId == batchId &&
        item.Status != "REJECTED", ct);
    if (batchCheckoutLocked)
        return Results.Conflict(new
        {
            code = "batch_upload_locked_after_payment_start",
            message = "This batch has a payment attempt or a verified payment. Start a new batch for additional files so every file has a clear payment total."
        });

    var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
    if (!subscription.IsAccessActive)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "This shop's trial or subscription period is no longer active.");
    }

    var stationDefaultPrinterId = await db.QrStations.AsNoTracking()
        .Where(station => station.Id == session.QrStationId && station.TenantId == session.TenantId)
        .Select(station => station.PrinterDeviceId)
        .SingleOrDefaultAsync(ct);
    var localAgentAvailable = await GetLocalAgentInfoAsync(
        db, session.TenantId, stationDefaultPrinterId, now, ct) is not null;
    if (!subscription.CloudAssistedPrintingEnabled && localAgentAvailable)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Secure cloud uploads are not included in this shop's plan while its local Agent is available.");
    }

    if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, file.Length))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "This document exceeds the maximum size configured for this shop's plan.");
    }

    var preflightQuota = await entitlements.CheckCapacityAsync(
        session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    if (!preflightQuota.Allowed)
    {
        return Results.Conflict(new { code = preflightQuota.Code, message = preflightQuota.Message, current = preflightQuota.Current, maximum = preflightQuota.Maximum });
    }

    var documentId = Guid.NewGuid();
    StoredDocument stored;
    try
    {
        await using var input = file.OpenReadStream();
        stored = await storage.ValidateAndStoreAsync(
            session.TenantId, documentId, originalName, input, file.Length, ct);
    }
    catch (DocumentValidationException exception)
    {
        return Results.UnprocessableEntity(new { code = "document_not_supported", message = exception.Message });
    }

    var photoPageCount = 0;
    if (isPhotoUpload)
    {
        photoPageCount = PricingCalculator.PhotoPageCount(
            photoCount ?? 0, photoSizeCode ?? string.Empty, photosPerPage ?? 0);
        if (!string.Equals(stored.Analysis.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            stored.Analysis.PageCount != photoPageCount ||
            !PhotoSheetPdfRules.HasExactlyA4PortraitPages(stored.Analysis.PageSizesJson, photoPageCount))
        {
            await storage.DeleteAsync(stored.StorageKey);
            return Results.UnprocessableEntity(new
            {
                code = "photo_sheet_layout_invalid",
                message = $"The Photo Studio PDF must contain exactly {photoPageCount} portrait A4 page(s), each with one full-page 300-DPI photo raster."
            });
        }
    }

    var job = new PrintJob
    {
        TenantId = session.TenantId,
        QrStationId = session.QrStationId,
        PrinterDeviceId = stationDefaultPrinterId,
        CustomerSessionId = session.Id,
        BatchId = batchId,
        BatchGroupName = batchGroupName,
        BatchName = requestedBatchName,
        JobNumber = $"SP-{now:yyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
        Status = "AWAITING_CONFIGURATION",
        IdempotencyKey = idempotencyKey,
        PageCount = isPhotoUpload ? photoPageCount : stored.Analysis.PageCount,
        Copies = requestedCopies,
        ColourMode = requestedColourMode,
        PaperSize = requestedPaperSize,
        IsDuplex = requestedDuplex,
        Orientation = requestedOrientation,
        PrintKind = requestedPrintKind,
        PhotoSizeCode = photoSizeCode,
        PhotoCount = photoCount ?? 0,
        PhotosPerPage = photosPerPage,
        Currency = "INR",
        QuotedAmountMinor = 0,
        CreatedUtc = now,
        UpdatedUtc = now
    };
    var document = new PrintDocument
    {
        Id = documentId,
        TenantId = session.TenantId,
        PrintJobId = job.Id,
        OriginalFileName = originalName,
        MediaType = stored.Analysis.MediaType,
        FileExtension = stored.Analysis.Extension,
        SizeBytes = stored.SizeBytes,
        Sha256 = stored.Sha256,
        StorageKey = stored.StorageKey,
        PageSizesJson = stored.Analysis.PageSizesJson,
        CreatedUtc = now
    };

    try
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await entitlements.AcquireQuotaLockAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
        var racedJob = await db.PrintJobs.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId && item.IdempotencyKey == idempotencyKey)
            .Select(item => new
            {
                item.Id, item.CustomerSessionId, item.BatchId, item.BatchName, item.BatchGroupName,
                item.JobNumber, item.Status, item.PageCount, item.Copies, item.ColourMode,
                item.PaperSize, item.IsDuplex, item.Orientation, item.PrintKind,
                item.PhotoSizeCode, item.PhotoCount, item.PhotosPerPage, item.PricingVersion,
                item.Currency, item.QuotedAmountMinor, item.CreatedUtc
            })
            .SingleOrDefaultAsync(ct);
        if (racedJob is not null)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            if (racedJob.CustomerSessionId != session.Id)
                return Results.Conflict(new { code = "idempotency_key_reused", message = "This request key has already been used for another customer session." });
            var racedDocument = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId && item.PrintJobId == racedJob.Id)
                .Select(item => new { item.OriginalFileName, item.MediaType, item.SizeBytes, item.Sha256, item.PageSizesJson })
                .SingleOrDefaultAsync(ct);
            if (racedDocument is null ||
                (!string.IsNullOrWhiteSpace(rawBatchId) && racedJob.BatchId != batchId) ||
                racedJob.BatchGroupName != batchGroupName ||
                racedJob.Copies != requestedCopies ||
                racedJob.ColourMode != requestedColourMode ||
                racedJob.PaperSize != requestedPaperSize ||
                racedJob.IsDuplex != requestedDuplex ||
                racedJob.Orientation != requestedOrientation ||
                racedJob.PrintKind != requestedPrintKind ||
                racedJob.PhotoSizeCode != photoSizeCode ||
                racedJob.PhotoCount != (photoCount ?? 0) ||
                racedJob.PhotosPerPage != photosPerPage ||
                (requestedBatchName is not null && racedJob.BatchName != requestedBatchName) ||
                racedDocument.OriginalFileName != originalName ||
                racedDocument.SizeBytes != file.Length ||
                !string.Equals(racedDocument.Sha256, stored.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new
                {
                    code = "idempotency_key_reused",
                    message = "This upload key already belongs to a different file or set of print options. Refresh the request instead of changing the original job."
                });
            }
            return Results.Ok(new { Job = racedJob, Document = racedDocument, DuplicateRequest = true });
        }

        var batchIdOwnedByAnotherSession =
            await db.PrintJobs.AsNoTracking().AnyAsync(item =>
                item.TenantId == session.TenantId &&
                item.BatchId == batchId &&
                item.CustomerSessionId != session.Id, ct) ||
            await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(item =>
                item.TenantId == session.TenantId &&
                item.BatchId == batchId &&
                item.CustomerSessionId != session.Id, ct);
        if (batchIdOwnedByAnotherSession)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Conflict(new
            {
                code = "batch_id_already_owned",
                message = "This batch identifier is already associated with another customer session. Start a new batch and retry the upload."
            });
        }

        _ = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
            .ToListAsync(ct);
        var hasPrivateBatchItem = await db.PrintJobs.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId && item.CustomerSessionId == session.Id &&
            item.BatchId == batchId && item.PrivatePrinting, ct) ||
            await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(item =>
                item.TenantId == session.TenantId && item.CustomerSessionId == session.Id &&
                item.BatchId == batchId && item.PrivatePrinting, ct);
        if (hasPrivateBatchItem)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Conflict(new
            {
                code = "batch_private_mode_mismatch",
                message = "This batch already contains Private Printing documents. Keep one privacy choice for the entire batch, or start a new batch."
            });
        }

        var checkoutStartedAfterUpload = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId &&
            item.CustomerSessionId == session.Id &&
            item.BatchId == batchId &&
            item.Status != "REJECTED", ct);
        if (checkoutStartedAfterUpload)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Conflict(new
            {
                code = "batch_upload_locked_after_payment_start",
                message = "This batch payment started while the file was uploading. The file was not added; start a new batch for it."
            });
        }

        var batchJobsForName = await db.PrintJobs
            .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
            .ToListAsync(ct);
        var batchTicketsForName = await db.LocalPrintUploadTickets
            .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
            .ToListAsync(ct);
        string? effectiveBatchName;
        try
        {
            effectiveBatchName = PrintBatchNameRules.Resolve(requestedBatchName,
                batchJobsForName.Select(item => item.BatchName).Concat(batchTicketsForName.Select(item => item.BatchName)));
        }
        catch (ArgumentException)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Conflict(new { code = "batch_name_locked", message = "This print batch already has a different name. Refresh the original request and keep its saved batch name, or start a new batch." });
        }
        if (effectiveBatchName is not null)
        {
            foreach (var existingBatchJob in batchJobsForName) existingBatchJob.BatchName ??= effectiveBatchName;
            foreach (var existingBatchTicket in batchTicketsForName) existingBatchTicket.BatchName ??= effectiveBatchName;
            job.BatchName = effectiveBatchName;
        }

        var batchJobSizes = await (
            from batchJob in db.PrintJobs.AsNoTracking()
            join batchDocument in db.PrintDocuments.AsNoTracking()
                on new { batchJob.TenantId, PrintJobId = batchJob.Id } equals new { batchDocument.TenantId, batchDocument.PrintJobId }
            where batchJob.TenantId == session.TenantId &&
                  batchJob.CustomerSessionId == session.Id &&
                  batchJob.BatchId == batchId
            select batchDocument.SizeBytes).ToListAsync(ct);
        var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
            .Where(ticket => ticket.TenantId == session.TenantId &&
                             ticket.CustomerSessionId == session.Id &&
                             ticket.BatchId == batchId &&
                             ticket.CompletedUtc == null &&
                             ticket.ExpiresUtc > now)
            .Select(ticket => ticket.SizeBytes).ToListAsync(ct);
        if (!PrintBatchCapacityRules.CanAdd(
            batchJobSizes.Count + pendingBatchTicketSizes.Count,
            checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
            file.Length))
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"A print batch can contain up to {UploadLimits.MaxBatchFiles} files and {UploadLimits.MaxBatchBytes / (1024 * 1024)} MB in total.");
        }
        var quota = await entitlements.CheckCapacityAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
        }

        db.PrintJobs.Add(job);
        db.PrintDocuments.Add(document);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = null,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = "DOCUMENT_UPLOADED",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch
    {
        await storage.DeleteAsync(stored.StorageKey);
        throw;
    }

    return Results.Created($"/api/v1/public/jobs/{job.Id}", new
    {
        Job = new
        {
            job.Id,
            job.BatchGroupName,
            job.BatchName,
            job.JobNumber,
            job.Status,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.Orientation,
            job.PricingVersion,
            job.Currency,
            job.QuotedAmountMinor,
            job.CreatedUtc
        },
        Document = new
        {
            document.Id,
            document.OriginalFileName,
            document.MediaType,
            document.SizeBytes,
            document.Sha256,
            document.PageSizesJson
        },
        DuplicateRequest = false
    });
})
.WithMetadata(new RequestSizeLimitAttribute(UploadLimits.MaxMultipartRequestBytes))
.RequireRateLimiting("document-upload");

publicApi.MapPost("/sessions/current/local-upload-tickets", async (
    CreateLocalPrintUploadTicketRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) ||
        string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100 ||
        string.IsNullOrWhiteSpace(request.GrantToken) || request.GrantToken.Length is < 40 or > 128)
    {
        return Results.BadRequest(new { code = "local_upload_ticket_request_invalid", message = "A live print session, idempotency key and one-use upload grant are required." });
    }

    string batchGroupName;
    try
    {
        batchGroupName = PrintBatchGroupRules.Normalize(request.BatchGroupName);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { code = "invalid_batch_group", message = exception.Message });
    }

    string? requestedBatchName;
    try
    {
        requestedBatchName = PrintBatchNameRules.Normalize(request.BatchName);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { code = "invalid_batch_name", message = exception.Message });
    }

    var originalName = request.OriginalFileName?.Split('/', '\\').LastOrDefault() ?? string.Empty;
    originalName = new string(originalName.Where(character => !char.IsControl(character)).ToArray()).Trim();
    if (string.IsNullOrWhiteSpace(originalName))
    {
        return Results.BadRequest(new { code = "invalid_file_name", message = "The uploaded document name is invalid." });
    }
    originalName = originalName[..Math.Min(originalName.Length, 255)];
    var extension = string.IsNullOrWhiteSpace(request.DetectedFileExtension)
        ? Path.GetExtension(originalName).ToLowerInvariant()
        : request.DetectedFileExtension.Trim().ToLowerInvariant();
    if (request.PrivatePrinting)
    {
        originalName = "Private document";
        batchGroupName = "Private documents";
        requestedBatchName = null;
    }
    var mediaType = extension switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => null
    };
    var isIdDocumentUpload = request.PrintKind == "ID_DOCUMENT";
    var hasPhotoMetadata = request.PhotoSizeCode is not null || request.PhotoCount is not null || request.PhotosPerPage is not null;
    var validPhotoMetadata = request.PrintKind == "PHOTO" &&
        request.PhotoSizeCode is string photoSizeCode &&
        (photoSizeCode is "35x45" or "25x35" or "2x2" or "4x6") &&
        request.PhotoCount is >= 1 and <= UploadLimits.MaxPhotoCount &&
        request.PhotosPerPage is int photosPerPage &&
        photosPerPage >= 1 &&
        photosPerPage <= PricingCalculator.MaximumPhotosPerA4Sheet(photoSizeCode);
    var invalidPrivatePhotoUpload = request.PrivatePrinting && request.PrintKind == "PHOTO" &&
        (!validPhotoMetadata || mediaType != "application/pdf" ||
         request.ColourMode != "Colour" || request.PaperSize != "A4" ||
         request.IsDuplex || request.Copies != 1 ||
         request.Orientation is not ("AUTO" or "PORTRAIT"));
    if (request.PrintKind is not ("DOCUMENT" or "PHOTO" or "ID_DOCUMENT") ||
        (isIdDocumentUpload && (!request.PrivatePrinting ||
            mediaType != "application/pdf" ||
            request.ColourMode != "Colour" ||
            request.PaperSize != "A4" ||
            request.IsDuplex ||
            request.Copies != 1 ||
            request.Orientation != "PORTRAIT")) ||
        (hasPhotoMetadata && !validPhotoMetadata) ||
        invalidPrivatePhotoUpload)
        return Results.Conflict(new
        {
            code = isIdDocumentUpload ? "id_document_private_printing_required" : "photo_metadata_invalid",
            message = isIdDocumentUpload
                ? "ID card sheets require Private Printing, a one-page A4 portrait PDF, colour, one copy and single-sided output. Start a new Private Printing batch."
                : "Private Photo Studio sheets require a supported photo size, quantity, A4 sheet layout and a single-sided colour A4 PDF. No document was uploaded."
        });
    var sha = request.Sha256?.Trim().ToLowerInvariant();
    if (mediaType is null || request.SizeBytes is <= 0 or > UploadLimits.MaxFileBytes ||
        string.IsNullOrWhiteSpace(sha) || sha.Length != 64 || sha.Any(character => !Uri.IsHexDigit(character)) ||
        request.ColourMode is not ("Mono" or "Colour") ||
        request.PaperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
        request.Copies is < 1 or > 100 ||
        request.Orientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE"))
    {
        return Results.UnprocessableEntity(new { code = "document_not_supported", message = $"Local upload supports PDF, JPG/JPEG and PNG files up to {UploadLimits.MaxFileBytes / (1024 * 1024)} MB with a valid SHA-256 digest." });
    }

    var batchId = request.BatchId ?? Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var customerSessionTokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == customerSessionTokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null)
        .Select(item => new { item.Id, item.TenantId, item.QrStationId, item.ExpiresUtc })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    var hasPrivacyMismatch = await db.PrintJobs.AsNoTracking().AnyAsync(job =>
        job.TenantId == session.TenantId && job.CustomerSessionId == session.Id &&
        job.BatchId == batchId && job.PrivatePrinting != request.PrivatePrinting, ct) ||
        await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(ticket =>
            ticket.TenantId == session.TenantId && ticket.CustomerSessionId == session.Id &&
            ticket.BatchId == batchId && ticket.PrivatePrinting != request.PrivatePrinting, ct);
    if (hasPrivacyMismatch)
        return Results.Conflict(new
        {
            code = "batch_privacy_mode_mismatch",
            message = "Every file in a batch must use the same privacy choice. Start a new batch to change between Standard and Private Printing."
        });

    var grantHash = HashOpaqueToken(request.GrantToken);
    var existing = await db.LocalPrintUploadTickets.AsNoTracking()
        .SingleOrDefaultAsync(item =>
            item.TenantId == session.TenantId &&
            item.CustomerSessionId == session.Id &&
            item.IdempotencyKey == idempotencyKey, ct);
    if (existing is not null)
    {
        var existingGrantHash = Convert.FromHexString(existing.GrantHash);
        var suppliedGrantHash = Convert.FromHexString(grantHash);
        var completedPrivateTicket = existing.PrivatePrinting && existing.CompletedUtc is not null;
        if (!CryptographicOperations.FixedTimeEquals(existingGrantHash, suppliedGrantHash) ||
            existing.PrivatePrinting != request.PrivatePrinting ||
            existing.PrintKind != request.PrintKind ||
            (!completedPrivateTicket && (existing.SizeBytes != request.SizeBytes ||
                !string.Equals(existing.Sha256, sha, StringComparison.OrdinalIgnoreCase) ||
                existing.OriginalFileName != originalName ||
                existing.FileExtension != extension ||
                !string.Equals(existing.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))) ||
            (request.BatchId is Guid requestedBatchId && existing.BatchId != requestedBatchId) ||
            (requestedBatchName is not null && existing.BatchName != requestedBatchName) ||
            PrintBatchGroupRules.Normalize(existing.BatchGroupName) != batchGroupName ||
            existing.ColourMode != request.ColourMode ||
            existing.PaperSize != request.PaperSize ||
            existing.IsDuplex != request.IsDuplex ||
            existing.Copies != request.Copies ||
            existing.Orientation != request.Orientation ||
            existing.PhotoSizeCode != request.PhotoSizeCode ||
            existing.PhotoCount != request.PhotoCount ||
            existing.PhotosPerPage != request.PhotosPerPage)
        {
            return Results.Conflict(new { code = "local_upload_idempotency_conflict", message = "This upload attempt key is already associated with a different document, batch or set of print options." });
        }

        var originalTicket = existing;
        // Reuse the original ticket rather than creating a second reservation
        // when a never-completed LAN attempt's 15-minute lease has expired.
        // This is allowed only after the old claim lease ends, while the same
        // shop Agent remains paired/online and no PrintJob or document exists.
        var renewalNow = DateTimeOffset.UtcNow;
        if (LocalUploadRecoveryRules.CanRenewExpiredTicket(
                existing.CreatedUtc, existing.ExpiresUtc, existing.ClaimLeaseExpiresUtc,
                existing.CompletedUtc, renewalNow,
                TimeSpan.FromHours(LocalUploadRecoveryRules.MaximumRecoveryAgeHours)))
        {
            await using var renewalTransaction = await db.Database.BeginTransactionAsync(ct);
            var lockedTickets = await db.LocalPrintUploadTickets.FromSqlInterpolated(
                $"SELECT * FROM \"LocalPrintUploadTickets\" WHERE \"Id\" = {existing.Id} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
                .ToListAsync(ct);
            var lockedTicket = lockedTickets.SingleOrDefault();
            if (lockedTicket is not null)
            {
                // Return the authoritative row even when another retry renewed it
                // while this request was waiting for the lock.
                existing = lockedTicket;

                // Checkout takes the same batch-job row locks. Serialise lease
                // renewal with payment creation so a ticket cannot become active
                // after the batch has entered checkout.
                _ = await db.PrintJobs.FromSqlInterpolated(
                    $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {lockedTicket.TenantId} AND \"CustomerSessionId\" = {lockedTicket.CustomerSessionId} AND \"BatchId\" = {lockedTicket.BatchId} FOR UPDATE")
                    .ToListAsync(ct);
            }
            if (lockedTicket is not null &&
                lockedTicket.IdempotencyKey == idempotencyKey &&
                lockedTicket.TenantId == originalTicket.TenantId &&
                lockedTicket.CustomerSessionId == originalTicket.CustomerSessionId &&
                lockedTicket.PrintJobId == originalTicket.PrintJobId &&
                lockedTicket.BatchId == originalTicket.BatchId &&
                lockedTicket.QrStationId == originalTicket.QrStationId &&
                lockedTicket.PrinterDeviceId == originalTicket.PrinterDeviceId &&
                lockedTicket.AgentDeviceId == originalTicket.AgentDeviceId &&
                lockedTicket.GrantHash == originalTicket.GrantHash &&
                lockedTicket.SizeBytes == request.SizeBytes &&
                string.Equals(lockedTicket.Sha256, sha, StringComparison.OrdinalIgnoreCase) &&
                lockedTicket.OriginalFileName == originalName &&
                lockedTicket.BatchGroupName == originalTicket.BatchGroupName &&
                lockedTicket.BatchName == originalTicket.BatchName &&
                lockedTicket.ColourMode == request.ColourMode &&
                lockedTicket.PaperSize == request.PaperSize &&
                lockedTicket.IsDuplex == request.IsDuplex &&
                lockedTicket.Copies == request.Copies &&
                lockedTicket.Orientation == request.Orientation &&
                lockedTicket.CompletedUtc is null &&
                LocalUploadRecoveryRules.CanRenewExpiredTicket(
                    lockedTicket.CreatedUtc, lockedTicket.ExpiresUtc, lockedTicket.ClaimLeaseExpiresUtc,
                    lockedTicket.CompletedUtc, renewalNow,
                    TimeSpan.FromHours(LocalUploadRecoveryRules.MaximumRecoveryAgeHours)) &&
                !await db.PrintJobs.AsNoTracking().AnyAsync(item =>
                    item.TenantId == lockedTicket.TenantId && item.Id == lockedTicket.PrintJobId, ct) &&
                !await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
                    item.TenantId == lockedTicket.TenantId && item.PrintJobId == lockedTicket.PrintJobId, ct) &&
                !await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
                    item.TenantId == lockedTicket.TenantId &&
                    item.CustomerSessionId == lockedTicket.CustomerSessionId &&
                    item.BatchId == lockedTicket.BatchId &&
                    item.Status != "REJECTED", ct))
            {
                var currentAgentEndpoints = await (
                    from station in db.QrStations.AsNoTracking()
                    join printer in db.PrinterDevices.AsNoTracking()
                        on new { station.TenantId, PrinterId = station.PrinterDeviceId!.Value }
                        equals new { printer.TenantId, PrinterId = printer.Id }
                    join agent in db.AgentDevices.AsNoTracking()
                        on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
                        equals new { agent.TenantId, AgentId = agent.Id }
                    where station.Id == lockedTicket.QrStationId &&
                          station.TenantId == lockedTicket.TenantId &&
                          station.IsEnabled &&
                          station.PrinterDeviceId == lockedTicket.PrinterDeviceId &&
                          printer.IsActive &&
                          printer.AgentDeviceId == lockedTicket.AgentDeviceId &&
                          !string.IsNullOrWhiteSpace(printer.QueueName) &&
                          agent.Id == lockedTicket.AgentDeviceId &&
                          agent.RevokedUtc == null &&
                          agent.LastSeenUtc != null &&
                          agent.LastSeenUtc >= renewalNow.AddMinutes(-2)
                    select agent.LocalEndpointsJson
                ).SingleOrDefaultAsync(ct);

                if (currentAgentEndpoints is not null &&
                    AgentLocalEndpointRules.Normalize(ReadLocalAgentEndpoints(currentAgentEndpoints)).Count > 0)
                {
                    lockedTicket.ExpiresUtc = renewalNow.AddMinutes(15);
                    lockedTicket.ClaimedUtc = null;
                    lockedTicket.ClaimLeaseExpiresUtc = null;
                    await db.SaveChangesAsync(ct);
                    await renewalTransaction.CommitAsync(ct);
                    existing = lockedTicket;
                }
                else
                {
                    await renewalTransaction.RollbackAsync(ct);
                }
            }
            else
            {
                await renewalTransaction.RollbackAsync(ct);
            }
        }

        return Results.Ok(new
        {
            existing.Id,
            existing.PrintJobId,
            existing.PrivatePrinting,
            existing.BatchId,
            existing.BatchGroupName,
            existing.BatchName,
            existing.JobNumber,
            existing.AgentDeviceId,
            existing.ExpiresUtc,
            Completed = existing.CompletedUtc != null
        });
    }

    // An expired QR session may replay only a previously created ticket.
    // It cannot reserve new work or append files to an expired session.
    if (session.ExpiresUtc <= now) return Results.Unauthorized();

    var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
        item.TenantId == session.TenantId &&
        item.CustomerSessionId == session.Id &&
        item.BatchId == batchId &&
        item.Status != "REJECTED", ct);
    if (batchCheckoutLocked)
        return Results.Conflict(new
        {
            code = "batch_upload_locked_after_payment_start",
            message = "This batch has a payment attempt or a verified payment. Start a new batch for additional files so every file has a clear payment total."
        });

    var stationPrinter = await (
        from station in db.QrStations.AsNoTracking()
        join printer in db.PrinterDevices.AsNoTracking()
            on new { station.TenantId, PrinterId = station.PrinterDeviceId!.Value }
            equals new { printer.TenantId, PrinterId = printer.Id }
        join agent in db.AgentDevices.AsNoTracking()
            on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
            equals new { agent.TenantId, AgentId = agent.Id }
        where station.Id == session.QrStationId &&
              station.TenantId == session.TenantId &&
              station.IsEnabled &&
              station.PrinterDeviceId != null &&
              printer.IsActive &&
              printer.AgentDeviceId != null &&
              !string.IsNullOrWhiteSpace(printer.QueueName) &&
              agent.RevokedUtc == null &&
              agent.LastSeenUtc != null &&
              agent.LastSeenUtc >= now.AddMinutes(-2)
        select new
        {
            station.Id,
            PrinterDeviceId = printer.Id,
            AgentDeviceId = agent.Id,
            printer.Status,
            printer.LastSeenUtc,
            printer.OutputMode,
            agent.LocalEndpointsJson,
            agent.DocumentEncryptionPublicKey
        }
    ).SingleOrDefaultAsync(ct);

    if (stationPrinter is null ||
        (!request.PrivatePrinting &&
         AgentLocalEndpointRules.Normalize(ReadLocalAgentEndpoints(stationPrinter.LocalEndpointsJson)).Count == 0))
    {
        return Results.Conflict(new
        {
            code = "local_print_agent_unavailable",
            message = request.PrivatePrinting
                ? "The paired shop computer is offline. No document has been uploaded. Please retry when the shop computer reconnects."
                : "The shop computer is temporarily unavailable. SmartPrint can keep this request securely online; the shop can release it when an Agent is ready."
        });
    }
    if (request.PrivatePrinting)
    {
        var allowPrivateVirtualOutput = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct);
        var printerIsLive = stationPrinter.LastSeenUtc is DateTimeOffset printerSeen &&
            printerSeen >= now.AddMinutes(-2) &&
            stationPrinter.Status is "Ready" or "Busy";
        if (!PrivatePrintingOutputPolicy.IsEligible(allowPrivateVirtualOutput, environment.IsProduction(), stationPrinter.OutputMode) ||
            !printerIsLive)
        {
            return Results.Conflict(new
            {
                code = "private_printing_physical_output_required",
                message = "Private Printing requires a connected physical printer. Approved virtual printers are available only when the Company Main Admin enables non-production test output."
            });
        }
    }
    if (request.PrivatePrinting && string.IsNullOrWhiteSpace(stationPrinter.DocumentEncryptionPublicKey))
    {
        return Results.Conflict(new
        {
            code = "private_printing_unavailable",
            message = "This shop computer has not published its secure document-encryption key yet. Ask the operator to reconnect SmartPrint and try again."
        });
    }

    var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
    if (!subscription.IsAccessActive)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This shop's trial or subscription period is no longer active.");
    }
    if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, request.SizeBytes))
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "This document exceeds the maximum size configured for this shop's plan.");
    }

    var ticket = new LocalPrintUploadTicket
    {
        TenantId = session.TenantId,
        CustomerSessionId = session.Id,
        QrStationId = session.QrStationId,
        PrinterDeviceId = stationPrinter.PrinterDeviceId,
        AgentDeviceId = stationPrinter.AgentDeviceId,
        BatchId = batchId,
        BatchGroupName = batchGroupName,
        BatchName = requestedBatchName,
        JobNumber = $"SP-{now:yyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
        IdempotencyKey = idempotencyKey,
        GrantHash = grantHash,
        OriginalFileName = originalName,
        MediaType = mediaType,
        FileExtension = extension,
        SizeBytes = request.SizeBytes,
        Sha256 = sha,
        ColourMode = request.ColourMode,
        PaperSize = request.PaperSize,
        IsDuplex = request.IsDuplex,
        Copies = request.Copies,
        Orientation = request.Orientation,
        PrivatePrinting = request.PrivatePrinting,
        PrintKind = request.PrintKind,
        PhotoSizeCode = request.PhotoSizeCode,
        PhotoCount = request.PhotoCount,
        PhotosPerPage = request.PhotosPerPage,
        CreatedUtc = now,
        ExpiresUtc = now.AddMinutes(15)
    };
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await entitlements.AcquireQuotaLockAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);

    var batchIdOwnedByAnotherSession =
        await db.PrintJobs.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId &&
            item.BatchId == batchId &&
            item.CustomerSessionId != session.Id, ct) ||
        await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId &&
            item.BatchId == batchId &&
            item.CustomerSessionId != session.Id, ct);
    if (batchIdOwnedByAnotherSession)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_id_already_owned",
            message = "This batch identifier is already associated with another customer session. Start a new batch and retry the upload."
        });
    }

    _ = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
        .ToListAsync(ct);

    var hasOppositePrivacyMode = await db.PrintJobs.AsNoTracking().AnyAsync(item =>
        item.TenantId == session.TenantId && item.CustomerSessionId == session.Id &&
        item.BatchId == batchId && item.PrivatePrinting != request.PrivatePrinting, ct) ||
        await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId && item.CustomerSessionId == session.Id &&
            item.BatchId == batchId && item.PrivatePrinting != request.PrivatePrinting, ct);
    if (hasOppositePrivacyMode)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_private_mode_mismatch",
            message = "This batch already contains documents with a different privacy choice. Keep one privacy choice for the entire batch, or start a new batch."
        });
    }

    var checkoutStartedAfterTicketPreflight = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
        item.TenantId == session.TenantId &&
        item.CustomerSessionId == session.Id &&
        item.BatchId == batchId &&
        item.Status != "REJECTED", ct);
    if (checkoutStartedAfterTicketPreflight)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_upload_locked_after_payment_start",
            message = "This batch payment started while the file was being prepared. The file was not added; start a new batch for it."
        });
    }

    var racedTicket = await db.LocalPrintUploadTickets.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == session.TenantId &&
            item.CustomerSessionId == session.Id && item.IdempotencyKey == idempotencyKey, ct);
    if (racedTicket is not null)
    {
        await transaction.RollbackAsync(ct);
        var racedCompletedPrivateTicket = racedTicket.PrivatePrinting && racedTicket.CompletedUtc is not null;
        var sameOriginalDocument = racedCompletedPrivateTicket ||
            (racedTicket.SizeBytes == request.SizeBytes &&
             string.Equals(racedTicket.Sha256, sha, StringComparison.OrdinalIgnoreCase) &&
             racedTicket.OriginalFileName == originalName);
        var sameRequest = (request.BatchId is null || racedTicket.BatchId == request.BatchId.Value) &&
            (requestedBatchName is null || racedTicket.BatchName == requestedBatchName) &&
            sameOriginalDocument &&
            racedTicket.PrivatePrinting == request.PrivatePrinting &&
            PrintBatchGroupRules.Normalize(racedTicket.BatchGroupName) == batchGroupName &&
            racedTicket.ColourMode == request.ColourMode &&
            racedTicket.PaperSize == request.PaperSize &&
            racedTicket.IsDuplex == request.IsDuplex &&
            racedTicket.Copies == request.Copies &&
            racedTicket.Orientation == request.Orientation &&
            racedTicket.PhotoSizeCode == request.PhotoSizeCode &&
            racedTicket.PhotoCount == request.PhotoCount &&
            racedTicket.PhotosPerPage == request.PhotosPerPage &&
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(racedTicket.GrantHash),
                Convert.FromHexString(grantHash));
        if (!sameRequest)
            return Results.Conflict(new { code = "local_upload_idempotency_conflict", message = "This upload attempt key is already associated with a different document." });
        return Results.Ok(new
        {
            racedTicket.Id,
            racedTicket.PrintJobId,
            racedTicket.PrivatePrinting,
            racedTicket.BatchId,
            racedTicket.BatchGroupName,
            racedTicket.BatchName,
            racedTicket.JobNumber,
            racedTicket.AgentDeviceId,
            racedTicket.ExpiresUtc,
            Completed = racedTicket.CompletedUtc != null
        });
    }

    var batchJobsForName = await db.PrintJobs
        .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
        .ToListAsync(ct);
    var batchTicketsForName = await db.LocalPrintUploadTickets
        .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
        .ToListAsync(ct);
    string? effectiveBatchName;
    try
    {
        effectiveBatchName = PrintBatchNameRules.Resolve(requestedBatchName,
            batchJobsForName.Select(item => item.BatchName).Concat(batchTicketsForName.Select(item => item.BatchName)));
    }
    catch (ArgumentException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "batch_name_locked", message = "This print batch already has a different name. Refresh the original request and keep its saved batch name, or start a new batch." });
    }
    if (effectiveBatchName is not null)
    {
        foreach (var existingBatchJob in batchJobsForName) existingBatchJob.BatchName ??= effectiveBatchName;
        foreach (var existingBatchTicket in batchTicketsForName) existingBatchTicket.BatchName ??= effectiveBatchName;
        ticket.BatchName = effectiveBatchName;
    }

    var quota = await entitlements.CheckCapacityAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    if (!quota.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
    }

    // The tenant quota transaction lock above serialises ticket reservations,
    // including the first file in an empty batch where no child row exists yet.
    // Local-only document bytes cannot move to a different Windows computer.
    // A ticket may be recovered over HTTPS for 24 hours after its 15-minute
    // LAN claim lease expires. Keep its original Agent identity reserved for the
    // entire recovery window so a batch cannot silently span two shop computers.
    var localUploadRecoveryCutoff = now.AddHours(-LocalUploadRecoveryRules.MaximumRecoveryAgeHours);
    var existingLocalTicketAgents = await db.LocalPrintUploadTickets.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId &&
                       item.CustomerSessionId == session.Id &&
                       item.BatchId == batchId &&
                       (item.CompletedUtc != null || item.CreatedUtc >= localUploadRecoveryCutoff))
        .Select(item => (Guid?)item.AgentDeviceId)
        .ToListAsync(ct);
    var existingLocalDocumentAgents = await (
        from batchJob in db.PrintJobs.AsNoTracking()
        join batchDocument in db.PrintDocuments.AsNoTracking()
            on new { batchJob.TenantId, PrintJobId = batchJob.Id }
            equals new { batchDocument.TenantId, batchDocument.PrintJobId }
        where batchJob.TenantId == session.TenantId &&
              batchJob.CustomerSessionId == session.Id &&
              batchJob.BatchId == batchId &&
              batchDocument.StorageMode == "LOCAL_AGENT"
        select batchDocument.AgentDeviceId).ToListAsync(ct);
    var localAgentConflict = BatchLocalUploadRules.FindAgentConflict(
        existingLocalTicketAgents.Concat(existingLocalDocumentAgents),
        stationPrinter.AgentDeviceId);
    if (localAgentConflict is not null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = localAgentConflict,
            message = "Some files in this batch are already stored on another shop computer. Continue with Cloud-assisted upload for the remaining files; the shop can route the batch to the computer holding those files. To use a different computer, start a new batch and upload every file through Cloud-assisted upload."
        });
    }

    var batchJobSizes = await (
        from batchJob in db.PrintJobs.AsNoTracking()
        join batchDocument in db.PrintDocuments.AsNoTracking()
            on new { batchJob.TenantId, PrintJobId = batchJob.Id } equals new { batchDocument.TenantId, batchDocument.PrintJobId }
        where batchJob.TenantId == session.TenantId &&
              batchJob.CustomerSessionId == session.Id &&
              batchJob.BatchId == batchId
        select batchDocument.SizeBytes).ToListAsync(ct);
    var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId &&
                       item.CustomerSessionId == session.Id &&
                       item.BatchId == batchId &&
                       item.CompletedUtc == null &&
                       item.CreatedUtc >= localUploadRecoveryCutoff)
        .Select(item => item.SizeBytes).ToListAsync(ct);
    if (!PrintBatchCapacityRules.CanAdd(
        batchJobSizes.Count + pendingBatchTicketSizes.Count,
        checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
        request.SizeBytes))
    {
        await transaction.RollbackAsync(ct);
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
            title: $"A print batch can contain up to {UploadLimits.MaxBatchFiles} files and {UploadLimits.MaxBatchBytes / (1024 * 1024)} MB in total.");
    }

    db.LocalPrintUploadTickets.Add(ticket);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    return Results.Created($"/api/v1/public/sessions/current/local-upload-tickets/{ticket.Id}", new
    {
        ticket.Id,
        ticket.PrintJobId,
        ticket.PrivatePrinting,
        ticket.PrintKind,
        ticket.BatchId,
        ticket.BatchGroupName,
        ticket.BatchName,
        ticket.JobNumber,
        ticket.AgentDeviceId,
        ticket.ExpiresUtc,
        Completed = false
    });
}).RequireRateLimiting("document-upload");


publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/private-relay", async (
    Guid ticketId,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    PrivateRelayWakeupService relayWakeups,
    ILoggerFactory loggerFactory,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
    var wrappedKey = context.Request.Headers["X-Print-Encrypted-Key"].FirstOrDefault();
    var ivText = context.Request.Headers["X-Print-Encrypted-Iv"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) || string.IsNullOrWhiteSpace(rawGrant) ||
        rawGrant.Length is < 40 or > 128 || string.IsNullOrWhiteSpace(wrappedKey) ||
        wrappedKey.Length > 2048 || string.IsNullOrWhiteSpace(ivText) || ivText.Length > 64)
        return Results.Unauthorized();

    byte[] keyBytes;
    byte[] ivBytes;
    try
    {
        keyBytes = Convert.FromBase64String(wrappedKey);
        ivBytes = Convert.FromBase64String(ivText);
    }
    catch (FormatException) { return Results.BadRequest(new { code = "private_relay_envelope_invalid" }); }
    var envelopeValid = keyBytes.Length is >= 256 and <= 512 && ivBytes.Length == 12;
    CryptographicOperations.ZeroMemory(keyBytes);
    CryptographicOperations.ZeroMemory(ivBytes);
    if (!envelopeValid)
        return Results.BadRequest(new { code = "private_relay_envelope_invalid" });

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    var ticket = await db.LocalPrintUploadTickets
        .SingleOrDefaultAsync(item => item.Id == ticketId &&
            item.TenantId == session.TenantId && item.CustomerSessionId == session.Id &&
            item.PrivatePrinting, ct);
    if (ticket is null) return Results.NotFound();
    byte[] grantHash;
    byte[] expectedHash;
    try
    {
        grantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
        expectedHash = Convert.FromHexString(ticket.GrantHash);
    }
    catch (FormatException) { return Results.Unauthorized(); }
    if (!CryptographicOperations.FixedTimeEquals(grantHash, expectedHash))
        return Results.Unauthorized();
    if (ticket.CompletedUtc is not null)
        return Results.Conflict(new { code = "private_relay_already_completed", message = "This private print request has already reached the shop queue." });
    if (ticket.PrivateRelayFailedUtc is not null)
        return Results.Conflict(new { code = "private_relay_validation_failed", message = ticket.PrivateRelayFailureMessage });
    if (!await IsPrivatePrintingOutputQueueReadyAsync(
            db, environment, ticket.TenantId, ticket.AgentDeviceId, ticket.PrinterDeviceId, ct))
        return Results.Conflict(new
        {
            code = "private_printing_output_policy_changed",
            message = "The shop's private-printing output eligibility changed before the encrypted transfer started. No relay file was accepted. Refresh the shop connection and retry."
        });

    // An accepted relay already has an immutable ticket and grant. A retry must
    // continue polling that same transfer rather than replace a possibly claimed payload.
    if (!string.IsNullOrWhiteSpace(ticket.PrivateRelayStorageKey) && ticket.PrivateRelayUploadedUtc is not null)
        return Results.Ok(new { Accepted = true, DuplicateRequest = true, ticket.PrintJobId, ticket.JobNumber });

    if (!context.Request.HasFormContentType)
        return Results.BadRequest(new { code = "private_relay_form_required" });
    var form = await context.Request.ReadFormAsync(ct);
    if (form.Files.Count != 1)
        return Results.BadRequest(new { code = "private_relay_file_required" });
    var file = form.Files[0];
    if (file.Length != ticket.SizeBytes + UploadLimits.PrivateRelayEncryptionOverheadBytes || file.Length > UploadLimits.MaxFileBytes + UploadLimits.PrivateRelayEncryptionOverheadBytes)
        return Results.UnprocessableEntity(new { code = "private_relay_size_mismatch", message = "The encrypted file size does not match its original upload ticket." });

    string storageKey;
    try
    {
        await using var input = file.OpenReadStream();
        storageKey = await storage.StoreOpaquePrivateRelayAsync(
            ticket.TenantId, ticket.Id, input, file.Length, ct);
    }
    catch (InvalidDataException exception)
    {
        return Results.UnprocessableEntity(new { code = "private_relay_payload_invalid", message = exception.Message });
    }

    try
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockedTickets = await db.LocalPrintUploadTickets.FromSqlInterpolated(
            $"SELECT * FROM \"LocalPrintUploadTickets\" WHERE \"Id\" = {ticketId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
            .ToListAsync(ct);
        var locked = lockedTickets.SingleOrDefault();
        if (locked is null || locked.CompletedUtc is not null || locked.PrivateRelayFailedUtc is not null ||
            locked.PrivateRelayUploadedUtc is not null)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(storageKey);
            if (locked?.CompletedUtc is not null || locked?.PrivateRelayUploadedUtc is not null)
                return Results.Ok(new { Accepted = true, DuplicateRequest = true, locked.PrintJobId, locked.JobNumber });
            return Results.Conflict(new { code = "private_relay_state_changed", message = "This upload has changed state. Retry the same file to refresh its status." });
        }

        locked.PrivateRelayStorageKey = storageKey;
        locked.PrivateRelayWrappedKey = wrappedKey;
        locked.PrivateRelayIv = ivText;
        locked.PrivateRelayUploadedUtc = now;
        locked.ExpiresUtc = now.AddHours(24);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch
    {
        // The commit result can be ambiguous after a connection interruption. Never
        // delete a relay object while a committed ticket still references it.
        try
        {
            var isReferenced = await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(
                item => item.Id == ticketId && item.TenantId == session.TenantId &&
                        item.PrivateRelayStorageKey == storageKey,
                CancellationToken.None);
            if (!isReferenced)
                await storage.DeleteAsync(storageKey);
        }
        catch (Exception cleanupException)
        {
            loggerFactory.CreateLogger("PrivateRelayUpload").LogWarning(
                cleanupException,
                "Could not immediately reconcile private relay storage object {StorageKey}; the orphan sweeper will retry.",
                storageKey);
        }
        throw;
    }

    // The durable ticket is committed before notifying. PostgreSQL broadcasts to
    // every API instance, so the paired Agent wakes even when upload and listener
    // traffic land on different replicas. Notification failure never fails upload.
    await relayWakeups.NotifyAgentAsync(ticket.AgentDeviceId);

    return Results.Ok(new
    {
        Accepted = true,
        DuplicateRequest = false,
        ticket.PrintJobId,
        ticket.JobNumber
    });
})
.WithMetadata(new RequestSizeLimitAttribute(UploadLimits.MaxMultipartRequestBytes))
.RequireRateLimiting("document-upload");

publicApi.MapGet("/sessions/current/local-upload-tickets/{ticketId:guid}/private-relay-status", async (
    Guid ticketId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) || string.IsNullOrWhiteSpace(rawGrant) || rawGrant.Length is < 40 or > 128)
        return Results.Unauthorized();

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == ticketId &&
            item.TenantId == session.TenantId && item.CustomerSessionId == session.Id &&
            item.PrivatePrinting, ct);
    if (ticket is null) return Results.NotFound();
    byte[] suppliedHash;
    byte[] expectedHash;
    try
    {
        suppliedHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
        expectedHash = Convert.FromHexString(ticket.GrantHash);
    }
    catch (FormatException) { return Results.Unauthorized(); }
    if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash))
        return Results.Unauthorized();

    if (ticket.PrivateRelayFailedUtc is not null)
        return Results.Ok(new { State = "FAILED", Message = ticket.PrivateRelayFailureMessage ?? "The shop computer could not validate this file. Please select it again." });
    if (ticket.CompletedUtc is null && ticket.ExpiresUtc <= now)
        return Results.Ok(new { State = "EXPIRED", Message = "The shop computer did not receive this file in time. Please retry the same file." });

    if (ticket.CompletedUtc is not null)
    {
        var result = await (
            from job in db.PrintJobs.AsNoTracking()
            join document in db.PrintDocuments.AsNoTracking()
                on new { job.TenantId, PrintJobId = job.Id } equals new { document.TenantId, document.PrintJobId }
            where job.TenantId == ticket.TenantId && job.Id == ticket.PrintJobId
            select new
            {
                Job = new
                {
                    job.Id, job.JobNumber, job.Status, job.PageCount, job.Copies, job.ColourMode,
                    job.PaperSize, job.IsDuplex, job.Orientation, job.Currency, job.QuotedAmountMinor,
                    job.CreatedUtc, job.BatchGroupName, job.BatchName, job.BatchId, job.PrintKind,
                    job.PhotoSizeCode, job.PhotoCount, job.PhotosPerPage, job.PhotoUnitPriceMinorSnapshot
                },
                Document = new
                {
                    document.Id, OriginalFileName = "Private document", document.MediaType,
                    document.SizeBytes, document.Sha256, document.PageSizesJson
                }
            }).SingleOrDefaultAsync(ct);
        if (result is null)
            return Results.Conflict(new { code = "private_relay_completion_missing", message = "The shop's validation result is being reconciled. Retry the same file." });
        return Results.Ok(new { State = "READY", UploadResult = new { result.Job, result.Document, DuplicateRequest = false } });
    }

    return Results.Ok(new { State = "PENDING", Message = "The shop computer is securely receiving and checking your document." });
}).RequireRateLimiting("public-api");

publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery", async (
    Guid ticketId,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    const int maximumRecoveryAgeHours = LocalUploadRecoveryRules.MaximumRecoveryAgeHours;
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) ||
        string.IsNullOrWhiteSpace(rawGrant) ||
        rawGrant.Length is < 40 or > 128)
        return Results.Unauthorized();

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    // Preflight without holding a database lock while the customer's file is
    // uploaded over HTTPS. A second check under row lock below resolves races.
    var preflightTicket = await db.LocalPrintUploadTickets.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == ticketId &&
                                      item.TenantId == session.TenantId &&
                                      item.CustomerSessionId == session.Id, ct);
    if (preflightTicket is null) return Results.NotFound();
    if (preflightTicket.PrivatePrinting)
    {
        return Results.Conflict(new
        {
            code = "private_printing_cloud_recovery_blocked",
            message = "This is a Private Printing request. Cloud document storage and HTTPS recovery are disabled. Reconnect to the original shop computer and retry the same upload."
        });
    }

    var suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
    var preflightGrantHash = Convert.FromHexString(preflightTicket.GrantHash);
    if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, preflightGrantHash))
        return Results.Unauthorized();

    var preflightJob = await db.PrintJobs.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == preflightTicket.PrintJobId &&
                                      item.TenantId == preflightTicket.TenantId &&
                                      item.CustomerSessionId == preflightTicket.CustomerSessionId, ct);
    var preflightDocument = await db.PrintDocuments.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == preflightTicket.TenantId &&
                                      item.PrintJobId == preflightTicket.PrintJobId, ct);
    if (preflightJob is not null || preflightDocument is not null || preflightTicket.CompletedUtc is not null)
    {
        if (preflightJob is null || preflightDocument is null ||
            !LocalUploadRecoveryRules.MatchesOriginalDocument(
                preflightTicket.SizeBytes, preflightTicket.Sha256,
                preflightDocument.SizeBytes, preflightDocument.Sha256))
            return Results.Conflict(new
            {
                code = "local_upload_recovery_state_conflict",
                message = "The original shop upload is being reconciled. Refresh this print request; do not upload it again."
            });

        return Results.Ok(new
        {
            Job = new
            {
                preflightJob.Id, preflightJob.JobNumber, preflightJob.Status,
                preflightJob.PageCount, preflightJob.Copies, preflightJob.ColourMode,
                preflightJob.PaperSize, preflightJob.IsDuplex, preflightJob.Orientation,
                preflightJob.PricingVersion, preflightJob.Currency,
                preflightJob.QuotedAmountMinor, preflightJob.CreatedUtc,
                preflightJob.BatchGroupName,
                preflightJob.BatchName,
                preflightJob.BatchId
            },
            Document = new
            {
                preflightDocument.Id, preflightDocument.OriginalFileName,
                preflightDocument.MediaType, preflightDocument.SizeBytes,
                preflightDocument.Sha256, preflightDocument.PageSizesJson
            },
            DuplicateRequest = true
        });
    }

    if (!LocalUploadRecoveryRules.IsWithinRecoveryWindow(preflightTicket.CreatedUtc, now,
        TimeSpan.FromHours(maximumRecoveryAgeHours)))
        return Results.Conflict(new
        {
            code = "local_upload_recovery_expired",
            message = "This upload attempt is too old to recover. Start a new print request; the original ticket will not be duplicated."
        });

    if (!context.Request.HasFormContentType)
        return Results.BadRequest(new { code = "multipart_form_required", message = "Select the same document to continue its secure upload." });
    var form = await context.Request.ReadFormAsync(ct);
    if (form.Files.Count != 1)
        return Results.BadRequest(new { code = "one_file_required", message = "Continue with one original document per print request." });
    var file = form.Files[0];
    if (file.Length != preflightTicket.SizeBytes || file.Length is <= 0 or > UploadLimits.MaxFileBytes)
        return Results.UnprocessableEntity(new { code = "local_upload_recovery_size_mismatch", message = "The selected file does not match the original upload attempt." });

    var documentId = Guid.NewGuid();
    StoredDocument stored;
    try
    {
        await using var input = file.OpenReadStream();
        stored = await storage.ValidateAndStoreAsync(
            preflightTicket.TenantId, documentId, preflightTicket.OriginalFileName, input, preflightTicket.SizeBytes, ct);
    }
    catch (DocumentValidationException exception)
    {
        return Results.UnprocessableEntity(new { code = "document_not_supported", message = exception.Message });
    }

    if (!LocalUploadRecoveryRules.MatchesOriginalDocument(
        preflightTicket.SizeBytes, preflightTicket.Sha256,
        stored.SizeBytes, stored.Sha256))
    {
        await storage.DeleteAsync(stored.StorageKey);
        return Results.UnprocessableEntity(new
        {
            code = "local_upload_recovery_hash_mismatch",
            message = "The selected file content differs from the original upload. Choose the exact same document to continue."
        });
    }

    try
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        async Task<IResult> RejectAndCleanAsync(IResult response)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return response;
        }

        var lockedTickets = await db.LocalPrintUploadTickets.FromSqlInterpolated(
            $"SELECT * FROM \"LocalPrintUploadTickets\" WHERE \"Id\" = {ticketId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
            .ToListAsync(ct);
        var ticket = lockedTickets.SingleOrDefault();
        if (ticket is null) return await RejectAndCleanAsync(Results.NotFound());

        var stillActive = await db.CustomerPrintSessions.AsNoTracking().AnyAsync(item =>
            item.Id == session.Id && item.TenantId == session.TenantId &&
            item.TokenHash == tokenHash && item.Status == "Active" &&
            item.RevokedUtc == null, ct);
        if (!stillActive) return await RejectAndCleanAsync(Results.Unauthorized());

        byte[] lockedGrantHash;
        try
        {
            lockedGrantHash = Convert.FromHexString(ticket.GrantHash);
        }
        catch (FormatException)
        {
            return await RejectAndCleanAsync(Results.Unauthorized());
        }
        if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, lockedGrantHash))
            return await RejectAndCleanAsync(Results.Unauthorized());

        var existingJob = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId &&
                                          item.TenantId == ticket.TenantId &&
                                          item.CustomerSessionId == ticket.CustomerSessionId, ct);
        var existingDocument = await db.PrintDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == ticket.TenantId &&
                                          item.PrintJobId == ticket.PrintJobId, ct);
        if (existingJob is not null || existingDocument is not null || ticket.CompletedUtc is not null)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            if (existingJob is null || existingDocument is null ||
                !LocalUploadRecoveryRules.MatchesOriginalDocument(
                    ticket.SizeBytes, ticket.Sha256,
                    existingDocument.SizeBytes, existingDocument.Sha256))
                return Results.Conflict(new
                {
                    code = "local_upload_recovery_state_conflict",
                    message = "The original shop upload is being reconciled. Refresh this print request; do not upload it again."
                });

            return Results.Ok(new
            {
                Job = new
                {
                    existingJob.Id, existingJob.JobNumber, existingJob.Status,
                    existingJob.PageCount, existingJob.Copies, existingJob.ColourMode,
                    existingJob.PaperSize, existingJob.IsDuplex, existingJob.Orientation,
                    existingJob.PricingVersion, existingJob.Currency,
                    existingJob.QuotedAmountMinor, existingJob.CreatedUtc,
                    existingJob.BatchGroupName,
                    existingJob.BatchName,
                    existingJob.BatchId
                },
                Document = new
                {
                    existingDocument.Id, existingDocument.OriginalFileName,
                    existingDocument.MediaType, existingDocument.SizeBytes,
                    existingDocument.Sha256, existingDocument.PageSizesJson
                },
                DuplicateRequest = true
            });
        }

        var commitNow = DateTimeOffset.UtcNow;
        if (!LocalUploadRecoveryRules.IsWithinRecoveryWindow(ticket.CreatedUtc, commitNow,
            TimeSpan.FromHours(maximumRecoveryAgeHours)))
            return await RejectAndCleanAsync(Results.Conflict(new
            {
                code = "local_upload_recovery_expired",
                message = "This upload attempt is too old to recover. Start a new print request; the original ticket will not be duplicated."
            }));

        var subscription = await entitlements.GetForTenantAsync(ticket.TenantId, ct);
        if (!subscription.IsAccessActive)
            return await RejectAndCleanAsync(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "This shop's trial or subscription period is no longer active."));
        var recoveryAgentAvailable = await GetLocalAgentInfoAsync(
            db, ticket.TenantId, ticket.PrinterDeviceId, commitNow, ct) is not null;
        if (!subscription.CloudAssistedPrintingEnabled && recoveryAgentAvailable)
            return await RejectAndCleanAsync(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "Secure HTTPS recovery is not available while the paired shop Agent is online on this plan."));
        if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, ticket.SizeBytes))
            return await RejectAndCleanAsync(Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "This document exceeds the maximum size configured for this shop's plan."));

        await entitlements.AcquireQuotaLockAsync(ticket.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
        var hasExistingMonthlyReservation = LocalUploadRecoveryRules.HasExistingMonthlyQuotaReservation(
            ticket.CreatedUtc, ticket.CompletedUtc, DateTimeOffset.UtcNow,
            TimeSpan.FromHours(maximumRecoveryAgeHours));
        if (!hasExistingMonthlyReservation)
        {
            var quota = await entitlements.CheckCapacityAsync(
                ticket.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
            if (!quota.Allowed)
                return await RejectAndCleanAsync(Results.Conflict(new
                {
                    code = quota.Code, message = quota.Message,
                    current = quota.Current, maximum = quota.Maximum
                }));
        }

        _ = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {ticket.TenantId} AND \"CustomerSessionId\" = {ticket.CustomerSessionId} AND \"BatchId\" = {ticket.BatchId} FOR UPDATE")
            .ToListAsync(ct);
        var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
            item.TenantId == ticket.TenantId &&
            item.CustomerSessionId == ticket.CustomerSessionId &&
            item.BatchId == ticket.BatchId &&
            item.Status != "REJECTED", ct);
        if (batchCheckoutLocked)
            return await RejectAndCleanAsync(Results.Conflict(new
            {
                code = "batch_upload_locked_after_payment_start",
                message = "This batch already has a payment attempt. The original file cannot be added to a second checkout."
            }));

        var batchJobSizes = await (
            from batchJob in db.PrintJobs.AsNoTracking()
            join batchDocument in db.PrintDocuments.AsNoTracking()
                on new { batchJob.TenantId, PrintJobId = batchJob.Id }
                equals new { batchDocument.TenantId, batchDocument.PrintJobId }
            where batchJob.TenantId == ticket.TenantId &&
                  batchJob.CustomerSessionId == ticket.CustomerSessionId &&
                  batchJob.BatchId == ticket.BatchId
            select batchDocument.SizeBytes).ToListAsync(ct);
        var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
            .Where(item => item.TenantId == ticket.TenantId &&
                           item.CustomerSessionId == ticket.CustomerSessionId &&
                           item.BatchId == ticket.BatchId &&
                           item.Id != ticket.Id &&
                           item.CompletedUtc == null &&
                           item.CreatedUtc >= commitNow.AddHours(-maximumRecoveryAgeHours))
            .Select(item => item.SizeBytes).ToListAsync(ct);
        if (!PrintBatchCapacityRules.CanAdd(
            batchJobSizes.Count + pendingBatchTicketSizes.Count,
            checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
            ticket.SizeBytes))
            return await RejectAndCleanAsync(Results.Conflict(new
            {
                code = "batch_capacity_exceeded",
                message = "This original file no longer fits within the batch limit. Contact the shop before creating another print request."
            }));

        var job = new PrintJob
        {
            Id = ticket.PrintJobId,
            TenantId = ticket.TenantId,
            QrStationId = ticket.QrStationId,
            PrinterDeviceId = ticket.PrinterDeviceId,
            CustomerSessionId = ticket.CustomerSessionId,
            BatchId = ticket.BatchId,
            BatchGroupName = ticket.BatchGroupName,
            BatchName = ticket.BatchName,
            JobNumber = ticket.JobNumber,
            Status = "AWAITING_CONFIGURATION",
            IdempotencyKey = $"LOCAL-{ticket.Id:N}",
            PageCount = stored.Analysis.PageCount,
            Copies = ticket.Copies,
            ColourMode = ticket.ColourMode,
            PaperSize = ticket.PaperSize,
            IsDuplex = ticket.IsDuplex,
            Orientation = ticket.Orientation,
            PrintKind = ticket.PrintKind,
            PhotoSizeCode = ticket.PhotoSizeCode,
            PhotoCount = ticket.PhotoCount ?? 0,
            PhotosPerPage = ticket.PhotosPerPage,
            Currency = "INR",
            QuotedAmountMinor = 0,
            CreatedUtc = commitNow,
            UpdatedUtc = commitNow
        };
        var document = new PrintDocument
        {
            Id = documentId,
            TenantId = ticket.TenantId,
            PrintJobId = ticket.PrintJobId,
            OriginalFileName = ticket.OriginalFileName,
            MediaType = stored.Analysis.MediaType,
            FileExtension = stored.Analysis.Extension,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            StorageKey = stored.StorageKey,
            StorageMode = "CLOUD",
            PageSizesJson = stored.Analysis.PageSizesJson,
            CreatedUtc = commitNow
        };

        ticket.CompletedUtc = commitNow;
        db.PrintJobs.Add(job);
        db.PrintDocuments.Add(document);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = null,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = "LOCAL_UPLOAD_RECOVERED_TO_HTTPS",
            CreatedUtc = commitNow
        });
        await db.SaveChangesAsync(ct);
        // Once CommitAsync is attempted, its outcome can be ambiguous. Preserve
        // the encrypted file on that path; the next retry reconciles the same ticket.
        await transaction.CommitAsync(ct);

        return Results.Ok(new
        {
            Job = new
            {
                job.Id, job.JobNumber, job.Status, job.PageCount, job.Copies,
                job.ColourMode, job.PaperSize, job.IsDuplex, job.Orientation, job.PricingVersion,
                job.Currency, job.QuotedAmountMinor, job.CreatedUtc, job.BatchGroupName, job.BatchName, job.BatchId
            },
            Document = new
            {
                document.Id, document.OriginalFileName, document.MediaType,
                document.SizeBytes, document.Sha256, document.PageSizesJson
            },
            DuplicateRequest = false
        });
    }
    catch
    {
        // A database commit can fail after the server commits but before the
        // response reaches this process. Only delete the encrypted blob after
        // a fresh DB read proves no committed document references it.
        try
        {
            var documentCommitted = await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
                item.Id == documentId &&
                item.TenantId == preflightTicket.TenantId &&
                item.StorageKey == stored.StorageKey, CancellationToken.None);
            if (!documentCommitted)
                await storage.DeleteAsync(stored.StorageKey);
        }
        catch
        {
            // Preserve the encrypted file whenever commit state cannot be proven.
        }
        throw;
    }
})
.WithMetadata(new RequestSizeLimitAttribute(UploadLimits.MaxMultipartRequestBytes))
.RequireRateLimiting("document-upload");

publicApi.MapPost("/jobs/{jobId:guid}/quote", async (
    Guid jobId,
    PrintQuoteRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
    if (!subscription.IsAccessActive)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "This shop's trial or subscription period is no longer active.");

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    var savedQuoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
    var canRefreshExpiredQuote = savedQuoteExpired &&
        (job.Status is "AWAITING_CUSTOMER_APPROVAL" or "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED");
    if (!PrintJobQuoteRules.CanCreateQuote(job.Status) && !canRefreshExpiredQuote)
    {
        return Results.Conflict(new
        {
            code = "job_not_ready_for_quote",
            message = "A new quote can only be created for a newly uploaded job or a genuinely expired quote with no active payment."
        });
    }

    var isPhotoPrint = request.PrintKind == "PHOTO";
    var isIdDocument = request.PrintKind == "ID_DOCUMENT";
    if ((request.PrintKind is not null && request.PrintKind is not ("DOCUMENT" or "PHOTO" or "ID_DOCUMENT")) ||
        (isIdDocument && (!job.PrivatePrinting ||
            job.PrintKind != "ID_DOCUMENT" ||
            job.PageCount != 1 ||
            request.Copies != 1 ||
            request.ColourMode != "Colour" ||
            request.PaperSize != "A4" ||
            request.IsDuplex ||
            request.Orientation != "PORTRAIT")) ||
        (isPhotoPrint && (
            job.PrintKind == "ID_DOCUMENT" ||
            (job.PrintKind == "PHOTO" &&
                (request.PhotoSizeCode != job.PhotoSizeCode ||
                 request.PhotoCount != job.PhotoCount ||
                 request.PhotosPerPage != job.PhotosPerPage)))) ||
        (job.PrintKind == "ID_DOCUMENT" && !isIdDocument) ||
        (job.PrintKind == "PHOTO" && !isPhotoPrint) ||
        job.PageCount is <= 0 or > 500 ||
        request.Copies is < 1 or > 100 ||
        request.PaperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
        request.ColourMode is not ("Mono" or "Colour") ||
        request.Orientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE") ||
        (isPhotoPrint &&
            (request.Copies != 1 || request.ColourMode != "Colour" ||
             request.IsDuplex || request.PhotoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6") ||
             request.PhotoCount is null || request.PhotoCount is < 1 or > UploadLimits.MaxPhotoCount ||
             request.PhotosPerPage is < 1)) ||
        (isPhotoPrint && (request.PaperSize != "A4" || request.Orientation != "AUTO")) ||
        (!isPhotoPrint && (request.PhotoSizeCode is not null || request.PhotoCount is not null || request.PhotosPerPage is not null)))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printOptions"] = ["Document printing supports A4, A3, A5, Letter or Legal paper, automatic/portrait/landscape orientation, 1–100 copies, Mono or Colour and single/double-sided. Photo sheets use one A4 page and a supported photo size/count."]
        });
    }

    if (isIdDocument)
    {
        var idDocument = await db.PrintDocuments.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId && item.PrintJobId == job.Id)
            .Select(item => new { item.MediaType, item.PageSizesJson })
            .SingleOrDefaultAsync(ct);
        if (idDocument is null ||
            !string.Equals(idDocument.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            !IdCardPdfRules.HasExactlyA4PortraitPage(idDocument.PageSizesJson))
        {
            await transaction.RollbackAsync(ct);
            return Results.UnprocessableEntity(new
            {
                code = "id_document_pdf_invalid",
                message = "The ID card sheet must be a valid, single-page A4 portrait PDF. Recheck the prepared sheet before requesting a quote."
            });
        }
    }

    var paymentPreferences = await db.ShopPaymentProfiles.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId)
        .Select(item => new { item.InstantPaymentEnabled })
        .SingleOrDefaultAsync(ct);
    var pricing = await db.ShopPricingProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == session.TenantId && item.IsConfigured, ct);
    if (pricing is null)
    {
        return Results.Conflict(new
        {
            code = "shop_pricing_not_configured",
            message = "This shop has not configured its print prices yet. Please ask the shop operator."
        });
    }

    try
    {
        long amount;
        long photoUnitPriceMinor = 0;
        long[] pageRefundAllocations;
        if (isPhotoPrint)
        {
            var photoSizeCode = request.PhotoSizeCode!;
            var photoCount = request.PhotoCount!.Value;
            var photosPerPage = request.PhotosPerPage!.Value;
            var expectedPageCount = PricingCalculator.PhotoPageCount(photoCount, photoSizeCode, photosPerPage);
            if (job.PageCount != expectedPageCount)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pageCount"] = [$"The prepared PDF must contain exactly {expectedPageCount} A4 page(s) for {photoCount} photos of the selected size."]
                });
            }

            var photoDocument = await db.PrintDocuments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == job.TenantId && item.PrintJobId == job.Id, ct);
            if (photoDocument is null ||
                !PhotoSheetPdfRules.HasExactlyA4PortraitPages(photoDocument.PageSizesJson, expectedPageCount))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["photoSheet"] = ["The saved Photo Studio document must contain the selected number of portrait A4 pages with a full-page photo raster."]
                });
            }

            photoUnitPriceMinor = PricingCalculator.PhotoUnitRate(photoSizeCode, pricing);
            amount = PricingCalculator.CalculatePhoto(photoCount, photoSizeCode, pricing);
            pageRefundAllocations = PricingCalculator.CalculatePhotoPageRefundAllocations(
                photoCount, photoSizeCode, photosPerPage, photoUnitPriceMinor);
            job.PrintKind = "PHOTO";
            job.PhotoSizeCode = photoSizeCode;
            job.PhotoCount = photoCount;
            job.PhotoUnitPriceMinorSnapshot = photoUnitPriceMinor;
        }
        else
        {
            amount = PricingCalculator.Calculate(
                job.PageCount,
                request.Copies,
                request.ColourMode,
                request.IsDuplex,
                pricing);
            pageRefundAllocations = PricingCalculator.CalculatePageRefundAllocations(
                job.PageCount, request.Copies, request.ColourMode, request.IsDuplex, pricing);
            job.PrintKind = isIdDocument ? "ID_DOCUMENT" : "DOCUMENT";
            job.PhotoSizeCode = null;
            job.PhotoCount = 0;
            job.PhotosPerPage = null;
            job.PhotoUnitPriceMinorSnapshot = 0;
        }

        job.ColourMode = request.ColourMode;
        job.PaperSize = request.PaperSize;
        job.IsDuplex = request.IsDuplex;
        job.Copies = request.Copies;
        job.Orientation = request.Orientation;
        job.Currency = "INR";
        job.QuotedAmountMinor = amount;
        job.PageRefundAllocationsJson = JsonSerializer.Serialize(pageRefundAllocations);
        job.PricingVersion = pricing.Version;
        job.QuoteExpiresUtc = now.AddMinutes(10);
        var previousStatus = job.Status;
        job.Status = paymentPreferences?.InstantPaymentEnabled == true
            ? "AWAITING_PAYMENT"
            : "AWAITING_CUSTOMER_APPROVAL";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousStatus,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = paymentPreferences?.InstantPaymentEnabled == true
                ? "INSTANT_CHECKOUT_QUOTE_CREATED"
                : "QUOTE_CREATED",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new
        {
            job.Id,
            job.JobNumber,
            job.Status,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.Orientation,
            job.PrintKind,
            job.PhotoSizeCode,
            job.PhotoCount,
        job.PhotosPerPage,
            PhotoUnitPriceMinor = photoUnitPriceMinor,
            job.Currency,
            job.QuotedAmountMinor,
            AmountMajor = amount / 100m,
            job.PricingVersion,
            job.QuoteExpiresUtc
        });
    }
    catch (DuplexPricingNotConfiguredException exception)
    {
        return Results.Conflict(new { code = "duplex_pricing_not_configured", message = exception.Message });
    }
    catch (OverflowException)
    {
        return Results.UnprocessableEntity(new { code = "quote_overflow", message = "The requested print configuration exceeds the pricing limit." });
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["printOptions"] = [exception.Message] });
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["printOptions"] = [exception.Message] });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { code = "pricing_not_available", message = exception.Message });
    }
});



publicApi.MapPost("/batches/{batchId:guid}/payments", async (
    Guid batchId,
    StartPrintPaymentRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken)) return Results.Unauthorized();
    if (request.Method is not ("UPI" or "CASH"))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["method"] = ["Choose UPI or cash at counter."]
        });

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await entitlements.AcquireQuotaLockAsync(
        session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    var jobs = (await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
        .ToListAsync(ct))
        .OrderBy(item => item.CreatedUtc)
        .ToList();
    if (jobs.Count == 0) return Results.NotFound();
    if (jobs.Count > PrintBatchCapacityRules.MaximumFiles)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_file_limit_exceeded",
            message = $"This batch contains more than {PrintBatchCapacityRules.MaximumFiles} files and cannot be checked out safely. Contact the shop before attempting another payment."
        });
    }

    var pendingLocalUpload = await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(ticket =>
        ticket.TenantId == session.TenantId &&
        ticket.CustomerSessionId == session.Id &&
        ticket.BatchId == batchId &&
        ticket.CompletedUtc == null &&
        ticket.ExpiresUtc > now, ct);
    if (pendingLocalUpload)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_uploads_still_processing",
            message = "A document is still being transferred to the shop computer. Wait for its upload to finish or expire, then refresh the batch before checkout."
        });
    }

    var activeBatchPayment = await db.PrintBatchPayments.SingleOrDefaultAsync(
        item => item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id &&
                item.BatchId == batchId &&
                (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 item.Status == "CUSTOMER_REPORTED" ||
                 item.Status == "CASH_AWAITING_PAYMENT"),
        ct);
    if (activeBatchPayment is not null)
    {
        if (activeBatchPayment.Method != request.Method)
            return Results.Conflict(new
            {
                code = "batch_payment_attempt_already_active",
                message = "A payment attempt is already active for this batch. Complete or ask the shop to reject it before selecting another method."
            });
        return Results.Ok(BuildBatchPaymentResponse(
            activeBatchPayment,
            jobs.Count(item => item.Status is not ("REJECTED" or "REFUNDED"))));
    }

    var settledOrApprovedStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "REJECTED", "REFUNDED", "AWAITING_PAYMENT", "PAYMENT_REJECTED",
        "AWAITING_CUSTOMER_PAYMENT", "PAYMENT_REPORTED", "AWAITING_COUNTER_PAYMENT",
        "PAID_AUTHORISED", "LOCAL_QUEUE_CLAIMED", "LOCAL_QUEUE_READY",
        "PRINT_AUTHORISED", "PRINT_SUBMITTING", "SPOOLER_ACCEPTED", "PRINTING",
        "OUTPUT_UNCONFIRMED", "COMPLETED"
    };
    var pendingReview = jobs.FirstOrDefault(job => !settledOrApprovedStates.Contains(job.Status));
    if (pendingReview is not null)
        return Results.Conflict(new
        {
            code = "batch_review_not_complete",
            message = "The shop must finish reviewing every file in this batch before checkout can start."
        });

    var payableJobs = jobs
        .Where(job => job.Status is "AWAITING_PAYMENT" or "PAYMENT_REJECTED")
        .ToList();
    if (payableJobs.Count == 0)
        return Results.Conflict(new
        {
            code = "batch_has_no_payable_files",
            message = "There are no accepted files awaiting payment in this batch."
        });

    var expiredQuotes = payableJobs
        .Where(job => job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
        .ToArray();
    if (expiredQuotes.Length > 0)
    {
        foreach (var expiredJob in expiredQuotes)
        {
            var previousStatus = expiredJob.Status;
            expiredJob.Status = "QUOTE_EXPIRED";
            expiredJob.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = expiredJob.TenantId,
                PrintJobId = expiredJob.Id,
                PreviousStatus = previousStatus,
                NewStatus = expiredJob.Status,
                ActorType = "System",
                ReasonCode = "QUOTE_EXPIRED_BEFORE_BATCH_CHECKOUT",
                CreatedUtc = now
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_quote_expired",
            jobIds = expiredQuotes.Select(job => job.Id).ToArray(),
            message = "At least one accepted file has an expired quote. Refresh the affected quote(s) before starting batch checkout. No payment was started."
        });
    }

    if (payableJobs.Any(job => job.QuotedAmountMinor <= 0 || job.Currency != "INR"))
        return Results.Conflict(new
        {
            code = "batch_quote_invalid",
            message = "Every accepted file must have a valid INR quote before batch checkout."
        });

    var payableJobIds = payableJobs.Select(job => job.Id).ToArray();
    var batchJobIds = jobs.Select(job => job.Id).ToArray();
    var unresolvedIndividualPayment = await db.PrintPayments.AsNoTracking().AnyAsync(
        item => item.TenantId == session.TenantId &&
                item.BatchPaymentId == null &&
                batchJobIds.Contains(item.PrintJobId) &&
                item.Status != "REJECTED" &&
                item.Status != "REFUNDED",
        ct);
    if (unresolvedIndividualPayment)
        return Results.Conflict(new
        {
            code = "batch_contains_individual_payment",
            message = "At least one file in this batch already has an individual payment record. Finish or resolve that payment before starting consolidated checkout; SmartPrint will not create a second collection path for the same batch."
        });

    var settings = await db.ShopPaymentProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == session.TenantId, ct);
    if (settings is null ||
        (request.Method == "UPI" && (!settings.UpiEnabled ||
                                     string.IsNullOrWhiteSpace(settings.UpiId) ||
                                     string.IsNullOrWhiteSpace(settings.PayeeName))) ||
        (request.Method == "CASH" && !settings.CashEnabled))
        return Results.Conflict(new
        {
            code = "payment_method_unavailable",
            message = "The shop has not enabled the selected payment method."
        });

    long totalAmountMinor;
    try
    {
        totalAmountMinor = payableJobs.Aggregate(0L, (total, job) => checked(total + job.QuotedAmountMinor));
    }
    catch (OverflowException)
    {
        return Results.UnprocessableEntity(new
        {
            code = "batch_payment_overflow",
            message = "The accepted batch total exceeds the supported payment amount."
        });
    }

    var batchPayment = new PrintBatchPayment
    {
        TenantId = session.TenantId,
        CustomerSessionId = session.Id,
        BatchId = batchId,
        Method = request.Method,
        Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "CASH_AWAITING_PAYMENT",
        AmountMinor = totalAmountMinor,
        Currency = "INR",
        UpiIdSnapshot = request.Method == "UPI" ? settings.UpiId : null,
        PayeeNameSnapshot = request.Method == "UPI" ? settings.PayeeName : null,
        CreatedUtc = now,
        UpdatedUtc = now
    };

    db.PrintBatchPayments.Add(batchPayment);
    foreach (var job in payableJobs)
    {
        var payment = new PrintPayment
        {
            TenantId = session.TenantId,
            PrintJobId = job.Id,
            BatchPaymentId = batchPayment.Id,
            Method = request.Method,
            Status = batchPayment.Status,
            AmountMinor = job.QuotedAmountMinor,
            Currency = job.Currency,
            UpiIdSnapshot = batchPayment.UpiIdSnapshot,
            PayeeNameSnapshot = batchPayment.PayeeNameSnapshot,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var previousStatus = job.Status;
        job.Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT";
        job.UpdatedUtc = now;
        db.PrintPayments.Add(payment);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousStatus,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = request.Method == "UPI"
                ? "BATCH_UPI_PAYMENT_INTENT_CREATED"
                : "BATCH_CASH_AT_COUNTER_SELECTED",
            CreatedUtc = now
        });
    }

    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch (DbUpdateException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_attempt_already_active",
            message = "A batch payment was started at the same time. Refresh the batch status and continue with the active attempt."
        });
    }

    return Results.Ok(BuildBatchPaymentResponse(batchPayment, payableJobs.Count));
});


publicApi.MapPost("/jobs/{jobId:guid}/payments", async (
    Guid jobId,
    StartPrintPaymentRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    if (request.Method is not ("UPI" or "CASH"))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["method"] = ["Choose UPI or cash at counter."]
        });
    }

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (job.Status is not ("AWAITING_PAYMENT" or "PAYMENT_REJECTED"))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "job_not_ready_for_payment",
            message = "The shop must approve the quote before payment can be started."
        });
    }

    var settings = await db.ShopPaymentProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == session.TenantId, ct);
    if (settings is null ||
        (request.Method == "UPI" && !settings.UpiEnabled) ||
        (request.Method == "CASH" && !settings.CashEnabled))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "payment_method_unavailable",
            message = "The shop has not enabled the selected payment method."
        });
    }

    var activePayment = await db.PrintPayments.SingleOrDefaultAsync(
        item => item.TenantId == session.TenantId &&
                item.PrintJobId == job.Id &&
                item.BatchPaymentId == null &&
                (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 item.Status == "CUSTOMER_REPORTED" ||
                 item.Status == "CASH_AWAITING_PAYMENT"),
        ct);
    if (job.BatchId is not null && activePayment is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_checkout_required",
            message = "This file belongs to a batch. Start payment from the batch checkout so the customer pays once for all accepted files."
        });
    }

    if (activePayment is not null)
    {
        await transaction.RollbackAsync(ct);
        if (activePayment.Method != request.Method)
        {
            return Results.Conflict(new
            {
                code = "payment_attempt_already_active",
                message = "A payment attempt is already active. Ask the shop to reject it before selecting another method."
            });
        }

        return Results.Ok(BuildPaymentResponse(activePayment, job));
    }

    var payment = new PrintPayment
    {
        TenantId = session.TenantId,
        PrintJobId = job.Id,
        Method = request.Method,
        Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "CASH_AWAITING_PAYMENT",
        AmountMinor = job.QuotedAmountMinor,
        Currency = job.Currency,
        UpiIdSnapshot = request.Method == "UPI" ? settings.UpiId : null,
        PayeeNameSnapshot = request.Method == "UPI" ? settings.PayeeName : null,
        CreatedUtc = now,
        UpdatedUtc = now
    };
    var previousStatus = job.Status;
    job.Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT";
    job.UpdatedUtc = now;
    db.PrintPayments.Add(payment);
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "Customer",
        ReasonCode = request.Method == "UPI" ? "UPI_PAYMENT_INTENT_CREATED" : "CASH_AT_COUNTER_SELECTED",
        CreatedUtc = now
    });

    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch (DbUpdateException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "payment_attempt_already_active",
            message = "A payment attempt was started at the same time. Refresh the job status and continue with the active attempt."
        });
    }

    return Results.Ok(BuildPaymentResponse(payment, job));
});

publicApi.MapPost("/batch-payments/{paymentId:guid}/report", async (
    Guid paymentId,
    ReportPrintPaymentRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var reference = request.ReferenceNumber?.Trim();
    if (string.IsNullOrWhiteSpace(reference) ||
        reference.Length is < 4 or > 100 ||
        reference.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["referenceNumber"] = ["Enter the UPI transaction reference/UTR (4–100 letters or digits, with optional hyphens/underscores)."]
        });

    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken)) return Results.Unauthorized();
    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedPayments = await db.PrintBatchPayments.FromSqlInterpolated(
        $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
        .ToListAsync(ct);
    var batchPayment = lockedPayments.SingleOrDefault();
    if (batchPayment is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (batchPayment.Method != "UPI")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_not_awaiting_reference",
            message = "Only an active UPI batch payment can receive a transaction reference."
        });
    }
    if (batchPayment.Status == "CUSTOMER_REPORTED" &&
        string.Equals(batchPayment.PaymentReference, reference, StringComparison.Ordinal))
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new
        {
            batchPayment.Id,
            batchPayment.Status,
            batchPayment.AmountMinor,
            batchPayment.Currency,
            batchPayment.PaymentReference,
            DuplicateRequest = true,
            Message = "This reference was already submitted. The shop still needs to verify the transaction."
        });
    }
    if (batchPayment.Status != "AWAITING_CUSTOMER_PAYMENT")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_not_awaiting_reference",
            message = "This batch payment is no longer accepting a transaction reference."
        });
    }

    var filePayments = await db.PrintPayments
        .Where(item => item.TenantId == session.TenantId && item.BatchPaymentId == batchPayment.Id)
        .ToListAsync(ct);
    var jobIds = filePayments.Select(item => item.PrintJobId).ToArray();
    var jobs = await db.PrintJobs
        .Where(item => item.TenantId == session.TenantId &&
                       item.CustomerSessionId == session.Id &&
                       jobIds.Contains(item.Id))
        .ToListAsync(ct);
    if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, filePayments, jobs))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_allocations_mismatch",
            message = "The batch total and per-file payment allocations do not match. The reference was not applied; contact the shop before paying again."
        });
    }

    batchPayment.PaymentReference = reference;
    batchPayment.Status = "CUSTOMER_REPORTED";
    batchPayment.UpdatedUtc = now;
    var jobsById = jobs.ToDictionary(item => item.Id);
    foreach (var filePayment in filePayments)
    {
        filePayment.PaymentReference = reference;
        filePayment.Status = "CUSTOMER_REPORTED";
        filePayment.UpdatedUtc = now;
        var job = jobsById[filePayment.PrintJobId];
        if (job.Status is not ("REJECTED" or "REFUNDED"))
        {
            var previousStatus = job.Status;
            job.Status = "PAYMENT_REPORTED";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = "CUSTOMER_SUBMITTED_BATCH_UPI_REFERENCE",
                CreatedUtc = now
            });
        }
    }

    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_changed_concurrently",
            message = "This batch payment changed while the reference was being submitted. Refresh the batch status before trying again."
        });
    }

    return Results.Ok(new
    {
        batchPayment.Id,
        batchPayment.Status,
        batchPayment.AmountMinor,
        batchPayment.Currency,
        batchPayment.PaymentReference,
        FileCount = filePayments.Count,
        Message = "The reference was submitted. The shop must verify the single batch payment in its bank/UPI app before any file is authorised."
    });
});


publicApi.MapPost("/payments/{paymentId:guid}/report", async (
    Guid paymentId,
    ReportPrintPaymentRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var reference = request.ReferenceNumber?.Trim();
    if (string.IsNullOrWhiteSpace(reference) ||
        reference.Length is < 4 or > 100 ||
        reference.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["referenceNumber"] = ["Enter the UPI transaction reference/UTR (4–100 letters or digits, with optional hyphens/underscores)."]
        });
    }

    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var payment = await db.PrintPayments.SingleOrDefaultAsync(
        item => item.Id == paymentId && item.TenantId == session.TenantId && item.BatchPaymentId == null, ct);
    if (payment is null)
    {
        var isBatchAllocation = await db.PrintPayments.AsNoTracking().AnyAsync(
            item => item.Id == paymentId && item.TenantId == session.TenantId && item.BatchPaymentId != null, ct);
        if (isBatchAllocation)
            return Results.Conflict(new
            {
                code = "batch_payment_parent_required",
                message = "This file is part of a batch payment. Submit the UPI reference once from the batch checkout."
            });
        return Results.NotFound();
    }

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == payment.PrintJobId && item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id, ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (payment.Method != "UPI")
    {
        return Results.Conflict(new
        {
            code = "payment_not_awaiting_reference",
            message = "Only an active UPI payment request can receive a transaction reference."
        });
    }

    if (payment.Status == "CUSTOMER_REPORTED" &&
        string.Equals(payment.PaymentReference, reference, StringComparison.Ordinal))
    {
        return Results.Ok(new
        {
            payment.Id,
            payment.Method,
            payment.Status,
            payment.AmountMinor,
            payment.Currency,
            payment.PaymentReference,
            JobId = job.Id,
            JobNumber = job.JobNumber,
            JobStatus = job.Status,
            DuplicateRequest = true,
            Message = "This reference was already submitted. The shop still needs to verify the transaction."
        });
    }

    if (payment.Status != "AWAITING_CUSTOMER_PAYMENT")
    {
        return Results.Conflict(new
        {
            code = "payment_not_awaiting_reference",
            message = "This UPI payment is no longer accepting a transaction reference."
        });
    }

    payment.PaymentReference = reference;
    payment.Status = "CUSTOMER_REPORTED";
    payment.UpdatedUtc = now;
    var previousStatus = job.Status;
    job.Status = "PAYMENT_REPORTED";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "Customer",
        ReasonCode = "CUSTOMER_SUBMITTED_UPI_REFERENCE",
        CreatedUtc = now
    });
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Conflict(new
        {
            code = "payment_changed_concurrently",
            message = "This payment changed while the reference was being submitted. Refresh the job status before trying again."
        });
    }

    return Results.Ok(new
    {
        payment.Id,
        payment.Method,
        payment.Status,
        payment.AmountMinor,
        payment.Currency,
        payment.PaymentReference,
        JobId = job.Id,
        JobNumber = job.JobNumber,
        JobStatus = job.Status,
        Message = "The reference was submitted. The shop must verify the payment in its bank/UPI app before printing is authorised."
    });
});

publicApi.MapPost("/jobs/{jobId:guid}/accept-quote", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var tokenHash = HashOpaqueToken(rawToken);
    var now = DateTimeOffset.UtcNow;
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (job.Status == "AWAITING_SHOP_APPROVAL")
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc, DuplicateRequest = true });
    }

    if (job.Status != "AWAITING_CUSTOMER_APPROVAL")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "quote_not_awaiting_acceptance", message = "This job does not have a quote awaiting your acceptance." });
    }

    if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
    {
        var expiredFromStatus = job.Status;
        job.Status = "QUOTE_EXPIRED";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = expiredFromStatus,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = "QUOTE_EXPIRED_BEFORE_ACCEPTANCE",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Conflict(new { code = "quote_expired", message = "This quote has expired. Request a new quote to continue." });
    }

    var previousStatus = job.Status;
    job.Status = "AWAITING_SHOP_APPROVAL";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "Customer",
        ReasonCode = "CUSTOMER_ACCEPTED_QUOTE",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.Currency, job.QuotedAmountMinor, job.UpdatedUtc });
});

publicApi.MapPost("/sessions/current/renew", async (
    HttpContext context,
    SmartPrintDbContext db,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.SingleOrDefaultAsync(
        item => item.TokenHash == tokenHash &&
                item.Status == "Active" &&
                item.RevokedUtc == null &&
                item.ExpiresUtc > now,
        ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    // A customer who signs in after opening the QR flow can still attach this
    // active session to their account. Never move a session between accounts.
    var customerId = CustomerIdentity.GetCustomerId(context.User);
    if (customerId is Guid signedInCustomerId)
    {
        if (session.CustomerUserId is Guid existingCustomerId &&
            existingCustomerId != signedInCustomerId)
        {
            return Results.Forbid();
        }

        if (session.CustomerUserId is null)
        {
            session.CustomerUserId = signedInCustomerId;
        }
    }

    // Renew only when the existing expiry is approaching; repeated foreground
    // refreshes do not cause a database write on every customer status poll.
    var shouldExtend = session.ExpiresUtc <= now.AddMinutes(10);
    if (shouldExtend)
    {
        session.ExpiresUtc = now.AddMinutes(15);
    }

    // Persist account association even when the session did not need its expiry
    // extended. Anonymous guest sessions still produce no database write here.
    if (customerId is not null || shouldExtend)
    {
        await db.SaveChangesAsync(ct);
    }

    var stationPrinterId = await db.QrStations.AsNoTracking()
        .Where(station => station.Id == session.QrStationId &&
                          station.TenantId == session.TenantId &&
                          station.IsEnabled)
        .Select(station => station.PrinterDeviceId)
        .SingleOrDefaultAsync(ct);
    var localAgent = await GetLocalAgentInfoAsync(db, session.TenantId, stationPrinterId, now, ct);
    var paymentOptions = await db.ShopPaymentProfiles.AsNoTracking()
        .Where(profile => profile.TenantId == session.TenantId)
        .Select(profile => new
        {
            profile.UpiEnabled,
            profile.UpiId,
            profile.PayeeName,
            profile.CashEnabled,
            profile.InstantPaymentEnabled
        })
        .SingleAsync(ct);

    var privatePrintingVirtualOutputEnabled = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct);
    return Results.Ok(new
    {
        session.ExpiresUtc,
        LocalAgent = localAgent,
        PaymentOptions = paymentOptions,
        PrivatePrintingVirtualOutputEnabled = privatePrintingVirtualOutputEnabled
    });
}).RequireRateLimiting("qr-session");

publicApi.MapGet("/jobs/{jobId:guid}/status", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var tokenHash = HashOpaqueToken(rawToken);
    var now = DateTimeOffset.UtcNow;
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var job = await db.PrintJobs.AsNoTracking()
        .Where(item => item.Id == jobId && item.TenantId == session.TenantId &&
                       item.CustomerSessionId == session.Id)
        .Select(item => new
        {
            item.Id,
            item.PrivatePrinting,
            item.JobNumber,
            item.Status,
            item.PageCount,
            item.Copies,
            item.ColourMode,
            item.PaperSize,
            item.IsDuplex,
            item.Orientation,
            item.PricingVersion,
            item.PrintKind,
            item.PhotoSizeCode,
            item.PhotoCount,
            item.PhotosPerPage,
            item.PhotoUnitPriceMinorSnapshot,
            item.Currency,
            item.QuotedAmountMinor,
            item.QuoteExpiresUtc,
            item.RejectedPageNumbersJson,
            item.PdfBrandingEnabled,
            item.OutputType,
            item.OutputCompletedUtc,
            item.PrivateDocumentPurgedUtc,
            item.UpdatedUtc
        })
        .SingleOrDefaultAsync(ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    var timeline = await db.PrintJobEvents.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
        .OrderBy(item => item.CreatedUtc)
        .Select(item => new { item.NewStatus, item.ActorType, item.CreatedUtc })
        .ToListAsync(ct);
    var payment = await db.PrintPayments.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
        .OrderByDescending(item => item.CreatedUtc)
        .Select(item => new
        {
            item.Id,
            item.BatchPaymentId,
            item.Method,
            item.Status,
            item.AmountMinor,
            item.RefundedAmountMinor,
            item.Currency,
            item.UpiIdSnapshot,
            item.PayeeNameSnapshot,
            item.PaymentReference,
            item.RejectionReason,
            item.CreatedUtc,
            item.UpdatedUtc,
            item.VerifiedUtc
        })
        .FirstOrDefaultAsync(ct);
    PrintBatchPayment? batchPayment = null;
    if (payment?.BatchPaymentId is Guid batchPaymentId)
    {
        batchPayment = await db.PrintBatchPayments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == batchPaymentId && item.TenantId == session.TenantId, ct);
    }
    var batchFileCount = batchPayment is null
        ? 0
        : await db.PrintPayments.AsNoTracking()
            .CountAsync(item => item.TenantId == session.TenantId &&
                                item.BatchPaymentId == batchPayment.Id, ct);
    var paymentStatus = payment is null ? null : new
    {
        payment.Id,
        payment.BatchPaymentId,
        payment.Method,
        payment.Status,
        payment.AmountMinor,
        payment.RefundedAmountMinor,
        payment.Currency,
        payment.UpiIdSnapshot,
        payment.PayeeNameSnapshot,
        payment.PaymentReference,
        payment.RejectionReason,
        payment.CreatedUtc,
        payment.UpdatedUtc,
        payment.VerifiedUtc,
        UpiUri = batchPayment is null
            ? BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
                payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency, job.JobNumber)
            : null,
        BatchPayment = batchPayment is null ? null : new
        {
            batchPayment.Id,
            batchPayment.Method,
            batchPayment.Status,
            batchPayment.AmountMinor,
            batchPayment.Currency,
            batchPayment.BatchId,
            FileCount = batchFileCount,
            batchPayment.UpiIdSnapshot,
            batchPayment.PayeeNameSnapshot,
            batchPayment.PaymentReference,
            batchPayment.RejectionReason,
            batchPayment.CreatedUtc,
            batchPayment.UpdatedUtc,
            batchPayment.VerifiedUtc,
            UpiUri = BuildUpiUri(batchPayment.Id, batchPayment.Method, batchPayment.UpiIdSnapshot,
                batchPayment.PayeeNameSnapshot, batchPayment.AmountMinor, batchPayment.Currency,
                "Batch " + batchPayment.BatchId.ToString("N")[..8].ToUpperInvariant())
        }
    };
    return Results.Ok(new { Job = job, Timeline = timeline, Payment = paymentStatus });
});


publicApi.MapPost("/sessions/current/resume", async (
    ResumeCustomerSessionRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) ||
        string.IsNullOrWhiteSpace(request.PublicCode) ||
        request.PublicCode.Length > 160)
    {
        return Results.Unauthorized();
    }

    var tokenHash = HashOpaqueToken(rawToken);
    var codeHash = HashOpaqueToken(request.PublicCode);
    var now = DateTimeOffset.UtcNow;
    var station = await db.QrStations.AsNoTracking()
        .Where(item => item.PublicCodeHash == codeHash && item.IsEnabled)
        .Select(item => new { item.Id, item.TenantId, item.PrinterDeviceId })
        .SingleOrDefaultAsync(ct);
    if (station is null)
    {
        return Results.NotFound(new { code = "station_not_available", message = "This print station is unavailable." });
    }

    var session = await db.CustomerPrintSessions.AsNoTracking()
        .SingleOrDefaultAsync(item =>
            item.TokenHash == tokenHash &&
            item.TenantId == station.TenantId &&
            item.QrStationId == station.Id &&
            item.Status == "Active" &&
            item.RevokedUtc == null &&
            item.ExpiresUtc > now, ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var shopProfile = await db.ShopProfiles.AsNoTracking()
        .Where(profile => profile.TenantId == station.TenantId)
        .Select(profile => new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.Currency })
        .SingleAsync(ct);
    var paymentOptions = await db.ShopPaymentProfiles.AsNoTracking()
        .Where(profile => profile.TenantId == station.TenantId)
        .Select(profile => new
        {
            profile.UpiEnabled,
            profile.UpiId,
            profile.PayeeName,
            profile.CashEnabled,
            profile.InstantPaymentEnabled
        })
        .SingleAsync(ct);
    var printer = station.PrinterDeviceId is Guid printerId
        ? await db.PrinterDevices.AsNoTracking()
            .Where(item => item.Id == printerId && item.TenantId == station.TenantId && item.IsActive)
            .Select(item => new { item.DisplayName, item.Manufacturer, item.Model, item.Status, item.CapabilitiesJson })
            .SingleOrDefaultAsync(ct)
        : null;
    var customerEntitlements = await entitlements.GetForTenantAsync(station.TenantId, ct);

    return Results.Ok(new
    {
        session.Id,
        session.Status,
        session.ExpiresUtc,
        SessionToken = rawToken,
        Shop = shopProfile,
        PaymentOptions = paymentOptions,
        Printer = printer,
        LocalAgent = await GetLocalAgentInfoAsync(db, station.TenantId, station.PrinterDeviceId, now, ct),
        CloudAssistedPrintingEnabled = customerEntitlements.CloudAssistedPrintingEnabled,
        PrivatePrintingVirtualOutputEnabled = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct),
        PhotoPricing = await db.ShopPricingProfiles.AsNoTracking()
            .Where(item => item.TenantId == station.TenantId)
            .Select(item => new
            {
                item.Photo35x45Minor,
                item.Photo25x35Minor,
                item.Photo2x2Minor,
                item.Photo4x6Minor
            })
            .SingleOrDefaultAsync(ct)
    });
}).RequireRateLimiting("qr-session");

publicApi.MapPost("/stations/{publicCode}/sessions", async (
    string publicCode,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    IHostEnvironment environment,
    CancellationToken ct) =>
{
    var codeHash = HashOpaqueToken(publicCode);
    var station = await db.QrStations.AsNoTracking().SingleOrDefaultAsync(
        item => item.PublicCodeHash == codeHash && item.IsEnabled, ct);
    if (station is null)
    {
        return Results.NotFound(new { code = "station_not_available", message = "This print station is unavailable. Please ask the shop operator for help." });
    }

    var now = DateTimeOffset.UtcNow;
    var rawToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    var session = new CustomerPrintSession
    {
        TenantId = station.TenantId,
        QrStationId = station.Id,
        CustomerUserId = CustomerIdentity.GetCustomerId(context.User),
        TokenHash = HashOpaqueToken(rawToken),
        CreatedUtc = now,
        ExpiresUtc = now.AddMinutes(15)
    };
    db.CustomerPrintSessions.Add(session);
    await db.SaveChangesAsync(ct);
    var customerEntitlements = await entitlements.GetForTenantAsync(station.TenantId, ct);
    return Results.Created("/api/v1/public/sessions/current", new
    {
        session.Id,
        session.Status,
        session.ExpiresUtc,
        SessionToken = rawToken,
        Shop = await db.ShopProfiles.AsNoTracking()
            .Where(profile => profile.TenantId == station.TenantId)
            .Select(profile => new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.Currency })
            .SingleAsync(ct),
        PaymentOptions = await db.ShopPaymentProfiles.AsNoTracking()
            .Where(profile => profile.TenantId == station.TenantId)
            .Select(profile => new
            {
                profile.UpiEnabled,
                profile.UpiId,
                profile.PayeeName,
                profile.CashEnabled,
                profile.InstantPaymentEnabled
            })
            .SingleAsync(ct),
        LocalAgent = await GetLocalAgentInfoAsync(db, station.TenantId, station.PrinterDeviceId, now, ct),
        CloudAssistedPrintingEnabled = customerEntitlements.CloudAssistedPrintingEnabled,
        PrivatePrintingVirtualOutputEnabled = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct),
        PhotoPricing = await db.ShopPricingProfiles.AsNoTracking()
            .Where(item => item.TenantId == station.TenantId)
            .Select(item => new
            {
                item.Photo35x45Minor,
                item.Photo25x35Minor,
                item.Photo2x2Minor,
                item.Photo4x6Minor
            })
            .SingleOrDefaultAsync(ct),
        Printer = station.PrinterDeviceId is Guid printerId
            ? await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.Id == printerId && printer.TenantId == station.TenantId && printer.IsActive)
                .Select(printer => new { printer.DisplayName, printer.Manufacturer, printer.Model, printer.Status, printer.CapabilitiesJson })
                .SingleOrDefaultAsync(ct)
            : null
    });
}).RequireRateLimiting("qr-session");

publicApi.MapGet("/sessions/current", async (
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var tokenHash = HashOpaqueToken(rawToken);
    var now = DateTimeOffset.UtcNow;
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId, item.QrStationId, item.ExpiresUtc })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var shopProfile = await db.ShopProfiles.AsNoTracking()
        .Where(profile => profile.TenantId == session.TenantId)
        .Select(profile => new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.Currency })
        .SingleAsync(ct);
    var stationPrinterId = await db.QrStations.AsNoTracking()
        .Where(station => station.Id == session.QrStationId && station.TenantId == session.TenantId && station.IsEnabled)
        .Select(station => station.PrinterDeviceId)
        .SingleOrDefaultAsync(ct);
    var localAgent = await GetLocalAgentInfoAsync(db, session.TenantId, stationPrinterId, now, ct);
    return Results.Ok(new { session.Id, session.ExpiresUtc, Shop = shopProfile, LocalAgent = localAgent });
});


static async Task<bool> GetPrivatePrintingVirtualOutputEnabledAsync(
    SmartPrintDbContext db,
    IHostEnvironment environment,
    CancellationToken ct)
{
    // Production is an independent fail-closed boundary. A stale or mistakenly
    // enabled database value can never permit virtual output in a live deployment.
    if (environment.IsProduction()) return false;
    return await db.PlatformOperationalSettings.AsNoTracking()
        .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
        .Select(item => (bool?)item.AllowPrivatePrintingVirtualOutput)
        .SingleOrDefaultAsync(ct) ?? false;
}

static async Task<bool> IsPrivatePrintingOutputQueueReadyAsync(
    SmartPrintDbContext db,
    IHostEnvironment environment,
    Guid tenantId,
    Guid agentDeviceId,
    Guid printerDeviceId,
    CancellationToken ct)
{
    var allowVirtualOutput = await GetPrivatePrintingVirtualOutputEnabledAsync(db, environment, ct);
    var printer = await db.PrinterDevices.AsNoTracking()
        .Where(item => item.Id == printerDeviceId &&
                       item.TenantId == tenantId &&
                       item.AgentDeviceId == agentDeviceId &&
                       item.IsActive &&
                       item.QueueName != null && item.QueueName != "")
        .Select(item => new { item.OutputMode, item.Status, item.LastSeenUtc })
        .SingleOrDefaultAsync(ct);
    var now = DateTimeOffset.UtcNow;
    if (printer is null ||
        !PrivatePrintingOutputPolicy.IsEligible(allowVirtualOutput, environment.IsProduction(), printer.OutputMode) ||
        printer.LastSeenUtc is null || printer.LastSeenUtc < now.AddMinutes(-2) ||
        printer.Status is not ("Ready" or "Busy"))
        return false;

    return await db.AgentDevices.AsNoTracking().AnyAsync(agent =>
        agent.Id == agentDeviceId &&
        agent.TenantId == tenantId &&
        agent.RevokedUtc == null &&
        agent.Status != "Revoked" &&
        agent.LastSeenUtc != null &&
        agent.LastSeenUtc >= now.AddMinutes(-2), ct);
}

static async Task<(bool Allowed, DateTimeOffset? SuspendedAtUtc)> GetAgentClaimAccessAsync(
    Guid tenantId,
    SmartPrintDbContext db,
    CancellationToken ct)
{
    if (db.Database.CurrentTransaction is null)
        throw new InvalidOperationException("A transaction is required for a suspended-tenant claim check.");

    // Hold shared locks until commit. Tenant suspension/deactivation and platform recovery-policy
    // updates take update locks on these same rows, preventing a time-of-check/time-of-use race.
    var tenant = await db.Tenants.FromSqlInterpolated(
        $"""SELECT * FROM "Tenants" WHERE "Id" = {tenantId} FOR SHARE""")
        .SingleOrDefaultAsync(ct);
    if (tenant is null) return (false, null);
    if (tenant.Status == "Active") return (true, null);
    if (tenant.Status != "Suspended") return (false, null);

    var settings = await db.PlatformOperationalSettings.FromSqlRaw(
        """SELECT * FROM "PlatformOperationalSettings" WHERE "Id" = 1 FOR SHARE""")
        .SingleOrDefaultAsync(ct);
    if (settings?.AllowPaidJobClaimsWhenSuspended != true) return (false, null);

    var suspendedAt = await db.TenantStatusAudits.AsNoTracking()
        .Where(item => item.TenantId == tenantId && item.NewStatus == "Suspended")
        .OrderByDescending(item => item.CreatedUtc)
        .Select(item => (DateTimeOffset?)item.CreatedUtc)
        .FirstOrDefaultAsync(ct);
    return (suspendedAt is not null, suspendedAt);
}

static bool IsTenantRecoveryOperation(
    HttpContext context,
    bool shopOperations,
    bool allowPaidJobClaimsWhileSuspended = false)
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (TenantAccessRules.IsReadRecoveryOperation(context.Request.Method, path, shopOperations))
        return true;
    if (!shopOperations && allowPaidJobClaimsWhileSuspended &&
        HttpMethods.IsPost(context.Request.Method) &&
        (path.EndsWith("/jobs/claim", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith("/jobs/print-claim", StringComparison.OrdinalIgnoreCase)))
        return true;
    if (shopOperations)
    {
        return HttpMethods.IsPost(context.Request.Method) &&
            (path.EndsWith("/confirm-output", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/refund-preview", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/refund", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/reject", StringComparison.OrdinalIgnoreCase));
    }

    if (HttpMethods.IsPatch(context.Request.Method) &&
        path.EndsWith("/revoke", StringComparison.OrdinalIgnoreCase))
        return true;

    if (!HttpMethods.IsPost(context.Request.Method))
        return false;

    return path.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/printers/sync", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/received", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/pdf-exported", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/private-test-output", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/private-document-purged", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/spooler-accepted", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/print-blocked", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/output-unconfirmed", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/spooler-status", StringComparison.OrdinalIgnoreCase);
}

static async ValueTask<object?> EnforceTenantActiveAsync(
    EndpointFilterInvocationContext context,
    EndpointFilterDelegate next,
    bool allowSubscriptionRead)
{
    var http = context.HttpContext;
    var db = http.RequestServices.GetRequiredService<SmartPrintDbContext>();
    Guid? tenantId = null;

    if (TryGetTenantId(http.User, out var userTenantId))
        tenantId = userTenantId;
    else if (Guid.TryParse(http.Request.Headers["X-Print-Agent-Id"].FirstOrDefault(), out var agentId))
        tenantId = await db.AgentDevices.AsNoTracking()
            .Where(item => item.Id == agentId)
            .Select(item => (Guid?)item.TenantId)
            .SingleOrDefaultAsync(http.RequestAborted);
    else if (http.Request.RouteValues.TryGetValue("publicCode", out var routeCode) && routeCode is not null)
    {
        var codeHash = HashOpaqueToken(routeCode.ToString()!);
        tenantId = await db.QrStations.AsNoTracking()
            .Where(item => item.PublicCodeHash == codeHash)
            .Select(item => (Guid?)item.TenantId)
            .SingleOrDefaultAsync(http.RequestAborted);
    }
    else
    {
        var sessionToken = http.Request.Headers["X-Print-Session"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            var tokenHash = HashOpaqueToken(sessionToken);
            tenantId = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash)
                .Select(item => (Guid?)item.TenantId)
                .SingleOrDefaultAsync(http.RequestAborted);
        }
    }

    if (tenantId is not Guid resolvedTenantId) return await next(context);
    if (allowSubscriptionRead &&
        http.Request.Path.Value?.EndsWith("/subscription", StringComparison.OrdinalIgnoreCase) == true)
        return await next(context);

    var status = await db.Tenants.AsNoTracking()
        .Where(item => item.Id == resolvedTenantId)
        .Select(item => item.Status)
        .SingleOrDefaultAsync(http.RequestAborted);
    var allowPaidJobClaimsWhileSuspended = status == "Suspended" &&
        (await db.PlatformOperationalSettings.AsNoTracking()
            .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
            .Select(item => (bool?)item.AllowPaidJobClaimsWhenSuspended)
            .SingleOrDefaultAsync(http.RequestAborted) ?? false);
    if (!TenantAccessRules.IsActive(status) &&
        !IsTenantRecoveryOperation(http, allowSubscriptionRead, allowPaidJobClaimsWhileSuspended))
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "This shop is suspended or inactive. New print work is blocked; existing jobs and financial recovery remain accessible.");

    return await next(context);
}

static string[] ReadLocalAgentEndpoints(string? json)
{
    try
    {
        return JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(json) ? "[]" : json)
            ?? Array.Empty<string>();
    }
    catch (JsonException)
    {
        return Array.Empty<string>();
    }
}

static async Task<object?> GetLocalAgentInfoAsync(
    SmartPrintDbContext db, Guid tenantId, Guid? printerDeviceId, DateTimeOffset now, CancellationToken ct)
{
    if (printerDeviceId is not Guid assignedPrinterId) return null;

    var agent = await (
        from printer in db.PrinterDevices.AsNoTracking()
        join device in db.AgentDevices.AsNoTracking()
            on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
            equals new { device.TenantId, AgentId = device.Id }
        where printer.Id == assignedPrinterId && printer.TenantId == tenantId &&
              printer.IsActive && printer.AgentDeviceId != null &&
              device.RevokedUtc == null && device.LastSeenUtc != null &&
              device.LastSeenUtc >= now.AddMinutes(-2)
        select new
        {
            device.Id,
            device.LocalEndpointsJson,
            device.DocumentEncryptionPublicKey,
            PrinterOutputMode = printer.OutputMode,
            PrinterStatus = printer.Status,
            PrinterLastSeenUtc = printer.LastSeenUtc
        }
    ).SingleOrDefaultAsync(ct);

    if (agent is null) return null;
    var endpoints = ReadLocalAgentEndpoints(agent.LocalEndpointsJson);
    return new
    {
        AgentDeviceId = agent.Id,
        Endpoints = endpoints,
        DocumentEncryptionPublicKey = agent.DocumentEncryptionPublicKey,
        agent.PrinterOutputMode,
        agent.PrinterStatus,
        agent.PrinterLastSeenUtc
    };
}

static async Task<AgentDevice?> AuthenticateAgentAsync(
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken cancellationToken)
{
    if (!Guid.TryParse(context.Request.Headers["X-Print-Agent-Id"].FirstOrDefault(), out var agentId))
    {
        return null;
    }

    var secret = context.Request.Headers["X-Print-Agent-Secret"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(secret) || secret.Length > 256)
    {
        return null;
    }

    var device = await db.AgentDevices.SingleOrDefaultAsync(
        item => item.Id == agentId && item.RevokedUtc == null && item.Status != "Revoked",
        cancellationToken);
    if (device is null)
    {
        return null;
    }

    var suppliedHash = Convert.FromHexString(HashOpaqueToken(secret));
    var storedHash = Convert.FromHexString(device.SecretHash);
    return CryptographicOperations.FixedTimeEquals(suppliedHash, storedHash) ? device : null;
}

static bool IsValidUpiId(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
    {
        return false;
    }

    var parts = value.Split('@');
    return parts.Length == 2 &&
           parts[0].Length is >= 2 and <= 128 &&
           parts[1].Length is >= 2 and <= 64 &&
           parts[0].All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-') &&
           parts[1].All(character => char.IsLetterOrDigit(character) || character is '.' or '-');
}

static string? BuildUpiUri(Guid paymentId, string method, string? upiId, string? payeeName, long amountMinor, string currency, string jobNumber)
{
    if (method != "UPI" || string.IsNullOrWhiteSpace(upiId))
    {
        return null;
    }

    return "upi://pay?pa=" + Uri.EscapeDataString(upiId) +
           "&pn=" + Uri.EscapeDataString(payeeName ?? string.Empty) +
           "&am=" + (amountMinor / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
           "&cu=" + Uri.EscapeDataString(currency) +
           "&tn=" + Uri.EscapeDataString("Smart Print " + jobNumber) +
           "&tr=" + Uri.EscapeDataString(paymentId.ToString("N"));
}

static object BuildPaymentResponse(PrintPayment payment, PrintJob job) =>
    new
    {
        payment.Id,
        payment.Method,
        payment.Status,
        payment.AmountMinor,
        payment.RefundedAmountMinor,
        payment.Currency,
        payment.CreatedUtc,
        payment.UpdatedUtc,
        payment.VerifiedUtc,
        payment.UpiIdSnapshot,
        payment.PayeeNameSnapshot,
        payment.PaymentReference,
        UpiUri = BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
            payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency, job.JobNumber),
        JobId = job.Id,
        JobNumber = job.JobNumber,
        JobStatus = job.Status
    };

static object BuildBatchPaymentResponse(PrintBatchPayment payment, int fileCount) =>
    new
    {
        payment.Id,
        payment.Method,
        payment.Status,
        payment.AmountMinor,
        payment.Currency,
        payment.UpiIdSnapshot,
        payment.PayeeNameSnapshot,
        payment.PaymentReference,
        payment.RejectionReason,
        payment.CreatedUtc,
        payment.UpdatedUtc,
        payment.VerifiedUtc,
        BatchId = payment.BatchId,
        FileCount = fileCount,
        UpiUri = BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
            payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency,
            "Batch " + payment.BatchId.ToString("N")[..8].ToUpperInvariant())
    };

static bool IsValidPageSizesJson(string value)
{
    try
    {
        using var parsed = JsonDocument.Parse(value);
        return parsed.RootElement.ValueKind == JsonValueKind.Array;
    }
    catch (JsonException)
    {
        return false;
    }
}

static bool IsHexColor(string value) =>
    value is { Length: 7 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);

static string HashOpaqueToken(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

await app.RunAsync();

public partial class Program { }
