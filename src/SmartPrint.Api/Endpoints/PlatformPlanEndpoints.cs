using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Endpoints;

public static class PlatformPlanEndpoints
{
    public static RouteGroupBuilder MapSubscriptionPlanEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/plans", async (SmartPrintDbContext db, CancellationToken ct) =>
        {
            var plans = await db.SubscriptionPlans.AsNoTracking()
                .OrderBy(plan => plan.Code)
                .ToListAsync(ct);
            var planIds = plans.Select(plan => plan.Id).ToArray();
            var versions = await db.SubscriptionPlanVersions.AsNoTracking()
                .Where(version => planIds.Contains(version.PlanId))
                .OrderByDescending(version => version.Version)
                .ToListAsync(ct);

            var response = plans.Select(plan => new PlatformPlanResponse(
                plan.Id,
                plan.Code,
                plan.Name,
                plan.Description,
                plan.IsActive,
                plan.IsSystemPlan,
                plan.CreatedUtc,
                plan.UpdatedUtc,
                versions.Where(version => version.PlanId == plan.Id)
                    .Select(ToResponse)
                    .ToArray()))
                .ToArray();

            return Results.Ok(response);
        });

        platform.MapPost("/plans", async (
            CreatePlatformPlanRequest request,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (request.Version is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["version"] = ["Plan pricing and entitlement settings are required."]
                });
            }

            var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
            var name = request.Name?.Trim() ?? string.Empty;
            var errors = ValidateMetadata(code, name, request.Description);
            AddVersionErrors(errors, request.Version);
            if (errors.Count != 0)
            {
                return Results.ValidationProblem(errors);
            }

            if (string.Equals(code, "TRIAL", StringComparison.Ordinal))
            {
                return Results.Conflict(new { message = "TRIAL is a protected system plan code." });
            }

            if (await db.SubscriptionPlans.AnyAsync(plan => plan.Code == code, ct))
            {
                return Results.Conflict(new { message = "A plan with this code already exists." });
            }

            var now = DateTimeOffset.UtcNow;
            var plan = new SubscriptionPlan
            {
                Code = code,
                Name = name,
                Description = NormalizeDescription(request.Description),
                IsActive = true,
                IsSystemPlan = false,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var version = CreateVersion(plan.Id, 1, request.Version, now);
            db.SubscriptionPlans.Add(plan);
            db.SubscriptionPlanVersions.Add(version);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/platform/plans/{plan.Id}", new
            {
                plan.Id,
                plan.Code,
                plan.Name,
                plan.Description,
                plan.IsActive,
                plan.IsSystemPlan,
                version = ToResponse(version)
            });
        });

        platform.MapPut("/plans/{planId:guid}", async (
            Guid planId,
            UpdatePlatformPlanRequest request,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var name = request.Name?.Trim() ?? string.Empty;
            var errors = ValidateMetadata(null, name, request.Description, allowMissingCode: true);
            if (errors.Count != 0)
            {
                return Results.ValidationProblem(errors);
            }

            var plan = await db.SubscriptionPlans.SingleOrDefaultAsync(item => item.Id == planId, ct);
            if (plan is null)
            {
                return Results.NotFound();
            }

            if (plan.IsSystemPlan)
            {
                return Results.Conflict(new { message = "System plans are protected and cannot be edited." });
            }

            plan.Name = name;
            plan.Description = NormalizeDescription(request.Description);
            plan.IsActive = request.IsActive;
            plan.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                plan.Id,
                plan.Code,
                plan.Name,
                plan.Description,
                plan.IsActive,
                plan.UpdatedUtc
            });
        });

        platform.MapPut("/plans/{planId:guid}/draft", async (
            Guid planId,
            PlatformPlanVersionRequest request,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var errors = ValidateVersion(request);
            if (errors.Count != 0)
            {
                return Results.ValidationProblem(errors);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var draftLockKey = $"platform-plan-draft:{planId:N}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(6, hashtext({draftLockKey}))", ct);

            var plan = await db.SubscriptionPlans.SingleOrDefaultAsync(item => item.Id == planId, ct);
            if (plan is null)
            {
                return Results.NotFound();
            }

            if (plan.IsSystemPlan)
            {
                return Results.Conflict(new { message = "System plan entitlements are protected." });
            }

            // The partial unique index guarantees one draft per plan. Order defensively
            // so older databases with duplicate drafts still recover instead of throwing.
            var drafts = await db.SubscriptionPlanVersions
                .Where(version => version.PlanId == planId && version.Status == "Draft")
                .OrderByDescending(version => version.Version)
                .ThenByDescending(version => version.UpdatedUtc)
                .ToListAsync(ct);
            var draft = drafts.FirstOrDefault();
            var now = DateTimeOffset.UtcNow;
            foreach (var supersededDraft in drafts.Skip(1))
            {
                supersededDraft.Status = "Retired";
                supersededDraft.UpdatedUtc = now;
            }

            if (draft is null)
            {
                var latestVersion = await db.SubscriptionPlanVersions
                    .Where(version => version.PlanId == planId)
                    .Select(version => version.Version)
                    .DefaultIfEmpty(0)
                    .MaxAsync(ct);
                draft = CreateVersion(planId, checked(latestVersion + 1), request, now);
                db.SubscriptionPlanVersions.Add(draft);
            }
            else
            {
                ApplyVersion(draft, request, now);
            }

            plan.UpdatedUtc = now;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(ToResponse(draft));
        });

        platform.MapPost("/plans/{planId:guid}/versions/{versionNumber:int}/publish", async (
            Guid planId,
            int versionNumber,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var plan = await db.SubscriptionPlans.SingleOrDefaultAsync(item => item.Id == planId, ct);
            if (plan is null)
            {
                return Results.NotFound();
            }

            if (plan.IsSystemPlan)
            {
                return Results.Conflict(new { message = "System plan versions cannot be changed." });
            }

            var version = await db.SubscriptionPlanVersions.SingleOrDefaultAsync(
                item => item.PlanId == planId && item.Version == versionNumber, ct);
            if (version is null)
            {
                return Results.NotFound();
            }

            if (version.Status != "Draft")
            {
                return Results.Conflict(new { message = "Only a draft plan version can be published." });
            }

            if (version.MonthlyPriceMinor is null && version.AnnualPriceMinor is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["price"] = ["Set at least one monthly or annual price before publishing. Enter zero explicitly for a free plan."]
                });
            }

            var now = DateTimeOffset.UtcNow;
            version.Status = "Published";
            version.PublishedUtc = now;
            version.UpdatedUtc = now;
            plan.UpdatedUtc = now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(version));
        });

        return platform;
    }

    private static Dictionary<string, string[]> ValidateMetadata(
        string? code,
        string name,
        string? description,
        bool allowMissingCode = false)
    {
        var errors = new Dictionary<string, string[]>();
        if (!allowMissingCode && !SubscriptionPlanRules.IsValidCode(code))
        {
            errors["code"] = ["Use 2–40 uppercase letters, digits or underscores; the first character must be a letter."];
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            errors["name"] = ["Plan name is required and must be 100 characters or fewer."];
        }

        if (description is { Length: > 500 })
        {
            errors["description"] = ["Description must be 500 characters or fewer."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateVersion(PlatformPlanVersionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var currency = request.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!SubscriptionPlanRules.IsValidCurrency(currency))
        {
            errors["currency"] = ["Use a three-letter uppercase currency code, for example INR."];
        }

        if (!SubscriptionPlanRules.IsValidPriceMinor(request.MonthlyPriceMinor) || !SubscriptionPlanRules.IsValidPriceMinor(request.AnnualPriceMinor))
        {
            errors["price"] = ["Prices cannot be negative. Use zero for a free interval or leave it empty when that interval is unavailable."];
        }

        AddPositiveLimitError(errors, nameof(request.MaxPrinters), request.MaxPrinters);
        AddPositiveLimitError(errors, nameof(request.MaxAgentDevices), request.MaxAgentDevices);
        AddPositiveLimitError(errors, nameof(request.MaxQrStations), request.MaxQrStations);
        AddPositiveLimitError(errors, nameof(request.MaxMonthlyPrintJobs), request.MaxMonthlyPrintJobs);
        if (!SubscriptionPlanRules.IsValidPositiveByteLimit(request.MaxDocumentBytes))
        {
            errors["maxDocumentBytes"] = ["Maximum document size must be a positive number of bytes or empty for no configured cap."];
        }

        return errors;
    }

    private static void AddVersionErrors(Dictionary<string, string[]> errors, PlatformPlanVersionRequest request)
    {
        foreach (var pair in ValidateVersion(request))
        {
            errors[pair.Key] = pair.Value;
        }
    }

    private static void AddPositiveLimitError(Dictionary<string, string[]> errors, string key, int? value)
    {
        if (!SubscriptionPlanRules.IsValidPositiveLimit(value))
        {
            errors[key] = ["Use a positive whole number or leave the limit empty for no configured cap."];
        }
    }

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static SubscriptionPlanVersion CreateVersion(
        Guid planId,
        int version,
        PlatformPlanVersionRequest request,
        DateTimeOffset now)
    {
        var result = new SubscriptionPlanVersion
        {
            PlanId = planId,
            Version = version,
            CreatedUtc = now
        };
        ApplyVersion(result, request, now);
        return result;
    }

    private static void ApplyVersion(
        SubscriptionPlanVersion target,
        PlatformPlanVersionRequest request,
        DateTimeOffset now)
    {
        target.Currency = request.Currency.Trim().ToUpperInvariant();
        target.MonthlyPriceMinor = request.MonthlyPriceMinor;
        target.AnnualPriceMinor = request.AnnualPriceMinor;
        target.MaxPrinters = request.MaxPrinters;
        target.MaxAgentDevices = request.MaxAgentDevices;
        target.MaxQrStations = request.MaxQrStations;
        target.MaxMonthlyPrintJobs = request.MaxMonthlyPrintJobs;
        target.MaxDocumentBytes = request.MaxDocumentBytes;
        target.CloudAssistedPrintingEnabled = request.CloudAssistedPrintingEnabled;
        target.WhiteLabelEnabled = request.WhiteLabelEnabled;
        target.AdvancedReportsEnabled = request.AdvancedReportsEnabled;
        target.UpdatedUtc = now;
    }

    private static PlatformPlanVersionResponse ToResponse(SubscriptionPlanVersion version) => new(
        version.Id,
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
        version.CreatedUtc,
        version.UpdatedUtc,
        version.PublishedUtc);
}

public sealed record CreatePlatformPlanRequest(
    string Code,
    string Name,
    string? Description,
    PlatformPlanVersionRequest Version);

public sealed record UpdatePlatformPlanRequest(string Name, string? Description, bool IsActive);

public sealed record PlatformPlanVersionRequest(
    string Currency,
    long? MonthlyPriceMinor,
    long? AnnualPriceMinor,
    int? MaxPrinters,
    int? MaxAgentDevices,
    int? MaxQrStations,
    int? MaxMonthlyPrintJobs,
    long? MaxDocumentBytes,
    bool CloudAssistedPrintingEnabled,
    bool WhiteLabelEnabled,
    bool AdvancedReportsEnabled);

public sealed record PlatformPlanVersionResponse(
    Guid Id,
    int Version,
    string Status,
    string Currency,
    long? MonthlyPriceMinor,
    long? AnnualPriceMinor,
    int? MaxPrinters,
    int? MaxAgentDevices,
    int? MaxQrStations,
    int? MaxMonthlyPrintJobs,
    long? MaxDocumentBytes,
    bool CloudAssistedPrintingEnabled,
    bool WhiteLabelEnabled,
    bool AdvancedReportsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? PublishedUtc);

public sealed record PlatformPlanResponse(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    bool IsActive,
    bool IsSystemPlan,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<PlatformPlanVersionResponse> Versions);
