$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "..\build-windows-installer.iss"
$scriptText = Get-Content -LiteralPath $scriptPath -Raw

$desktopEntry = [regex]::Match(
    $scriptText,
    '(?im)^Source:\s*"artifacts\\desktop\\\*";\s*DestDir:\s*"{app}";\s*Excludes:\s*"([^"]+)"'
)
if (-not $desktopEntry.Success) {
    throw "The customer desktop payload must have an explicit Inno Setup Excludes list."
}

$excludePatterns = @($desktopEntry.Groups[1].Value -split "," | ForEach-Object { $_.Trim() })
foreach ($required in @(
    "MicrosoftEdgeWebView2RuntimeInstallerX64.exe",
    "SmartPrint.Desktop.QA.exe"
)) {
    if ($excludePatterns -notcontains $required) {
        throw "The customer installer desktop payload must exclude '$required' using Inno Setup's comma-separated Excludes syntax."
    }
}

if ($desktopEntry.Groups[1].Value.Contains(";")) {
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
