using System.Net.WebSockets;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using SquashAgent.Configuration;
using SquashAgent.Execution;
using SquashAgent.Identity;
using SquashAgent.Protocol;
using SquashAgent.Storage;

namespace SquashAgent.Connection;

public sealed class AgentConnection
{
    private readonly AgentOptions _options;
    private readonly DeviceIdentityStore _identityStore;
    private readonly ExecutionStore _executionStore;
    private readonly PowerShellExecutor _executor;
    private readonly ILogger<AgentConnection> _logger;
    private string? _deviceToken;

    public AgentConnection(
        AgentOptions options,
        DeviceIdentityStore identityStore,
        ExecutionStore executionStore,
        PowerShellExecutor executor,
        ILogger<AgentConnection> logger)
    {
        _options = options;
        _identityStore = identityStore;
        _executionStore = executionStore;
        _executor = executor;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var identity = await _identityStore.LoadAsync(ct)
            ?? throw new InvalidOperationException("Agent is not enrolled.");

        if (string.IsNullOrWhiteSpace(identity.DeviceTokenProtectedBase64))
            throw new InvalidOperationException("Device credential is missing.");

        var protectedToken = Convert.FromBase64String(identity.DeviceTokenProtectedBase64);
        _deviceToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(
            protectedToken, optionalEntropy: null, scope: DataProtectionScope.LocalMachine));

        var delay = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(identity, ct);
                delay = 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent connection failed; retrying in {Delay}s", delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                delay = Math.Min(delay * 2, _options.ReconnectMaxSeconds);
            }
        }
    }

    private async Task ConnectAndRunAsync(DeviceIdentity identity, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + _deviceToken);
        ws.Options.SetRequestHeader("X-Device-Id", identity.DeviceId);

        var uri = BuildWebSocketUri(_options.ControlPlaneBaseUrl, _options.WebSocketPath);
        _logger.LogInformation("Connecting device {DeviceId} to {Uri}", identity.DeviceId, uri);
        await ws.ConnectAsync(uri, ct);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = ReceiveLoopAsync(ws, linked.Token);
        var heartbeat = HeartbeatLoopAsync(ws, linked.Token);

        await Task.WhenAny(receive, heartbeat);
        linked.Cancel();
        try { await Task.WhenAll(receive, heartbeat); } catch { }
        if (ws.State != WebSocketState.Closed) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > 2 * 1024 * 1024)
                    throw new InvalidOperationException("WebSocket message exceeds 2MB limit.");
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            await HandleMessageAsync(ws, json, ct);
        }
    }

    private async Task HandleMessageAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("type", out var typeElement)) return;
        var type = typeElement.GetString();

        if (type == "execute")
        {
            var message = JsonSerializer.Deserialize<ExecuteMessage>(json)
                ?? throw new InvalidOperationException("Invalid execute message.");

            var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(message.Script))).ToLowerInvariant();
            if (!CryptographicEquals(expectedHash, message.ScriptSha256))
            {
                await SendAsync(ws, new ExecutionResultMessage("execution_result", message.ExecutionId, "failed", null, "", "", 0, false, "SCRIPT_HASH_MISMATCH"), ct);
                return;
            }

            // Persist before execution: duplicate deliveries cannot execute twice.
            var created = await _executionStore.TryCreateAsync(message.ExecutionId, ct);
            if (!created)
            {
                var previous = await _executionStore.GetAsync(message.ExecutionId, ct);
                if (previous?.ResultJson is not null)
                    await SendRawAsync(ws, previous.ResultJson, ct);
                return;
            }

            await SendAsync(ws, new ExecutionAckMessage("execution_ack", message.MessageId, message.ExecutionId, message.ScriptSha256), ct);
            _ = ExecuteAndReportAsync(ws, message, ct);
        }
        else if (type == "heartbeat_ack")
        {
            // Receipt is enough; heartbeat loop controls liveness.
        }
    }

    private async Task ExecuteAndReportAsync(ClientWebSocket ws, ExecuteMessage message, CancellationToken connectionCt)
    {
        ExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(message.Script, message.TimeoutSeconds, connectionCt);
        }
        catch (OperationCanceledException) when (connectionCt.IsCancellationRequested)
        {
            return; // Connection loss; job remains running in local state for a production reconciliation strategy.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Execution {ExecutionId} failed unexpectedly", message.ExecutionId);
            result = new ExecutionResult("failed", null, "", "", 0, false, "AGENT_EXECUTION_ERROR");
        }

        var response = new ExecutionResultMessage(
            "execution_result", message.ExecutionId, result.Status, result.ExitCode,
            result.Stdout, result.Stderr, result.DurationMs, result.OutputTruncated, result.ErrorCode);

        var json = JsonSerializer.Serialize(response);
        await _executionStore.CompleteAsync(message.ExecutionId, result.Status, json, CancellationToken.None);

        if (ws.State == WebSocketState.Open)
            await SendRawAsync(ws, json, CancellationToken.None);
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await SendAsync(ws, new HeartbeatMessage("heartbeat", DateTimeOffset.UtcNow), ct);
        }
    }

    private static Uri BuildWebSocketUri(string baseUrl, string path)
    {
        var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" };
        return builder.Uri;
    }

    private static async Task SendAsync<T>(ClientWebSocket ws, T message, CancellationToken ct)
        => await SendRawAsync(ws, JsonSerializer.Serialize(message), ct);

    private static async Task SendRawAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static bool CryptographicEquals(string a, string b)
    {
        var aa = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return aa.Length == bb.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aa, bb);
    }
}
