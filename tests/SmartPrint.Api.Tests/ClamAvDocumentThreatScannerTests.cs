using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Security;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class ClamAvDocumentThreatScannerTests
{
    private static readonly byte[] EicarTestSample = Encoding.ASCII.GetBytes(
        @"X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    [Fact]
    public async Task SendsNulTerminatedInstreamChunksAndAcceptsOnlyAnExplicitCleanResult()
    {
        var sample = RandomNumberGenerator.GetBytes(150_000);
        var (scanner, server) = await StartScannerAsync("stream: OK");

        var scan = scanner.ScanAsync(sample, CancellationToken.None);
        var received = await server;
        await scan;

        Assert.Equal(sample, received);
    }

    [Fact]
    public async Task RejectsTheEicarTestSignatureReportedByClamAv()
    {
        var (scanner, server) = await StartScannerAsync("stream: Eicar-Test-Signature FOUND");
        var scan = scanner.ScanAsync(EicarTestSample, CancellationToken.None);

        var received = await server;
        var exception = await Assert.ThrowsAsync<DocumentThreatScanException>(() => scan);

        Assert.Equal(EicarTestSample, received);
        Assert.True(exception.ThreatDetected);
    }

    [Fact]
    public async Task FailsClosedWhenClamAvReturnsAnError()
    {
        var (scanner, server) = await StartScannerAsync("stream: INSTREAM size limit exceeded. ERROR");
        var scan = scanner.ScanAsync(EicarTestSample, CancellationToken.None);

        await server;
        var exception = await Assert.ThrowsAsync<DocumentThreatScanException>(() => scan);

        Assert.False(exception.ThreatDetected);
    }

    [Fact]
    public async Task FailsClosedWhenClamAvCannotBeReached()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var scanner = CreateScanner(port);

        var exception = await Assert.ThrowsAsync<DocumentThreatScanException>(
            () => scanner.ScanAsync(EicarTestSample, CancellationToken.None));

        Assert.False(exception.ThreatDetected);
    }

    [Fact]
    public async Task RejectsOversizedDocumentsBeforeOpeningANetworkConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var scanner = CreateScanner(port);

        var exception = await Assert.ThrowsAsync<DocumentThreatScanException>(
            () => scanner.ScanAsync(new byte[20 * 1024 * 1024 + 1], CancellationToken.None));

        Assert.False(exception.ThreatDetected);
        listener.Stop();
    }

    private static async Task<(ClamAvDocumentThreatScanner Scanner, Task<byte[]> Server)> StartScannerAsync(
        string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using (listener)
            using (var client = await listener.AcceptTcpClientAsync())
            await using (var stream = client.GetStream())
            {
                var command = await ReadNullTerminatedAsync(stream);
                Assert.Equal("zINSTREAM", command);

                using var received = new MemoryStream();
                var header = new byte[sizeof(uint)];
                while (true)
                {
                    await stream.ReadExactlyAsync(header);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (length == 0) break;
                    if (length > 1024 * 1024) throw new InvalidDataException("Unexpected oversized ClamAV frame.");
                    var chunk = new byte[checked((int)length)];
                    await stream.ReadExactlyAsync(chunk);
                    await received.WriteAsync(chunk);
                }

                await stream.WriteAsync(Encoding.UTF8.GetBytes(response + "\0"));
                await stream.FlushAsync();
                return received.ToArray();
            }
        });

        return (CreateScanner(port), server);
    }

    private static async Task<string> ReadNullTerminatedAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) throw new EndOfStreamException();
            if (buffer[0] == 0) return Encoding.ASCII.GetString(bytes.ToArray());
            bytes.Add(buffer[0]);
        }
    }

    private static ClamAvDocumentThreatScanner CreateScanner(int port)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DocumentSecurity:ClamAv:Host"] = "127.0.0.1",
                ["DocumentSecurity:ClamAv:Port"] = port.ToString(),
                ["DocumentSecurity:ClamAv:TimeoutSeconds"] = "10"
            })
            .Build();
        return new ClamAvDocumentThreatScanner(configuration);
    }
}
