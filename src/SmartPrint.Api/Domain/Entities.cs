using Microsoft.AspNetCore.Identity;

namespace SmartPrint.Api.Domain;

public sealed class AppUser : IdentityUser<Guid>
{
    public Guid? TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = string.Empty;
    public string BusinessName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TenantStatusAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string PreviousStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlatformOperationalSettings
{
    public const int SingletonIdValue = 1;
    public const int DefaultTrialMaxMonthlyPrintJobs = 3000;

    public int Id { get; set; } = SingletonIdValue;
    public int TrialMaxMonthlyPrintJobs { get; set; } = DefaultTrialMaxMonthlyPrintJobs;
    public int CloudCompletedDocumentRetentionDays { get; set; } = 7;
    public int AgentCompletedDocumentRetentionDays { get; set; } = 7;
    public int AbandonedDocumentRetentionDays { get; set; } = 30;
    public int PendingLocalUploadRetentionDays { get; set; } = 30;
    public int ExpiredUploadTicketRetentionDays { get; set; } = 7;
    public int EmptyCustomerSessionRetentionDays { get; set; } = 90;
    public int StaleRefreshTokenRetentionDays { get; set; } = 30;
    public int ApiRetentionSweepHours { get; set; } = 1;
    public int AgentRetentionSweepHours { get; set; } = 1;
    public bool AllowPaidJobClaimsWhenSuspended { get; set; } = true;
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlatformOperationalSettingsAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ChangesJson { get; set; } = "{}";
    public string Reason { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ShopProfile
{
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string AccentColor { get; set; } = "#7662E8";
    public string Currency { get; set; } = "INR";
    public string TimeZone { get; set; } = "Asia/Kolkata";
    public bool WhiteLabelEnabled { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ShopPricingProfile
{
    public Guid TenantId { get; set; }
    public bool IsConfigured { get; set; }
    public long MonoPerSideMinor { get; set; }
    public long ColourPerSideMinor { get; set; }
    public long MonoDuplexPerSheetMinor { get; set; }
    public long ColourDuplexPerSheetMinor { get; set; }
    public long MinimumChargeMinor { get; set; }
    public long Photo35x45Minor { get; set; }
    public long Photo25x35Minor { get; set; }
    public long Photo2x2Minor { get; set; }
    public long Photo4x6Minor { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TenantSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? PlanVersionId { get; set; }
    public string PlanCode { get; set; } = "TRIAL";
    public string Status { get; set; } = "Trial";
    public string BillingInterval { get; set; } = "Trial";
    public DateTimeOffset PeriodStartUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset PeriodEndUtc { get; set; } = DateTimeOffset.UtcNow.AddDays(14);
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlatformSubscriptionAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? PreviousSubscriptionId { get; set; }
    public string? PreviousStatus { get; set; }
    public Guid AssignedSubscriptionId { get; set; }
    public Guid PlanVersionId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public string BillingInterval { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "INR";
    public DateTimeOffset PeriodStartUtc { get; set; }
    public DateTimeOffset PeriodEndUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? PaymentReference { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ShopSubscriptionPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid PlanVersionId { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public string BillingInterval { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "INR";
    public string ClientRequestId { get; set; } = string.Empty;
    public string RazorpayOrderId { get; set; } = string.Empty;
    public string? RazorpayPaymentId { get; set; }
    public string Status { get; set; } = "CREATED";
    public Guid? AssignedSubscriptionId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedUtc { get; set; }
}

public sealed class RazorpayWebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? RazorpayOrderId { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public string PayloadSha256 { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedUtc { get; set; }
}

public sealed class SubscriptionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystemPlan { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SubscriptionPlanVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = "Draft";
    public string Currency { get; set; } = "INR";
    public long? MonthlyPriceMinor { get; set; }
    public long? AnnualPriceMinor { get; set; }
    public int? MaxPrinters { get; set; }
    public int? MaxAgentDevices { get; set; }
    public int? MaxQrStations { get; set; }
    public int? MaxMonthlyPrintJobs { get; set; }
    public long? MaxDocumentBytes { get; set; }
    public bool CloudAssistedPrintingEnabled { get; set; }
    public bool WhiteLabelEnabled { get; set; }
    public bool AdvancedReportsEnabled { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedUtc { get; set; }
}

public static class SubscriptionPlanDefaults
{
    public static readonly Guid TrialPlanId = Guid.Parse("e0af4c2a-82b8-4bd1-9bc4-cb3bffb51001");
    public static readonly Guid TrialVersionId = Guid.Parse("f2b849a1-1be8-4a88-a6c5-7e8e80d0c002");
}

public sealed class PrinterDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? AgentDeviceId { get; set; }
    public bool IsDefault { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ConnectionType { get; set; } = "WindowsQueue";
    public string OutputMode { get; set; } = "PHYSICAL";
    public string? ConnectionAddress { get; set;}
    public string? QueueName { get; set; }
    public string CapabilitiesJson { get; set; } = "{}";
    public string Status { get; set; } = "Unknown";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class QrStation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? PrinterDeviceId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PublicCodeHash { get; set; } = string.Empty;
    // Public station URLs are printed and shared; retain their code so the same QR renders after reload.
    public string? PublicCode { get; set; }
    public string PublicCodeHint { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CustomerPrintSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid QrStationId { get; set; }
    public Guid? CustomerUserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(15);
    public DateTimeOffset? RevokedUtc { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddDays(30);
    public DateTimeOffset? RevokedUtc { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}

public sealed class PrintJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? QrStationId { get; set; }
    // Shop-originated orders have no customer QR session. They remain tenant-scoped
    // PrintJobs and use the same approval, payment and dispatch state machine.
    public Guid? CustomerSessionId { get; set; }
    public Guid? CustomerUserId { get; set; }
    public string Origin { get; set; } = "CUSTOMER_QR";
    public bool PrivatePrinting { get; set; }
    public DateTimeOffset? PrivateDocumentPurgeRequestedUtc { get; set; }
    public DateTimeOffset? PrivateDocumentPurgedUtc { get; set; }
    public string? RecipientName { get; set; }
    public string? RecipientPhone { get; set; }
    public Guid? PrinterDeviceId { get; set; }
    public string? PrinterNameSnapshot { get; set; }
    public string? PrinterQueueNameSnapshot { get; set; }
    public string? PrinterAgentNameSnapshot { get; set; }
    public DateTimeOffset? PrinterSnapshotUtc { get; set; }
    public Guid? ClaimedByAgentDeviceId { get; set; }
    public Guid? BatchId { get; set; }
    public string? BatchGroupName { get; set; }
    public string? BatchName { get; set; }
    public string RejectedPageNumbersJson { get; set; } = "[]";
    public string PageRefundAllocationsJson { get; set; } = "[]";
    public string JobNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Uploaded";
    public string IdempotencyKey { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public bool PdfBrandingEnabled { get; set; }
    public int Copies { get; set; } = 1;
    public string ColourMode { get; set; } = "Mono";
    public string PaperSize { get; set; } = "A4";
    public bool IsDuplex { get; set; }
    public string Orientation { get; set; } = "AUTO";
    public string PrintKind { get; set; } = "DOCUMENT";
    public string? PhotoSizeCode { get; set; }
    public int PhotoCount { get; set; }
    // The selected sheet density is part of the immutable photo quote/layout snapshot.
    // Nullable for legacy PHOTO jobs created before this column existed.
    public int? PhotosPerPage { get; set; }
    public long PhotoUnitPriceMinorSnapshot { get; set; }
    public int? PricingVersion { get; set; }
    public DateTimeOffset? QuoteExpiresUtc { get; set; }
    public string Currency { get; set; } = "INR";
    public long QuotedAmountMinor { get; set; }
    public DateTimeOffset? AgentLeaseExpiresUtc { get; set; }
    public DateTimeOffset? AgentClaimedUtc { get; set; }
    public DateTimeOffset? LocalQueueReceivedUtc { get; set; }
    public int AgentDispatchAttempts { get; set; }
    public DateTimeOffset? PrintDispatchLeaseExpiresUtc { get; set; }
    public int PrintDispatchAttempts { get; set; }
    public int? SpoolerJobId { get; set; }
    public string? SpoolerJobName { get; set; }
    public string? SpoolerStatus { get; set; }
    public DateTimeOffset? SpoolerSubmittedUtc { get; set; }
    public DateTimeOffset? SpoolerObservedUtc { get; set; }
    public DateTimeOffset? OutputConfirmedUtc { get; set; }
    public string? FailureCode { get; set; }
    public string OutputType { get; set; } = "PHYSICAL";
    public string? OutputFilePath { get; set; }
    public string? OutputFileName { get; set; }
    public string? OutputSha256 { get; set; }
    public long? OutputSizeBytes { get; set; }
    public DateTimeOffset? OutputCompletedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PrintDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PrintJobId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string StorageMode { get; set; } = "CLOUD";
    public Guid? AgentDeviceId { get; set; }
    public string PageSizesJson { get; set; } = "[]";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PurgedUtc { get; set; }
}

public sealed class LocalPrintUploadTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool PrivatePrinting { get; set; }
    public string PrintKind { get; set; } = "DOCUMENT";
    public string? PhotoSizeCode { get; set; }
    public int? PhotoCount { get; set; }
    public int? PhotosPerPage { get; set; }
    public Guid TenantId { get; set; }
    public Guid CustomerSessionId { get; set; }
    public Guid? BatchId { get; set; }
    public string? BatchGroupName { get; set; }
    public string? BatchName { get; set; }
    public Guid QrStationId { get; set; }
    public Guid PrinterDeviceId { get; set; }
    public Guid AgentDeviceId { get; set; }
    public Guid PrintJobId { get; set; } = Guid.NewGuid();
    public string JobNumber { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string GrantHash { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string ColourMode { get; set; } = "Mono";
    public string PaperSize { get; set; } = "A4";
    public bool IsDuplex { get; set; }
    public int Copies { get; set; } = 1;
    public string Orientation { get; set; } = "AUTO";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);
    public DateTimeOffset? ClaimedUtc { get; set; }
    public DateTimeOffset? ClaimLeaseExpiresUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? PrivateRelayStorageKey { get; set; }
    public string? PrivateRelayWrappedKey { get; set; }
    public string? PrivateRelayIv { get; set; }
    public DateTimeOffset? PrivateRelayUploadedUtc { get; set; }
    public DateTimeOffset? PrivateRelayFailedUtc { get; set; }
    public string? PrivateRelayFailureCode { get; set; }
    public string? PrivateRelayFailureMessage { get; set; }
}


public sealed class PrintJobEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PrintJobId { get; set; }
    public string? PreviousStatus { get; set; }
    public string NewStatus { get; set; } = string.Empty;
    public string ActorType { get; set; } = string.Empty;
    public Guid? ActorId { get; set; }
    public string? ReasonCode { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}


public sealed class AgentDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string FingerprintHash { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public string LocalEndpointsJson { get; set; } = "[]";
    public string? DocumentEncryptionPublicKey { get; set; }
    public string Status { get; set; } = "Online";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
}

public sealed class AgentEnrollmentCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);
    public DateTimeOffset? UsedUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
}


public sealed class ShopPaymentProfile
{
    public Guid TenantId { get; set; }
    public bool UpiEnabled { get; set; }
    public string? UpiId { get; set; }
    public string? PayeeName { get; set; }
    public bool CashEnabled { get; set; }
    public bool InstantPaymentEnabled { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PrintRefund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PrintJobId { get; set; }
    public Guid PrintPaymentId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "INR";
    public string Method { get; set; } = string.Empty;
    public string Status { get; set; } = "RECORDED_AS_SENT";
    public string? Reference { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid RefundedByUserId { get; set; }
    public DateTimeOffset RefundedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PrintBatchPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CustomerSessionId { get; set; }
    public Guid BatchId { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "INR";
    public string? UpiIdSnapshot { get; set; }
    public string? PayeeNameSnapshot { get; set; }
    public string? PaymentReference { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public DateTimeOffset? VerifiedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PrintPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PrintJobId { get; set; }
    public Guid? BatchPaymentId { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public long RefundedAmountMinor { get; set; }
    public string Currency { get; set; } = "INR";
    public string? UpiIdSnapshot { get; set; }
    public string? PayeeNameSnapshot { get; set; }
    public string? PaymentReference { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public DateTimeOffset? VerifiedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
