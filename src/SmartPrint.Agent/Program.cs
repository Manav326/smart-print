using SmartPrint.Agent;

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
    options.ServiceName = "QRtoPrint Smart Print Agent";
});
builder.Services.AddHttpClient("cloud", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Agent requests carry credentials in custom headers. Do not allow an API
    // redirect to forward those headers to another host or origin.
    AllowAutoRedirect = false
});
builder.Services.AddSingleton<AgentCredentialStore>();
builder.Services.AddSingleton<AgentDocumentEncryptionKeyStore>();
builder.Services.AddSingleton<WindowsDefenderDocumentThreatScanner>();
builder.Services.AddSingleton<ILocalDocumentWorkerServiceStarter, WindowsLocalDocumentWorkerServiceStarter>();
builder.Services.AddSingleton<WindowsLocalDocumentAnalyzer>();
builder.Services.AddSingleton<IAgentEnrollmentHandler, AgentEnrollmentHandler>();
builder.Services.AddSingleton<WindowsPrinterDiscovery>();
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
