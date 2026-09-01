using Microsoft.Data.Sqlite;

namespace SquashAgent.Storage;

public sealed record StoredExecution(string ExecutionId, string Status, string? ResultJson);

public sealed class ExecutionStore
{
    private readonly string _connectionString;

    public ExecutionStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "agent.db")
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS executions (
                execution_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                result_json TEXT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoredExecution?> GetAsync(string executionId, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT execution_id, status, result_json FROM executions WHERE execution_id = $id";
        command.Parameters.AddWithValue("$id", executionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new StoredExecution(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task<bool> TryCreateAsync(string executionId, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO executions(execution_id,status,updated_at) VALUES($id,'running',$now)";
        command.Parameters.AddWithValue("$id", executionId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task CompleteAsync(string executionId, string status, string resultJson, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE executions SET status=$status,result_json=$result,updated_at=$now WHERE execution_id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$result", resultJson);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", executionId);
        await command.ExecuteNonQueryAsync(ct);
    }
}
