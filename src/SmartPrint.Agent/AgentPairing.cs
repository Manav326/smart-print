using System.Net.Http;
using System.IO;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace SmartPrint.Agent;

public sealed class AgentEnrollmentException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class AgentPairing
{
    public static async Task RunAsync(IConfiguration configuration)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Smart Print Agent can only be paired on Windows.");

        var apiBaseUrl = configuration["Agent:ApiBaseUrl"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            Console.Write("Smart Print API base URL: ");
            apiBaseUrl = Console.ReadLine()?.Trim();
        }

        var code = configuration["Agent:EnrollmentCode"]?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            Console.WriteLine();
            Console.WriteLine("Enter the one-time pairing code generated in your shop workspace.");
            Console.Write("Pairing code: ");
            code = Console.ReadLine()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("A SmartPrint API address and one-time pairing code are required.");

        var credentials = await EnrollAsync(apiBaseUrl, code, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine("This computer is now paired with the shop.");
        Console.WriteLine($"Agent ID: {credentials.AgentDeviceId}");
        Console.WriteLine("The agent secret has been stored using Windows DPAPI (LocalMachine scope).");
        Console.WriteLine("Do not copy or share the secret. Start the QRtoPrint Smart Print Agent service next.");
    }

    public static Uri ValidateApiBaseUrl(string apiBaseUrl)
    {
        if (!Uri.TryCreate(apiBaseUrl?.Trim(), UriKind.Absolute, out var apiUri) ||
            apiUri.Scheme is not ("https" or "http") ||
            apiUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(apiUri.Query) ||
            !string.IsNullOrEmpty(apiUri.Fragment) ||
            !string.IsNullOrEmpty(apiUri.UserInfo))
        {
            throw new InvalidOperationException("The configured SmartPrint API address must be a canonical HTTPS origin.");
        }

        // Clear-text HTTP is acceptable only for an explicitly local development
        // API. Never send a one-time enrollment code or Agent secret over a shop
        // Wi-Fi or other non-loopback HTTP connection.
        if (apiUri.Scheme == Uri.UriSchemeHttp && !apiUri.IsLoopback)
        {
            throw new InvalidOperationException("The SmartPrint API must use HTTPS. HTTP is allowed only for localhost development.");
        }

        return new Uri(apiUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    public static async Task<AgentCredentials> EnrollAsync(
        string apiBaseUrl,
        string enrollmentCode,
        CancellationToken cancellationToken,
        AgentCredentialStore? credentialStore = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Smart Print Agent can only be paired on Windows.");

        var apiUri = ValidateApiBaseUrl(apiBaseUrl);

        if (enrollmentCode.Length != 32 ||
            !enrollmentCode.All(value => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            throw new InvalidOperationException("The one-time enrollment authorization is invalid.");

        var fingerprint = GetMachineFingerprint();
        using var enrollmentHandler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(enrollmentHandler)
        {
            BaseAddress = new Uri(apiUri.ToString().TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        var request = new AgentEnrollmentRequest(enrollmentCode, fingerprint, Environment.MachineName);
        using var response = await client.PostAsJsonAsync("api/v1/agent/enroll", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                try
                {
                    using var errorDocument = JsonDocument.Parse(responseBody);
                    var error = errorDocument.RootElement;
                    var code = error.TryGetProperty("code", out var codeValue)
                        ? codeValue.GetString()
                        : null;
                    var message = error.TryGetProperty("message", out var messageValue)
                        ? messageValue.GetString()
                        : null;

                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        throw new AgentEnrollmentException(
                            code,
                            string.IsNullOrWhiteSpace(message)
                                ? "The shop could not accept this computer enrollment."
                                : message);
                    }
                }
                catch (JsonException)
                {
                    // Fall through to a safe generic HTTP error for an invalid server response.
                }
            }

            throw new InvalidOperationException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "The one-time computer authorization expired or was already used. Please try again.",
                System.Net.HttpStatusCode.Forbidden => "Your shop account is not permitted to connect another computer.",
                _ => $"Computer enrollment failed with HTTP {(int)response.StatusCode}. Please try again."
            });
        }

        var enrolled = await response.Content.ReadFromJsonAsync<AgentEnrollmentResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The server returned an empty pairing response.");

        var credentials = new AgentCredentials(apiUri.ToString().TrimEnd('/'), enrolled.AgentDeviceId, enrolled.AgentSecret, DateTimeOffset.UtcNow);
        (credentialStore ?? new AgentCredentialStore()).Save(credentials);
        return credentials;
    }

    public static async Task<int> CheckExistingAsync(string? configuredApiBaseUrl = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The SmartPrint Agent can only be checked on Windows.");
            return 1;
        }

        AgentCredentials? credentials;
        try
        {
            credentials = new AgentCredentialStore().LoadForCloudRequests(configuredApiBaseUrl);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"The saved SmartPrint pairing could not be read: {exception.Message}");
            return 2;
        }

        if (credentials is null)
        {
            Console.Error.WriteLine("This computer does not have a saved SmartPrint pairing.");
            return 2;
        }

        Uri apiUri;
        try
        {
            apiUri = ValidateApiBaseUrl(credentials.ApiBaseUrl);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"The saved SmartPrint API address is not secure: {exception.Message}");
            return 2;
        }

        try
        {
            using var verificationHandler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(verificationHandler)
            {
                BaseAddress = new Uri(apiUri.ToString().TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(15)
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/agent/heartbeat")
            {
                Content = JsonContent.Create(new
                {
                    version = typeof(AgentPairing).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    operatingSystem = Environment.OSVersion.VersionString,
                    localEndpoints = LocalNetworkEndpointDiscovery.Discover()
                })
            };
            request.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
            request.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);

            using var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("The existing SmartPrint pairing is active.");
                return 0;
            }
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                Console.Error.WriteLine("The saved SmartPrint pairing is no longer active.");
                return 2;
            }

            var detail = await response.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"The existing SmartPrint pairing could not be verified ({(int)response.StatusCode}): {detail}");
            return 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Console.Error.WriteLine($"Could not contact the SmartPrint service to verify this pairing: {exception.Message}");
            return 1;
        }
    }

    private static string GetMachineFingerprint()
    {
        var machineGuid = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid")?.ToString();
        var material = string.IsNullOrWhiteSpace(machineGuid)
            ? $"{Environment.MachineName}|{Environment.OSVersion.VersionString}"
            : machineGuid;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}