namespace SmartPrint.Api.Domain;

/// <summary>
/// Defines the monotonic, durable processing lifecycle for end-to-end encrypted
/// customer uploads. These values contain no document content or personal data.
/// </summary>
public static class PrivateRelayProgressRules
{
    public const string WaitingForUpload = "WAITING_FOR_UPLOAD";
    public const string ReceivingEncryptedPayload = "RECEIVING_ENCRYPTED_PAYLOAD";
    public const string WaitingForAgent = "WAITING_FOR_AGENT";
    public const string DownloadingEncryptedPayload = "DOWNLOADING_ENCRYPTED_PAYLOAD";
    public const string DecryptingAndAuthenticating = "DECRYPTING_AND_AUTHENTICATING";
    public const string ValidatingDocument = "VALIDATING_DOCUMENT";
    public const string SecuringLocalQueue = "SECURING_LOCAL_QUEUE";
    public const string Ready = "READY";
    public const string Failed = "FAILED";
    public const string Expired = "EXPIRED";

    public static int Rank(string stage) => stage switch
    {
        WaitingForUpload => 0,
        ReceivingEncryptedPayload => 1,
        WaitingForAgent => 2,
        DownloadingEncryptedPayload => 3,
        DecryptingAndAuthenticating => 4,
        ValidatingDocument => 5,
        SecuringLocalQueue => 6,
        Ready => 7,
        Failed or Expired => 8,
        _ => -1
    };

    public static bool IsAgentStage(string stage) => stage is
        DownloadingEncryptedPayload or
        DecryptingAndAuthenticating or
        ValidatingDocument or
        SecuringLocalQueue;

    public static bool CanAdvance(string currentStage, string requestedStage) =>
        IsAgentStage(requestedStage) &&
        Rank(currentStage) >= 0 &&
        Rank(requestedStage) > Rank(currentStage) &&
        currentStage is not (Ready or Failed or Expired);

    public static string CustomerMessage(string stage) => stage switch
    {
        WaitingForUpload => "Waiting to send your secure file",
        ReceivingEncryptedPayload => "Securely sending your file to the shop",
        WaitingForAgent => "File received · waiting for the shop computer",
        DownloadingEncryptedPayload => "Shop computer is receiving your file",
        DecryptingAndAuthenticating => "Checking file security",
        ValidatingDocument => "Checking that the file can be printed",
        SecuringLocalQueue => "Saving your file securely on the shop computer",
        Ready => "Secure upload confirmed · awaiting shop review",
        Failed => "The shop computer could not verify this file",
        Expired => "The secure upload expired before the file reached the print queue",
        _ => "Your secure file is being prepared"
    };
}
