using System.Text.Json.Serialization;

namespace SquashAgent.Protocol;

public sealed record ExecuteMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("script")] string Script,
    [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds,
    [property: JsonPropertyName("script_sha256")] string ScriptSha256);

public sealed record ExecutionAckMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("script_sha256")] string ScriptSha256);

public sealed record ExecutionResultMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("output_truncated")] bool OutputTruncated,
    [property: JsonPropertyName("error_code")] string? ErrorCode);

public sealed record HeartbeatMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);

public sealed record HeartbeatAckMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);
