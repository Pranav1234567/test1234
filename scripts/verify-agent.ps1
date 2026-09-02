param(
    [Parameter(Mandatory=$true)]
    [string]$ControlPlaneBaseUrl,

    [string]$DeviceId
)

$ErrorActionPreference = 'Stop'

$service = Get-Service -Name 'SquashAgent' -ErrorAction Stop
if ($service.Status -ne 'Running') {
    throw "SquashAgent service is not running. Current status: $($service.Status)"
}

Write-Host '[OK] SquashAgent Windows service is RUNNING'

$devices = Invoke-RestMethod -Uri "$($ControlPlaneBaseUrl.TrimEnd('/'))/v1/devices" -Method Get
if (-not $devices) { throw 'Control plane returned no enrolled devices.' }

if ($DeviceId) {
    $device = $devices | Where-Object { $_.deviceId -eq $DeviceId }
} else {
    $device = $devices | Where-Object { $_.hostname -eq $env:COMPUTERNAME }
}

if (-not $device) {
    throw "Agent is not enrolled. No matching device found."
}

if ($device.status -ne 'ONLINE') {
    throw "Agent is enrolled but not ONLINE. Status: $($device.status)"
}

Write-Host "[OK] Device enrolled: $($device.deviceId)"
Write-Host "[OK] Agent status: ONLINE"
Write-Host ''
Write-Host 'End-to-end verification PASSED.'
