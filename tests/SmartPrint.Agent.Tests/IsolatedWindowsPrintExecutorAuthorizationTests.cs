using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class IsolatedWindowsPrintExecutorAuthorizationTests
{
    [Fact]
    public async Task PrintWorkerRequestsFreshParentAuthorisationBeforeReturningAReceipt()
    {
        var runner = new AuthorisationAwareChildRunner(allow: true);
        var executor = new IsolatedWindowsPrintExecutor(
            NullLogger<IsolatedWindowsPrintExecutor>.Instance,
            childProcessRunner: runner);
        var callbackCount = 0;

        var receipt = await executor.PrintAsync(
            Entry(), Guid.NewGuid(), CancellationToken.None,
            _ =>
            {
                callbackCount++;
                return Task.FromResult(true);
            });

        Assert.Equal(1, callbackCount);
        Assert.Equal("--print-job", runner.Command);
        Assert.Equal(481, receipt.JobId);
        Assert.Equal("VIRTUAL-TEST-SP-481", receipt.DocumentName);
    }

    [Fact]
    public async Task DeniedOrUnavailableAuthorisationNeverReturnsASpoolerReceipt()
    {
        var runner = new AuthorisationAwareChildRunner(allow: false);
        var executor = new IsolatedWindowsPrintExecutor(
            NullLogger<IsolatedWindowsPrintExecutor>.Instance,
            childProcessRunner: runner);

        var exception = await Assert.ThrowsAsync<PrintExecutionBlockedException>(() =>
            executor.PrintAsync(
                Entry(), Guid.NewGuid(), CancellationToken.None,
                _ => Task.FromResult(false)));

        Assert.Equal("SPOOLER_SUBMISSION_NOT_AUTHORISED", exception.ReasonCode);
    }

    [Fact]
    public async Task AuthorisationTransportFailureFailsClosed()
    {
        var runner = new AuthorisationAwareChildRunner(allow: true);
        var executor = new IsolatedWindowsPrintExecutor(
            NullLogger<IsolatedWindowsPrintExecutor>.Instance,
            childProcessRunner: runner);

        var exception = await Assert.ThrowsAsync<PrintExecutionBlockedException>(() =>
            executor.PrintAsync(
                Entry(), Guid.NewGuid(), CancellationToken.None,
                _ => throw new HttpRequestException("simulated authorization transport failure")));

        Assert.Equal("SPOOLER_SUBMISSION_NOT_AUTHORISED", exception.ReasonCode);
    }

    private static AgentLocalQueueEntry Entry()
    {
        var bytes = "%PDF-1.7"u8.ToArray();
        var now = DateTimeOffset.UtcNow;
        return new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-VIRTUAL-481", Guid.NewGuid(),
            "Android IPP Virtual Printer", "SmartPrint Virtual IPP Test Queue",
            "virtual-test.pdf", "application/pdf",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength, 1, 1, "Mono", "A4", false, "INR", 100,
            "unused", "ACKNOWLEDGED", now, now.AddMinutes(3),
            SpoolerJobName: "VIRTUAL-TEST-SP-481",
            OutputMode: "UNSUPPORTED_VIRTUAL");
    }

    private sealed class AuthorisationAwareChildRunner(bool allow) : IAgentChildProcessRunner
    {
        public string? Command { get; private set; }

        public async Task<AgentChildProcessResult> ExecuteAsync(
            string command,
            string standardInput,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<bool>>? beforeSpoolerSubmission = null)
        {
            Command = command;
            var authorised = false;
            try
            {
                authorised = allow && beforeSpoolerSubmission is not null &&
                    await beforeSpoolerSubmission(cancellationToken);
            }
            catch
            {
                authorised = false;
            }

            if (!authorised)
            {
                const string blocked = "BLOCKED|{\"code\":\"SPOOLER_SUBMISSION_NOT_AUTHORISED\",\"message\":\"The server did not authorise spooler submission.\"}";
                return new AgentChildProcessResult(10, "READY\nAUTHORIZE_SPOOLER_SUBMISSION\n" + blocked, string.Empty, false);
            }

            var receipt = JsonSerializer.Serialize(
                new SpoolerPrintReceipt(481, "VIRTUAL-TEST-SP-481"),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new AgentChildProcessResult(
                0,
                "READY\nAUTHORIZE_SPOOLER_SUBMISSION\nSUBMITTING\nRESULT|" + receipt,
                string.Empty,
                false);
        }
    }
}
