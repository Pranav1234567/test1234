param(
    [Parameter(Mandatory=$true)]
    [string]$ControlPlaneBaseUrl,

    [Parameter(Mandatory=$true)]
    [string]$BootstrapToken,

    [string]$InstallDir = "$env:ProgramFiles\SquashAgent"
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this installer as Administrator. MSP tooling should invoke it elevated."
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path "C:\ProgramData\SquashAgent" | Out-Null

# The publish directory is expected to sit beside this script as ./publish.
$source = Join-Path $PSScriptRoot 'publish'
if (-not (Test-Path $source)) { throw "Missing publish directory: $source" }
Copy-Item "$source\*" $InstallDir -Recurse -Force

$config = Join-Path $InstallDir 'appsettings.json'
$json = Get-Content $config -Raw | ConvertFrom-Json
$json.SquashAgent.ControlPlaneBaseUrl = $ControlPlaneBaseUrl.TrimEnd('/')
$json.SquashAgent.BootstrapToken = $BootstrapToken
$json | ConvertTo-Json -Depth 10 | Set-Content $config -Encoding UTF8

$service = Get-Service -Name 'SquashAgent' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service SquashAgent -Force }
    sc.exe delete SquashAgent | Out-Null
    Start-Sleep -Seconds 1
}

sc.exe create SquashAgent binPath= "`"$InstallDir\SquashAgent.exe`"" start= auto DisplayName= "Squash Agent" | Out-Null
sc.exe failure SquashAgent reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
Start-Service SquashAgent

Write-Host "Squash Agent installed and started."
