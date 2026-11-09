using System.ComponentModel;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Hosting;

namespace SmartPrint.Agent;

public sealed class AgentControlPipeHostedService(
    IConfiguration configuration,
    ILogger<AgentControlPipeHostedService> logger,
    IAgentEnrollmentHandler enrollmentHandler,
    AgentCredentialStore credentialStore,
    LocalAgentApiState localAgentApiState,
    LocalNetworkManager localNetworkManager,
    AgentRuntimeReadiness runtimeReadiness,
    string? pipeNameOverride = null) : BackgroundService
{
    public const string PipeName = "QRtoPrint.SmartPrint.Agent.Control";
    public const string PipePath = @"\\.\pipe\" + PipeName;

    private readonly string pipeName = ResolvePipeName(pipeNameOverride);
    private readonly string pipePath = @"\\.\pipe\" + pipeName;

    private static string ResolvePipeName(string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
            return PipeName;

        if (requestedName.Length > 200 ||
            !requestedName.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw new ArgumentException("The SmartPrint control pipe name contains unsupported characters.", nameof(requestedName));
        }

        return requestedName;
    }
    private const int MaximumRequestBytes = 4096;
    private static readonly JsonSerializerOptions PipeJsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogInformation("The local desktop control pipe is available only on Windows.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe(pipePath);
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                await HandleConnectionAsync(pipe, requestTimeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "The SmartPrint desktop control pipe connection ended unexpectedly.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The SmartPrint desktop control pipe could not process a request.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint PipeFirstInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private static int firstPipeInstance = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeNative(
        string pipeName,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        ref NativeSecurityAttributes securityAttributes);

    private static NamedPipeServerStream CreatePipe(string pipePath)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // .NET's public PipeOptions do not expose PIPE_REJECT_REMOTE_CLIENTS.
        // Create the local-only pipe with the same explicit ACL, then wrap the
        // overlapped handle so WaitForConnectionAsync remains non-blocking.
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            var attributes = new NativeSecurityAttributes
            {
                Length = Marshal.SizeOf<NativeSecurityAttributes>(),
                SecurityDescriptor = descriptorPointer,
                InheritHandle = 0
            };

            var isFirstPipeInstance = Volatile.Read(ref firstPipeInstance) == 1;
            var openMode = PipeAccessDuplex | FileFlagOverlapped |
                (isFirstPipeInstance ? PipeFirstInstance : 0u);
            var handle = CreateNamedPipeNative(
                pipePath,
                openMode,
                PipeRejectRemoteClients,
                maxInstances: 1,
                outBufferSize: 4096,
                inBufferSize: 4096,
                defaultTimeout: 0,
                ref attributes);

            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Could not create the local SmartPrint control pipe.");
            }

            if (isFirstPipeInstance)
            {
                Interlocked.Exchange(ref firstPipeInstance, 0);
            }

            return new NamedPipeServerStream(
                PipeDirection.InOut,
                isAsync: true,
                isConnected: false,
                handle);
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorPointer);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        // Read a bounded line instead of StreamReader.ReadLineAsync(), which can
        // allocate without limit for a malicious local pipe client.
        var requestBuffer = new char[MaximumRequestBytes + 1];
        var requestLength = 0;
        var requestComplete = false;
        while (requestLength < requestBuffer.Length)
        {
            var count = await reader.ReadAsync(requestBuffer.AsMemory(requestLength, 1), ct);
            if (count == 0)
                break;
            if (requestBuffer[requestLength] == '\n')
            {
                requestComplete = true;
                break;
            }
            requestLength += count;
        }

        var requestLine = new string(requestBuffer, 0, requestLength).TrimEnd('\r');
        if (!requestComplete ||
            string.IsNullOrWhiteSpace(requestLine) ||
            Encoding.UTF8.GetByteCount(requestLine) > MaximumRequestBytes)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(false, null, "Invalid desktop enrollment request."), PipeJsonOptions));
            return;
        }

        PipeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PipeRequest>(requestLine, PipeJsonOptions);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null ||
            (!string.Equals(request.Operation, "status", StringComparison.Ordinal) &&
             !string.Equals(request.Operation, "enroll", StringComparison.Ordinal) &&
             !string.Equals(request.Operation, "network-status", StringComparison.Ordinal) &&
             !string.Equals(request.Operation, "select-network", StringComparison.Ordinal) &&
             !string.Equals(request.Operation, "start-phone-test", StringComparison.Ordinal)))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(false, null, "Invalid SmartPrint service operation."), PipeJsonOptions));
            return;
        }

        if (string.Equals(request.Operation, "status", StringComparison.Ordinal))
        {
            try
            {
                var credentials = credentialStore.LoadForCloudRequests(configuration["Agent:ApiBaseUrl"]);
                var paired = credentials is not null;
                var status = paired ? "CONNECTED" : credentialStore.IsRevoked() ? "REVOKED" : "UNPAIRED";
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new PipeResponse(true, credentials?.AgentDeviceId, null, true, paired, status, localAgentApiState.IsRunning, credentials?.ApiBaseUrl ?? configuration["Agent:ApiBaseUrl"], Readiness: runtimeReadiness.GetSnapshot()),
                    PipeJsonOptions));
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning("SmartPrint local identity status could not be read ({FailureType}).", exception.GetType().Name);
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new PipeResponse(false, null, "The local SmartPrint identity could not be read.", true, false, "IDENTITY_UNAVAILABLE", localAgentApiState.IsRunning, configuration["Agent:ApiBaseUrl"], Readiness: runtimeReadiness.GetSnapshot()),
                    PipeJsonOptions));
            }
            return;
        }

        if (request.Operation is "network-status" or "select-network" or "start-phone-test")
        {
            try
            {
                LocalNetworkSnapshot? snapshot = null;
                LocalPhoneTest? phoneTest = null;
                if (request.Operation == "network-status")
                    snapshot = localNetworkManager.GetSnapshot();
                else if (request.Operation == "select-network")
                {
                    if (credentialStore.Load() is null)
                        throw new InvalidOperationException("Connect this computer to your shop first.");
                    if (string.IsNullOrWhiteSpace(request.NetworkId))
                        throw new InvalidOperationException("Choose a shop connection.");
                    snapshot = localNetworkManager.SelectNetwork(request.NetworkId);
                }
                else
                {
                    if (credentialStore.Load() is null)
                        throw new InvalidOperationException("Connect this computer to your shop first.");
                    phoneTest = localNetworkManager.StartPhoneTest();
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new PipeResponse(true, null, null, NetworkSnapshot: snapshot, PhoneTest: phoneTest),
                    PipeJsonOptions));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
                    NetworkInformationException or CryptographicException or JsonException)
            {
                logger.LogWarning("The shop connection could not be updated ({FailureType}).", exception.GetType().Name);
                var message = exception is InvalidOperationException or ArgumentException
                    ? exception.Message
                    : "SmartPrint could not update the shop connection. Refresh and try again.";
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new PipeResponse(false, null, message),
                    PipeJsonOptions));
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(request.EnrollmentCode) ||
            request.EnrollmentCode.Length != 32 ||
            !request.EnrollmentCode.All(IsBase64UrlCharacter))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(false, null, "The one-time enrollment authorization is invalid."), PipeJsonOptions));
            return;
        }

        var apiBaseUrl = configuration["Agent:ApiBaseUrl"];
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(false, null, "The SmartPrint service is not configured. Repair or reinstall SmartPrint."), PipeJsonOptions));
            return;
        }

        try
        {
            var credentials = await enrollmentHandler.EnrollAsync(apiBaseUrl, request.EnrollmentCode, ct);
            logger.LogInformation("SmartPrint Agent enrollment completed for device {AgentDeviceId}.", credentials.AgentDeviceId);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(true, credentials.AgentDeviceId, null), PipeJsonOptions));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            var enrollmentFailure = exception as AgentEnrollmentException;
            var userMessage = enrollmentFailure?.Message ??
                (exception is InvalidOperationException &&
                 exception.Message.StartsWith("This computer is still paired with an active SmartPrint shop.", StringComparison.Ordinal)
                    ? exception.Message
                    : "The computer could not be connected. Check the internet connection and try again.");
            logger.LogWarning("SmartPrint Agent enrollment was declined or unavailable ({FailureType}).", exception.GetType().Name);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new PipeResponse(false, null, userMessage, Code: enrollmentFailure?.Code),
                PipeJsonOptions));
        }
    }

    private static bool IsBase64UrlCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';

    private sealed record PipeRequest(string Operation, string? EnrollmentCode, string? NetworkId = null);
    private sealed record PipeResponse(
        bool Success,
        Guid? AgentDeviceId,
        string? Message,
        bool? ServiceReachable = null,
        bool? Paired = null,
        string? Status = null,
        bool? LocalApiReachable = null,
        string? ApiBaseUrl = null,
        string? Code = null,
        LocalNetworkSnapshot? NetworkSnapshot = null,
        LocalPhoneTest? PhoneTest = null,
        AgentRuntimeReadinessSnapshot? Readiness = null);
}