using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace SmartPrint.Agent;

public interface IAgentChildProcessRunner
{
    Task<AgentChildProcessResult> ExecuteAsync(string command, string standardInput, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record AgentChildProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public sealed class AgentChildProcessStartException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Starts the same published Agent executable as a short-lived worker. The child
/// inherits the service token, so LocalService retains the same protected-queue
/// and Windows printer access without passing credentials or plaintext files.
/// </summary>
public sealed class AgentChildProcessRunner : IAgentChildProcessRunner
{
    public async Task<AgentChildProcessResult> ExecuteAsync(
        string command,
        string standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(standardInput);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var process = new Process { StartInfo = CreateStartInfo(command) };
        try
        {
            if (!process.Start())
                throw new AgentChildProcessStartException("Windows did not start the isolated Agent worker.");
        }
        catch (AgentChildProcessStartException) { throw; }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new AgentChildProcessStartException(
                "SmartPrint could not start its isolated Windows printer worker.", exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            try
            {
                if (standardInput.Length > 0)
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutSource.Token);
                await process.StandardInput.FlushAsync(timeoutSource.Token);
                process.StandardInput.Close();
            }
            catch (IOException) when (process.HasExited)
            {
                // A child which exits before consuming its small control request
                // is still a normal, inspectable child-process failure.
                try { process.StandardInput.Close(); } catch (IOException) { }
            }

            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut)
                cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }

        return new AgentChildProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask,
            timedOut);
    }

    private static ProcessStartInfo CreateStartInfo(string command)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new AgentChildProcessStartException("The current Agent executable path is unavailable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        // Framework-dependent development runs are hosted by dotnet.exe. Published
        // Windows service builds launch SmartPrint.Agent.exe directly.
        if (string.Equals(
                Path.GetFileNameWithoutExtension(executablePath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly) || !File.Exists(entryAssembly))
                throw new AgentChildProcessStartException("The Agent entry assembly path is unavailable.");
            startInfo.ArgumentList.Add(entryAssembly);
        }

        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
