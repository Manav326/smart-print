using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartPrint.Documents;

namespace SmartPrint.Agent;

/// <summary>
/// The Agent never parses customer-supplied PDFs or images in its own process.
/// It sends bytes through a local named pipe to the LocalService document worker.
/// </summary>
public sealed class WindowsLocalDocumentAnalyzer
{
    public const string DefaultPipeName = "QRtoPrint.SmartPrint.DocumentWorker.v1";
    private const int MaximumDocumentBytes = 20 * 1024 * 1024;
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly string pipeName;
    private readonly TimeSpan timeout;
    private readonly ILocalDocumentWorkerServiceStarter serviceStarter;

    public WindowsLocalDocumentAnalyzer(
        IConfiguration configuration,
        ILocalDocumentWorkerServiceStarter serviceStarter)
    {
        pipeName = configuration["Agent:DocumentWorkerPipeName"]?.Trim() ?? DefaultPipeName;
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Contains('\\') || pipeName.Contains('/'))
        {
            throw new InvalidOperationException("The local document worker pipe name is invalid.");
        }

        var timeoutSeconds = configuration.GetValue<int?>("Agent:DocumentWorkerTimeoutSeconds") ?? 90;
        if (timeoutSeconds is < 5 or > 180)
        {
            throw new InvalidOperationException("The local document worker timeout is outside the supported range.");
        }

        timeout = TimeSpan.FromSeconds(timeoutSeconds);
        this.serviceStarter = serviceStarter;
    }

    public async Task<DocumentAnalysis> AnalyzeAsync(
        ReadOnlyMemory<byte> document,
        string extension,
        CancellationToken cancellationToken)
    {
        if (document.IsEmpty || document.Length > MaximumDocumentBytes)
        {
            throw new DocumentValidationException("The document is outside the supported processing size range.");
        }

        if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
        {
            throw new DocumentValidationException("The document extension is not supported.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;
        byte[]? responseBytes = null;

        try
        {
            // The LocalService parser is demand-started only for this upload path.
            // The Agent's print queue/worker does not wait for or depend on it.
            await serviceStarter.EnsureRunningAsync(token);

            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);

            await pipe.ConnectAsync(5000, token);

            var lengthBytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, document.Length);
            await pipe.WriteAsync(lengthBytes, token);
            await pipe.WriteAsync(new[] { checked((byte)extension.Length) }, token);
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(extension), token);
            await pipe.WriteAsync(document, token);
            await pipe.FlushAsync(token);

            await pipe.ReadExactlyAsync(lengthBytes, token);
            var responseLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (responseLength is <= 0 or > MaximumResponseBytes)
            {
                throw new DocumentProcessingException("The isolated document worker returned an invalid response. Upload was blocked.");
            }

            responseBytes = new byte[responseLength];
            await pipe.ReadExactlyAsync(responseBytes, token);
            var response = JsonSerializer.Deserialize<WorkerResponse>(responseBytes);
            if (response is null)
            {
                throw new DocumentProcessingException("The isolated document worker returned an empty response. Upload was blocked.");
            }

            if (response.ProtocolVersion != 1)
            {
                throw new DocumentProcessingException(
                    "The local document parser version is not compatible. Normal printing remains available; this upload was blocked.");
            }

            if (!response.Success)
            {
                throw new DocumentValidationException(
                    string.IsNullOrWhiteSpace(response.Error)
                        ? "The document could not be analysed safely."
                        : response.Error);
            }

            return response.Analysis ?? throw new DocumentProcessingException(
                "The isolated document worker returned no analysis. Upload was blocked.");
        }
        catch (DocumentValidationException)
        {
            throw;
        }
        catch (DocumentProcessingException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new DocumentProcessingException(
                "The isolated local document parser timed out. The upload was blocked.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            throw new DocumentProcessingException(
                "The isolated local document processor is unavailable or returned an invalid response. The upload was blocked.",
                exception);
        }
        finally
        {
            if (responseBytes is not null)
            {
                CryptographicOperations.ZeroMemory(responseBytes);
            }
        }
    }

    private sealed record WorkerResponse(int ProtocolVersion, bool Success, DocumentAnalysis? Analysis, string? Error);
}
