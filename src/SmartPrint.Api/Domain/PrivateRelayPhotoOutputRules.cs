namespace SmartPrint.Api.Domain;

/// <summary>
/// Carries the customer's selected Photo Studio output contract from the
/// private upload ticket onto the canonical print job.
/// </summary>
public static class PrivateRelayPhotoOutputRules
{
    public static void ApplyTicketOptions(PrintJob job, LocalPrintUploadTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(ticket);

        job.PhotoOutputLayout = ticket.PhotoOutputLayout;
        job.PhotoPaperFinish = ticket.PhotoPaperFinish;
    }
}
