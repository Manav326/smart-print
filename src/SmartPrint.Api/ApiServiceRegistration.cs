using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api;

public static class ApiServiceRegistration
{
    public static string[] AddApiServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString,
        string signingKeyText,
        string issuer,
        string audience)
    {
        services.AddProblemDetails();
        services.AddMemoryCache();
        services.AddHttpClient<PublicDownloadService>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartPrint-Download-Proxy");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        services.AddDbContext<SmartPrintDbContext>(options => options.UseNpgsql(connectionString)
            .ReplaceService<IMigrationsIdGenerator, LegacyCompatibleMigrationsIdGenerator>());
        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<SmartPrintDbContext>()
            .AddSignInManager();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyText)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "shop_role"
                };
            });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(PlatformAdminAuthorization.PolicyName, policy =>
                policy.RequireAssertion(context => PlatformAdminAuthorization.IsPlatformAdmin(context.User)));
        });

        services.AddScoped<TokenIssuer>();
        services.AddScoped<TenantEntitlementService>();
        services.AddScoped<PlatformOverviewService>();
        services.AddScoped<PlatformTenantStatusService>();
        services.AddScoped<AccountRegistrationService>();
        services.AddScoped<AuthenticationSessionService>();
        services.AddScoped<ShopAccountService>();
        services.AddScoped<ShopPrinterService>();
        services.AddScoped<ShopStationService>();
        services.AddScoped<ShopPricingService>();
        services.AddScoped<ShopPaymentSettingsService>();
        services.AddScoped<ShopPaymentReviewService>();
        services.AddScoped<ShopCustomerQueryService>();
        services.AddHttpClient<SubscriptionRazorpayService>();
        services.AddSingleton<DocumentStorage>();
        services.AddSingleton<PlatformAdCreativeStorage>();
        services.AddHostedService<DocumentRetentionWorker>();
        services.AddHostedService<PrivateRelayRetentionWorker>();
        services.AddSingleton<PrivateRelayWakeupService>(serviceProvider =>
            new PrivateRelayWakeupService(
                connectionString,
                serviceProvider.GetRequiredService<ILogger<PrivateRelayWakeupService>>()));
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<PrivateRelayWakeupService>());

        services.AddRateLimiter(options =>
        {
            options.AddPolicy("authentication", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    RateLimitPartitionKeys.GetRateLimitPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            options.AddPolicy("public-api", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    RateLimitPartitionKeys.GetRateLimitPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            options.AddPolicy("agent-api", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    RateLimitPartitionKeys.GetAgentRateLimitPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            options.AddPolicy("qr-session", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    RateLimitPartitionKeys.GetRateLimitPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            options.AddPolicy("document-upload", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    RateLimitPartitionKeys.GetSessionRateLimitPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

        var configuredCorsOrigins = configuration.GetSection("Cors:AllowedOrigins")
            .Get<string[]>()?
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var corsOrigins = (configuredCorsOrigins is { Length: > 0 }
            ? configuredCorsOrigins
            : new[]
            {
                "http://localhost:3000",
                "http://localhost:3001",
                "http://127.0.0.1:3000"
            })
            .Append("http://tauri.localhost")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        services.AddCors(options => options.AddPolicy("web", policy => policy
            .WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()));

        return corsOrigins;
    }
}
