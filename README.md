# Squash AI-Native RMM — Windows Agent

This is the endpoint portion of the take-home. The agent is intentionally dumb: it enrolls, opens an outbound WebSocket, receives an execution command, runs PowerShell, bounds output, and reports a structured result.

## Architecture

```text
Windows boot
   |
   v
Windows Service (SquashAgent)
   |
   +-- DeviceIdentityStore
   +-- EnrollmentClient
   +-- AgentConnection (WebSocket)
   +-- PowerShellExecutor
   +-- ExecutionStore (SQLite)
```

The endpoint never needs an inbound connection. It initiates `wss://.../v1/agent/connect`, which works through NAT/firewalls that permit outbound HTTPS/WSS.

## Build

On a Windows machine with .NET 8 SDK:

```powershell
dotnet restore .\src\SquashAgent\SquashAgent.csproj
dotnet publish .\src\SquashAgent\SquashAgent.csproj -c Release -r win-x64 --self-contained true -o .\installer\publish
```

## Install

From an elevated PowerShell prompt:

```powershell
.\installer\install.ps1 `
  -ControlPlaneBaseUrl "https://control-plane.example.com" `
  -BootstrapToken "ONE_TIME_TOKEN"
```

The installer is unattended: it copies the service, writes configuration, creates an auto-start Windows Service, configures service recovery, and starts it.

## Credential storage

The enrollment endpoint returns a `device_token`. The agent stores the token encrypted with Windows DPAPI using `LocalMachine` scope, alongside the device identity. The WebSocket client decrypts it only when opening the connection. Do not use an environment variable for the final credential.

## WebSocket protocol

Server -> agent:

```json
{
  "type": "execute",
  "message_id": "msg-123",
  "execution_id": "exec-456",
  "script": "Get-Service",
  "timeout_seconds": 30,
  "script_sha256": "..."
}
```

Agent -> server:

```json
{
  "type": "execution_ack",
  "message_id": "msg-123",
  "execution_id": "exec-456",
  "script_sha256": "..."
}
```

Agent -> server on completion:

```json
{
  "type": "execution_result",
  "execution_id": "exec-456",
  "status": "completed",
  "exit_code": 0,
  "stdout": "...",
  "stderr": "",
  "duration_ms": 421,
  "output_truncated": false,
  "error_code": null
}
```

## Important design choices

### NAT / low latency
The agent maintains a persistent outbound WebSocket. The control plane never initiates a TCP connection to the endpoint. This avoids polling and allows a job to travel over an already-open connection.

### Deterministic endpoint
No LLM, diagnosis, policy engine, or approval logic runs on the agent.

### Idempotency
The server supplies an execution ID. The local SQLite store records it before execution. A duplicate WebSocket delivery is therefore not executed a second time.

### Output safety
stdout/stderr are capped at 1 MiB each and `output_truncated` is reported.

### Timeout
Every PowerShell process has a bounded execution time. A timed-out process tree is killed and a terminal timeout result is returned.

### Stable identity
The device ID is deterministically derived from Windows `MachineGuid`, so reinstalling the agent on the same Windows installation retains the same ID.

### Revocation
The control plane must reject revoked device credentials. The agent should also reconnect and receive a close/revocation response when a device is revoked.

## Production-hardening items before submission

1. Persist the device token encrypted with DPAPI rather than using `SQUASH_DEVICE_TOKEN`.
2. Add server-issued credential rotation.
3. Add a reconciliation protocol for jobs interrupted by agent process restart.
4. Add explicit maximum script size and WebSocket frame size.
5. Add concurrency policy (for example, one execution at a time per device for the take-home).
6. Add integration tests against the control plane.
7. Add Windows event logging / structured local logs with secrets redacted.

## Local Windows VM end-to-end test

This test deliberately keeps the control plane and Windows agent on different machines. The Windows VM initiates the only agent connection, so the test does not depend on the endpoint being directly reachable from the control plane.

### 1. Publish the Windows agent

From a Windows development environment with the .NET 8 SDK:

```powershell
.\scripts\publish-agent.ps1
```

This creates `installer\publish`, which is the payload consumed by `installer\install.ps1`.

### 2. Start the control plane so the VM can reach it

Find the host machine's IP address on the VM's reachable network. Then run the control plane bound to all interfaces:

```powershell
$env:ASPNETCORE_URLS = "http://0.0.0.0:5089"
dotnet run --project .\src\SquashControlPlane\SquashControlPlane.csproj
```

The health endpoint should be reachable from the Windows VM at:

```text
http://<CONTROL_PLANE_IP>:5089/
```

Do not use `localhost` in the Windows VM configuration. `localhost` always means the Windows VM itself.

### 3. Put the installer payload on the Windows VM

Copy the `installer` directory to the VM using your VM shared folder or deployment tooling. The directory must contain:

```text
installer\
  install.ps1
  publish\
    SquashAgent.exe
    ...other published files...
```

This simulates the MSP pushing an installer package. The production deployment mechanism can be replaced later; it does not change the agent/control-plane network model.

### 4. Run the installer unattended

Open an elevated PowerShell session on the VM, or have the deployment tool execute the same command elevated:

```powershell
.\install.ps1 `
  -ControlPlaneBaseUrl "http://<CONTROL_PLANE_IP>:5089" `
  -BootstrapToken "dev-bootstrap-token"
```

There are no installer prompts. The script installs the agent, creates an auto-start Windows Service, configures recovery, and starts it.

### 5. Verify the service and ONLINE state

From the Windows VM:

```powershell
.\scripts\verify-agent.ps1 `
  -ControlPlaneBaseUrl "http://<CONTROL_PLANE_IP>:5089"
```

Expected output:

```text
[OK] SquashAgent Windows service is RUNNING
[OK] Device enrolled: <device-id>
[OK] Agent status: ONLINE

End-to-end verification PASSED.
```

You can also verify directly from the control plane machine:

```text
GET http://localhost:5089/v1/devices
```

The device should contain:

```json
{
  "status": "ONLINE",
  "connected": true
}
```

### 6. Reboot test

The most important unattended-agent test is:

```powershell
Restart-Computer
```

After Windows boots, without logging in, the `SquashAgent` service should start automatically, reconnect to the control plane, and return to `ONLINE`.

### 7. NAT/firewall interpretation

For a stronger local simulation, place the Windows VM on a NAT network where the control-plane host is reachable but there is no inbound port-forward to the Windows VM. The agent still initiates:

```text
Windows VM -> NAT -> Control Plane
```

The control plane never opens a connection to the Windows VM. If the VM changes networks and the WebSocket breaks, `AgentConnection` reconnects using exponential backoff.
