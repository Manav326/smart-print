#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet("All", "Web", "Windows", "NativeWindows")]
    [string]$Platform = "All",
    [switch]$SkipInstall,
    [switch]$Help,
    [switch]$Headed,
    [string]$BaseUrl = "",
    [ValidateRange(1024,65535)][int]$WebView2DebugPort = 9228,
    [string]$NativeExecutablePath = "",
    [string]$ArtifactDirectory = "artifacts/mixed-batch-refund"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

if ($Help) {
    Write-Host @"
SmartPrint mixed-file page rejection and refund acceptance runner

Usage:
  .\scripts\qa\mixed-batch-refund-test.ps1 [-Platform All|Web|Windows|NativeWindows] [-SkipInstall] [-Headed] [-BaseUrl http://127.0.0.1:4174] [-NativeExecutablePath path] [-WebView2DebugPort port] [-ArtifactDirectory path]

Platforms:
  Web           Runs the shop portal UI in Chromium.
  Windows       Runs the desktop-entry contract test and the actual native Windows
                Tauri/WebView2 application.
  NativeWindows Runs only the native Windows Tauri/WebView2 application.
  All           Runs Web, the desktop-entry contract test and native WebView2 test.

The native test uses a localhost-only CDP port and the real Tauri secure-session
command. It restores the previously saved Windows Credential Manager session
before exit. All refunds and payments are isolated QA fixtures; no real transfer,
live payment, printer dispatch or physical output occurs.
"@
    exit 0
}

if ($Platform -in @("All", "Windows", "NativeWindows") -and -not $IsWindows) {
    throw "The Windows desktop renderer variant must run on Windows."
}
if ($NativeExecutablePath -and $Platform -notin @("All", "Windows", "NativeWindows")) {
    throw "-NativeExecutablePath is valid only for Windows/native Windows test modes."
}
if ($NativeExecutablePath) {
    $NativeExecutablePath = (Resolve-Path -LiteralPath $NativeExecutablePath -ErrorAction Stop).Path
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
$modes = if ($Platform -eq "All") { @("web", "desktop", "desktop-native") }
    elseif ($Platform -eq "Web") { @("web") }
    elseif ($Platform -eq "NativeWindows") { @("desktop-native") }
    elseif ($Platform -eq "Windows") { @("desktop", "desktop-native") }
    else { @("web", "desktop", "desktop-native") }

$trackedEnvNames = @(
    "SMARTPRINT_MIXED_BATCH_REFUND_APP",
    "SMARTPRINT_WEBVIEW2_DEBUG_PORT",
    "SMARTPRINT_MIXED_BATCH_REFUND_ARTIFACT_DIR",
    "SMARTPRINT_MIXED_BATCH_REFUND_EXECUTABLE",
    "SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL",
    "SMARTPRINT_MIXED_BATCH_REFUND_HEADED"
)
$previousEnv = @{}
foreach ($name in $trackedEnvNames) { $previousEnv[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }

try {
$validatedBaseUrl = ""
if ($BaseUrl) {
    $baseUri = $null
    if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$baseUri) -or
        $baseUri.Scheme -ne "http" -or -not $baseUri.IsLoopback -or
        $baseUri.AbsolutePath -ne "/" -or $baseUri.Query -or $baseUri.Fragment) {
        throw "-BaseUrl must be a loopback HTTP origin with no path, query or fragment."
    }
    $validatedBaseUrl = $baseUri.GetLeftPart([UriPartial]::Authority)
}
foreach ($mode in $modes) {
    $safeMode = if ($mode -eq "desktop") { "windows-desktop-contract" }
        elseif ($mode -eq "desktop-native") { "windows-native-webview2" }
        else { "web" }
    $env:SMARTPRINT_MIXED_BATCH_REFUND_APP = $mode
    $env:SMARTPRINT_WEBVIEW2_DEBUG_PORT = [string]$WebView2DebugPort
    $env:SMARTPRINT_MIXED_BATCH_REFUND_ARTIFACT_DIR = Join-Path $ArtifactDirectory $safeMode
    if ($Headed) { $env:SMARTPRINT_MIXED_BATCH_REFUND_HEADED = "1" } else { Remove-Item Env:SMARTPRINT_MIXED_BATCH_REFUND_HEADED -ErrorAction SilentlyContinue }
    if ($mode -eq "desktop-native" -and $NativeExecutablePath) {
        $env:SMARTPRINT_MIXED_BATCH_REFUND_EXECUTABLE = $NativeExecutablePath
        $env:SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL = "http://127.0.0.1:1420"
    } elseif ($mode -eq "web" -and $validatedBaseUrl) {
        Remove-Item Env:SMARTPRINT_MIXED_BATCH_REFUND_EXECUTABLE -ErrorAction SilentlyContinue
        $env:SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL = $validatedBaseUrl
    } else {
        Remove-Item Env:SMARTPRINT_MIXED_BATCH_REFUND_EXECUTABLE -ErrorAction SilentlyContinue
        Remove-Item Env:SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $env:SMARTPRINT_MIXED_BATCH_REFUND_ARTIFACT_DIR | Out-Null
    Write-Host "Running mixed-batch page rejection/refund acceptance: $safeMode"
    node $runner
    if ($LASTEXITCODE -ne 0) { throw "Mixed-batch page rejection/refund acceptance failed for $safeMode." }
}

Write-Host "Mixed-batch page rejection/refund acceptance completed for: $($modes -join ', ')."
}
finally {
    foreach ($name in $trackedEnvNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnv[$name], "Process")
    }
}
