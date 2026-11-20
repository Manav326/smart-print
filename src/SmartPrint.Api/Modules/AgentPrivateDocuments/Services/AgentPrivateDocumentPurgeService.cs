using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

namespace SmartPrint.Api.Modules.AgentPrivateDocuments.Services;

public enum AgentPrivateDocumentPurgeResultKind
{
    NotFound,
    Conflict,
    Duplicate,
    Purged
}

public sealed record AgentPrivateDocumentPurgeResult(
    AgentPrivateDocumentPurgeResultKind Kind,
    Guid Id = default,
    DateTimeOffset? PurgedUtc = null,
    string? Code = null,
    string? Message = null);

/// <summary>
/// Owns tenant-scoped private-source purge discovery and acknowledgement.
/// The Agent only confirms deletion; the API never asks it to delete a source
/// before verified output or while reconciliation still requires the source.
/// </summary>
public sealed class AgentPrivateDocumentPurgeService(SmartPrintDbContext db)
{
    public async Task<IReadOnlyList<Guid>> GetPendingJobIdsAsync(
        Guid tenantId,
        Guid agentDeviceId,
        CancellationToken ct)
    {
        return await db.PrintJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId &&
                          job.ClaimedByAgentDeviceId == agentDeviceId &&
                          job.PrivatePrinting &&
                          ((job.Status == "COMPLETED" &&
                            job.OutputType == PrivatePrintingOutputPolicy.Physical &&
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
    }

    public async Task<AgentPrivateDocumentPurgeResult> AcknowledgeAsync(
        Guid tenantId,
        Guid agentDeviceId,
        Guid jobId,
        CancellationToken ct)
    {
        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == tenantId, ct);
        if (job is null)
            return new AgentPrivateDocumentPurgeResult(AgentPrivateDocumentPurgeResultKind.NotFound);

        if (job.PrivateDocumentPurgedUtc is not null)
        {
            return new AgentPrivateDocumentPurgeResult(
                AgentPrivateDocumentPurgeResultKind.Duplicate,
                job.Id,
                job.PrivateDocumentPurgedUtc);
        }

        var authorization = AgentPrivateDocumentPurgeRules.ValidateAcknowledgement(
            job.PrivatePrinting,
            job.Status,
            job.OutputType,
            job.OutputConfirmedUtc,
            job.OutputCompletedUtc,
            job.PrivateDocumentPurgeRequestedUtc,
            job.ClaimedByAgentDeviceId,
            agentDeviceId);
        if (authorization != AgentPrivateDocumentPurgeAuthorizationFailure.None)
        {
            return Conflict(
                job.Id,
                "private_document_purge_not_authorized",
                "The private source can be purged only after verified physical output or a separately recorded non-production test output. The source must remain available for reconciliation.");
        }

        var document = await db.PrintDocuments.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.PrintJobId == jobId, ct);
        if (document is null)
        {
            return Conflict(
                job.Id,
                "private_document_record_missing",
                "The private document record is unavailable; the purge cannot be acknowledged safely.");
        }

        if (document.StorageMode != "LOCAL_AGENT" || document.AgentDeviceId != agentDeviceId)
        {
            return Conflict(
                job.Id,
                "private_document_storage_mismatch",
                "This private source is not assigned to the acknowledging Agent.");
        }

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
            item.TenantId == tenantId && item.PrintJobId == jobId && item.PrivatePrinting, ct);
        if (uploadTicket is not null)
        {
            uploadTicket.OriginalFileName = "Private document";
            uploadTicket.MediaType = "application/octet-stream";
            uploadTicket.FileExtension = ".private";
            uploadTicket.SizeBytes = 0;
            uploadTicket.Sha256 = string.Empty;
            // Keep only the one-way grant digest so customers can reconcile a lost
            // upload response without authorizing retrieval or cloud recovery.
            uploadTicket.BatchGroupName = "Private documents";
            uploadTicket.BatchName = null;
        }

        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = job.Status,
            NewStatus = job.Status,
            ActorType = "PrintAgent",
            ActorId = agentDeviceId,
            ReasonCode = "PRIVATE_SOURCE_DOCUMENT_PURGED_AND_METADATA_REDACTED",
            CreatedUtc = now
        });

        await db.SaveChangesAsync(ct);
        return new AgentPrivateDocumentPurgeResult(
            AgentPrivateDocumentPurgeResultKind.Purged,
            job.Id,
            job.PrivateDocumentPurgedUtc);
    }

    private static AgentPrivateDocumentPurgeResult Conflict(Guid jobId, string code, string message) =>
        new(AgentPrivateDocumentPurgeResultKind.Conflict, jobId, Code: code, Message: message);
}
