using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartPrint.Agent;

/// <summary>
/// Private command surface used only by disposable child processes launched by
/// the Agent. Requests contain queue/job identifiers, never pairing credentials.
/// Protected document bytes are reopened from the LocalService/SYSTEM-only queue.
/// </summary>
internal static class AgentChildCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<bool> TryHandleAsync(string[] args)
    {
        var command = args.FirstOrDefault(argument => argument.StartsWith("--", StringComparison.Ordinal));
        if (command is null)
            return false;

        switch (command)
        {
            case "--probe-printer-capabilities":
                Environment.ExitCode = await RunCapabilityProbeAsync();
                return true;
            case "--discover-printers":
                Environment.ExitCode = await RunDiscoveryAsync();
                return true;
            case "--print-job":
                Environment.ExitCode = await RunPrintJobAsync();
                return true;
            case "--export-pdf":
                Environment.ExitCode = await RunPdfExportAsync();
                return true;
            case "--get-spooler-job":
                Environment.ExitCode = await RunGetSpoolerJobAsync();
                return true;
            case "--release-retained-job":
                Environment.ExitCode = await RunReleaseRetainedJobAsync();
                return true;
            case "--verify-printer-discovery":
                Environment.ExitCode = await VerifyPrinterDiscoveryAsync();
                return true;
            default:
                return false;
        }
    }

    private static async Task<int> RunCapabilityProbeAsync()
    {
        try
        {
            var request = await ReadInputAsync<PrinterCapabilityProbeRequest>();
            if (request.QueueName.Length is 0 or > 256 ||
                (request.DriverName?.Length ?? 0) > 256)
                throw new InvalidDataException("The printer capability request is invalid.");

            Console.Out.WriteLine(WindowsPrinterCapabilityDiscovery.DiscoverJson(
                request.QueueName, request.DriverName));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Capability probe failed: " + exception.GetType().Name);
            return 2;
        }
    }

    private static async Task<int> RunDiscoveryAsync()
    {
        try
        {
            var discovery = new WindowsPrinterDiscovery(
                NullLogger<WindowsPrinterDiscovery>.Instance,
                executeInCurrentProcess: true);
            var result = await discovery.DiscoverInProcessAsync(CancellationToken.None);
            if (!result.Success)
                return 2;

            Console.Out.WriteLine(JsonSerializer.Serialize(result.Printers, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Printer discovery failed: " + exception.GetType().Name);
            return 2;
        }
    }

    private static async Task<int> VerifyPrinterDiscoveryAsync()
    {
        var discovery = new WindowsPrinterDiscovery(
            NullLogger<WindowsPrinterDiscovery>.Instance,
            new WindowsPrinterCapabilityProbeRunner(
                NullLogger<WindowsPrinterCapabilityProbeRunner>.Instance));
        var result = await discovery.TryDiscoverAsync(CancellationToken.None);
        if (!result.Success || result.Printers.Count == 0)
        {
            Console.Error.WriteLine("The isolated discovery pipeline did not return any Windows printer queues.");
            return 2;
        }

        Console.Out.WriteLine($"PRINTER_DISCOVERY_OK|{result.Printers.Count}");
        return 0;
    }

    private static async Task<int> RunPrintJobAsync()
    {
        var submissionStarting = false;
        try
        {
            var request = await ReadInputAsync<PrintJobChildRequest>();
            if (request.JobId == Guid.Empty || request.AgentDeviceId == Guid.Empty ||
                request.QueueName.Length is 0 or > 256 || request.SpoolerJobName.Length is 0 or > 180)
                throw new PrintExecutionBlockedException(
                    "PRINT_WORKER_REQUEST_INVALID",
                    "The isolated print request is invalid.");

            var store = new AgentLocalQueueStore();
            await store.InitializeAsync(CancellationToken.None);
            var entry = store.Find(request.JobId)
                ?? throw new PrintExecutionBlockedException(
                    "LOCAL_QUEUE_JOB_MISSING",
                    "The protected local print job is no longer available.");
            if (!string.Equals(entry.QueueName, request.QueueName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.SpoolerJobName, request.SpoolerJobName, StringComparison.Ordinal))
                throw new PrintExecutionBlockedException(
                    "LOCAL_QUEUE_PROOF_MISMATCH",
                    "The protected local print job no longer matches its assigned Windows queue.");

            using var documentStream = store.OpenDocumentReadStream(request.JobId, request.AgentDeviceId);
            Console.Out.WriteLine("READY");
            Console.Out.Flush();

            var receipt = new WindowsPrintExecutor().Print(
                entry,
                documentStream,
                CancellationToken.None,
                () =>
                {
                    submissionStarting = true;
                    Console.Out.WriteLine("SUBMITTING");
                    Console.Out.Flush();
                });
            WriteProtocol("RESULT", receipt);
            return 0;
        }
        catch (PrintExecutionBlockedException exception)
        {
            WriteProtocol("BLOCKED", new ChildOperationFailure(exception.ReasonCode, SafeMessage(exception.Message)));
            return 10;
        }
        catch (PrintSubmissionUncertainException exception)
        {
            WriteProtocol("UNCERTAIN", new ChildOperationFailure(exception.ReasonCode, SafeMessage(exception.Message)));
            return 11;
        }
        catch (Exception exception)
        {
            var failure = new ChildOperationFailure(
                submissionStarting ? "PRINT_WORKER_FAILURE_AFTER_SUBMISSION" : "PRINT_WORKER_FAILURE_BEFORE_SUBMISSION",
                "The isolated print worker failed (" + exception.GetType().Name + ").");
            WriteProtocol(submissionStarting ? "UNCERTAIN" : "BLOCKED", failure);
            return submissionStarting ? 11 : 10;
        }
    }

    private static async Task<int> RunPdfExportAsync()
    {
        try
        {
            var request = await ReadInputAsync<PdfExportChildRequest>();
            if (request.JobId == Guid.Empty || request.AgentDeviceId == Guid.Empty)
                throw new PrintExecutionBlockedException("PDF_EXPORT_REQUEST_INVALID", "The PDF export request is invalid.");

            var exportDirectory = Path.GetFullPath(request.ExportDirectory);
            var destination = Path.GetFullPath(request.Destination);
            var prefix = exportDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!destination.StartsWith(prefix, comparison))
                throw new PrintExecutionBlockedException(
                    "PDF_EXPORT_DESTINATION_INVALID",
                    "The PDF export destination is outside the configured export directory.");

            var store = new AgentLocalQueueStore();
            await store.InitializeAsync(CancellationToken.None);
            var entry = store.Find(request.JobId)
                ?? throw new PrintExecutionBlockedException("LOCAL_QUEUE_JOB_MISSING", "The protected local PDF source is unavailable.");
            using var documentStream = store.OpenDocumentReadStream(request.JobId, request.AgentDeviceId);
            var result = await new WindowsPrintExecutor().ExportPdfUsingWindowsPrinterOrFallbackAsync(
                entry with { OutputMode = PrinterOutputModeRules.PdfExport },
                documentStream,
                destination,
                exportDirectory,
                CancellationToken.None);
            WriteProtocol("RESULT", result);
            return 0;
        }
        catch (PrintExecutionBlockedException exception)
        {
            WriteProtocol("BLOCKED", new ChildOperationFailure(exception.ReasonCode, SafeMessage(exception.Message)));
            return 10;
        }
        catch (PrintSubmissionUncertainException exception)
        {
            WriteProtocol("UNCERTAIN", new ChildOperationFailure(exception.ReasonCode, SafeMessage(exception.Message)));
            return 11;
        }
        catch (Exception exception)
        {
            WriteProtocol("UNCERTAIN", new ChildOperationFailure(
                "PDF_EXPORT_WORKER_FAILURE",
                "The isolated PDF export worker failed (" + exception.GetType().Name + ")."));
            return 11;
        }
    }

    private static async Task<int> RunGetSpoolerJobAsync()
    {
        try
        {
            var request = await ReadInputAsync<SpoolerJobChildRequest>();
            ValidateSpoolerRequest(request);
            var job = new WindowsPrintExecutor().GetJob(request.QueueName, request.JobId);
            WriteProtocol("RESULT", job);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Spooler status worker failed: " + exception.GetType().Name);
            return 2;
        }
    }

    private static async Task<int> RunReleaseRetainedJobAsync()
    {
        try
        {
            var request = await ReadInputAsync<SpoolerJobChildRequest>();
            ValidateSpoolerRequest(request);
            new WindowsPrintExecutor().ReleaseRetainedJob(request.QueueName, request.JobId);
            WriteProtocol("RESULT", true);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Spooler release worker failed: " + exception.GetType().Name);
            return 2;
        }
    }

    private static void ValidateSpoolerRequest(SpoolerJobChildRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.QueueName) || request.QueueName.Length > 256 || request.JobId <= 0)
            throw new InvalidDataException("The spooler operation request is invalid.");
    }

    private static async Task<T> ReadInputAsync<T>() where T : class
    {
        var input = await JsonSerializer.DeserializeAsync<T>(
            Console.OpenStandardInput(), JsonOptions, CancellationToken.None);
        return input ?? throw new InvalidDataException("The isolated Agent worker request is empty.");
    }

    private static void WriteProtocol<T>(string kind, T value)
    {
        Console.Out.WriteLine(kind + "|" + JsonSerializer.Serialize(value, JsonOptions));
        Console.Out.Flush();
    }

    private static string SafeMessage(string value) =>
        string.IsNullOrWhiteSpace(value) ? "The requested printer operation was blocked." :
        value.Length <= 300 ? value : value[..300];
}
