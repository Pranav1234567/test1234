param(
    [Parameter(Mandatory=$true)]
    [string]$ControlPlaneBaseUrl,

    [Parameter(Mandatory=$true)]
    [string]$BootstrapToken,

    [string]$InstallDir = "$env:ProgramFiles\SquashAgent"
)

$ErrorActionPreference = 'Stop'

function Write-InstallLog([string]$Message) {
    Write-Host "[SquashAgent] $Message"
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this installer as Administrator. MSP tooling should invoke it elevated."
}

$ControlPlaneBaseUrl = $ControlPlaneBaseUrl.TrimEnd('/')
if ($ControlPlaneBaseUrl -notmatch '^https?://') {
    throw "ControlPlaneBaseUrl must start with http:// or https://"
}

$source = Join-Path $PSScriptRoot 'publish'
$agentExe = Join-Path $source 'SquashAgent.exe'
if (-not (Test-Path $agentExe)) {
    throw "Missing published agent: $agentExe. Run the publish command first."
}

Write-InstallLog "Installing from $source"
Write-InstallLog "Control plane: $ControlPlaneBaseUrl"

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path 'C:\ProgramData\SquashAgent' | Out-Null
Copy-Item "$source\*" $InstallDir -Recurse -Force

$config = Join-Path $InstallDir 'appsettings.json'
if (-not (Test-Path $config)) {
    throw "Missing agent configuration: $config"
}

$json = Get-Content $config -Raw | ConvertFrom-Json
$json.SquashAgent.ControlPlaneBaseUrl = $ControlPlaneBaseUrl
$json.SquashAgent.BootstrapToken = $BootstrapToken
$json | ConvertTo-Json -Depth 10 | Set-Content $config -Encoding UTF8

$service = Get-Service -Name 'SquashAgent' -ErrorAction SilentlyContinue
if ($service) {
    Write-InstallLog 'Existing SquashAgent service found; replacing it.'
    if ($service.Status -ne 'Stopped') {
        Stop-Service SquashAgent -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete SquashAgent | Out-Null
    Start-Sleep -Seconds 1
}

& sc.exe create SquashAgent `
    binPath= "`"$InstallDir\SquashAgent.exe`"" `
    start= auto `
    DisplayName= "Squash Agent" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to create SquashAgent Windows service." }

& sc.exe failure SquashAgent reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null

Start-Service SquashAgent
(Get-Service SquashAgent).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

Write-InstallLog 'Installation complete.'
Write-InstallLog 'Windows service: Running'
Write-InstallLog 'The agent will enroll and connect to the control plane in the background.'
