using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;

namespace SquashAgent.Identity;

public sealed class DeviceIdentity
{
    public required string DeviceId { get; init; }
    public required string PublicKeyPem { get; init; }
    public required string PrivateKeyProtectedBase64 { get; init; }
    public required string DeviceTokenProtectedBase64 { get; init; }
}

public sealed class DeviceIdentityStore
{
    private const string RegistryPath = @"SOFTWARE\SquashAgent";
    private const string DeviceIdName = "DeviceId";
    private readonly string _path;

    public DeviceIdentityStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "identity.json");
    }

    public async Task<DeviceIdentity?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return null;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<DeviceIdentity>(stream, cancellationToken: ct);
    }

    public async Task SaveAsync(DeviceIdentity identity, CancellationToken ct)
    {
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, identity, cancellationToken: ct);
        File.Move(tmp, _path, true);
    }

    public static string GetStableDeviceId()
    {
        var machineGuid = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
            "MachineGuid",
            null)?.ToString();

        if (string.IsNullOrWhiteSpace(machineGuid))
            throw new InvalidOperationException("Windows MachineGuid is unavailable.");

        // Deterministic UUID-like ID from the OS machine identity.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("squash-device:" + machineGuid));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // UUID v5-like marker.
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString();
    }
}
