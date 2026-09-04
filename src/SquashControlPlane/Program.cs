using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SquashControlPlane;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ControlPlaneState>();
builder.Services.AddSingleton<ExecutionStore>();

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
// Device execution
// ------------------------------------------------------------

app.MapPost(
    "/v1/devices/{deviceId}/executions",
    async (
        string deviceId,
        HttpRequest request,
        ExecuteScriptRequest body,
        ExecutionStore executions,
        ControlPlaneState state) =>
    {
        var idempotencyKey = request.Headers["Idempotency-Key"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.BadRequest(new
            {
                error = "idempotency_key_required"
            });
        }

        if (string.IsNullOrWhiteSpace(body.Script))
        {
            return Results.BadRequest(new
            {
                error = "script_required"
            });
        }

        if (body.TimeoutSeconds <= 0 || body.TimeoutSeconds > 300)
        {
            return Results.BadRequest(new
            {
                error = "invalid_timeout_seconds",
                max = 300
            });
        }

        var created = executions.TryCreate(
            deviceId,
            idempotencyKey,
            body.Script,
            body.TimeoutSeconds,
            out var execution);

        // If this was a retry, return the existing execution.
        if (!created)
        {
            return Results.Ok(new
            {
                execution_id = execution.ExecutionId,
                status = execution.Status.ToString().ToLowerInvariant()
            });
        }

        var dispatched = await state.DispatchExecutionAsync(execution);

        if (dispatched)
        {
            executions.TryUpdate(
                execution.ExecutionId,
                e => e.Status = ExecutionStatus.Running);
        }

        return Results.Accepted(
            $"/v1/executions/{execution.ExecutionId}",
            new
            {
                execution_id = execution.ExecutionId,
                status = execution.Status.ToString().ToLowerInvariant()
            });
    });



app.MapGet(
"/v1/executions/{executionId}",
(string executionId, ExecutionStore executions) =>
{
    var execution = executions.Get(executionId);

    if (execution is null)
    {
        return Results.NotFound(new
        {
            error = "execution_not_found"
        });
    }

    return Results.Ok(new
    {
        execution_id = execution.ExecutionId,
        device_id = execution.DeviceId,
        status = execution.Status.ToString().ToLowerInvariant(),
        exit_code = execution.ExitCode,
        stdout = execution.Stdout,
        stderr = execution.Stderr,
        duration_ms = execution.DurationMs,
        output_truncated = execution.OutputTruncated,
        created_at = execution.CreatedAt,
        updated_at = execution.UpdatedAt
    });
});


// ------------------------------------------------------------
// Agent WebSocket
// ------------------------------------------------------------

app.Map("/v1/agent/connect", async (
    HttpContext context,
    ControlPlaneState state,
    ExecutionStore executions) =>
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
        await ReceiveAgentMessages(
            socket,
            device,
            state,
            executions);
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
    Device device,
    ControlPlaneState state,
    ExecutionStore executions)
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
            var executionId =
                document.RootElement
                    .GetProperty("execution_id")
                    .GetString();

            if (string.IsNullOrWhiteSpace(executionId))
            {
                Console.WriteLine(
                    "[EXECUTION RESULT] Missing execution_id");
                continue;
            }

            var status =
                document.RootElement
                    .GetProperty("status")
                    .GetString();

            var exitCode =
                document.RootElement.TryGetProperty(
                    "exit_code",
                    out var exitCodeElement) &&
                exitCodeElement.ValueKind != JsonValueKind.Null
                    ? exitCodeElement.GetInt32()
                    : (int?)null;

            var stdout =
                document.RootElement.TryGetProperty(
                    "stdout",
                    out var stdoutElement)
                    ? stdoutElement.GetString() ?? ""
                    : "";

            var stderr =
                document.RootElement.TryGetProperty(
                    "stderr",
                    out var stderrElement)
                    ? stderrElement.GetString() ?? ""
                    : "";

            var durationMs =
                document.RootElement.TryGetProperty(
                    "duration_ms",
                    out var durationElement)
                    ? durationElement.GetInt64()
                    : (long?)null;

            var outputTruncated =
                document.RootElement.TryGetProperty(
                    "output_truncated",
                    out var truncatedElement) &&
                truncatedElement.GetBoolean();

            var updated = executions.TryUpdate(
                executionId,
                execution =>
                {
                    execution.Status =
                        status switch
                        {
                            "succeeded" =>
                                ExecutionStatus.Succeeded,

                            "failed" =>
                                ExecutionStatus.Failed,

                            "timed_out" =>
                                ExecutionStatus.TimedOut,

                            _ =>
                                ExecutionStatus.Failed
                        };

                    execution.ExitCode = exitCode;
                    execution.Stdout = stdout;
                    execution.Stderr = stderr;
                    execution.DurationMs = durationMs;
                    execution.OutputTruncated = outputTruncated;
                });

            if (!updated)
            {
                Console.WriteLine(
                    $"[EXECUTION RESULT] " +
                    $"Unknown execution={executionId}");

                continue;
            }

            Console.WriteLine(
                $"[EXECUTION COMPLETE] " +
                $"Execution={executionId} Status={status}");
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

public sealed record ExecuteScriptRequest(
    string Script,
    int TimeoutSeconds = 30);

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

    public async Task<bool> DispatchExecutionAsync(
    ExecutionRecord execution)
{
    if (!Devices.TryGetValue(execution.DeviceId, out var device))
    {
        return false;
    }

    var socket = device.Socket;

    if (socket is null || socket.State != WebSocketState.Open)
    {
        return false;
    }

    var scriptSha256 =
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(execution.Script)))
        .ToLowerInvariant();

    var message = JsonSerializer.Serialize(
        new
        {
            type = "execute",
            message_id = Guid.NewGuid().ToString(),
            execution_id = execution.ExecutionId,
            script = execution.Script,
            timeout_seconds = execution.TimeoutSeconds,
            script_sha256 = scriptSha256
        });

    try
    {
        var bytes = Encoding.UTF8.GetBytes(message);

        await socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        Console.WriteLine(
            $"[EXECUTION DISPATCHED] " +
            $"Execution={execution.ExecutionId} " +
            $"Device={execution.DeviceId}");

        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"[EXECUTION DISPATCH ERROR] " +
            $"Execution={execution.ExecutionId}: {ex.Message}");

        return false;
    }
}
}