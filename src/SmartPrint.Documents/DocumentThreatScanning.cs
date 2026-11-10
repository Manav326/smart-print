namespace SmartPrint.Documents;

public interface IDocumentThreatScanner
{
    Task ScanAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken);
}

public class DocumentValidationException(string message) : Exception(message);

public sealed class DocumentThreatScanException(
    string message,
    bool threatDetected,
    Exception? innerException = null)
    : DocumentValidationException(message)
{
    public bool ThreatDetected { get; } = threatDetected;
}
