using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Endpoints;

/// <summary>
/// Central platform-wide printer access management.
/// Printers are allowed by default. Only an explicit, reasoned hold restricts them.
/// </summary>
public static class PlatformPrinterAccessEndpoints
{
    public static RouteGroupBuilder MapPlatformPrinterAccessEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/printer-access", async (
            string? query,
            string? state,
            int? offset,
            int? limit,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var search = query?.Trim();
            var normalizedState = (state ?? "all").Trim().ToLowerInvariant();
            var pageOffset = Math.Max(0, offset ?? 0);
            var pageSize = Math.Clamp(limit ?? 50, 1, 100);
            var now = DateTimeOffset.UtcNow;

            var queueQuery =
                from printer in db.PrinterDevices.AsNoTracking()
                join tenant in db.Tenants.AsNoTracking() on printer.TenantId equals tenant.Id
                select new
                {
                    printer.TenantId,
                    ShopName = tenant.BusinessName,
                    ShopSlug = tenant.Slug,
                    printer.Id,
                    printer.DisplayName,
                    printer.Manufacturer,
                    printer.Model,
                    printer.ConnectionType,
                    printer.OutputMode,
                    printer.ConnectionAddress,
                    printer.QueueName,
                    printer.AgentDeviceId,
                    printer.IdentityFingerprint,
                    printer.Status,
                    printer.LastSeenUtc,
                    printer.IsActive,
                    printer.IsDefault,
                    printer.IsPlatformHeld,
                    printer.PlatformHoldReason,
                    printer.PlatformHeldByUserId,
                    printer.PlatformHeldUtc,
                    printer.CapabilitiesJson
                };

            if (normalizedState == "held")
                queueQuery = queueQuery.Where(item => item.IsPlatformHeld);
            else if (normalizedState == "available")
                queueQuery = queueQuery.Where(item => !item.IsPlatformHeld);

            if (!string.IsNullOrWhiteSpace(search))
            {
                queueQuery = queueQuery.Where(item =>
                    item.ShopName.Contains(search) ||
                    item.ShopSlug.Contains(search) ||
                    item.DisplayName.Contains(search) ||
                    (item.QueueName != null && item.QueueName.Contains(search)) ||
                    (item.Model != null && item.Model.Contains(search)) ||
                    (item.ConnectionAddress != null && item.ConnectionAddress.Contains(search)));
            }

            var total = await queueQuery.LongCountAsync(ct);
            var queues = await queueQuery
                .OrderByDescending(item => item.IsPlatformHeld)
                .ThenBy(item => item.ShopName)
                .ThenBy(item => item.DisplayName)
                .Skip(pageOffset)
                .Take(pageSize)
                .ToListAsync(ct);

            var agentIds = queues.Where(item => item.AgentDeviceId.HasValue)
                .Select(item => item.AgentDeviceId!.Value).Distinct().ToArray();
            var agentSnapshots = await db.AgentDevices.AsNoTracking()
                .Where(agent => agentIds.Contains(agent.Id))
                .Select(agent => new
                {
                    agent.Id,
                    agent.DeviceName,
                    agent.Status,
                    agent.RevokedUtc,
                    agent.LastSeenUtc
                })
                .ToDictionaryAsync(agent => agent.Id, ct);

            var printerIds = queues.Select(item => item.Id).ToArray();
            var audits = await db.PrinterPlatformAccessAudits.AsNoTracking()
                .Where(item => printerIds.Contains(item.PrinterDeviceId))
                .OrderByDescending(item => item.CreatedUtc)
                .Take(500)
                .ToListAsync(ct);

            var actorIds = audits.Select(item => item.ActorUserId)
                .Concat(queues.Where(item => item.PlatformHeldByUserId.HasValue)
                    .Select(item => item.PlatformHeldByUserId!.Value))
                .Distinct().ToArray();
            var actors = await db.Users.AsNoTracking()
                .Where(user => actorIds.Contains(user.Id))
                .Select(user => new { user.Id, user.DisplayName, user.UserName })
                .ToDictionaryAsync(
                    user => user.Id,
                    user => !string.IsNullOrWhiteSpace(user.DisplayName)
                        ? user.DisplayName
                        : user.UserName ?? "Platform administrator",
                    ct);

