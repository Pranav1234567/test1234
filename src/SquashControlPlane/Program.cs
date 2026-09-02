using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ControlPlaneState>();

var app = builder.Build();

app.UseWebSockets();

const string BootstrapToken = "dev-bootstrap-token";

// ------------------------------------------------------------
// Health check
// ------------------------------------------------------------

app.MapGet("/", () =>
    Results.Ok(new
    {
        service = "SquashControlPlane",
        status = "ok"
    }));

// ------------------------------------------------------------
// Install Script
// ------------------------------------------------------------

app.MapGet("/v1/agent/install-script", (HttpContext context) =>
{
    var scriptPath = Path.GetFullPath(
        Path.Combine(
            builder.Environment.ContentRootPath,
            "..",
            "..",
            "scripts",
            "install-agent.ps1"
        )
    );

    if (!File.Exists(scriptPath))
    {
        return Results.NotFound(new
        {
            error = "install_script_not_found",
            path = scriptPath
        });
    }

    return Results.Text(
        File.ReadAllText(scriptPath),
        "text/plain"
    );
});

// ------------------------------------------------------------
// Download the Windows agent package
// ------------------------------------------------------------

app.MapGet("/v1/agent/download", (HttpContext context) =>
{
    var packagePath = Path.GetFullPath(
        Path.Combine(
            builder.Environment.ContentRootPath,
            "..",
            "..",
            "installer",
            "SquashAgent.zip"
        )
    );

    if (!File.Exists(packagePath))
    {
        return Results.NotFound(new
        {
            error = "agent_package_not_found",
            path = packagePath
        });
    }

    return Results.File(
        packagePath,
        "application/zip",
        "SquashAgent.zip"
    );
});


// ------------------------------------------------------------
// Enrollment
// ------------------------------------------------------------

app.MapPost("/v1/enrollment", async (
    HttpRequest request,
    ControlPlaneState state) =>
{
    var enrollment =
        await JsonSerializer.DeserializeAsync<EnrollmentRequest>(
            request.Body);

    if (enrollment is null)
    {
        return Results.BadRequest(new
        {
            error = "invalid_request"
        });
    }

    if (enrollment.BootstrapToken != BootstrapToken)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(enrollment.DeviceId))
    {
        return Results.BadRequest(new
        {
            error = "device_id_required"
        });
    }

    var deviceToken =
        Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));

    var device = new Device
    {
        DeviceId = enrollment.DeviceId,
        Hostname = enrollment.Hostname,
        PublicKey = enrollment.PublicKey,
        AgentVersion = enrollment.AgentVersion,
        DeviceToken = deviceToken,
        LastSeen = DateTimeOffset.UtcNow
    };

    state.Devices[device.DeviceId] = device;

    Console.WriteLine(
        $"[ENROLLMENT] Device={device.DeviceId} " +
        $"Hostname={device.Hostname}");

    return Results.Ok(
        new EnrollmentResponse(
            device.DeviceId,
            deviceToken));
});


// ------------------------------------------------------------
// Device status
// ------------------------------------------------------------

app.MapGet("/v1/devices", (ControlPlaneState state) =>
{
    var devices = state.Devices.Values.Select(device => new
    {
        device.DeviceId,
        device.Hostname,
        device.AgentVersion,
        status = device.Socket?.State == WebSocketState.Open ? "ONLINE" : "OFFLINE",
        connected = device.Socket?.State == WebSocketState.Open,
        device.LastSeen
    });

    return Results.Ok(devices);
});

app.MapGet("/v1/devices/{deviceId}", (string deviceId, ControlPlaneState state) =>
{
    if (!state.Devices.TryGetValue(deviceId, out var device))
        return Results.NotFound(new { error = "device_not_found" });

    var online = device.Socket?.State == WebSocketState.Open;

    return Results.Ok(new
    {
        device.DeviceId,
        device.Hostname,
        device.AgentVersion,
        status = online ? "ONLINE" : "OFFLINE",
        connected = online,
        device.LastSeen
    });
});


// ------------------------------------------------------------
// Agent WebSocket
// ------------------------------------------------------------

