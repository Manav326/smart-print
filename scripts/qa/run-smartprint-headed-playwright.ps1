<#
.SYNOPSIS
Run selected headed Playwright acceptance scenarios on the configured SmartPrint website.

.DESCRIPTION
Opens a visible Microsoft Edge/Chromium browser against the canonical public
origin in scripts/qa/smartprint-qa.config.json. Select the workspace/customer
scenarios with the switches below. Credentials are read from process
environment variables or requested interactively as secure prompts.

.PARAMETER Help
Show this guide and exit without opening a browser or prompting for credentials.

.PARAMETER RunShopWorkspace
Run the dedicated QA shop-owner workspace checks. Requires a dedicated QA
shop email/password (environment variables or prompts).

.PARAMETER RunPlatformAdmin
Run the dedicated platform-admin workspace checks. Requires dedicated QA
admin credentials (environment variables or prompts).

.PARAMETER RunCustomerStation
Run the customer QR station checks. Requires -StationPath for a dedicated
disposable station such as /s/QA_STATION_01.

.PARAMETER RunCustomerUploadWorkflow
Run the real customer-upload workflow. Requires -RunShopWorkspace,
-RunCustomerStation, -StationPath and -ExpectedQaShopName. Uses the dedicated
QA shop and station to ensure the upload is associated with the intended shop.

.PARAMETER StationPath
Dedicated customer station URL path, for example /s/QA_STATION_01. Required
with -RunCustomerStation. Never supply a customer's live station by accident.

.PARAMETER ExpectedQaShopName
Exact display name of the dedicated QA shop. Required with
-RunCustomerUploadWorkflow to guard against uploads to the wrong shop.

.EXAMPLE
.\scripts\qa\run-smartprint-headed-playwright.ps1 -Help
Show the available scenarios and requirements.

.EXAMPLE
.\scripts\qa\run-smartprint-headed-playwright.ps1 -RunShopWorkspace
Run the dedicated QA shop workspace checks. The script prompts for missing
QA credentials without echoing the password.

.EXAMPLE
.\scripts\qa\run-smartprint-headed-playwright.ps1 -RunPlatformAdmin
Run the dedicated platform-admin workspace checks.

.EXAMPLE
.\scripts\qa\run-smartprint-headed-playwright.ps1 -RunCustomerStation -StationPath '/s/QA_STATION_01'
Run customer station checks against the specified disposable QA station.

.EXAMPLE
.\scripts\qa\run-smartprint-headed-playwright.ps1 -RunShopWorkspace -RunCustomerStation -RunCustomerUploadWorkflow -StationPath '/s/QA_STATION_01' -ExpectedQaShopName 'SmartPrint QA Shop'
Run the upload flow through the dedicated shop and station. Do not use a real
customer station or a non-QA shop.

.NOTES
This is a separate live website suite, not the local Docker-backed E2E runner.
It does not change Cloudflare configuration. It can create temporary QA
sessions and perform the dedicated QA upload workflow, so use only QA
accounts/stations. No real payments or refunds are submitted.
#>

[CmdletBinding()]
param(
    [switch]$Help,
    [switch]$RunShopWorkspace,
    [switch]$RunPlatformAdmin,
    [switch]$RunCustomerStation,
    [switch]$RunCustomerUploadWorkflow,
    [string]$StationPath,
    [string]$ExpectedQaShopName
)

if ($Help) {
    Get-Help -Name $PSCommandPath -Full | Out-String -Width 180 | Write-Host
    exit 0
}

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

$qaConfigPath = Join-Path $PSScriptRoot "smartprint-qa.config.json"
if (-not (Test-Path -LiteralPath $qaConfigPath -PathType Leaf)) {
    throw "The central SmartPrint QA configuration is missing: $qaConfigPath"
}
$qaConfig = Get-Content -LiteralPath $qaConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop
$publicOrigin = [Uri]$qaConfig.origins.public
$minimumNodeVersion = [Version]$qaConfig.runtime.minimumNodeVersion
if ($publicOrigin.Scheme -ne "https" -or $publicOrigin.AbsolutePath -ne "/" -or
    $publicOrigin.UserInfo -or $publicOrigin.Query -or $publicOrigin.Fragment) {
    throw "The configured SmartPrint public origin must be a plain HTTPS origin."
}
$canonicalPublicOrigin = $publicOrigin.GetLeftPart([UriPartial]::Authority)

$environmentNames = @(
    "PLAYWRIGHT_MODULE_DIR",
    "SMARTPRINT_LIVE_QA_BASE_URL",
    "LIVE_QA_ARTIFACT_DIR",
    "LIVE_QA_CHECK_SHOP_WORKSPACE",
    "LIVE_QA_CHECK_PLATFORM_ADMIN",
    "LIVE_QA_CHECK_CUSTOMER_STATION",
    "LIVE_QA_CHECK_CUSTOMER_UPLOAD_WORKFLOW",
    "SMARTPRINT_LIVE_QA_EXPECTED_QA_SHOP_NAME",
    "SMARTPRINT_LIVE_QA_SHOP_EMAIL",
    "SMARTPRINT_LIVE_QA_SHOP_PASSWORD",
    "SMARTPRINT_LIVE_QA_ADMIN_EMAIL",
    "SMARTPRINT_LIVE_QA_ADMIN_PASSWORD",
    "SMARTPRINT_LIVE_QA_STATION_PATH"
)
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

function Set-ProcessSecretFromPrompt {
    param([Parameter(Mandatory)][string]$VariableName, [Parameter(Mandatory)][string]$Prompt)
    $secure = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        [Environment]::SetEnvironmentVariable(
            $VariableName,
            [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer),
            "Process"
        )
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        $secure.Dispose()
    }
}

