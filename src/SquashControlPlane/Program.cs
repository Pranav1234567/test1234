using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ControlPlaneStore>();
builder.Services.AddHostedService<ExecutionMonitor>();

var app = builder.Build();
app.UseWebSockets();

var store = app.Services.GetRequiredService<ControlPlaneStore>();
await store.InitializeAsync(CancellationToken.None);

static string? CallerApiKey(HttpRequest request) =>
    request.Headers.Authorization.FirstOrDefault() is { } auth &&
    auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? auth["Bearer ".Length..].Trim()
        : null;

static bool ConstantEquals(string? a, string? b)
{
    if (a is null || b is null) return false;
    var aa = Encoding.UTF8.GetBytes(a);
    var bb = Encoding.UTF8.GetBytes(b);
    return aa.Length == bb.Length && CryptographicOperations.FixedTimeEquals(aa, bb);
}

static bool IsApiAuthorized(HttpRequest request)
{
    var configured = Environment.GetEnvironmentVariable("SQUASH_API_KEY") ?? "dev-api-key";
    return ConstantEquals(CallerApiKey(request), configured);
}

static string BootstrapToken() => Environment.GetEnvironmentVariable("SQUASH_BOOTSTRAP_TOKEN") ?? "dev-bootstrap-token";

app.MapGet("/", () => Results.Ok(new { service = "SquashControlPlane", status = "ok" }));

app.MapPost("/v1/enrollment", async (EnrollmentRequest request, ControlPlaneStore state, CancellationToken ct) =>
{
    if (!ConstantEquals(request.BootstrapToken, BootstrapToken()))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.DeviceId))
        return Results.BadRequest(new { error = "device_id_required" });
    if (string.IsNullOrWhiteSpace(request.PublicKey))
        return Results.BadRequest(new { error = "public_key_required" });

    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var enrolled = await state.EnrollAsync(request, token, ct);
    if (enrolled is null)
        return Results.Conflict(new { error = "bootstrap_token_already_used_or_device_already_enrolled" });

    Console.WriteLine($"[ENROLLMENT] Device={request.DeviceId} Hostname={request.Hostname}");
    return Results.Ok(new EnrollmentResponse(request.DeviceId, token));
});

app.MapGet("/v1/devices", async (HttpRequest request, ControlPlaneStore state, CancellationToken ct) =>
{
    if (!IsApiAuthorized(request)) return Results.Unauthorized();
    return Results.Ok(await state.ListDevicesAsync(ct));
});

app.MapGet("/v1/devices/{deviceId}", async (string deviceId, HttpRequest request, ControlPlaneStore state, CancellationToken ct) =>
{
    if (!IsApiAuthorized(request)) return Results.Unauthorized();
    var device = await state.GetDeviceAsync(deviceId, ct);
    return device is null ? Results.NotFound(new { error = "device_not_found" }) : Results.Ok(device);
});

app.MapPost("/v1/devices/{deviceId}/executions", async (
    string deviceId,
    HttpRequest request,
    ExecuteRequest body,
    ControlPlaneStore state,
    CancellationToken ct) =>
{
    if (!IsApiAuthorized(request)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.Script)) return Results.BadRequest(new { error = "script_required" });
    if (body.Script.Length > state.MaxScriptChars) return Results.BadRequest(new { error = "script_too_large", max_chars = state.MaxScriptChars });
    if (body.TimeoutSeconds is < 1 or > 300) return Results.BadRequest(new { error = "timeout_seconds_out_of_range", min = 1, max = 300 });
    if (string.IsNullOrWhiteSpace(body.IdempotencyKey)) return Results.BadRequest(new { error = "idempotency_key_required" });

    var device = await state.GetDeviceAsync(deviceId, ct);
    if (device is null) return Results.NotFound(new { error = "device_not_found" });

    var created = await state.CreateExecutionAsync(deviceId, body, ct);
    if (created.Error is not null)
        return Results.Conflict(new { error = created.Error });

    var execution = created.Execution!;
    await state.DispatchPendingAsync(deviceId, ct);

    return Results.Accepted($"/v1/executions/{execution.ExecutionId}", new
    {
        execution_id = execution.ExecutionId,
        status = execution.Status
    });
});

