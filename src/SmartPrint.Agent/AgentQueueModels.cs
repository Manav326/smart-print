namespace SmartPrint.Agent;

public sealed record AgentClaimedJob(
    Guid Id,
    string JobNumber,
    string Status,
    int PageCount,
    int Copies,
    string ColourMode,
    string PaperSize,
    bool IsDuplex,
    string Currency,
    long QuotedAmountMinor,
    Guid? PrinterDeviceId,
    string PrinterName,
    string? QueueName,
    string DocumentName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset LeaseExpiresUtc,
    string StorageMode = "CLOUD",
    string OutputMode = PrinterOutputModeRules.Physical,
    string Orientation = "AUTO",
    bool PdfBrandingEnabled = false,
    bool PrivatePrinting = false,
    bool PrivatePurgePending = false);

public sealed record AgentJobClaimResponse(
    IReadOnlyList<AgentClaimedJob> Jobs,
    DateTimeOffset ClaimedUtc);

public sealed record AgentJobReceivedRequest(string Sha256, long SizeBytes);

public sealed record LocalPrintUploadCompletionRequest(
    int PageCount,
    string PageSizesJson,
    string FileExtension,
    string MediaType,
    long SizeBytes,
    string Sha256);

public sealed record AgentLocalQueueEntry(
    Guid JobId,
    string JobNumber,
    Guid? PrinterDeviceId,
    string PrinterName,
    string? QueueName,
    string DocumentName,
    string MediaType,
    string Sha256,
    long SizeBytes,
    int PageCount,
    int Copies,
    string ColourMode,
    string PaperSize,
    bool IsDuplex,
    string Currency,
    long QuotedAmountMinor,
    string DocumentPath,
    string State,
    DateTimeOffset ReceivedUtc,
    DateTimeOffset LeaseExpiresUtc,
    int? SpoolerJobId = null,
    string? SpoolerJobName = null,
    DateTimeOffset? SpoolerSubmittedUtc = null,
    string? SpoolerStatus = null,
    string? FailureCode = null,
    string OutputMode = PrinterOutputModeRules.Physical,
    string? OutputFilePath = null,
    string? OutputFileName = null,
    string? OutputFileSha256 = null,
    long? OutputFileSizeBytes = null,
    IReadOnlyList<int>? RejectedPageNumbers = null,
    DateTimeOffset? CompletedUtc = null,
    string Orientation = "AUTO",
    bool PdfBrandingEnabled = false,
    bool PrivatePrinting = false);

public sealed record AgentLocalQueueProof(Guid JobId, string Sha256, long SizeBytes);
public sealed record AgentPrintClaimRequest(IReadOnlyList<AgentLocalQueueProof> LocalJobs);
public sealed record AgentPrintClaimResponse(IReadOnlyList<AgentPrintClaimedJob> Jobs, DateTimeOffset ClaimedUtc);
public sealed record AgentPrintClaimedJob(Guid Id, string JobNumber, string Status, string QueueName, string SpoolerJobName, DateTimeOffset LeaseExpiresUtc, string OutputMode = PrinterOutputModeRules.Physical, IReadOnlyList<int>? RejectedPageNumbers = null);
public sealed record AgentPdfExportedRequest(string FileName, string OutputPath, long SizeBytes, string Sha256);
public sealed record PdfExportReceipt(string FileName, string OutputPath, long SizeBytes, string Sha256);
public sealed record AgentSpoolerAcceptedRequest(int SpoolerJobId, string SpoolerJobName, string SpoolerStatus);
public sealed record AgentPrintBlockedRequest(string ReasonCode, string Message);
public sealed record AgentSpoolerStatusRequest(int SpoolerJobId, string SpoolerStatus, int PagesPrinted, int TotalPages);
public sealed record WindowsSpoolerJob(int JobId, string DocumentName, string Status, uint StatusFlags, int PagesPrinted, int TotalPages);

public sealed record AgentCloudSpoolerStatusResponse(Guid Id, string Status, bool? DuplicateRequest = null);
public sealed record AgentLocalUploadStatusResponse(
    bool Completed,
    DateTimeOffset ExpiresUtc,
    bool SafeToDelete = false,
    DateTimeOffset? DeleteAfterUtc = null);

public sealed record LocalAgentPhotoRevisionRequest(
    string ExpectedOriginalSha256,
    long ExpectedOriginalSizeBytes,
    string Sha256,
    long SizeBytes,
    int PageCount,
    string PageSizesJson,
    string PhotoSizeCode,
    int PhotoCount,
    int PhotosPerPage);

public sealed record LocalAgentPhotoRevisionAuthorizationResponse(
    Guid Id,
    string OriginalSha256,
    long OriginalSizeBytes,
    string NewSha256,
    long NewSizeBytes,
    string PhotoSizeCode,
    int PhotoCount,
    int PageCount,
    int PhotosPerPage,
    long PhotoUnitPriceMinor,
    long QuotedAmountMinor,
    int PricingVersion);
public static class AgentLocalQueueBatchSelector
{
    public static IReadOnlyList<T> SelectNext<T>(
        IReadOnlyList<T> items,
        ref int nextIndex,
        int maximumItems)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (maximumItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumItems));

        if (items.Count == 0)
        {
            nextIndex = 0;
            return Array.Empty<T>();
        }

        var startIndex = ((nextIndex % items.Count) + items.Count) % items.Count;
        var count = Math.Min(maximumItems, items.Count - startIndex);
        var selected = new T[count];
        for (var index = 0; index < count; index++)
        {
            selected[index] = items[startIndex + index];
        }

        nextIndex = startIndex + count;
        if (nextIndex >= items.Count)
            nextIndex = 0;
        return selected;
    }
}

