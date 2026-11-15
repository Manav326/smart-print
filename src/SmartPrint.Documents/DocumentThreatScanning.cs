namespace SmartPrint.Documents;

public interface IDocumentThreatScanner
{
    Task ScanAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken);
}

public class DocumentValidationException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class DocumentThreatScanException(
    string message,
    bool threatDetected,
    Exception? innerException = null)
    : DocumentValidationException(message, innerException)
{
    public bool ThreatDetected { get; } = threatDetected;
}

public interface IDocumentAnalyzer
{
    Task<DocumentAnalysis> AnalyzeAsync(
        ReadOnlyMemory<byte> document,
        string extension,
        CancellationToken cancellationToken);
}

public sealed class DocumentProcessingException(string message, Exception? innerException = null)
    : Exception(message, innerException);
