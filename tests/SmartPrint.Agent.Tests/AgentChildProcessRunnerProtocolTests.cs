using System.Diagnostics;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentChildProcessRunnerProtocolTests
{
    [Theory]
    [InlineData(true, "ALLOW", 0)]
    [InlineData(false, "DENY", 17)]
    public async Task RealChildProcessReceivesOneShotAuthorizationReply(
        bool allow, string expectedReply, int expectedExitCode)
    {
        var runner = new AgentChildProcessRunner(CreateProtocolChild);
        var callbackCount = 0;

        var result = await runner.ExecuteAsync(
            "protocol-test",
            "{\\"request\\":true}",
            TimeSpan.FromSeconds(10),
            CancellationToken.None,
            _ =>
            {
                callbackCount++;
                return Task.FromResult(allow);
            });

        Assert.False(result.TimedOut);
        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(1, callbackCount);
        Assert.Contains("AUTHORIZE_SPOOLER_SUBMISSION", result.StandardOutput);
        Assert.Contains("RECEIVED:" + expectedReply, result.StandardOutput);
    }

    [Fact]
    public async Task RealChildProcessReceivesDenyWhenAuthorizationCallbackFails()
    {
        var runner = new AgentChildProcessRunner(CreateProtocolChild);

        var result = await runner.ExecuteAsync(
            "protocol-test",
            "{\\"request\\":true}",
            TimeSpan.FromSeconds(10),
            CancellationToken.None,
            _ => throw new HttpRequestException("simulated API outage"));

        Assert.False(result.TimedOut);
        Assert.Equal(17, result.ExitCode);
        Assert.Contains("RECEIVED:DENY", result.StandardOutput);
    }

    private static ProcessStartInfo CreateProtocolChild(string _)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$request = [Console]::ReadLine(); " +
                "[Console]::WriteLine('AUTHORIZE_SPOOLER_SUBMISSION'); " +
                "$reply = [Console]::ReadLine(); " +
                "[Console]::WriteLine('RECEIVED:' + $reply); " +
                "if ($reply -eq 'ALLOW') { exit 0 } else { exit 17 }");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                "IFS= read -r request; " +
                "printf '%s\\n' AUTHORIZE_SPOOLER_SUBMISSION; " +
                "IFS= read -r reply; " +
                "printf 'RECEIVED:%s\\n' \"$reply\"; " +
                "if [ \"$reply\" = ALLOW ]; then exit 0; else exit 17; fi");
        }

        return startInfo;
    }
}
