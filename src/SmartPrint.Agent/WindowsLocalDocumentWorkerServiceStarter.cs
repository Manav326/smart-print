using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using SmartPrint.Documents;

namespace SmartPrint.Agent;

public interface ILocalDocumentWorkerServiceStarter
{
    Task EnsureRunningAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Starts the optional document parser only when an upload/revision needs parsing.
/// The primary printing Agent and its queue do not depend on this service.
/// </summary>
public sealed class WindowsLocalDocumentWorkerServiceStarter : ILocalDocumentWorkerServiceStarter
{
    public const string ServiceName = "QRtoPrintSmartPrintDocumentWorker";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly SemaphoreSlim StartGate = new(initialCount: 1, maxCount: 1);

    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await StartGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var service = new ServiceController(ServiceName);
            service.Refresh();

            if (service.Status == ServiceControllerStatus.Stopped)
            {
                if (service.StartType == ServiceStartMode.Disabled)
                {
                    throw new DocumentProcessingException(
                        "The optional document parser is disabled. Normal printing is still available; uploads are temporarily unavailable.");
                }

                try
                {
                    service.Start();
                }
                catch (InvalidOperationException)
                {
                    // Another local service manager may have started it between
                    // Refresh and Start. Continue only if it really left Stopped.
                    service.Refresh();
                    if (service.Status == ServiceControllerStatus.Stopped)
                    {
                        throw;
                    }
                }
            }

            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                service.Refresh();

                if (service.Status == ServiceControllerStatus.Running)
                {
                    return;
                }

                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    throw new InvalidOperationException(
                        "The optional document parser stopped before it became ready.");
                }

                if (elapsed.Elapsed >= StartTimeout)
                {
                    throw new System.TimeoutException(
                        "The optional document parser did not reach Running state within the startup limit.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }
        catch (DocumentProcessingException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or System.TimeoutException)
        {
            throw new DocumentProcessingException(
                "The optional document parser could not be started. Normal printing is unaffected; this upload was blocked.",
                exception);
        }
        finally
        {
            StartGate.Release();
        }
    }
}