app.MapGet("/v1/executions/{executionId}", async (string executionId, HttpRequest request, ControlPlaneStore state, CancellationToken ct) =>
{
    if (!IsApiAuthorized(request)) return Results.Unauthorized();
    var execution = await state.GetExecutionAsync(executionId, ct);
    return execution is null ? Results.NotFound(new { error = "execution_not_found" }) : Results.Ok(execution);
});

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

app.Map("/v1/agent/connect", async (HttpContext context, ControlPlaneStore state) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket connection required.");
        return;
    }

    var deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault();
    var auth = context.Request.Headers.Authorization.FirstOrDefault();
    var token = auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? auth["Bearer ".Length..].Trim() : null;

    if (string.IsNullOrWhiteSpace(deviceId) || !await state.AuthenticateDeviceAsync(deviceId, token, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    state.SetConnection(deviceId, socket);
    Console.WriteLine($"[CONNECTED] Device={deviceId}");

    try
    {
        await state.DispatchPendingAsync(deviceId, context.RequestAborted);
        await ReceiveAgentMessagesAsync(socket, deviceId, state, context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] Device={deviceId}: {ex.Message}");
    }
    finally
    {
        state.ClearConnection(deviceId, socket);
        Console.WriteLine($"[DISCONNECTED] Device={deviceId}");
    }
});

app.Run();

static async Task ReceiveAgentMessagesAsync(WebSocket socket, string deviceId, ControlPlaneStore state, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 2 * 1024 * 1024) throw new InvalidOperationException("WebSocket message exceeds 2MB.");
        } while (!result.EndOfMessage);

        using var doc = JsonDocument.Parse(message.ToArray());
        var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "heartbeat")
        {
            await state.SendAsync(deviceId, new { type = "heartbeat_ack" }, ct);
        }
        else if (type == "execution_ack")
        {
            var executionId = doc.RootElement.GetProperty("execution_id").GetString()!;
            await state.MarkRunningAsync(executionId, ct);
        }
        else if (type == "execution_result")
        {
            var resultMessage = JsonSerializer.Deserialize<ExecutionResultMessage>(message.ToArray())!;
            await state.CompleteExecutionAsync(resultMessage, ct);
        }
    }
}

public record EnrollmentRequest(
    [property: JsonPropertyName("bootstrap_token")] string BootstrapToken,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("public_key")] string PublicKey,
    [property: JsonPropertyName("agent_version")] string AgentVersion);

