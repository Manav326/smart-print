using System.Text.Json;

namespace SmartPrint.Agent;

/// <summary>
/// Keeps native spooler, GDI, PrintTicket and PDF renderer work out of the long-lived
/// Agent service. A child failure before StartDoc is a blocked attempt; after the
/// submission signal it is outcome-uncertain and must never be automatically retried.
/// </summary>
public sealed class IsolatedWindowsPrintExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<IsolatedWindowsPrintExecutor> logger;
    private readonly TimeSpan printTimeout;
    private readonly TimeSpan exportTimeout;
    private readonly TimeSpan spoolerOperationTimeout;

    public IsolatedWindowsPrintExecutor(
        ILogger<IsolatedWindowsPrintExecutor> logger,
        IConfiguration? configuration = null)
    {
        this.logger = logger;
        printTimeout = TimeSpan.FromMinutes(Math.Clamp(
            configuration?.GetValue<int?>("Agent:PrintWorkerTimeoutMinutes") ?? 20, 2, 120));
        exportTimeout = TimeSpan.FromMinutes(Math.Clamp(
            configuration?.GetValue<int?>("Agent:PdfExportWorkerTimeoutMinutes") ?? 10, 1, 60));
        spoolerOperationTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration?.GetValue<int?>("Agent:SpoolerWorkerTimeoutSeconds") ?? 15, 3, 60));
    }

    public async Task<SpoolerPrintReceipt> PrintAsync(
        AgentLocalQueueEntry entry,
        Guid agentDeviceId,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new PrintJobChildRequest(
            entry.JobId, agentDeviceId, entry.QueueName ?? string.Empty, entry.SpoolerJobName ?? string.Empty), JsonOptions);

        AgentChildProcessResult child;
        try
        {
            child = await AgentChildProcess.ExecuteAsync(
                "--print-job", request, printTimeout, cancellationToken);
        }
        catch (AgentChildProcessStartException exception)
        {
            throw new PrintExecutionBlockedException(
                "PRINT_WORKER_UNAVAILABLE",
                "SmartPrint could not start the isolated print worker. No Windows spooler submission was attempted.",
                exception);
        }

        var lines = ReadLines(child.StandardOutput);
        var resultLine = FindLine(lines, "RESULT|");
        if (resultLine is not null)
        {
            var receipt = DeserializePayload<SpoolerPrintReceipt>(resultLine);
            if (receipt is { JobId: > 0 } && !string.IsNullOrWhiteSpace(receipt.DocumentName))
                return receipt;
        }

        var blockedLine = FindLine(lines, "BLOCKED|");
        if (blockedLine is not null)
        {
            var failure = DeserializePayload<ChildOperationFailure>(blockedLine);
            throw new PrintExecutionBlockedException(
                failure?.Code ?? "PRINT_WORKER_BLOCKED",
                failure?.Message ?? "The isolated printer worker rejected the job before spooler acceptance.");
        }

        var uncertainLine = FindLine(lines, "UNCERTAIN|");
        if (uncertainLine is not null)
        {
            var failure = DeserializePayload<ChildOperationFailure>(uncertainLine);
            throw new PrintSubmissionUncertainException(
                failure?.Code ?? "PRINT_WORKER_OUTCOME_UNCERTAIN",
                failure?.Message ?? "The isolated printer worker could not confirm the spooler submission.");
        }

        var submissionStarted = lines.Contains("SUBMITTING", StringComparer.Ordinal);
        if (submissionStarted)
            throw new PrintSubmissionUncertainException(
                child.TimedOut ? "PRINT_WORKER_TIMEOUT_AFTER_SUBMISSION" : "PRINT_WORKER_CRASHED_AFTER_SUBMISSION",
                "The isolated printer worker stopped after beginning Windows spooler submission. Inspect the printer queue before retrying.");

        logger.LogWarning(
            "Isolated printer worker exited before spooler submission. ExitCode={ExitCode}; TimedOut={TimedOut}; Queue={QueueName}; Error={Error}",
            child.ExitCode, child.TimedOut, entry.QueueName, Truncate(child.StandardError, 300));
        throw new PrintExecutionBlockedException(
            child.TimedOut ? "PRINT_WORKER_TIMEOUT_BEFORE_SUBMISSION" : "PRINT_WORKER_CRASHED_BEFORE_SUBMISSION",
            "The printer worker stopped before Windows accepted the print job. The Agent remains available and this job was not automatically submitted.");
    }

    public async Task<PdfExportExecutionResult> ExportPdfAsync(
        Guid jobId,
        Guid agentDeviceId,
        string destination,
        string exportDirectory,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new PdfExportChildRequest(
            jobId, agentDeviceId, destination, exportDirectory), JsonOptions);
        AgentChildProcessResult child;
        try
        {
            child = await AgentChildProcess.ExecuteAsync(
                "--export-pdf", request, exportTimeout, cancellationToken);
        }
        catch (AgentChildProcessStartException exception)
        {
            throw new PrintExecutionBlockedException(
                "PDF_EXPORT_WORKER_UNAVAILABLE",
                "SmartPrint could not start the isolated PDF export worker.",
                exception);
        }

        var lines = ReadLines(child.StandardOutput);
        var resultLine = FindLine(lines, "RESULT|");
        if (resultLine is not null)
        {
            var result = DeserializePayload<PdfExportExecutionResult>(resultLine);
            if (result?.Receipt is not null)
                return result;
        }

        var blockedLine = FindLine(lines, "BLOCKED|");
        if (blockedLine is not null)
        {
            var failure = DeserializePayload<ChildOperationFailure>(blockedLine);
            throw new PrintExecutionBlockedException(
                failure?.Code ?? "PDF_EXPORT_BLOCKED",
                failure?.Message ?? "The isolated PDF export worker rejected the request.");
        }

        var uncertainLine = FindLine(lines, "UNCERTAIN|");
        if (uncertainLine is not null)
        {
            var failure = DeserializePayload<ChildOperationFailure>(uncertainLine);
            throw new PrintSubmissionUncertainException(
                failure?.Code ?? "PDF_EXPORT_OUTCOME_UNCERTAIN",
                failure?.Message ?? "The isolated PDF export worker could not confirm its output.");
        }

        throw new PrintSubmissionUncertainException(
            child.TimedOut ? "PDF_EXPORT_WORKER_TIMEOUT" : "PDF_EXPORT_WORKER_FAILED",
            "The isolated PDF export worker stopped without a receipt. SmartPrint will reconcile the destination file before allowing another attempt.");
    }

    public async Task<WindowsSpoolerJob?> GetJobAsync(
        string queueName,
        int jobId,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new SpoolerJobChildRequest(queueName, jobId), JsonOptions);
        var child = await AgentChildProcess.ExecuteAsync(
            "--get-spooler-job", request, spoolerOperationTimeout, cancellationToken);
        EnsureSpoolerWorkerSucceeded(child, "read");
        var resultLine = FindLine(ReadLines(child.StandardOutput), "RESULT|")
            ?? throw new IOException("The isolated spooler worker returned no job-status result.");
        return DeserializePayload<WindowsSpoolerJob?>(resultLine);
    }

    public async Task ReleaseRetainedJobAsync(
        string queueName,
        int jobId,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new SpoolerJobChildRequest(queueName, jobId), JsonOptions);
        var child = await AgentChildProcess.ExecuteAsync(
            "--release-retained-job", request, spoolerOperationTimeout, cancellationToken);
        EnsureSpoolerWorkerSucceeded(child, "release");
        var resultLine = FindLine(ReadLines(child.StandardOutput), "RESULT|")
            ?? throw new IOException("The isolated spooler worker returned no release result.");
        if (DeserializePayload<bool>(resultLine) != true)
            throw new IOException("Windows did not confirm release of the retained spooler job.");
    }

    private static void EnsureSpoolerWorkerSucceeded(AgentChildProcessResult child, string operation)
    {
        if (child.TimedOut || child.ExitCode != 0)
            throw new IOException(
                $"The isolated Windows spooler worker could not {operation} the spooler job. " +
                $"ExitCode={child.ExitCode}; TimedOut={child.TimedOut}.");
    }

    private static string[] ReadLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? FindLine(IEnumerable<string> lines, string prefix) =>
        lines.LastOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static T? DeserializePayload<T>(string line)
    {
        var separator = line.IndexOf('|');
        if (separator < 0 || separator == line.Length - 1)
            return default;
        return JsonSerializer.Deserialize<T>(line[(separator + 1)..], JsonOptions);
    }

    private static string Truncate(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maximum ? value : value[..maximum];
}

public sealed record PrintJobChildRequest(
    Guid JobId,
    Guid AgentDeviceId,
    string QueueName,
    string SpoolerJobName);

public sealed record PdfExportChildRequest(
    Guid JobId,
    Guid AgentDeviceId,
    string Destination,
    string ExportDirectory);

public sealed record SpoolerJobChildRequest(string QueueName, int JobId);

public sealed record ChildOperationFailure(string Code, string Message);
