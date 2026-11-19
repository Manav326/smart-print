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
        Rank(requestedStage) >= Rank(currentStage) &&
        currentStage is not (Ready or Failed or Expired);

    public static string CustomerMessage(string stage) => stage switch
    {
        WaitingForUpload => "Waiting for the encrypted file transfer to start",
        ReceivingEncryptedPayload => "Securely receiving your encrypted file",
        WaitingForAgent => "Encrypted file received · waiting for the shop computer",
        DownloadingEncryptedPayload => "Shop computer is receiving the encrypted file",
        DecryptingAndAuthenticating => "Shop computer is decrypting and authenticating the file",
        ValidatingDocument => "Checking the document format, pages and integrity",
        SecuringLocalQueue => "Securing the validated file in the shop's local print queue",
        Ready => "Ready at the shop · the file is safely in the local print queue",
        Failed => "The shop computer could not validate this file",
        Expired => "The secure upload expired before it reached the shop queue",
        _ => "Your encrypted file is being prepared"
    };
}
