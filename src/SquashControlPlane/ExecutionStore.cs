using System.Collections.Concurrent;

namespace SquashControlPlane;

public enum ExecutionStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    TimedOut,
    DeviceUnreachable
}

public sealed class ExecutionRecord
{
    public required string ExecutionId { get; init; }
    public required string DeviceId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Script { get; init; }

    public int TimeoutSeconds { get; init; }

    public ExecutionStatus Status { get; set; } = ExecutionStatus.Queued;

    public int? ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public long? DurationMs { get; set; }
    public bool OutputTruncated { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ExecutionStore
{
    private readonly ConcurrentDictionary<string, ExecutionRecord> _executions = new();

    // deviceId + idempotencyKey -> executionId
    private readonly ConcurrentDictionary<string, string> _idempotency = new();

    public bool TryCreate(
        string deviceId,
        string idempotencyKey,
        string script,
        int timeoutSeconds,
        out ExecutionRecord execution)
    {
        var idempotencyLookup = $"{deviceId}:{idempotencyKey}";

        if (_idempotency.TryGetValue(idempotencyLookup, out var existingId) &&
            _executions.TryGetValue(existingId, out var existing))
        {
            execution = existing;
            return false;
        }

        execution = new ExecutionRecord
        {
            ExecutionId = Guid.NewGuid().ToString(),
            DeviceId = deviceId,
            IdempotencyKey = idempotencyKey,
            Script = script,
            TimeoutSeconds = timeoutSeconds
        };

        // ConcurrentDictionary gives us atomic insertion of the idempotency key.
        if (!_idempotency.TryAdd(idempotencyLookup, execution.ExecutionId))
        {
            var id = _idempotency[idempotencyLookup];
            execution = _executions[id];
            return false;
        }

        _executions[execution.ExecutionId] = execution;
        return true;
    }

    public ExecutionRecord? Get(string executionId)
    {
        return _executions.TryGetValue(executionId, out var execution)
            ? execution
            : null;
    }

    public bool TryUpdate(
        string executionId,
        Action<ExecutionRecord> update)
    {
        if (!_executions.TryGetValue(executionId, out var execution))
            return false;

        lock (execution)
        {
            update(execution);
            execution.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return true;
    }
}