[CmdletBinding()]
param(
    [switch]$RunShopWorkspace,
    [switch]$RunPlatformAdmin,
    [switch]$RunCustomerStation,
    [switch]$RunCustomerUploadWorkflow,
    [string]$StationPath,
    [string]$ExpectedQaShopName
)

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
