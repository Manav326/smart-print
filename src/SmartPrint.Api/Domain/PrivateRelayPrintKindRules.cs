namespace SmartPrint.Api.Domain;

public static class PrivateRelayPrintKindRules
{
    /// <summary>
    /// Maps the upload-ticket type to the persisted PrintJob type.
    /// ID_DOCUMENT is a ticket-only discriminator used for stricter PDF
    /// validation; the resulting print job is still a normal DOCUMENT job.
    /// </summary>
    public static bool TryMapTicketKindToPrintJobKind(
        string? ticketPrintKind,
        out string printJobKind)
    {
        printJobKind = ticketPrintKind switch
        {
            "DOCUMENT" or "ID_DOCUMENT" => "DOCUMENT",
            "PHOTO" => "PHOTO",
            _ => string.Empty
        };

        return printJobKind.Length > 0;
    }
}
