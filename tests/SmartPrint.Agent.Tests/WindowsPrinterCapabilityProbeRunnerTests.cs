using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class WindowsPrinterCapabilityProbeRunnerTests
{
    [Fact]
    public async Task SuccessfulCapabilityReportIsCachedPerQueueAndDriver()
    {
        if (!OperatingSystem.IsWindows()) return;

        var runner = new QueueResponseRunner(
            new AgentChildProcessResult(0,
                """{"schemaVersion":2,"queueName":"Office USB","driverName":"Office Driver","duplex":true}""",
                string.Empty, false),
            new AgentChildProcessResult(0,
                """{"schemaVersion":2,"queueName":"Office USB","driverName":"New Driver","duplex":false}""",
                string.Empty, false));
        var probe = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            childProcessRunner: runner);

        var first = await probe.ProbeAsync("Office USB", "Office Driver", CancellationToken.None);
        var cached = await probe.ProbeAsync("office usb", "Office Driver", CancellationToken.None);
        var changedDriver = await probe.ProbeAsync("Office USB", "New Driver", CancellationToken.None);

        Assert.Contains("\"duplex\":true", first);
        Assert.Equal(first, cached);
        Assert.Contains("\"duplex\":false", changedDriver);
        Assert.Equal(2, runner.CallCount);
        Assert.Equal(
            new[] { "--probe-printer-capabilities", "--probe-printer-capabilities" },
            runner.Commands);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("""{"queueName":"Different Queue","duplex":true}""")]
    [InlineData("""{"duplex":true}""")]
    [InlineData("""{"queueName":42,"duplex":true}""")]
    public async Task InvalidOrMismatchedCapabilityReportFailsClosedAndIsBackedOff(string response)
    {
        if (!OperatingSystem.IsWindows()) return;

        var runner = new QueueResponseRunner(
            new AgentChildProcessResult(0, response, string.Empty, false));
        var probe = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            childProcessRunner: runner);

        Assert.Equal("{}", await probe.ProbeAsync("Office USB", "Office Driver", CancellationToken.None));
        Assert.Equal("{}", await probe.ProbeAsync("Office USB", "Office Driver", CancellationToken.None));
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task TimedOutAndOversizedCapabilityReportsRemainUnknownAndAreBackedOff()
    {
        if (!OperatingSystem.IsWindows()) return;

        var runner = new QueueResponseRunner(
            new AgentChildProcessResult(-1, string.Empty, "timeout", true),
            new AgentChildProcessResult(0, new string('x', 15_001), string.Empty, false));
        var probe = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            childProcessRunner: runner);

        Assert.Equal("{}", await probe.ProbeAsync("Slow Queue", "Slow Driver", CancellationToken.None));
        Assert.Equal("{}", await probe.ProbeAsync("Slow Queue", "Slow Driver", CancellationToken.None));
        Assert.Equal("{}", await probe.ProbeAsync("Large Queue", "Large Driver", CancellationToken.None));
        Assert.Equal("{}", await probe.ProbeAsync("Large Queue", "Large Driver", CancellationToken.None));
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public async Task CapabilityFailureForOneQueueDoesNotPoisonAnotherQueue()
    {
        if (!OperatingSystem.IsWindows()) return;

        var runner = new QueueResponseRunner(
            new AgentChildProcessResult(1, string.Empty, "driver crash", false),
            new AgentChildProcessResult(0,
                """{"schemaVersion":2,"queueName":"Healthy Queue","color":true}""",
                string.Empty, false));
        var probe = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            childProcessRunner: runner);

        Assert.Equal("{}", await probe.ProbeAsync("Broken Queue", "Broken Driver", CancellationToken.None));
        var healthy = await probe.ProbeAsync("Healthy Queue", "Healthy Driver", CancellationToken.None);

        Assert.Contains("\"color\":true", healthy);
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public async Task CancellationIsPropagatedInsteadOfCachedAsDriverFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        var runner = new BlockingChildProcessRunner();
        var probe = new WindowsPrinterCapabilityProbeRunner(
            NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance,
            childProcessRunner: runner);
        using var cancellation = new CancellationTokenSource();

        var pending = probe.ProbeAsync("Office USB", "Office Driver", cancellation.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, runner.CallCount);
    }

    private sealed class QueueResponseRunner(params AgentChildProcessResult[] responses)
        : IAgentChildProcessRunner
    {
        private readonly Queue<AgentChildProcessResult> pending = new(responses);
        public List<string> Commands { get; } = [];
        public int CallCount => Commands.Count;

        public Task<AgentChildProcessResult> ExecuteAsync(
            string command,
            string standardInput,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<bool>>? beforeSpoolerSubmission = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (!pending.TryDequeue(out var result))
                throw new InvalidOperationException("No fake capability response was configured.");
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingChildProcessRunner : IAgentChildProcessRunner
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<AgentChildProcessResult> ExecuteAsync(
            string command,
            string standardInput,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<bool>>? beforeSpoolerSubmission = null)
        {
            CallCount++;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AgentChildProcessResult(0, "{}", string.Empty, false);
        }
    }
}
