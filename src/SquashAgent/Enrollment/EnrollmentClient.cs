using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using SquashAgent.Identity;

namespace SquashAgent.Enrollment;

public sealed class EnrollmentClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public EnrollmentClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<DeviceIdentity> EnrollAsync(string bootstrapToken, string deviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bootstrapToken))
            throw new InvalidOperationException("Bootstrap token is required for first enrollment.");

        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKey();
        var publicKey = rsa.ExportSubjectPublicKeyInfo();

        var request = new EnrollmentRequest(
            bootstrapToken,
            deviceId,
            Environment.MachineName,
            Convert.ToBase64String(publicKey),
            typeof(EnrollmentClient).Assembly.GetName().Version?.ToString() ?? "0.1.0");

        using var response = await _http.PostAsJsonAsync(_baseUrl + "/v1/enrollment", request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: ct)
                     ?? throw new InvalidOperationException("Empty enrollment response.");

        // DPAPI CurrentUser is not suitable for a Windows service if the service account changes.
        // LocalMachine scope makes the credential decryptable by the service account on this host.
        var protectedKey = ProtectedData.Protect(
            privateKey,
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);
        var protectedToken = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(result.DeviceToken),
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);

        return new DeviceIdentity
        {
            DeviceId = result.DeviceId,
            PublicKeyPem = Convert.ToBase64String(publicKey),
            PrivateKeyProtectedBase64 = Convert.ToBase64String(protectedKey),
            DeviceTokenProtectedBase64 = Convert.ToBase64String(protectedToken)
        };
    }

    private sealed record EnrollmentRequest(
        [property: JsonPropertyName("bootstrap_token")] string BootstrapToken,
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("hostname")] string Hostname,
        [property: JsonPropertyName("public_key")] string PublicKey,
        [property: JsonPropertyName("agent_version")] string AgentVersion);

    private sealed record EnrollmentResponse(
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("device_token")] string DeviceToken);
}
