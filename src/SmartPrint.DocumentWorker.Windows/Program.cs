using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using SmartPrint.Documents;

const int maximumDocumentBytes = 20 * 1024 * 1024;
const int maximumResponseBytes = 1024 * 1024;

if (args.Any(argument => argument.Equals("--analyze-document-request", StringComparison.OrdinalIgnoreCase)))
{
    await AnalyzeChildRequestAsync();
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "QRtoPrint Smart Print Document Worker";
});
builder.Services.AddHostedService<WindowsDocumentWorkerService>();
await builder.Build().RunAsync();

static async Task AnalyzeChildRequestAsync()
{
    try
    {
        var input = Console.OpenStandardInput();
        var lengthBytes = new byte[sizeof(int)];
        await input.ReadExactlyAsync(lengthBytes);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > maximumDocumentBytes)
        {
            await WriteChildResponseAsync(false, null, "The document is outside the supported processing size range.");
            return;
        }

        var extensionLengthBuffer = new byte[1];
        await input.ReadExactlyAsync(extensionLengthBuffer);
        var extensionLength = extensionLengthBuffer[0];
        if (extensionLength is < 1 or > 8)
        {
            await WriteChildResponseAsync(false, null, "The document extension is invalid.");
            return;
        }

        var extensionBytes = new byte[extensionLength];
        await input.ReadExactlyAsync(extensionBytes);
        var extension = System.Text.Encoding.ASCII.GetString(extensionBytes).ToLowerInvariant();
        if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
        {
            await WriteChildResponseAsync(false, null, "The document extension is not supported.");
            return;
        }

        var document = new byte[length];
        try
        {
            await input.ReadExactlyAsync(document);
            var analysis = DocumentAnalyzer.Analyse(document, extension);
            await WriteChildResponseAsync(true, analysis, null);
        }
        catch (DocumentValidationException exception)
        {
            await WriteChildResponseAsync(false, null, exception.Message);
        }
        catch
        {
            await WriteChildResponseAsync(false, null, "The document could not be analysed safely.");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(document);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(extensionBytes);
        }
    }
    catch
    {
        // The parent treats an empty/malformed reply as a failed worker request.
        Environment.ExitCode = 2;
    }
}

static async Task WriteChildResponseAsync(bool success, DocumentAnalysis? analysis, string? error)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(new WorkerResponse(1, success, analysis, error));
    if (payload.Length > maximumResponseBytes)
    {
        Environment.ExitCode = 3;
        return;
    }

    var output = Console.OpenStandardOutput();
    await output.WriteAsync(payload);
    await output.FlushAsync();
}

internal sealed record WorkerResponse(int ProtocolVersion, bool Success, DocumentAnalysis? Analysis, string? Error);

