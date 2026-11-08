#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet("All", "Web", "Windows")]
    [string]$Platform = "All",
    [switch]$SkipInstall,
    [switch]$Help,
    [string]$ArtifactDirectory = "artifacts/mixed-batch-refund"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

if ($Help) {
    Write-Host @"
SmartPrint mixed-file page rejection and refund acceptance runner

Usage:
  .\scripts\qa\mixed-batch-refund-test.ps1 [-Platform All|Web|Windows] [-SkipInstall] [-ArtifactDirectory path]

Platforms:
  Web      Runs the shop portal UI in Chromium.
  Windows  Runs the desktop entry (apps/desktop/src/main.tsx) on Windows with a
           Tauri IPC contract test double.
  All      Runs Web and Windows desktop-entry acceptance.

The packaged native WebView2 window is separately smoke-launched by GitHub Actions.
Refunds and payments are in-memory QA fixtures; no real transfer, live payment,
printer dispatch or physical output occurs.
"@
    exit 0
}

if ($Platform -in @("All", "Windows") -and -not $IsWindows) {
    throw "The Windows desktop renderer variant must run on Windows."
}

$runner = "apps/portal/tests/mixed-batch-refund.browser.mjs"
if (-not (Test-Path -LiteralPath $runner)) { throw "Mixed-batch refund runner is missing: $runner" }
node --check $runner
if ($LASTEXITCODE -ne 0) { throw "Mixed-batch refund runner syntax validation failed." }

if (-not $SkipInstall) {
    pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw "pnpm dependency installation failed." }
}

$cache = (npm config get cache).Trim()
$module = Get-ChildItem -Path (Join-Path $cache "_npx") -Filter "index.mjs" -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\node_modules\\playwright\\index\.mjs$" } |
    Select-Object -First 1
if (-not $module) {
    npx --yes --package=playwright@1.56.1 playwright install chromium
    if ($LASTEXITCODE -ne 0) { throw "Playwright Chromium installation failed." }
    $module = Get-ChildItem -Path (Join-Path $cache "_npx") -Filter "index.mjs" -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\node_modules\\playwright\\index\.mjs$" } |
        Select-Object -First 1
}
if (-not $module) { throw "Could not locate the isolated Playwright package." }

$env:PLAYWRIGHT_MODULE_DIR = $module.DirectoryName
$env:VITE_API_BASE_URL = ""
$modes = if ($Platform -eq "All") { @("web", "desktop") } elseif ($Platform -eq "Web") { @("web") } else { @("desktop") }

foreach ($mode in $modes) {
    $safeMode = if ($mode -eq "desktop") { "windows-desktop" } else { "web" }
    $env:SMARTPRINT_MIXED_BATCH_REFUND_APP = $mode
    $env:SMARTPRINT_MIXED_BATCH_REFUND_ARTIFACT_DIR = Join-Path $ArtifactDirectory $safeMode
    New-Item -ItemType Directory -Force -Path $env:SMARTPRINT_MIXED_BATCH_REFUND_ARTIFACT_DIR | Out-Null
    Write-Host "Running mixed-batch page rejection/refund acceptance: $safeMode"
    node $runner
    if ($LASTEXITCODE -ne 0) { throw "Mixed-batch page rejection/refund acceptance failed for $safeMode." }
}
Write-Host "Mixed-batch page rejection/refund acceptance completed for: $($modes -join ', ')."
