[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$qaConfigPath = Join-Path $PSScriptRoot "smartprint-qa.config.json"
$tauriConfigPath = Join-Path $repositoryRoot "apps/desktop/src-tauri/tauri.conf.json"

$qaConfig = Get-Content -LiteralPath $qaConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop
$tauriConfig = Get-Content -LiteralPath $tauriConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop
$debugPort = [int]$qaConfig.apps.windows.webViewDevToolsPort

if ($debugPort -lt 1024 -or $debugPort -gt 65535) {
    throw "The configured WebView2 DevTools port must be between 1024 and 65535."
}
if (-not $tauriConfig.app.windows -or @($tauriConfig.app.windows).Count -lt 1) {
    throw "The Tauri configuration must define at least one desktop window."
}

# Supplying additionalBrowserArgs replaces WRY's default argument string.
# Preserve WRY's normal feature flags and add only this CI run's private port.
$browserArguments = "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection --remote-debugging-port=$debugPort --remote-allow-origins=*"
$windows = @($tauriConfig.app.windows)
foreach ($window in $windows) {
    $window | Add-Member -MemberType NoteProperty -Name additionalBrowserArgs -Value $browserArguments -Force
}

# Tauri merges arrays by replacement, so include the complete original window
# objects rather than emitting a partial window with only additionalBrowserArgs.
$overlay = [ordered]@{
    app = [ordered]@{
        windows = $windows
    }
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$json = ConvertTo-Json -InputObject $overlay -Depth 32
[IO.File]::WriteAllText($resolvedOutput, $json, [Text.UTF8Encoding]::new($false))

Write-Host "Prepared CI-only Tauri WebView2 arguments on loopback DevTools port $debugPort." -ForegroundColor Cyan
Write-Host "Base tauri.conf.json remains unchanged."
