using System.Net;

namespace SmartPrint.Agent;

/// <summary>
/// Only document-validation responses are terminal. Readiness conflicts,
/// rate limits and service errors remain safe to retry with the same ticket.
/// </summary>
public static class PrivateRelayCompletionFailureRules
{
    public static bool IsPermanentDocumentRejection(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.UnprocessableEntity;

    public static string CustomerMessage(string? code) => code switch
    {
        "photo_sheet_layout_invalid" =>
            "The prepared Photo Studio sheet does not match its saved photo size or page layout. Reopen Photo Studio, prepare the sheet again, then upload it.",
        "photo_metadata_invalid" =>
            "The saved Photo Studio sheet is missing its print details. Refresh the Photo Studio library and prepare the sheet again.",
        "id_document_pdf_invalid" =>
            "The prepared ID sheet could not be verified. Reopen ID Studio and prepare it again.",
        "private_document_page_analysis_invalid" =>
            "The shop computer could not read the PDF page information. Prepare the document again and retry.",
        "private_document_validation_mismatch" =>
            "The shop computer could not verify the uploaded document. Select the saved document again and retry.",
        _ =>
            "The shop computer could not validate this PDF. Reopen the source in its editor, prepare it again, then upload it."
    };
}