try {
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) { $node = Get-Command node -ErrorAction Stop }
    $nodeVersion = (& $node.Source --version).Trim()
    $nodeVersionMatch = [regex]::Match($nodeVersion, '^v(?<version>\d+\.\d+\.\d+)')
    $actualNodeVersion = if ($nodeVersionMatch.Success) { [Version]$nodeVersionMatch.Groups["version"].Value } else { [Version]"0.0.0" }
    if (-not $nodeVersionMatch.Success -or $actualNodeVersion -lt $minimumNodeVersion) {
        throw "Node.js $minimumNodeVersion or newer is required. Found $nodeVersion."
    }

    $testScript = Join-Path $PSScriptRoot "live-environment-windows.mjs"
    if (-not (Test-Path -LiteralPath $testScript -PathType Leaf)) {
        throw "The headed live Playwright suite is missing: $testScript"
    }
    if ([string]::IsNullOrWhiteSpace($env:PLAYWRIGHT_MODULE_DIR) -or
        -not (Test-Path -LiteralPath (Join-Path $env:PLAYWRIGHT_MODULE_DIR "index.mjs") -PathType Leaf)) {
        throw "PLAYWRIGHT_MODULE_DIR must point to an installed Playwright package. Run the repository SmartPrint QA orchestrator first or install the pinned Playwright package."
    }

    $env:SMARTPRINT_LIVE_QA_BASE_URL = $canonicalPublicOrigin
    if ([string]::IsNullOrWhiteSpace($env:LIVE_QA_ARTIFACT_DIR)) {
        $env:LIVE_QA_ARTIFACT_DIR = Join-Path "artifacts" ("live-environment-qa-windows-headed-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    }

    $env:LIVE_QA_CHECK_SHOP_WORKSPACE = if ($RunShopWorkspace) { "true" } else { "false" }
    $env:LIVE_QA_CHECK_PLATFORM_ADMIN = if ($RunPlatformAdmin) { "true" } else { "false" }
    $env:LIVE_QA_CHECK_CUSTOMER_STATION = if ($RunCustomerStation) { "true" } else { "false" }
    $env:LIVE_QA_CHECK_CUSTOMER_UPLOAD_WORKFLOW = if ($RunCustomerUploadWorkflow) { "true" } else { "false" }
    $env:SMARTPRINT_LIVE_QA_EXPECTED_QA_SHOP_NAME = $ExpectedQaShopName

    if ($RunShopWorkspace) {
        if ([string]::IsNullOrWhiteSpace($env:SMARTPRINT_LIVE_QA_SHOP_EMAIL)) {
            $env:SMARTPRINT_LIVE_QA_SHOP_EMAIL = Read-Host "Dedicated QA shop email"
        }
        if ([string]::IsNullOrWhiteSpace($env:SMARTPRINT_LIVE_QA_SHOP_PASSWORD)) {
            Set-ProcessSecretFromPrompt "SMARTPRINT_LIVE_QA_SHOP_PASSWORD" "Dedicated QA shop password"
        }
    }
    if ($RunPlatformAdmin) {
        if ([string]::IsNullOrWhiteSpace($env:SMARTPRINT_LIVE_QA_ADMIN_EMAIL)) {
            $env:SMARTPRINT_LIVE_QA_ADMIN_EMAIL = Read-Host "Dedicated QA platform-admin email"
        }
        if ([string]::IsNullOrWhiteSpace($env:SMARTPRINT_LIVE_QA_ADMIN_PASSWORD)) {
            Set-ProcessSecretFromPrompt "SMARTPRINT_LIVE_QA_ADMIN_PASSWORD" "Dedicated QA platform-admin password"
        }
    }
    if ($RunCustomerStation) {
        if ([string]::IsNullOrWhiteSpace($StationPath)) {
            $StationPath = Read-Host "Dedicated disposable test station path (for example /s/QA_STATION_01)"
        }
        if ($StationPath -notmatch "^/s/[A-Za-z0-9_-]+/?$") {
            throw "StationPath must be /s/PUBLIC_CODE for a dedicated disposable test station."
        }
        $env:SMARTPRINT_LIVE_QA_STATION_PATH = $StationPath
    }
    if ($RunCustomerUploadWorkflow) {
        if (-not $RunShopWorkspace -or -not $RunCustomerStation) {
            throw "Customer upload workflow requires -RunShopWorkspace and -RunCustomerStation."
        }
        if ([string]::IsNullOrWhiteSpace($ExpectedQaShopName)) {
            $ExpectedQaShopName = Read-Host "Exact expected QA shop display name"
        }
        if ([string]::IsNullOrWhiteSpace($ExpectedQaShopName)) {
            throw "ExpectedQaShopName is required to prevent uploads to the wrong shop."
        }
        $env:SMARTPRINT_LIVE_QA_EXPECTED_QA_SHOP_NAME = $ExpectedQaShopName
    }

    Write-Host ""
    Write-Host ("Target: " + $canonicalPublicOrigin) -ForegroundColor Cyan
    Write-Host "A visible Microsoft Edge window will open. Complete any Cloudflare challenge normally if one appears." -ForegroundColor Yellow
    Write-Host "This optional production-origin smoke suite is separate from the local real-data acceptance runner." -ForegroundColor Yellow
    Write-Host "It does not change Cloudflare settings." -ForegroundColor Yellow
    Write-Host ""

    & $node.Source $testScript
    $scriptExit = $LASTEXITCODE
    if ($scriptExit -ne 0) {
        throw "Playwright live QA reported failures (exit code $scriptExit). Review report.json and screenshots."
    }
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], "Process")
    }
}