            var items = queues.Select(queue =>
            {
                var agent = queue.AgentDeviceId is Guid agentId && agentSnapshots.TryGetValue(agentId, out var foundAgent)
                    ? foundAgent
                    : null;
                var history = audits
                    .Where(item => item.TenantId == queue.TenantId && item.PrinterDeviceId == queue.Id)
                    .Take(10)
                    .Select(item => new
                    {
                        item.Id,
                        item.Action,
                        item.Reason,
                        item.IdentityFingerprint,
                        item.CreatedUtc,
                        ActorName = actors.GetValueOrDefault(item.ActorUserId, "Platform administrator")
                    })
                    .ToArray();

                return new
                {
                    queue.TenantId,
                    queue.ShopName,
                    queue.ShopSlug,
                    queue.Id,
                    queue.DisplayName,
                    queue.Manufacturer,
                    queue.Model,
                    queue.ConnectionType,
                    queue.OutputMode,
                    queue.ConnectionAddress,
                    queue.QueueName,
                    queue.AgentDeviceId,
                    AgentName = agent?.DeviceName,
                    AgentOnline = agent is not null &&
                        agent.RevokedUtc is null &&
                        agent.Status == "Online" &&
                        agent.LastSeenUtc is not null &&
                        agent.LastSeenUtc >= now.AddMinutes(-2),
                    AgentLastSeenUtc = agent?.LastSeenUtc,
                    queue.IdentityFingerprint,
                    queue.Status,
                    queue.LastSeenUtc,
                    queue.IsActive,
                    queue.IsDefault,
                    queue.CapabilitiesJson,
                    queue.IsPlatformHeld,
                    queue.PlatformHoldReason,
                    PlatformHeldByName = queue.PlatformHeldByUserId is Guid heldBy
                        ? actors.GetValueOrDefault(heldBy, "Platform administrator")
                        : null,
                    queue.PlatformHeldUtc,
                    History = history
                };
            }).ToArray();

            return Results.Ok(new { items, total, offset = pageOffset, limit = pageSize });
        });

        platform.MapPost("/printer-access/{printerId:guid}/hold", async (
            Guid printerId,
            PrinterPlatformHoldRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var actorId = GetActorId(principal);
            if (actorId is null) return Results.Forbid();
            var reason = request.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 5 or > 500)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["reason"] = ["Provide a hold reason between 5 and 500 characters."]
                });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var locked = await db.PrinterDevices.FromSqlInterpolated(
                $"SELECT * FROM \"PrinterDevices\" WHERE \"Id\" = {printerId} FOR UPDATE")
                .ToListAsync(ct);
            var printer = locked.SingleOrDefault();
            if (printer is null) return Results.NotFound();

            if (printer.IsPlatformHeld)
            {
                await transaction.CommitAsync(ct);
                return Results.Ok(new
                {
                    printer.Id,
                    printer.TenantId,
                    printer.IsPlatformHeld,
                    printer.PlatformHoldReason,
                    printer.PlatformHeldUtc,
                    DuplicateRequest = true
                });
            }

            var now = DateTimeOffset.UtcNow;
            printer.IsPlatformHeld = true;
            printer.PlatformHoldReason = reason;
            printer.PlatformHeldByUserId = actorId.Value;
            printer.PlatformHeldUtc = now;
            printer.UpdatedUtc = now;
            db.PrinterPlatformAccessAudits.Add(new PrinterPlatformAccessAudit
            {
                TenantId = printer.TenantId,
                PrinterDeviceId = printer.Id,
                Action = "HOLD",
                Reason = reason,
                IdentityFingerprint = printer.IdentityFingerprint,
                ActorUserId = actorId.Value,
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Ok(new
            {
                printer.Id,
                printer.TenantId,
                printer.IsPlatformHeld,
                printer.PlatformHoldReason,
                printer.PlatformHeldUtc,
                DuplicateRequest = false
            });
        });

        platform.MapPost("/printer-access/{printerId:guid}/release", async (
            Guid printerId,
            PrinterPlatformHoldRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var actorId = GetActorId(principal);
            if (actorId is null) return Results.Forbid();
            var reason = request.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 5 or > 500)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["reason"] = ["Provide a release reason between 5 and 500 characters."]
                });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var locked = await db.PrinterDevices.FromSqlInterpolated(
                $"SELECT * FROM \"PrinterDevices\" WHERE \"Id\" = {printerId} FOR UPDATE")
                .ToListAsync(ct);
            var printer = locked.SingleOrDefault();
            if (printer is null) return Results.NotFound();

            if (!printer.IsPlatformHeld)
            {
                await transaction.CommitAsync(ct);
                return Results.Ok(new
                {
                    printer.Id,
                    printer.TenantId,
                    printer.IsPlatformHeld,
                    DuplicateRequest = true
                });
            }

            var now = DateTimeOffset.UtcNow;
            printer.IsPlatformHeld = false;
            printer.PlatformHoldReason = null;
            printer.PlatformHeldByUserId = null;
            printer.PlatformHeldUtc = null;
            printer.UpdatedUtc = now;
            db.PrinterPlatformAccessAudits.Add(new PrinterPlatformAccessAudit
            {
                TenantId = printer.TenantId,
                PrinterDeviceId = printer.Id,
                Action = "RELEASE",
                Reason = reason,
                IdentityFingerprint = printer.IdentityFingerprint,
                ActorUserId = actorId.Value,
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Ok(new
            {
                printer.Id,
                printer.TenantId,
                printer.IsPlatformHeld,
                DuplicateRequest = false
            });
        });

        return platform;
    }

    private static Guid? GetActorId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
            principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }
}

public sealed record PrinterPlatformHoldRequest(string Reason);