app.Map("/v1/agent/connect", async (
    HttpContext context,
    ControlPlaneState state) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync(
            "WebSocket connection required.");
        return;
    }

    var deviceId =
        context.Request.Headers["X-Device-Id"]
            .FirstOrDefault();

    var authorization =
        context.Request.Headers.Authorization
            .FirstOrDefault();

    if (string.IsNullOrWhiteSpace(deviceId) ||
        string.IsNullOrWhiteSpace(authorization) ||
        !authorization.StartsWith("Bearer ",
            StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode =
            StatusCodes.Status401Unauthorized;
        return;
    }

    var token =
        authorization["Bearer ".Length..].Trim();

    if (!state.Devices.TryGetValue(
            deviceId,
            out var device))
    {
        context.Response.StatusCode =
            StatusCodes.Status401Unauthorized;
        return;
    }

    if (!SecureEquals(token, device.DeviceToken))
    {
        context.Response.StatusCode =
            StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket =
        await context.WebSockets.AcceptWebSocketAsync();

    device.Socket = socket;
    device.LastSeen = DateTimeOffset.UtcNow;

    Console.WriteLine(
        $"[CONNECTED] Device={device.DeviceId}");

    try
    {
        await ReceiveAgentMessages(socket, device);
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"[ERROR] Device={device.DeviceId}: {ex.Message}");
    }
    finally
    {
        device.Socket = null;

        Console.WriteLine(
            $"[DISCONNECTED] Device={device.DeviceId}");
    }
});


// ------------------------------------------------------------
// Start server
// ------------------------------------------------------------

app.Run();


// ============================================================
// WebSocket message handling
// ============================================================

static async Task ReceiveAgentMessages(
    WebSocket socket,
    Device device)
{
    var buffer = new byte[64 * 1024];

    while (socket.State == WebSocketState.Open)
    {
        using var message = new MemoryStream();

        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(
                buffer,
                CancellationToken.None);

            if (result.MessageType ==
                WebSocketMessageType.Close)
            {
                return;
            }

            message.Write(
                buffer,
                0,
                result.Count);

        } while (!result.EndOfMessage);

        device.LastSeen = DateTimeOffset.UtcNow;

        var json =
            Encoding.UTF8.GetString(
                message.ToArray());

        Console.WriteLine(
            $"[MESSAGE] Device={device.DeviceId}: {json}");

        using var document =
            JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty(
                "type",
                out var typeElement))
        {
            continue;
        }

        var type = typeElement.GetString();

        // ----------------------------------------------------
        // Heartbeat
        // ----------------------------------------------------

        if (type == "heartbeat")
        {
            var response =
                JsonSerializer.Serialize(
                    new
                    {
                        type = "heartbeat_ack"
                    });

            await SendAsync(
                socket,
                response);
        }

        // ----------------------------------------------------
        // Execution result
        // ----------------------------------------------------

        else if (type == "execution_result")
        {
            Console.WriteLine(
                $"[EXECUTION RESULT] {json}");
        }

        // ----------------------------------------------------
        // Execution acknowledgement
        // ----------------------------------------------------

        else if (type == "execution_ack")
        {
            Console.WriteLine(
                $"[EXECUTION ACK] {json}");
        }
    }
}


// ============================================================
// Helpers
// ============================================================

static async Task SendAsync(
    WebSocket socket,
    string json)
{
    var bytes =
        Encoding.UTF8.GetBytes(json);

    await socket.SendAsync(
        bytes,
        WebSocketMessageType.Text,
        true,
        CancellationToken.None);
}


static bool SecureEquals(
    string a,
    string b)
{
    var aa =
        Encoding.UTF8.GetBytes(a);

    var bb =
        Encoding.UTF8.GetBytes(b);

    return aa.Length == bb.Length &&
           CryptographicOperations.FixedTimeEquals(
               aa,
               bb);
}


// ============================================================
// Models
// ============================================================

record EnrollmentRequest(
    [property: JsonPropertyName("bootstrap_token")]
    string BootstrapToken,

    [property: JsonPropertyName("device_id")]
    string DeviceId,

    [property: JsonPropertyName("hostname")]
    string Hostname,

    [property: JsonPropertyName("public_key")]
    string PublicKey,

    [property: JsonPropertyName("agent_version")]
    string AgentVersion);


record EnrollmentResponse(
    [property: JsonPropertyName("device_id")]
    string DeviceId,

    [property: JsonPropertyName("device_token")]
    string DeviceToken);


sealed class Device
{
    public required string DeviceId { get; init; }

    public required string Hostname { get; init; }

    public required string PublicKey { get; init; }

    public required string AgentVersion { get; init; }

    public required string DeviceToken { get; init; }

    public WebSocket? Socket { get; set; }

    public DateTimeOffset LastSeen { get; set; }
}


sealed class ControlPlaneState
{
    public ConcurrentDictionary<string, Device> Devices { get; } = new();
}