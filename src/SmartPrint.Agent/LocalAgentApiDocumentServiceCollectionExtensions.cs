using Microsoft.Extensions.DependencyInjection;

namespace SmartPrint.Agent;

/// <summary>
/// Registers the document services used by the Agent's embedded local HTTP API.
/// That WebApplication owns a separate service provider from the outer Windows
/// Service host, so these dependencies must be added to its container explicitly.
/// </summary>
public static class LocalAgentApiDocumentServiceCollectionExtensions
{
    public static IServiceCollection AddLocalAgentDocumentProcessingServices(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<WindowsDefenderDocumentThreatScanner>();
        services.AddSingleton<ILocalDocumentWorkerServiceStarter, WindowsLocalDocumentWorkerServiceStarter>();
        services.AddSingleton<WindowsLocalDocumentAnalyzer>();

        return services;
    }
}
