namespace SmartPrint.Api.Contracts;

public sealed record RegisterShopRequest(
    string BusinessName,
    string OwnerName,
    string Email,
    string Password);

public sealed record LoginRequest(string Email, string Password);
public sealed record RegisterCustomerRequest(string DisplayName, string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ChangeTenantStatusRequest(string Status, string Reason);
public sealed record ShopProfileRequest(string DisplayName, string? LogoUrl, string AccentColor);
public sealed record ShopOwnerAccountRequest(string OwnerName, string Email, string? CurrentPassword);

public sealed record CreatePrinterRequest(
    string DisplayName,
    string Manufacturer,
    string Model,
    string ConnectionType,
    string? ConnectionAddress,
    string? QueueName,
    string? CapabilitiesJson);

public sealed record CreateQrStationRequest(string DisplayName, Guid? PrinterDeviceId);

public sealed record ShopPricingRequest(
    long MonoPerSideMinor,
    long ColourPerSideMinor,
    long MonoDuplexPerSheetMinor,
    long ColourDuplexPerSheetMinor,
    long MinimumChargeMinor,
    long Photo35x45Minor = 0,
    long Photo25x35Minor = 0,
    long Photo2x2Minor = 0,
    long Photo4x6Minor = 0);

public sealed record PrintQuoteRequest(string ColourMode, string PaperSize, bool IsDuplex, int Copies, string? PrintKind = "DOCUMENT", string? PhotoSizeCode = null, int? PhotoCount = null, string Orientation = "AUTO", int? PhotosPerPage = null);

public sealed record UpdatePrintJobSettingsRequest(
    string ColourMode,
    string PaperSize,
    bool IsDuplex,
    int Copies,
    string Orientation = "AUTO");

public sealed record TokenResponse(
    string TokenType,
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresUtc);

public sealed record RegisterShopResponse(
    Guid TenantId,
    string TenantSlug,
    string BusinessName,
    string SubscriptionStatus,
    TokenResponse Tokens);


public sealed record CreateAgentEnrollmentCodeResponse(string Code, DateTimeOffset ExpiresUtc);

public sealed record AgentEnrollmentRequest(string Code, string Fingerprint, string DeviceName);

public sealed record AgentEnrollmentResponse(Guid AgentDeviceId, string AgentSecret, DateTimeOffset CreatedUtc);

public sealed record AgentHeartbeatRequest(
    string Version,
    string OperatingSystem,
    IReadOnlyList<string>? LocalEndpoints = null,
    string? DocumentEncryptionPublicKey = null);

public sealed record AgentPrinterDiscoveryRequest(
    string QueueName,
    string DriverName,
    string PortName,
    string Status,
    bool IsDefault,
    string OutputMode = "PHYSICAL",
    string CapabilitiesJson = "{}");

public sealed record AgentPrinterSyncRequest(IReadOnlyList<AgentPrinterDiscoveryRequest> Printers);


public sealed record ShopPaymentSettingsRequest(
    bool UpiEnabled,
    string? UpiId,
    string? PayeeName,
    bool CashEnabled,
    bool InstantPaymentEnabled = false);

public sealed record StartPrintPaymentRequest(string Method);

public sealed record ReportPrintPaymentRequest(string ReferenceNumber);

public sealed record RejectPrintPaymentRequest(string Reason);
public sealed record ApplyPrintBatchReviewRequest(
    IReadOnlyList<Guid> ReviewedJobIds,
    IReadOnlyList<Guid> AcceptedJobIds,
    bool PdfBrandingEnabled = false);


public sealed record ResumeCustomerSessionRequest(string PublicCode);


public sealed record AssignPrintJobPrinterRequest(Guid PrinterDeviceId);

public sealed record CreateLocalPrintUploadTicketRequest(
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    string GrantToken,
    Guid? BatchId = null,
    string? BatchGroupName = null,
    string? BatchName = null,
    string ColourMode = "Mono",
    string PaperSize = "A4",
    bool IsDuplex = false,
    int Copies = 1,
    string Orientation = "AUTO",
    bool PrivatePrinting = false,
    string PrintKind = "DOCUMENT",
    string? PhotoSizeCode = null,
    int? PhotoCount = null,
    int? PhotosPerPage = null,
    string? DetectedFileExtension = null);

public sealed record RecordPrintRefundRequest(
    string RefundMethod,
    string? RefundReference,
    string Reason,
    bool ConfirmedSent,
    string? IdempotencyKey = null,
    IReadOnlyList<int>? RejectedPageNumbers = null);

public sealed record PrintRefundPreviewRequest(IReadOnlyList<int> RejectedPageNumbers);

public sealed record LocalPrintUploadCompletionRequest(
    int PageCount,
    string PageSizesJson,
    string FileExtension,
    string MediaType,
    long SizeBytes,
    string Sha256);

public sealed record AgentJobReceivedRequest(string Sha256, long SizeBytes);


public sealed record AgentLocalQueueProof(Guid JobId, string Sha256, long SizeBytes);
public sealed record AgentPrintClaimRequest(IReadOnlyList<AgentLocalQueueProof> LocalJobs);
public sealed record AgentSpoolerAcceptedRequest(int SpoolerJobId, string SpoolerJobName, string SpoolerStatus);
public sealed record AgentPrintBlockedRequest(string ReasonCode, string Message);
public sealed record AgentSpoolerStatusRequest(int SpoolerJobId, string SpoolerStatus, int PagesPrinted, int TotalPages);


public sealed record AgentPdfExportedRequest(string FileName, string OutputPath, long SizeBytes, string Sha256);

public sealed record AgentPrivateRelayRejectRequest(string Code, string Message);

public sealed record AgentPrivateRelayItem(
    Guid TicketId, Guid PrintJobId, string JobNumber, Guid PrinterDeviceId,
    string PrinterName, string QueueName, string FileExtension, string MediaType,
    long SizeBytes, string Sha256, string ColourMode, string PaperSize,
    bool IsDuplex, int Copies, string Orientation, DateTimeOffset ExpiresUtc,
    string PrintKind = "DOCUMENT", string OutputMode = "PHYSICAL");

public sealed record AgentPrivateTestOutputRequest(string FileName, long SizeBytes, string Sha256);


public sealed record AgentPrivateRelayPendingResponse(IReadOnlyList<AgentPrivateRelayItem> Items);
