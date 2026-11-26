using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Endpoints;

public static class VirtualPrinterTestGrantEndpoints
{
    public static RouteGroupBuilder MapVirtualPrinterTestGrantEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/virtual-printer-test-grants", async (SmartPrintDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var queues = await (
                from printer in db.PrinterDevices.AsNoTracking()
                join tenant in db.Tenants.AsNoTracking() on printer.TenantId equals tenant.Id
                where printer.OutputMode == PrinterTypePolicy.UnsupportedVirtual
                orderby tenant.BusinessName, printer.DisplayName
                select new
                {
                    printer.TenantId,
                    ShopName = tenant.BusinessName,
                    ShopSlug = tenant.Slug,
                    printer.Id,
                    printer.DisplayName,
                    printer.Model,
                    printer.ConnectionType,
                    printer.ConnectionAddress,
                    printer.QueueName,
                    printer.AgentDeviceId,
                    printer.IdentityFingerprint,
                    printer.Status,
                    printer.LastSeenUtc,
                    printer.IsActive
                }).ToListAsync(ct);

            var printerIds = queues.Select(item => item.Id).ToArray();
            var grants = await db.VirtualPrinterTestGrants.AsNoTracking()
                .Where(item => printerIds.Contains(item.PrinterDeviceId))
                .OrderByDescending(item => item.CreatedUtc)
                .ToListAsync(ct);
            var actorIds = grants.SelectMany(grant => new Guid?[]
                { grant.ApprovedByUserId, grant.RevokedByUserId })
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
            var actorNames = await db.Users.AsNoTracking()
                .Where(user => actorIds.Contains(user.Id))
                .Select(user => new { user.Id, user.DisplayName, user.UserName })
                .ToDictionaryAsync(user => user.Id,
                    user => !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : user.UserName ?? "Platform administrator", ct);

            return Results.Ok(queues.Select(queue => new
            {
                queue.TenantId,
                queue.ShopName,
                queue.ShopSlug,
                queue.Id,
                queue.DisplayName,
                queue.Model,
                queue.ConnectionType,
                queue.ConnectionAddress,
                queue.QueueName,
                queue.AgentDeviceId,
                queue.IdentityFingerprint,
                queue.Status,
                queue.LastSeenUtc,
                queue.IsActive,
                Grants = grants.Where(grant =>
                    grant.TenantId == queue.TenantId && grant.PrinterDeviceId == queue.Id)
                    .Select(grant => new
                    {
                        grant.Id,
                        grant.Reason,
                        grant.ApprovedByUserId,
                        ApprovedByName = actorNames.GetValueOrDefault(grant.ApprovedByUserId, "Platform administrator"),
                        grant.CreatedUtc,
                        grant.ExpiresUtc,
                        grant.RevokedByUserId,
                        RevokedByName = grant.RevokedByUserId is Guid revokedBy
                            ? actorNames.GetValueOrDefault(revokedBy, "Platform administrator") : null,
                        grant.RevokedUtc,
                        grant.RevocationReason,
                        IsActive = VirtualPrinterTestGrantPolicy.IsGrantActive(
                            queue.TenantId, queue.TenantId, grant.TenantId, queue.AgentDeviceId,
                            grant.AgentDeviceId, queue.IdentityFingerprint,
                            grant.IdentityFingerprint, grant.CreatedUtc, grant.ExpiresUtc,
                            grant.RevokedUtc, now)
                    })
            }));
        });

        platform.MapPost("/virtual-printer-test-grants", async (
            VirtualPrinterTestGrantRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            if (environment.IsProduction())
                return Results.Conflict(new { code = "virtual_printer_test_disabled_in_production" });

            var actorId = GetActorId(principal);
            if (actorId is null) return Results.Forbid();
            var reason = request.Reason?.Trim();
            if (request.TenantId == Guid.Empty || request.PrinterDeviceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(reason) || reason.Length is < 5 or > 500)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["grant"] = ["A shop, printer and audit reason between 5 and 500 characters are required."]
                });

            var now = DateTimeOffset.UtcNow;
            var expiresUtc = request.ExpiresUtc ?? now.AddDays(7);
            if (!VirtualPrinterTestGrantPolicy.IsValidGrantLifetime(now, expiresUtc))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["expiresUtc"] = ["Grant expiry must be between one hour and 30 days from approval."]
                });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Serialize approvals for this exact queue so two administrators
            // cannot create overlapping active grants from concurrent requests.
            var lockedPrinters = await db.PrinterDevices.FromSqlInterpolated(
                $"SELECT * FROM \"PrinterDevices\" WHERE \"TenantId\" = {request.TenantId} AND \"Id\" = {request.PrinterDeviceId} FOR UPDATE")
                .ToListAsync(ct);
            var printer = lockedPrinters.SingleOrDefault();
            if (printer is null) return Results.NotFound();
            if (printer.OutputMode != PrinterTypePolicy.UnsupportedVirtual ||
                printer.AgentDeviceId is not Guid agentId ||
                string.IsNullOrWhiteSpace(printer.QueueName) ||
                !PrinterTypePolicy.IsConnectedPrinterPort(printer.ConnectionAddress) ||
                printer.LastSeenUtc is null || printer.LastSeenUtc < now - VirtualPrinterTestGrantPolicy.MaximumInventoryAge ||
                !PrinterTypePolicy.IsWorkingWindowsQueue(printer.Status))
                return Results.Conflict(new { code = "virtual_printer_not_ready", message = "The virtual queue must be discovered, connected and Ready or Busy on its paired Windows Agent before approval." });

            var agentOnline = await db.AgentDevices.AnyAsync(agent =>
                agent.Id == agentId && agent.TenantId == request.TenantId &&
                agent.RevokedUtc == null && agent.Status == "Online" &&
                agent.LastSeenUtc != null && agent.LastSeenUtc >= now - VirtualPrinterTestGrantPolicy.MaximumInventoryAge, ct);
            if (!agentOnline) return Results.Conflict(new { code = "virtual_printer_agent_offline" });

            var fingerprint = VirtualPrinterTestGrantPolicy.ComputeIdentityFingerprint(
                agentId, printer.QueueName, printer.Model, printer.ConnectionAddress);
            if (!string.Equals(printer.IdentityFingerprint, fingerprint, StringComparison.Ordinal))
                return Results.Conflict(new { code = "virtual_printer_identity_stale", message = "The queue identity changed. Wait for the next Agent inventory sync before approving it." });

            var current = await db.VirtualPrinterTestGrants
                .Where(grant => grant.TenantId == request.TenantId &&
                    grant.PrinterDeviceId == printer.Id && grant.RevokedUtc == null &&
                    grant.ExpiresUtc > now && grant.IdentityFingerprint == fingerprint)
                .OrderByDescending(grant => grant.CreatedUtc)
                .FirstOrDefaultAsync(ct);
            if (current is not null)
            {
                await transaction.CommitAsync(ct);
                return Results.Ok(new { current.Id, current.TenantId, current.PrinterDeviceId, current.AgentDeviceId,
                    current.IdentityFingerprint, current.Reason, current.CreatedUtc, current.ExpiresUtc, DuplicateRequest = true });
            }

            var grant = new VirtualPrinterTestGrant
            {
                TenantId = request.TenantId,
                PrinterDeviceId = printer.Id,
                AgentDeviceId = agentId,
                IdentityFingerprint = fingerprint,
                Reason = reason,
                ApprovedByUserId = actorId.Value,
                CreatedUtc = now,
                ExpiresUtc = expiresUtc
            };
            db.VirtualPrinterTestGrants.Add(grant);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created($"/api/v1/platform/virtual-printer-test-grants/{grant.Id}", new
            {
                grant.Id, grant.TenantId, grant.PrinterDeviceId, grant.AgentDeviceId,
                grant.IdentityFingerprint, grant.Reason, grant.CreatedUtc, grant.ExpiresUtc
            });
        });

        platform.MapDelete("/virtual-printer-test-grants/{grantId:guid}", async (
            Guid grantId,
            VirtualPrinterTestGrantRevocationRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            if (environment.IsProduction())
                return Results.Conflict(new { code = "virtual_printer_test_disabled_in_production" });
            var actorId = GetActorId(principal);
            if (actorId is null) return Results.Forbid();
            var reason = request.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 5 or > 500)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["reason"] = ["Provide a revocation reason between 5 and 500 characters."]
                });

            var grant = await db.VirtualPrinterTestGrants.SingleOrDefaultAsync(item => item.Id == grantId, ct);
            if (grant is null) return Results.NotFound();
            if (grant.RevokedUtc is null)
            {
                grant.RevokedByUserId = actorId.Value;
                grant.RevokedUtc = DateTimeOffset.UtcNow;
                grant.RevocationReason = reason;
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(new { grant.Id, grant.RevokedUtc, grant.RevokedByUserId, grant.RevocationReason });
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

public sealed record VirtualPrinterTestGrantRequest(
    Guid TenantId,
    Guid PrinterDeviceId,
    string Reason,
    DateTimeOffset? ExpiresUtc);

public sealed record VirtualPrinterTestGrantRevocationRequest(string Reason);
