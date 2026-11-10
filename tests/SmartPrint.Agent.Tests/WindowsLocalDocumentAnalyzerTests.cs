using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartPrint.Documents;

namespace SmartPrint.Agent.Tests;

public sealed class WindowsLocalDocumentAnalyzerTests
{
    [Fact]
    public async Task SendsTheDocumentOverTheNamedPipeAndReturnsOnlyAnalysisMetadata()
    {
        var pipeName = "SmartPrintDocumentWorkerTest" + Guid.NewGuid().ToString("N");
        var bytes = "synthetic PDF bytes"u8.ToArray();
        var expected = new DocumentAnalysis(1, "[{\"widthPoints\":595,\"heightPoints\":842}]", "application/pdf", ".pdf");

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var lengthBytes = new byte[sizeof(int)];
            await server.ReadExactlyAsync(lengthBytes);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            var extensionLength = server.ReadByte();
            Assert.Equal(bytes.Length, length);
            Assert.Equal(4, extensionLength);

            var extension = new byte[extensionLength];
            await server.ReadExactlyAsync(extension);
            var received = new byte[length];
            await server.ReadExactlyAsync(received);
            Assert.Equal(".pdf", System.Text.Encoding.ASCII.GetString(extension));
            Assert.Equal(bytes, received);

            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Success = true,
                Analysis = expected,
                Error = (string?)null
            });
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, response.Length);
            await server.WriteAsync(lengthBytes);
            await server.WriteAsync(response);
            await server.FlushAsync();
        });

        var analyzer = CreateAnalyzer(pipeName);
        var result = await analyzer.AnalyzeAsync(bytes, ".pdf", CancellationToken.None);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ConvertsWorkerValidationRejectionIntoDocumentValidationException()
    {
        var pipeName = "SmartPrintDocumentWorkerTest" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var header = new byte[sizeof(int)];
            await server.ReadExactlyAsync(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            var extensionLength = server.ReadByte();
            var discard = new byte[length + extensionLength];
            await server.ReadExactlyAsync(discard);

            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Success = false,
                Analysis = (DocumentAnalysis?)null,
                Error = "The PDF is damaged."
            });
            BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
            await server.WriteAsync(header);
            await server.WriteAsync(response);
            await server.FlushAsync();
        });

        var analyzer = CreateAnalyzer(pipeName);
        var exception = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            analyzer.AnalyzeAsync("%PDF-test"u8.ToArray(), ".pdf", CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("The PDF is damaged.", exception.Message);
    }

    [Fact]
    public async Task FailsClosedWhenTheLocalWorkerPipeIsUnavailable()
    {
        var analyzer = CreateAnalyzer("SmartPrintDocumentWorkerMissing" + Guid.NewGuid().ToString("N"));

        var exception = await Assert.ThrowsAsync<DocumentProcessingException>(() =>
            analyzer.AnalyzeAsync("%PDF-test"u8.ToArray(), ".pdf", CancellationToken.None));

        Assert.Contains("unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".docx")]
    public async Task RejectsUnsupportedExtensionsBeforeOpeningThePipe(string extension)
    {
        var analyzer = CreateAnalyzer("SmartPrintDocumentWorkerUnused" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            analyzer.AnalyzeAsync("sample"u8.ToArray(), extension, CancellationToken.None));
    }

    private sealed class TestWorkerServiceStarter : ILocalDocumentWorkerServiceStarter
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static WindowsLocalDocumentAnalyzer CreateAnalyzer(string pipeName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:DocumentWorkerPipeName"] = pipeName,
                ["Agent:DocumentWorkerTimeoutSeconds"] = "10"
            })
            .Build();
        return new WindowsLocalDocumentAnalyzer(configuration, new TestWorkerServiceStarter());
    }
}
