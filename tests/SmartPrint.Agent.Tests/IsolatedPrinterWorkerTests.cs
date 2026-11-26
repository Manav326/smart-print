using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class IsolatedPrinterWorkerTests
{
    [Fact]
    public async Task CapabilityProbeCrashReturnsUnknownAndIsBackedOff()
    {
        var runner = new FakeChildProcessRunner(new AgentChildProcessResult(
            unchecked((int)0xC0000374u), string.Empty, "native driver crash", false));
        var probes = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            childProcessRunner: runner);

        var first = await probes.ProbeAsync("Bad driver queue", "Broken driver", CancellationToken.None);
        var second = await probes.ProbeAsync("Bad driver queue", "Broken driver", CancellationToken.None);

        Assert.Equal("{}", first);
        Assert.Equal("{}", second);
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task CapabilityProbeTimeoutDoesNotBlockOtherQueues()
    {
        var runner = new FakeChildProcessRunner(
            new AgentChildProcessResult(-1, string.Empty, "timed out", true));
        var probes = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            childProcessRunner: runner);

        var failed = await probes.ProbeAsync("Slow queue", "Slow driver", CancellationToken.None);
        runner.NextResult = new AgentChildProcessResult(
            0,
            """{"schemaVersion":2,"queueName":"Good queue","color":true}""",
            string.Empty,
            false);
        var good = await probes.ProbeAsync("Good queue", "Good driver", CancellationToken.None);

        Assert.Equal("{}", failed);
        Assert.Contains("Good queue", good);
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public async Task PrintWorkerCrashBeforeSubmissionIsBlockedNotRetriedAsUncertain()
    {
        var runner = new FakeChildProcessRunner(
            new AgentChildProcessResult(unchecked((int)0xC0000374u), string.Empty, "heap corruption", false));
        var executor = new IsolatedWindowsPrintExecutor(
            NullLogger<IsolatedWindowsPrintExecutor>.Instance,
            childProcessRunner: runner);

        var exception = await Assert.ThrowsAsync<PrintExecutionBlockedException>(
            () => executor.PrintAsync(CreateEntry(), Guid.NewGuid(), CancellationToken.None, _ => Task.FromResult(true)));

        Assert.Equal("PRINT_WORKER_CRASHED_BEFORE_SUBMISSION", exception.ReasonCode);
    }

    [Fact]
    public async Task PrintWorkerCrashAfterSubmissionIsUncertainAndNeverAutomaticallyRetried()
    {
        var runner = new FakeChildProcessRunner(
            new AgentChildProcessResult(unchecked((int)0xC0000374u), "READY\nAUTHORIZE_SPOOLER_SUBMISSION\nSUBMITTING\n", "native driver crash", false));
        var executor = new IsolatedWindowsPrintExecutor(
            NullLogger<IsolatedWindowsPrintExecutor>.Instance,
            childProcessRunner: runner);

        var exception = await Assert.ThrowsAsync<PrintSubmissionUncertainException>(
            () => executor.PrintAsync(CreateEntry(), Guid.NewGuid(), CancellationToken.None, _ => Task.FromResult(true)));

        Assert.Equal("PRINT_WORKER_CRASHED_AFTER_SUBMISSION", exception.ReasonCode);
    }

    [Fact]
    public async Task SuccessfulPrintWorkerReturnsSpoolerReceipt()
    {
        var receipt = new SpoolerPrintReceipt(42, "SmartPrint-Test-Job");
        var runner = new FakeChildProcessRunner(
            new AgentChildProcessResult(
                0,
                "READY\nAUTHORIZE_SPOOLER_SUBMISSION\nSUBMITTING\nRESULT|" + System.Text.Json.JsonSerializer.Serialize(
                    receipt, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                string.Empty,
                false));
        var executor = new IsolatedWindowsPrintExecutor(
            NullLogger<IsolatedWindowsPrintExecutor>.Instance,
            childProcessRunner: runner);

        var result = await executor.PrintAsync(CreateEntry(), Guid.NewGuid(), CancellationToken.None, _ => Task.FromResult(true));

        Assert.Equal(receipt, result);
    }

    private static AgentLocalQueueEntry CreateEntry()
    {
        var bytes = "worker test"u8.ToArray();
        return new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-WORKER-TEST", Guid.NewGuid(), "Test printer", "Test queue",
            "test.pdf", "application/pdf",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.Length, 1, 1, "Mono", "A4", false, "INR", 100,
            "unused", "ACKNOWLEDGED", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            SpoolerJobName: "SmartPrint-Test-Job");
    }

    private sealed class FakeChildProcessRunner(AgentChildProcessResult initial) : IAgentChildProcessRunner
    {
        public int CallCount { get; private set; }
        public AgentChildProcessResult NextResult { get; set; } = initial;

        public async Task<AgentChildProcessResult> ExecuteAsync(
            string command,
            string standardInput,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<bool>>? beforeSpoolerSubmission = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (beforeSpoolerSubmission is not null &&
                NextResult.StandardOutput.Contains("AUTHORIZE_SPOOLER_SUBMISSION", StringComparison.Ordinal))
            {
                var allowed = await beforeSpoolerSubmission(cancellationToken);
                if (!allowed)
                {
                    return new AgentChildProcessResult(
                        10,
                        "READY\nAUTHORIZE_SPOOLER_SUBMISSION\nBLOCKED|{\\"code\\":\\"SPOOLER_SUBMISSION_NOT_AUTHORISED\\",\\"message\\":\\"Denied by parent Agent.\\"}",
                        string.Empty,
                        false);
                }
            }
            return NextResult;
        }
    }
}
