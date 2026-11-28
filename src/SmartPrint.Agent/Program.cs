using System.Net.Http;
using System.IO;
using SmartPrint.Agent;

if (await AgentChildCommands.TryHandleAsync(args))
    return;


if (args.Any(argument => argument.Equals("--check-pairing", StringComparison.OrdinalIgnoreCase)))
{
    var pairingBuilder = Host.CreateApplicationBuilder(Array.Empty<string>());
    Environment.ExitCode = await AgentPairing.CheckExistingAsync(pairingBuilder.Configuration["Agent:ApiBaseUrl"]);
    return;
}

if (args.Any(argument => argument.Equals("--pair", StringComparison.OrdinalIgnoreCase)))
{
    var pairingBuilder = Host.CreateApplicationBuilder(Array.Empty<string>());
    await AgentPairing.RunAsync(pairingBuilder.Configuration);
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    // This is the SCM key used by both the website installer and uninstaller.
    // Keep it identical across EXE and MSIX distribution paths.
    options.ServiceName = "QRtoPrintSmartPrintAgent";
});
builder.Services.AddHttpClient("cloud", client =>
{
    client.Timeout = TimeSpan.FromSeconds(AgentCloudHttpTiming.RequestTimeoutSeconds);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Agent requests carry credentials in custom headers. Do not allow an API
    // redirect to forward those headers to another host or origin.
    AllowAutoRedirect = false
});
builder.Services.AddSingleton<AgentCredentialStore>();
builder.Services.AddSingleton<AgentDocumentEncryptionKeyStore>();
builder.Services.AddSingleton<IAgentEnrollmentHandler, AgentEnrollmentHandler>();
builder.Services.AddSingleton<IAgentChildProcessRunner, AgentChildProcessRunner>();
builder.Services.AddSingleton<WindowsPrinterCapabilityProbeRunner>();
builder.Services.AddSingleton<WindowsPrinterDiscovery>();
builder.Services.AddSingleton<IsolatedWindowsPrintExecutor>();
builder.Services.AddSingleton<AgentLocalQueueStore>();
builder.Services.AddSingleton<WindowsPrintExecutor>();
builder.Services.AddSingleton<LocalAgentApiState>();
builder.Services.AddSingleton<AgentRuntimeReadiness>();
builder.Services.AddSingleton<LocalNetworkPreferenceStore>();
builder.Services.AddSingleton<LocalNetworkManager>();
builder.Services.AddHostedService<LocalAgentApiHostedService>();
builder.Services.AddHostedService<AgentControlPipeHostedService>();
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