public record EnrollmentResponse(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_token")] string DeviceToken);

public record ExecuteRequest(
    [property: JsonPropertyName("script")] string Script,
    [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds = 30,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey = "");

public record ExecutionResultMessage(
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("output_truncated")] bool OutputTruncated,
    [property: JsonPropertyName("error_code")] string? ErrorCode);

public sealed record DeviceView(
    string DeviceId, string Hostname, string AgentVersion, string Status, bool Connected, DateTimeOffset? LastSeen);

public sealed record ExecutionView(
    string ExecutionId, string DeviceId, string Status, string Script, int TimeoutSeconds,
    string IdempotencyKey, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int? ExitCode, string Stdout, string Stderr, long? DurationMs, bool OutputTruncated, string? ErrorCode);

public sealed class ControlPlaneStore
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, DeviceConnection> _connections = new();
    public int MaxScriptChars { get; } = 1_000_000;

    public ControlPlaneStore(IHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "controlplane.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        var cmd = db.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS devices (
          device_id TEXT PRIMARY KEY, hostname TEXT NOT NULL, public_key TEXT NOT NULL,
          agent_version TEXT NOT NULL, device_token_hash TEXT NOT NULL, enrolled_at TEXT NOT NULL,
          last_seen TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS bootstrap_tokens (
          token_hash TEXT PRIMARY KEY, used_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS executions (
          execution_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, script TEXT NOT NULL,
          timeout_seconds INTEGER NOT NULL, idempotency_key TEXT NOT NULL,
          status TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
          exit_code INTEGER NULL, stdout TEXT NOT NULL DEFAULT '', stderr TEXT NOT NULL DEFAULT '',
          duration_ms INTEGER NULL, output_truncated INTEGER NOT NULL DEFAULT 0, error_code TEXT NULL,
          UNIQUE(device_id, idempotency_key)
        );
        CREATE INDEX IF NOT EXISTS ix_executions_device_status ON executions(device_id, status);
        """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DeviceIdentityRecord?> EnrollAsync(EnrollmentRequest request, string token, CancellationToken ct)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        var tokenHash = Hash(request.BootstrapToken);
        var check = db.CreateCommand(); check.Transaction = tx;
        check.CommandText = "SELECT 1 FROM bootstrap_tokens WHERE token_hash=$h"; check.Parameters.AddWithValue("$h", tokenHash);
        if (await check.ExecuteScalarAsync(ct) is not null) return null;
        var existing = db.CreateCommand(); existing.Transaction = tx;
        existing.CommandText = "SELECT 1 FROM devices WHERE device_id=$id"; existing.Parameters.AddWithValue("$id", request.DeviceId);
        if (await existing.ExecuteScalarAsync(ct) is not null) return null;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var insertToken = db.CreateCommand(); insertToken.Transaction = tx;
        insertToken.CommandText = "INSERT INTO bootstrap_tokens(token_hash,used_at) VALUES($h,$now)";
        insertToken.Parameters.AddWithValue("$h", tokenHash); insertToken.Parameters.AddWithValue("$now", now); await insertToken.ExecuteNonQueryAsync(ct);
        var insert = db.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO devices(device_id,hostname,public_key,agent_version,device_token_hash,enrolled_at) VALUES($id,$host,$pk,$ver,$th,$now)";
        insert.Parameters.AddWithValue("$id", request.DeviceId); insert.Parameters.AddWithValue("$host", request.Hostname); insert.Parameters.AddWithValue("$pk", request.PublicKey); insert.Parameters.AddWithValue("$ver", request.AgentVersion); insert.Parameters.AddWithValue("$th", Hash(token)); insert.Parameters.AddWithValue("$now", now);
        // The hash above is intentionally replaced below with the actual device credential hash.
        insert.Parameters["$th"].Value = Hash(token);
        await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return new DeviceIdentityRecord(request.DeviceId, request.Hostname, request.PublicKey, request.AgentVersion);
    }

    private static bool ConstantEquals(string a, string b)
    {
    var left = System.Text.Encoding.UTF8.GetBytes(a);
    var right = System.Text.Encoding.UTF8.GetBytes(b);

    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        left,
        right);
    }

    public async Task<bool> AuthenticateDeviceAsync(string deviceId, string? token, CancellationToken ct)
    {
        if (token is null) return false;
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct);
        var cmd = db.CreateCommand(); cmd.CommandText = "SELECT device_token_hash FROM devices WHERE device_id=$id"; cmd.Parameters.AddWithValue("$id", deviceId);
        var stored = await cmd.ExecuteScalarAsync(ct);
        return stored is string hash && ConstantEquals(hash, Hash(token));
    }

    public void SetConnection(string deviceId, WebSocket socket) => _connections.AddOrUpdate(deviceId, _ => new DeviceConnection(socket), (_, old) => { try { old.Socket.Abort(); } catch { } return new DeviceConnection(socket); });
    public void ClearConnection(string deviceId, WebSocket socket) { if (_connections.TryGetValue(deviceId, out var c) && ReferenceEquals(c.Socket, socket)) _connections.TryRemove(deviceId, out _); }

    public async Task SendAsync(string deviceId, object message, CancellationToken ct)
    {
        if (!_connections.TryGetValue(deviceId, out var connection) || connection.Socket.State != WebSocketState.Open)
            throw new InvalidOperationException("Device is not connected.");
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await connection.SendLock.WaitAsync(ct);
        try { await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { connection.SendLock.Release(); }
    }

    public async Task<List<DeviceView>> ListDevicesAsync(CancellationToken ct)
    {
        var result = new List<DeviceView>();
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct);
        var cmd = db.CreateCommand(); cmd.CommandText = "SELECT device_id,hostname,agent_version,last_seen FROM devices ORDER BY hostname";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) { var id = reader.GetString(0); result.Add(new DeviceView(id, reader.GetString(1), reader.GetString(2), IsOnline(id) ? "ONLINE" : "OFFLINE", IsOnline(id), reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)))); }
        return result;
    }

    public async Task<DeviceView?> GetDeviceAsync(string id, CancellationToken ct) => (await ListDevicesAsync(ct)).FirstOrDefault(x => x.DeviceId == id);

    public async Task<(ExecutionView? Execution, string? Error)> CreateExecutionAsync(string deviceId, ExecuteRequest request, CancellationToken ct)
    {
        var existing = await GetByIdempotencyAsync(deviceId, request.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.Script != request.Script || existing.TimeoutSeconds != request.TimeoutSeconds) return (null, "idempotency_key_reused_with_different_request");
            return (existing, null);
        }
        var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct);
        var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO executions(execution_id,device_id,script,timeout_seconds,idempotency_key,status,created_at,updated_at) VALUES($id,$d,$s,$t,$k,'queued',$now,$now)";
        cmd.Parameters.AddWithValue("$id",id); cmd.Parameters.AddWithValue("$d",deviceId); cmd.Parameters.AddWithValue("$s",request.Script); cmd.Parameters.AddWithValue("$t",request.TimeoutSeconds); cmd.Parameters.AddWithValue("$k",request.IdempotencyKey); cmd.Parameters.AddWithValue("$now",now.ToString("O"));
        try { await cmd.ExecuteNonQueryAsync(ct); } catch (SqliteException) { return (await GetByIdempotencyAsync(deviceId, request.IdempotencyKey, ct), null); }
        return (await GetExecutionAsync(id, ct), null);
    }

    private async Task<ExecutionView?> GetByIdempotencyAsync(string deviceId, string key, CancellationToken ct)
    {
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd = db.CreateCommand(); cmd.CommandText = "SELECT * FROM executions WHERE device_id=$d AND idempotency_key=$k"; cmd.Parameters.AddWithValue("$d",deviceId); cmd.Parameters.AddWithValue("$k",key); await using var r = await cmd.ExecuteReaderAsync(ct); return await r.ReadAsync(ct) ? ReadExecution(r) : null;
    }

    public async Task<ExecutionView?> GetExecutionAsync(string id, CancellationToken ct)
    {
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd = db.CreateCommand(); cmd.CommandText = "SELECT * FROM executions WHERE execution_id=$id"; cmd.Parameters.AddWithValue("$id",id); await using var r = await cmd.ExecuteReaderAsync(ct); return await r.ReadAsync(ct) ? ReadExecution(r) : null;
    }

    public async Task DispatchPendingAsync(string deviceId, CancellationToken ct)
    {
        if (!IsOnline(deviceId)) return;
        var jobs = await GetPendingForDeviceAsync(deviceId, ct);
        foreach (var job in jobs)
        {
            if (!IsOnline(deviceId)) break;
            await MarkDispatchedAsync(job.ExecutionId, ct);
            try
            {
                await SendAsync(deviceId, new { type="execute", message_id=Guid.NewGuid().ToString("N"), execution_id=job.ExecutionId, script=job.Script, timeout_seconds=job.TimeoutSeconds, script_sha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.Script))).ToLowerInvariant() }, ct);
            }
            catch { }
        }
    }

    private async Task<List<ExecutionView>> GetPendingForDeviceAsync(string deviceId, CancellationToken ct)
    {
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd = db.CreateCommand(); cmd.CommandText = "SELECT * FROM executions WHERE device_id=$d AND status IN ('queued','dispatched') ORDER BY created_at"; cmd.Parameters.AddWithValue("$d",deviceId); await using var r = await cmd.ExecuteReaderAsync(ct); var list = new List<ExecutionView>(); while(await r.ReadAsync(ct)) list.Add(ReadExecution(r)); return list;
    }

    private async Task MarkDispatchedAsync(string id, CancellationToken ct) { await UpdateStatusAsync(id,"dispatched",null,ct); }
    public async Task MarkRunningAsync(string id, CancellationToken ct) { await UpdateStatusAsync(id,"running",null,ct); }

    public async Task CompleteExecutionAsync(ExecutionResultMessage result, CancellationToken ct)
    {
        var status = result.Status switch { "completed" => "completed", "timeout" => "timed_out", "failed" => "failed", _ => "failed" };
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE executions SET status=$s,updated_at=$now,exit_code=$e,stdout=$o,stderr=$err,duration_ms=$d,output_truncated=$tr,error_code=$ec WHERE execution_id=$id";
        cmd.Parameters.AddWithValue("$s",status); cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$e",(object?)result.ExitCode ?? DBNull.Value); cmd.Parameters.AddWithValue("$o",result.Stdout ?? ""); cmd.Parameters.AddWithValue("$err",result.Stderr ?? ""); cmd.Parameters.AddWithValue("$d",result.DurationMs); cmd.Parameters.AddWithValue("$tr",result.OutputTruncated ? 1 : 0); cmd.Parameters.AddWithValue("$ec",(object?)result.ErrorCode ?? DBNull.Value); cmd.Parameters.AddWithValue("$id",result.ExecutionId); await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateStatusAsync(string id,string status,string? error,CancellationToken ct){ await using var db=new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd=db.CreateCommand(); cmd.CommandText="UPDATE executions SET status=$s,updated_at=$now,error_code=COALESCE($e,error_code) WHERE execution_id=$id AND status NOT IN ('completed','failed','timed_out','unreachable')"; cmd.Parameters.AddWithValue("$s",status); cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$e",(object?)error??DBNull.Value); cmd.Parameters.AddWithValue("$id",id); await cmd.ExecuteNonQueryAsync(ct); }

    public async Task MarkUnreachableAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30).ToString("O");
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd=db.CreateCommand(); cmd.CommandText="UPDATE executions SET status='unreachable',updated_at=$now,error_code='DEVICE_UNREACHABLE' WHERE status IN ('queued','dispatched') AND created_at < $cutoff"; cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$cutoff",cutoff); await cmd.ExecuteNonQueryAsync(ct);
    }

    private bool IsOnline(string id) => _connections.TryGetValue(id,out var c) && c.Socket.State == WebSocketState.Open;
    private static ExecutionView ReadExecution(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(5),r.GetString(2),r.GetInt32(3),r.GetString(4),DateTimeOffset.Parse(r.GetString(6)),DateTimeOffset.Parse(r.GetString(7)),r.IsDBNull(8)?null:r.GetInt32(8),r.GetString(9),r.GetString(10),r.IsDBNull(11)?null:r.GetInt64(11),r.GetInt32(12)!=0,r.IsDBNull(13)?null:r.GetString(13));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record DeviceIdentityRecord(string DeviceId,string Hostname,string PublicKey,string AgentVersion);
sealed class DeviceConnection(WebSocket socket) { public WebSocket Socket { get; } = socket; public SemaphoreSlim SendLock { get; } = new(1,1); }

public sealed class ExecutionMonitor(ControlPlaneStore store, ILogger<ExecutionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await store.MarkUnreachableAsync(stoppingToken); } catch (Exception ex) { logger.LogWarning(ex,"Execution monitor failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