internal sealed class WindowsDocumentWorkerService(
    ILogger<WindowsDocumentWorkerService> logger,
    IHostApplicationLifetime applicationLifetime)
    : BackgroundService
{
    private const int RequestTimeoutSeconds = 90;
    private const int IdleShutdownSeconds = 120;
    private const int MaximumDocumentBytes = 20 * 1024 * 1024;
    private const int MaximumResponseBytes = 1024 * 1024;
    private const string PipeName = "QRtoPrint.SmartPrint.DocumentWorker.v1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "Could not lower the idle document worker service priority; parser child processes remain resource-limited.");
        }

        // Create the protected first instance once, then reuse the same instance.
        // FILE_FLAG_FIRST_PIPE_INSTANCE must not be supplied on subsequent
        // CreateNamedPipe calls; reusing this handle also closes the squatting race
        // between uploads while retaining the startup first-instance protection.
        await using var pipe = CreatePipe();
        while (!stoppingToken.IsCancellationRequested)
        {
            using var idleWait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            idleWait.CancelAfter(TimeSpan.FromSeconds(IdleShutdownSeconds));
            try
            {
                await pipe.WaitForConnectionAsync(idleWait.Token);
                await HandleRequestAsync(pipe, stoppingToken);

                // Disconnect waits until the Agent has consumed the full response.
                // Reuse this same protected server instance for the next upload.
                TryDisconnect(pipe);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug("Stopping the on-demand document worker after {IdleSeconds} seconds without uploads.", IdleShutdownSeconds);
                applicationLifetime.StopApplication();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Isolated local document worker rejected or failed a request.");
                if (pipe.IsConnected)
                {
                    try
                    {
                        await WriteResponseAsync(pipe, new WorkerResponse(
                            ProtocolVersion: 1,
                            Success: false,
                            Analysis: null,
                            Error: "The isolated document processor could not complete this request."));
                        pipe.WaitForPipeDrain();
                    }
                    catch (IOException) { }
                }

                // A client can break the connection while sending or reading.
                // Disconnect also resets that broken state so the instance can be reused.
                TryDisconnect(pipe);
            }
        }
    }

    private static void TryDisconnect(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Disconnect();
        }
        catch (IOException)
        {
            // The client may have closed its end while the request was being handled.
        }
        catch (InvalidOperationException)
        {
            // No connection was established or the pipe is already disconnected.
        }
    }

    private static NamedPipeServerStream CreatePipe() =>
        RestrictedNamedPipeServer.Create(PipeName);

    private async Task HandleRequestAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
        var token = timeout.Token;

        var lengthBytes = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(lengthBytes, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > MaximumDocumentBytes)
        {
            await WriteResponseAsync(pipe, new WorkerResponse(1, false, null, "The document is outside the supported processing size range."));
            return;
        }

        var extensionLengthBuffer = new byte[1];
        await pipe.ReadExactlyAsync(extensionLengthBuffer, token);
        var extensionLength = extensionLengthBuffer[0];
        if (extensionLength is < 1 or > 8)
        {
            await WriteResponseAsync(pipe, new WorkerResponse(1, false, null, "The document extension is invalid."));
            return;
        }

        var extensionBytes = new byte[extensionLength];
        await pipe.ReadExactlyAsync(extensionBytes, token);
        var extension = System.Text.Encoding.ASCII.GetString(extensionBytes).ToLowerInvariant();
        if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
        {
            await WriteResponseAsync(pipe, new WorkerResponse(1, false, null, "The document extension is not supported."));
            return;
        }

        var document = new byte[length];
        try
        {
            await pipe.ReadExactlyAsync(document, token);
            var response = await RunRestrictedParserAsync(document, extension, token);
            await WriteResponseAsync(pipe, response);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(document);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(extensionBytes);
        }
    }

    private static async Task<WorkerResponse> RunRestrictedParserAsync(
        byte[] document,
        string extension,
        CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("The isolated local document parser executable is unavailable.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            }
        };
        process.StartInfo.ArgumentList.Add("--analyze-document-request");

        if (!process.Start())
        {
            throw new InvalidOperationException("The isolated parser process could not be started.");
        }

        using var job = WindowsParserJob.TryCreate(process);
        if (job is null)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Windows could not apply the parser memory, CPU and process limits.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, MaximumResponseBytes, cancellationToken);

        try
        {
            // The parse child runs below normal priority, with its own job-object
            // hard cap. Printer spooler and the primary Agent retain CPU headroom.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, document.Length);
            await process.StandardInput.BaseStream.WriteAsync(header, cancellationToken);
            await process.StandardInput.BaseStream.WriteAsync(new[] { checked((byte)extension.Length) }, cancellationToken);
            await process.StandardInput.BaseStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(extension), cancellationToken);
            await process.StandardInput.BaseStream.WriteAsync(document, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken);
            var responseBytes = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0 || responseBytes.Length == 0)
            {
                throw new InvalidOperationException("The isolated parser process failed or exceeded its resource limits.");
            }

            var response = JsonSerializer.Deserialize<WorkerResponse>(responseBytes);
            return response ?? throw new InvalidOperationException("The isolated parser returned an invalid response.");
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) return output.ToArray();
                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException("The isolated parser response exceeded the allowed size.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task WriteResponseAsync(Stream pipe, WorkerResponse response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        if (bytes.Length > MaximumResponseBytes)
        {
            throw new InvalidDataException("The isolated parser response exceeded the allowed size.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
    }
}

internal sealed class WindowsParserJob : IDisposable
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint LimitActiveProcess = 0x00000008;
    private const uint LimitProcessMemory = 0x00000100;
    private const uint LimitKillOnJobClose = 0x00002000;
    private const uint ParserMemoryLimitBytes = 768 * 1024 * 1024;

    private readonly IntPtr handle;

    private WindowsParserJob(IntPtr handle) => this.handle = handle;

    public static WindowsParserJob? TryCreate(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;

        var job = new WindowsParserJob(handle);
        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = LimitActiveProcess | LimitProcessMemory | LimitKillOnJobClose,
                    ActiveProcessLimit = 1
                },
                ProcessMemoryLimit = new UIntPtr(ParserMemoryLimitBytes)
            };
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                job.Dispose();
                return null;
            }

            // Intentionally no fixed CPU quota. The parser runs below-normal priority,
            // so Windows schedules normal-priority printing/spooler work first and lets
            // analysis use otherwise available CPU. Memory and process-count limits remain.
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                job.Dispose();
                return null;
            }

            return job;
        }
        catch
        {
            job.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        uint informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal static class RestrictedNamedPipeServer
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint SddlRevision1 = 1;
    private const int BufferSize = 64 * 1024;

    public static NamedPipeServerStream Create(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The isolated document worker named pipe is Windows-only.");

        IntPtr securityDescriptor = IntPtr.Zero;
        try
        {
            // SERVICE_SID_TYPE_RESTRICTED includes the service SID in the normal
            // token and in the restricting SID list. Grant only SYSTEM (the Agent)
            // and this service's unique SID; do not grant LocalService broadly.
            // Remote clients are rejected by PIPE_REJECT_REMOTE_CLIENTS separately.
            var workerServiceSid = GetRestrictedServiceSid();
            var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;{workerServiceSid})";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    sddl, SddlRevision1, out securityDescriptor, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not create the restricted document-worker pipe security descriptor.");
            }

            var attributes = new NativeSecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<NativeSecurityAttributes>()),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = false
            };
            var pipePath = $@"\\.\pipe\{pipeName}";
            var handle = CreateNamedPipeW(
                pipePath,
                PipeAccessDuplex | FileFlagOverlapped | FileFlagWriteThrough | FileFlagFirstPipeInstance,
                PipeRejectRemoteClients,
                maxInstances: 1,
                outBufferSize: BufferSize,
                inBufferSize: BufferSize,
                defaultTimeout: 0,
                ref attributes);
            if (handle == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not create the restricted local document-worker pipe.");
            }

            var safeHandle = new SafePipeHandle(handle, ownsHandle: true);
            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    safeHandle);
            }
            catch
            {
                safeHandle.Dispose();
                throw;
            }
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
                _ = LocalFree(securityDescriptor);
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenRestrictedSids = 11;

    private static string GetRestrictedServiceSid()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Could not inspect the restricted document-worker service token.");

        try
        {
            _ = GetTokenInformation(token, TokenRestrictedSids, IntPtr.Zero, 0, out var requiredBytes);
            var error = Marshal.GetLastWin32Error();
            if (requiredBytes <= 0 || error != 122)
                throw new Win32Exception(error,
                    "Could not read the restricted document-worker service SID.");

            var buffer = Marshal.AllocHGlobal(requiredBytes);
            try
            {
                if (!GetTokenInformation(token, TokenRestrictedSids, buffer, requiredBytes, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not read the restricted document-worker service SID.");

                var count = Marshal.ReadInt32(buffer);
                var firstGroupOffset = Marshal.OffsetOf<TokenGroupsHeader>(
                    nameof(TokenGroupsHeader.FirstGroup)).ToInt32();
                var groupSize = Marshal.SizeOf<SidAndAttributes>();
                for (var index = 0; index < count; index++)
                {
                    var entry = IntPtr.Add(buffer, firstGroupOffset + index * groupSize);
                    var sidPointer = Marshal.ReadIntPtr(entry);
                    if (sidPointer == IntPtr.Zero)
                        continue;

                    var sid = new SecurityIdentifier(sidPointer);
                    if (sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal))
                        return sid.Value;
                }

                throw new InvalidOperationException(
                    "The LocalService worker is missing its restricted NT SERVICE SID. Reinstall the worker service with restricted SID mode.");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseTokenHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsHeader
    {
        public uint GroupCount;
        public SidAndAttributes FirstGroup;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseTokenHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "CreateNamedPipeW")]
    private static extern IntPtr CreateNamedPipeW(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        int outBufferSize,
        int inBufferSize,
        uint defaultTimeout,
        ref NativeSecurityAttributes securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
