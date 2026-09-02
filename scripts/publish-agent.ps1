$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\SquashAgent\SquashAgent.csproj'
$output = Join-Path $root 'installer\publish'

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host "Published agent to $output"
