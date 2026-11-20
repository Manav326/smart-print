namespace SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

/// <summary>
/// Restricts Agent rejection reports to known document-processing failures.
/// The message is bounded before it is persisted on the durable ticket.
/// </summary>
public static class AgentPrivateRelayRejectionRules
{
    public static bool IsValidRequest(string? code, string? message) =>
        (code is
            "document_not_supported" or
            "document_content_type_mismatch" or
            "private_document_encryption_required" or
            "private_document_integrity_failed" or
            "private_document_decryption_failed") &&
        !string.IsNullOrWhiteSpace(message) &&
        message.Length <= 400;
}
