$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "..\build-windows-installer.iss"
$scriptText = Get-Content -LiteralPath $scriptPath -Raw

$desktopEntry = @(
    $scriptText -split "\r?\n" |
        Where-Object { $_ -like 'Source: "artifacts\desktop\*";*DestDir: "{app}";*' }
) | Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($desktopEntry)) {
    throw "The customer desktop payload must have an explicit Inno Setup Excludes list."
}

$excludeMatch = [regex]::Match($desktopEntry, 'Excludes:\s*"([^"]+)"')
if (-not $excludeMatch.Success) {
    throw "The customer desktop payload is missing its Inno Setup Excludes parameter."
}

$excludePatterns = @($excludeMatch.Groups[1].Value -split "," | ForEach-Object { $_.Trim() })
foreach ($required in @(
    "MicrosoftEdgeWebView2RuntimeInstallerX64.exe",
    "SmartPrint.Desktop.QA.exe"
)) {
    if ($excludePatterns -notcontains $required) {
        throw "The customer installer desktop payload must exclude '$required' using Inno Setup's comma-separated Excludes syntax."
    }
}

if ($excludeMatch.Groups[1].Value.Contains(";")) {
    throw "Inno Setup Excludes entries must not be separated with semicolons."
}

$explicitQaSource = [regex]::IsMatch(
    $scriptText,
    '(?im)^Source:\s*"[^"]*SmartPrint\.Desktop\.QA\.exe"'
)
if ($explicitQaSource) {
    throw "The CI-only SmartPrint.Desktop.QA.exe must never be declared as a customer installer source."
}

Write-Host "PASS: Inno Setup customer payload excludes the QA-only DevTools executable and temporary WebView2 bootstrapper."
