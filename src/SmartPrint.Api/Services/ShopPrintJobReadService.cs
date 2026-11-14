using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api.Services;

public enum ShopPrintJobDocumentFailure
{
    None,
    NotFound,
    PrivatePrintingSourceLocked,
    StoredOnLocalAgent,
    Purged,
    PhotoSourceUnavailable,
    StorageUnavailable
}

public sealed record ShopPrintJobDocumentResult(
    ShopPrintJobDocumentFailure Failure,
    MemoryStream? Content = null,
    string? MediaType = null);

public enum ShopPrintJobPreviewFailure
{
    None,
    NotFound,
    PrivatePrintingSourceLocked,
    PhotoSourceUnavailable,
    PdfExportUnavailable,
    LocalAgentMissing,
    AgentOffline,
    EndpointMissing
}

public sealed record ShopPrintJobPreviewResult(
    ShopPrintJobPreviewFailure Failure,
    string? Mode = null,
    string? GrantToken = null,
    string[]? Endpoints = null,
    DateTimeOffset? ExpiresUtc = null);

public sealed class ShopPrintJobReadService(
    SmartPrintDbContext db,
    DocumentStorage storage)
{
    public async Task<ShopPrintJobDocumentResult> GetDocumentAsync(
        Guid tenantId,
        Guid jobId,
        bool photoSourcePreview,
        CancellationToken ct)
    {
        var privatePrinting = await db.PrintJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.Id == jobId)
            .Select(job => (bool?)job.PrivatePrinting)
            .SingleOrDefaultAsync(ct);
        if (privatePrinting is null)
            return new(ShopPrintJobDocumentFailure.NotFound);
        if (privatePrinting.Value)
            return new(ShopPrintJobDocumentFailure.PrivatePrintingSourceLocked);

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
            return new(ShopPrintJobDocumentFailure.NotFound);

        if (document.StorageMode == "LOCAL_AGENT")
            return new(ShopPrintJobDocumentFailure.StoredOnLocalAgent);

        if (document.PurgedUtc is not null)
            return new(ShopPrintJobDocumentFailure.Purged);

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
                sourceHash.Length != 64 ||
                !sourceHash.All(Uri.IsHexDigit))
            {
                return new(ShopPrintJobDocumentFailure.PhotoSourceUnavailable);
            }
        }

        try
        {
            return new(
                ShopPrintJobDocumentFailure.None,
                storage.OpenRead(storageKey),
                mediaType);
        }
        catch (IOException)
        {
            return new(ShopPrintJobDocumentFailure.StorageUnavailable);
        }
    }

    public async Task<ShopPrintJobPreviewResult> GetPreviewSourceAsync(
        Guid tenantId,
        Guid jobId,
        bool outputPreview,
        bool photoSourcePreview,
        string signingKeyText,
        string issuer,
        string audience,
        CancellationToken ct)
    {
        if (await db.PrintJobs.AsNoTracking().AnyAsync(
                job => job.Id == jobId && job.TenantId == tenantId && job.PrivatePrinting, ct))
        {
            return new(ShopPrintJobPreviewFailure.PrivatePrintingSourceLocked);
        }

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
                return new(ShopPrintJobPreviewFailure.NotFound);

            if (output.Status != "COMPLETED" || output.OutputType != "PDF_EXPORT" ||
                output.OutputCompletedUtc is null ||
                string.IsNullOrWhiteSpace(output.OutputFileName) ||
                output.OutputFileName.Length > 255 ||
                output.OutputFileName.Contains('/') ||
                output.OutputFileName.Contains('\\') ||
                !string.Equals(Path.GetExtension(output.OutputFileName), ".pdf", StringComparison.OrdinalIgnoreCase) ||
                output.OutputSizeBytes is not long outputSize ||
                outputSize <= 0 || outputSize > 100L * 1024 * 1024 ||
                output.OutputSha256 is not { Length: 64 } ||
                !output.OutputSha256.All(Uri.IsHexDigit) ||
                output.ClaimedByAgentDeviceId is not Guid outputAgentId)
            {
                return new(ShopPrintJobPreviewFailure.PdfExportUnavailable);
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
                return new(ShopPrintJobPreviewFailure.NotFound);

            if (photoSourcePreview)
            {
                var sourceMediaType = document.PhotoSourceMediaType ?? document.MediaType;
                var sourceName = document.PhotoSourceOriginalFileName ?? document.OriginalFileName;
                var sourceSize = document.PhotoSourceSizeBytes ?? document.SizeBytes;
                var sourceHash = document.PhotoSourceSha256 ?? document.Sha256;
                if (sourceMediaType is not ("image/jpeg" or "image/png") ||
                    string.IsNullOrWhiteSpace(sourceName) || sourceName.Length > 255 ||
                    sourceSize <= 0 ||
                    string.IsNullOrWhiteSpace(sourceHash) || sourceHash.Length != 64 ||
                    !sourceHash.All(Uri.IsHexDigit))
                {
                    return new(ShopPrintJobPreviewFailure.PhotoSourceUnavailable);
                }
            }

            if (!string.Equals(document.StorageMode, "LOCAL_AGENT", StringComparison.Ordinal))
                return new(ShopPrintJobPreviewFailure.None, Mode: "CLOUD");

            if (document.AgentDeviceId is not Guid documentAgentId)
                return new(ShopPrintJobPreviewFailure.LocalAgentMissing);

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
            return new(ShopPrintJobPreviewFailure.AgentOffline);

        var endpoints = ReadLocalAgentEndpoints(agent.LocalEndpointsJson);
        if (endpoints.Length == 0)
            return new(ShopPrintJobPreviewFailure.EndpointMissing);

        var expiresUtc = now.AddMinutes(2);
        var grantToken = LocalDocumentPreviewGrant.Issue(
            signingKeyText, issuer, audience, tenantId, jobId, agent.Id, now, expiresUtc);
        return new(
            ShopPrintJobPreviewFailure.None,
            "LOCAL_AGENT",
            grantToken,
            endpoints,
            expiresUtc);
    }

    private static string[] ReadLocalAgentEndpoints(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(
                string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
