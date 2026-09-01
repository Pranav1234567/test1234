$ErrorActionPreference = 'Stop'

$service = Get-Service -Name 'SquashAgent' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service SquashAgent -Force }
    sc.exe delete SquashAgent | Out-Null
}

$installDir = "$env:ProgramFiles\SquashAgent"
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }

# Deliberately keep ProgramData identity/audit data unless an explicit purge is requested.
Write-Host "Squash Agent removed. Persistent identity data remains under C:\ProgramData\SquashAgent."
