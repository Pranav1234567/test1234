using Microsoft.Data.Sqlite;

namespace SquashAgent.Storage;

public sealed record StoredExecution(string ExecutionId, string Status, string? ResultJson);

public sealed class ExecutionStore
{
    private readonly string _connectionString;
    public ExecutionStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "agent.db") }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct);
        var cmd = db.CreateCommand(); cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS executions (
              execution_id TEXT PRIMARY KEY,
              status TEXT NOT NULL,
              result_json TEXT NULL,
              updated_at TEXT NOT NULL
            );
            UPDATE executions SET status='interrupted' WHERE status IN ('running','accepted');
            """; await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoredExecution?> GetAsync(string id, CancellationToken ct)
    { await using var db=new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd=db.CreateCommand(); cmd.CommandText="SELECT execution_id,status,result_json FROM executions WHERE execution_id=$id"; cmd.Parameters.AddWithValue("$id",id); await using var r=await cmd.ExecuteReaderAsync(ct); return await r.ReadAsync(ct)?new StoredExecution(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2)):null; }

    public async Task<bool> TryCreateAsync(string id, CancellationToken ct)
    { await using var db=new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd=db.CreateCommand(); cmd.CommandText="INSERT OR IGNORE INTO executions(execution_id,status,updated_at) VALUES($id,'accepted',$now)"; cmd.Parameters.AddWithValue("$id",id); cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); return await cmd.ExecuteNonQueryAsync(ct)==1; }

    public async Task CompleteAsync(string id,string status,string json,CancellationToken ct)
    { await using var db=new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd=db.CreateCommand(); cmd.CommandText="UPDATE executions SET status=$status,result_json=$result,updated_at=$now WHERE execution_id=$id"; cmd.Parameters.AddWithValue("$status",status); cmd.Parameters.AddWithValue("$result",json); cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id",id); await cmd.ExecuteNonQueryAsync(ct); }

    public async Task<List<string>> GetPendingResultsAsync(CancellationToken ct)
    { await using var db=new SqliteConnection(_connectionString); await db.OpenAsync(ct); var cmd=db.CreateCommand(); cmd.CommandText="SELECT result_json FROM executions WHERE result_json IS NOT NULL AND status IN ('completed','failed','timeout') ORDER BY updated_at"; await using var r=await cmd.ExecuteReaderAsync(ct); var list=new List<string>(); while(await r.ReadAsync(ct)) list.Add(r.GetString(0)); return list; }
}
