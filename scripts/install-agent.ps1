param(
    [Parameter(Mandatory = $true)]
    [string]$ControlPlaneUrl,

    [Parameter(Mandatory = $true)]
    [string]$BootstrapToken
)

$ErrorActionPreference = "Stop"

# ------------------------------------------------------------
# Configuration
# ------------------------------------------------------------

$InstallRoot = "C:\Program Files\SquashAgent"
$DownloadPath = "$env:TEMP\SquashAgent.zip"
$ExtractPath = "$env:TEMP\SquashAgentExtract"
$ServiceName = "SquashAgent"
$ServiceDisplayName = "Squash Agent"
$ConfigPath = Join-Path $InstallRoot "appsettings.json"

# ------------------------------------------------------------
# Helpers
# ------------------------------------------------------------

function Write-Step {
    param([string]$Message)

    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# ------------------------------------------------------------
# 1. Download agent
# ------------------------------------------------------------

Write-Step "Downloading Squash Agent"

$DownloadUrl = "$ControlPlaneUrl/v1/agent/download"

if (Test-Path $DownloadPath) {
    Remove-Item $DownloadPath -Force
}

Invoke-WebRequest `
    -Uri $DownloadUrl `
    -OutFile $DownloadPath

if (-not (Test-Path $DownloadPath)) {
    throw "Agent download failed."
}

Write-Host "Downloaded: $DownloadPath"

# ------------------------------------------------------------
# 2. Extract agent
# ------------------------------------------------------------

Write-Step "Extracting Squash Agent"

if (Test-Path $ExtractPath) {
    Remove-Item $ExtractPath -Recurse -Force
}

New-Item `
    -ItemType Directory `
    -Path $ExtractPath `
    -Force | Out-Null

Expand-Archive `
    -Path $DownloadPath `
    -DestinationPath $ExtractPath `
    -Force

# Our ZIP currently contains a 'publish' directory.
$PublishedAgentPath = Join-Path $ExtractPath "publish"

if (-not (Test-Path $PublishedAgentPath)) {
    throw "Published agent directory was not found in the ZIP."
}

$AgentExe = Join-Path $PublishedAgentPath "SquashAgent.exe"

if (-not (Test-Path $AgentExe)) {
    throw "SquashAgent.exe was not found."
}

Write-Host "Agent executable: $AgentExe"

# ------------------------------------------------------------
# 3. Stop existing service if present
# ------------------------------------------------------------

Write-Step "Checking existing Windows Service"

$ExistingService = Get-Service `
    -Name $ServiceName `
    -ErrorAction SilentlyContinue

if ($null -ne $ExistingService) {

    Write-Host "Existing SquashAgent service found."

    if ($ExistingService.Status -eq "Running") {
        Stop-Service `
            -Name $ServiceName `
            -Force `
            -ErrorAction SilentlyContinue
    }

    sc.exe delete $ServiceName | Out-Null

    Start-Sleep -Seconds 2
}

# ------------------------------------------------------------
# 4. Install files
# ------------------------------------------------------------

Write-Step "Installing agent"

if (Test-Path $InstallRoot) {
    Remove-Item `
        $InstallRoot `
        -Recurse `
        -Force
}

New-Item `
    -ItemType Directory `
    -Path $InstallRoot `
    -Force | Out-Null

Copy-Item `
    "$PublishedAgentPath\*" `
    $InstallRoot `
    -Recurse `
    -Force

Write-Step "Configuring agent"

if (-not (Test-Path $ConfigPath)) {
    throw "Agent configuration file was not found: $ConfigPath"
}

$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json

$config.SquashAgent.ControlPlaneBaseUrl = $ControlPlaneUrl
$config.SquashAgent.BootstrapToken = $BootstrapToken

$config | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath -Encoding UTF8

Write-Host "Control Plane: $ControlPlaneUrl"
Write-Host "Bootstrap token configured."

$InstalledAgentExe = Join-Path $InstallRoot "SquashAgent.exe"

if (-not (Test-Path $InstalledAgentExe)) {
    throw "Agent installation failed."
}

Write-Host "Installed agent at:"
Write-Host $InstalledAgentExe

# ------------------------------------------------------------
# 5. Install Windows Service
# ------------------------------------------------------------

Write-Step "Installing Windows Service"

sc.exe create $ServiceName `
    binPath= "`"$InstalledAgentExe`"" `
    start= auto `
    DisplayName= "`"$ServiceDisplayName`"" | Out-Host

if ($LASTEXITCODE -ne 0) {
    throw "Failed to create Windows Service."
}

# ------------------------------------------------------------
# 6. Configure automatic restart
# ------------------------------------------------------------

Write-Step "Configuring service recovery"

sc.exe failure $ServiceName `
    reset= 86400 `
    actions= restart/5000/restart/10000/restart/30000 | Out-Host

# ------------------------------------------------------------
# 7. Start agent
# ------------------------------------------------------------

Write-Step "Starting Squash Agent"

Start-Service `
    -Name $ServiceName

Start-Sleep -Seconds 3

$Service = Get-Service `
    -Name $ServiceName

Write-Host ""
Write-Host "Service status: $($Service.Status)"

if ($Service.Status -ne "Running") {
    throw "SquashAgent service failed to start."
}

# ------------------------------------------------------------
# 8. Cleanup
# ------------------------------------------------------------

Write-Step "Cleaning up"

Remove-Item `
    $DownloadPath `
    -Force `
    -ErrorAction SilentlyContinue

Remove-Item `
    $ExtractPath `
    -Recurse `
    -Force `
    -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " Squash Agent installation successful!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Service: $ServiceName"
Write-Host "Status:  $($Service.Status)"
Write-Host ""