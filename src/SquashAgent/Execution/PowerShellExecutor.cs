using System.Diagnostics;
using System.Text;
using SquashAgent.Configuration;

namespace SquashAgent.Execution;

public sealed record ExecutionResult(
    string Status,
    int? ExitCode,
    string Stdout,
    string Stderr,
    long DurationMs,
    bool OutputTruncated,
    string? ErrorCode);

public sealed class PowerShellExecutor
{
    private readonly AgentOptions _options;

    public PowerShellExecutor(AgentOptions options) => _options = options;

    public async Task<ExecutionResult> ExecuteAsync(string script, int requestedTimeoutSeconds, CancellationToken ct)
    {
        var timeout = Math.Clamp(
            requestedTimeoutSeconds <= 0 ? _options.DefaultExecutionTimeoutSeconds : requestedTimeoutSeconds,
            1,
            _options.MaxExecutionTimeoutSeconds);

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell\\v1.0\\powershell.exe");

        if (!File.Exists(powershell))
            powershell = "pwsh.exe";

        var psi = new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File -",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var started = Stopwatch.GetTimestamp();
        process.Start();

        // PowerShell's stdin is used so we don't have to leave a script file containing customer code on disk.
        await process.StandardInput.WriteAsync(script);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, _options.MaxOutputBytes, ct);
        var stderrTask = ReadBoundedAsync(process.StandardError, _options.MaxOutputBytes, ct);
        var waitTask = process.WaitForExitAsync(ct);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ExecutionResult(
                "timeout", null, stdout.Text, stderr.Text,
                ElapsedMs(started), stdout.Truncated || stderr.Truncated, "EXECUTION_TIMEOUT");
        }

        var outResult = await stdoutTask;
        var errResult = await stderrTask;
        return new ExecutionResult(
            process.ExitCode == 0 ? "completed" : "failed",
            process.ExitCode,
            outResult.Text,
            errResult.Text,
            ElapsedMs(started),
            outResult.Truncated || errResult.Truncated,
            process.ExitCode == 0 ? null : "SCRIPT_NONZERO_EXIT");
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var buffer = new char[8192];
        var sb = new StringBuilder(Math.Min(maxBytes, 64 * 1024));
        var bytes = 0;
        var truncated = false;

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            var chunk = new string(buffer, 0, count);
            var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
            if (bytes + chunkBytes <= maxBytes)
            {
                sb.Append(chunk);
                bytes += chunkBytes;
            }
            else
            {
                var remaining = maxBytes - bytes;
                if (remaining > 0)
                {
                    var encoded = Encoding.UTF8.GetBytes(chunk);
                    sb.Append(Encoding.UTF8.GetString(encoded, 0, remaining));
                }
                truncated = true;
                // Continue draining so the child process doesn't block on a full pipe.
            }
        }
        return (sb.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best effort; terminal state is still reported */ }
    }

    private static long ElapsedMs(long start) => (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
}
