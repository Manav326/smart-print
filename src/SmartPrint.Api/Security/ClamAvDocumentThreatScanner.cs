using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using SmartPrint.Documents;

namespace SmartPrint.Api.Security;

/// <summary>
/// Fail-closed ClamAV client using clamd's bounded INSTREAM protocol.
/// The scanner is reachable only on the private Compose network.
/// </summary>
public sealed class ClamAvDocumentThreatScanner : IDocumentThreatScanner
{
    private const int MaximumDocumentBytes = 20 * 1024 * 1024;
    private const int MaximumResponseBytes = 4096;
    private const int ChunkBytes = 64 * 1024;
    private readonly string host;
    private readonly int port;
    private readonly TimeSpan timeout;

    public ClamAvDocumentThreatScanner(IConfiguration configuration)
    {
        host = configuration["DocumentSecurity:ClamAv:Host"]?.Trim() ?? string.Empty;
        port = configuration.GetValue<int?>("DocumentSecurity:ClamAv:Port") ?? 3310;
        var timeoutSeconds = configuration.GetValue<int?>("DocumentSecurity:ClamAv:TimeoutSeconds") ?? 90;

        if (string.IsNullOrWhiteSpace(host) ||
            host.Contains('/') ||
            port is < 1 or > 65535 ||
            timeoutSeconds is < 5 or > 300)
        {
            throw new InvalidOperationException(
                "Configure a valid private ClamAV host, port and timeout before accepting documents.");
        }

        timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    public async Task ScanAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken)
    {
        if (document.IsEmpty || document.Length > MaximumDocumentBytes)
        {
            throw new DocumentThreatScanException(
                "The document is outside the supported security-scanning size range.",
                threatDetected: false);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, token);
            await using var stream = client.GetStream();

            // clamd's z-prefixed INSTREAM command is NUL terminated.
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), token);

            var header = new byte[sizeof(uint)];
            for (var offset = 0; offset < document.Length; offset += ChunkBytes)
            {
                var length = Math.Min(ChunkBytes, document.Length - offset);
                BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)length));
                await stream.WriteAsync(header, token);
                await stream.WriteAsync(document.Slice(offset, length), token);
            }

            // A zero-length chunk terminates the stream.
            BinaryPrimitives.WriteUInt32BigEndian(header, 0);
            await stream.WriteAsync(header, token);
            await stream.FlushAsync(token);

            var response = await ReadResponseAsync(stream, token);
            if (response.EndsWith(" OK", StringComparison.Ordinal))
            {
                return;
            }

            if (response.Contains(" FOUND", StringComparison.Ordinal))
            {
                throw new DocumentThreatScanException(
                    "The document was rejected by the malware scanner.",
                    threatDetected: true);
            }

            throw new DocumentThreatScanException(
                "The malware scanner could not complete the document scan. The upload was blocked.",
                threatDetected: false);
        }
        catch (DocumentThreatScanException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DocumentThreatScanException(
                "The malware scanner is unavailable or timed out. The upload was blocked; please retry later.",
                threatDetected: false,
                innerException: exception);
        }
    }

    private static async Task<string> ReadResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var response = new List<byte>(128);
        var buffer = new byte[1];

        while (response.Count < MaximumResponseBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new IOException("ClamAV closed the scan response before returning a result.");
            }

            if (buffer[0] == 0)
            {
                return Encoding.UTF8.GetString(response.ToArray()).Trim();
            }

            response.Add(buffer[0]);
        }

        throw new IOException("ClamAV returned an oversized scan response.");
    }
}
