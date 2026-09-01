namespace SquashAgent.Configuration;

public sealed class AgentOptions
{
    public string ControlPlaneBaseUrl { get; set; } = "https://localhost:5001";
    public string WebSocketPath { get; set; } = "/v1/agent/connect";
    public string BootstrapToken { get; set; } = "";
    public int HeartbeatSeconds { get; set; } = 10;
    public int ReconnectMaxSeconds { get; set; } = 30;
    public int DefaultExecutionTimeoutSeconds { get; set; } = 30;
    public int MaxExecutionTimeoutSeconds { get; set; } = 300;
    public int MaxOutputBytes { get; set; } = 1024 * 1024;
    public string DataDirectory { get; set; } = @"C:\ProgramData\SquashAgent";
}
