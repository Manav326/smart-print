using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentControlPipeIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task WindowsPipe_EnrollsAgainstApi_AndPersistsTheAgentIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var deviceId = Guid.NewGuid();
        const string agentSecret = "integration-test-agent-secret";
        const string conflictEnrollmentCode = "QRTP-Integration-Conflict-000001";
        const string enrollmentCode = "QRTP-Integration-Test-Code-00001";

        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        var apiOrigin = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(apiOrigin + "/");
        listener.Start();

        var apiRequestTask = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var requestBody = await reader.ReadToEndAsync(timeout.Token);
                using var document = JsonDocument.Parse(requestBody);
                var root = document.RootElement;

                Assert.Equal("POST", context.Request.HttpMethod);
                Assert.Equal("/api/v1/agent/enroll", context.Request.Url?.AbsolutePath);
                Assert.Equal(
                    attempt == 0 ? conflictEnrollmentCode : enrollmentCode,
                    root.GetProperty("code").GetString());

                var fingerprint = root.GetProperty("fingerprint").GetString();
                Assert.NotNull(fingerprint);
                Assert.Equal(64, fingerprint!.Length);
                Assert.All(fingerprint, character => Assert.True(Uri.IsHexDigit(character)));
                Assert.Equal(Environment.MachineName, root.GetProperty("deviceName").GetString());

                byte[] payload;
                if (attempt == 0)
                {
                    payload = JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            code = "agent_already_enrolled",
                            message = "This computer is still paired with an active SmartPrint shop. Ask the owner of that shop to revoke its pairing before reconnecting it."
                        },
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                }
                else
                {
                    payload = JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            agentDeviceId = deviceId,
                            agentSecret,
                            createdUtc = DateTimeOffset.UtcNow
                        },
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    context.Response.StatusCode = (int)HttpStatusCode.Created;
                }

                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, timeout.Token);
                context.Response.Close();
            }

            return true;
        }, timeout.Token);

        var identityDirectory = Path.Combine(Path.GetTempPath(), "SmartPrint-AgentPipe-" + Guid.NewGuid().ToString("N"));
        var identityPath = Path.Combine(identityDirectory, "agent.identity");
        var credentialStore = new AgentCredentialStore(identityPath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:ApiBaseUrl"] = apiOrigin
            })
            .Build();
        var localApiState = new LocalAgentApiState();
        var networkPreferenceStore = new LocalNetworkPreferenceStore(Path.Combine(identityDirectory, "local-network.preference"));
        var localNetworkManager = new LocalNetworkManager(networkPreferenceStore, localApiState);
        var testPipeName = $"{AgentControlPipeHostedService.PipeName}.test.{Guid.NewGuid():N}";
        var service = new AgentControlPipeHostedService(
            configuration,
            NullLogger<AgentControlPipeHostedService>.Instance,
            new AgentEnrollmentHandler(credentialStore),
            credentialStore,
            localApiState,
            localNetworkManager,
            new AgentRuntimeReadiness(),
            testPipeName);

        try
        {
            Assert.Equal(@"\\.\pipe\QRtoPrint.SmartPrint.Agent.Control", AgentControlPipeHostedService.PipePath);
            await service.StartAsync(timeout.Token);

            using (var initialStatus = await SendPipeRequestAsync(testPipeName, "status", null, timeout.Token))
            {
                Assert.True(initialStatus.RootElement.GetProperty("success").GetBoolean());
                Assert.True(initialStatus.RootElement.GetProperty("serviceReachable").GetBoolean());
                Assert.False(initialStatus.RootElement.GetProperty("localApiReachable").GetBoolean());
                Assert.False(initialStatus.RootElement.GetProperty("paired").GetBoolean());
                Assert.Equal("UNPAIRED", initialStatus.RootElement.GetProperty("status").GetString());
                Assert.Equal(apiOrigin, initialStatus.RootElement.GetProperty("apiBaseUrl").GetString());
            }

            using (var conflict = await SendPipeRequestAsync(testPipeName, "enroll", conflictEnrollmentCode, timeout.Token))
            {
                Assert.False(conflict.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("agent_already_enrolled", conflict.RootElement.GetProperty("code").GetString());
                Assert.Contains("still paired with an active SmartPrint shop", conflict.RootElement.GetProperty("message").GetString());
                Assert.Null(credentialStore.Load());
                Assert.False(credentialStore.IsRevoked());
            }

            using (var response = await SendPipeRequestAsync(testPipeName, "enroll", enrollmentCode, timeout.Token))
            {
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(deviceId, response.RootElement.GetProperty("agentDeviceId").GetGuid());
                Assert.True(response.RootElement.GetProperty("message").ValueKind is JsonValueKind.Null);
                Assert.True(response.RootElement.GetProperty("code").ValueKind is JsonValueKind.Null);
            }

            Assert.True(await apiRequestTask);

            using (var pairedStatus = await SendPipeRequestAsync(testPipeName, "status", null, timeout.Token))
            {
                Assert.True(pairedStatus.RootElement.GetProperty("success").GetBoolean());
                Assert.True(pairedStatus.RootElement.GetProperty("serviceReachable").GetBoolean());
                Assert.False(pairedStatus.RootElement.GetProperty("localApiReachable").GetBoolean());
                Assert.True(pairedStatus.RootElement.GetProperty("paired").GetBoolean());
                Assert.Equal(deviceId, pairedStatus.RootElement.GetProperty("agentDeviceId").GetGuid());
                Assert.Equal("CONNECTED", pairedStatus.RootElement.GetProperty("status").GetString());
                Assert.Equal(apiOrigin, pairedStatus.RootElement.GetProperty("apiBaseUrl").GetString());
            }

            Assert.True(await apiRequestTask.WaitAsync(timeout.Token));
            var savedCredentials = credentialStore.Load();
            Assert.NotNull(savedCredentials);
            Assert.Equal(deviceId, savedCredentials!.AgentDeviceId);
            Assert.Equal(agentSecret, savedCredentials.AgentSecret);
            Assert.Equal(apiOrigin, savedCredentials.ApiBaseUrl);

            var staleHeartbeatIdentity = savedCredentials with { AgentSecret = "previous-agent-secret" };
            Assert.False(credentialStore.DeleteIfMatches(staleHeartbeatIdentity));
            var preservedCredentials = credentialStore.Load();
            Assert.NotNull(preservedCredentials);
            Assert.Equal(agentSecret, preservedCredentials!.AgentSecret);
            Assert.False(credentialStore.IsRevoked());

            // A real rejection of the current identity must remain visible after
            // the secret is cleared, so desktop startup does not silently re-enroll.
            Assert.True(credentialStore.DeleteIfMatches(preservedCredentials));
            Assert.True(credentialStore.IsRevoked());
            Assert.Null(credentialStore.Load());
            using (var revokedStatus = await SendPipeRequestAsync(testPipeName, "status", null, timeout.Token))
            {
                Assert.True(revokedStatus.RootElement.GetProperty("success").GetBoolean());
                Assert.True(revokedStatus.RootElement.GetProperty("serviceReachable").GetBoolean());
                Assert.False(revokedStatus.RootElement.GetProperty("paired").GetBoolean());
                Assert.Equal("REVOKED", revokedStatus.RootElement.GetProperty("status").GetString());
            }

            var renewedCredentials = preservedCredentials with
            {
                AgentDeviceId = Guid.NewGuid(),
                AgentSecret = "fresh-agent-secret",
                PairedUtc = DateTimeOffset.UtcNow
            };
            credentialStore.Save(renewedCredentials);
            Assert.False(credentialStore.IsRevoked());
            Assert.Equal(renewedCredentials.AgentDeviceId, credentialStore.Load()!.AgentDeviceId);
            using (var renewedStatus = await SendPipeRequestAsync(testPipeName, "status", null, timeout.Token))
            {
                Assert.True(renewedStatus.RootElement.GetProperty("success").GetBoolean());
                Assert.True(renewedStatus.RootElement.GetProperty("paired").GetBoolean());
                Assert.Equal(renewedCredentials.AgentDeviceId, renewedStatus.RootElement.GetProperty("agentDeviceId").GetGuid());
                Assert.Equal("CONNECTED", renewedStatus.RootElement.GetProperty("status").GetString());
            }

            // Corrupt the protected identity to ensure the pipe still reports
            // that the service is reachable, rather than falsely reporting it offline.
            File.WriteAllBytes(identityPath, [0x00, 0x01, 0x02, 0x03]);
            using (var unreadableStatus = await SendPipeRequestAsync(testPipeName, "status", null, timeout.Token))
            {
                Assert.False(unreadableStatus.RootElement.GetProperty("success").GetBoolean());
                Assert.True(unreadableStatus.RootElement.GetProperty("serviceReachable").GetBoolean());
                Assert.False(unreadableStatus.RootElement.GetProperty("paired").GetBoolean());
                Assert.Equal("IDENTITY_UNAVAILABLE", unreadableStatus.RootElement.GetProperty("status").GetString());
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
            listener.Close();
            credentialStore.Delete();
            if (Directory.Exists(identityDirectory))
                Directory.Delete(identityDirectory, recursive: true);
        }
    }

    private static async Task<JsonDocument> SendPipeRequestAsync(
        string pipeName,
        string operation,
        string? enrollmentCode,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000, cancellationToken);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var request = JsonSerializer.Serialize(
            new { operation, enrollmentCode },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await writer.WriteLineAsync(request);
        var responseLine = await reader.ReadLineAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(responseLine));
        return JsonDocument.Parse(responseLine!);
    }

}
