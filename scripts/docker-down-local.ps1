[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env.local"
$composeFile = Join-Path $repoRoot "compose.local.yml"
if (-not (Test-Path -LiteralPath $envPath)) { throw "No .env.local found. Start with scripts/docker-up-local.ps1." }
docker compose --project-name smart-print --env-file $envPath -f $composeFile down
if ($LASTEXITCODE -ne 0) { throw "Could not stop local SmartPrint." }
Write-Host "Local SmartPrint stopped. Database and document volumes were preserved." -ForegroundColor Green
