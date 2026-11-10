using System.ComponentModel;
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

    public Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();

            if (service.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
            {
                if (service.StartType == ServiceStartMode.Disabled)
                {
                    throw new DocumentProcessingException(
                        "The optional document parser is disabled. Normal printing is still available; uploads are temporarily unavailable.");
                }

                service.Start();
            }

            service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        catch (DocumentProcessingException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or TimeoutException)
        {
            throw new DocumentProcessingException(
                "The optional document parser could not be started. Normal printing is unaffected; this upload was blocked.",
                exception);
        }
    }
}
