[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$PackagePath,

    [string]$ReportPath,

    [string]$CertificatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$package = (Resolve-Path -LiteralPath $PackagePath).Path
if ([IO.Path]::GetExtension($package) -ine ".msix") {
    throw "PackagePath must point to the final .msix file."
}

$session = Get-Process -Id $PID
if ($session.SessionId -eq 0) {
    throw "WACK must run in an active interactive user session, not Session 0. Open an elevated PowerShell window on the Windows desktop and rerun."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $certificate = (Resolve-Path -LiteralPath $CertificatePath).Path
    Import-Certificate -FilePath $certificate -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
}

$appCertCandidates = @(
    (Join-Path \${env:ProgramFiles(x86)} "Windows Kits\10\App Certification Kit\appcert.exe"),
    (Join-Path $env:ProgramFiles "Windows Kits\10\App Certification Kit\appcert.exe")
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

if ($appCertCandidates.Count -eq 0) {
    throw "appcert.exe was not found. Install the Windows App Certification Kit component from the Windows SDK, then rerun."
}
$appCert = $appCertCandidates[0]

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path (Split-Path -Parent $package) "SmartPrint-MSIX-WACK-Report.xml"
}
$report = [IO.Path]::GetFullPath($ReportPath)
$reportDirectory = Split-Path -Parent $report
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
Remove-Item -LiteralPath $report -Force -ErrorAction SilentlyContinue

Write-Host "Resetting Windows App Certification Kit."
& $appCert reset
if ($LASTEXITCODE -ne 0) { throw "WACK reset failed with exit code $LASTEXITCODE." }

Write-Host "Running WACK against the exact MSIX: $package"
& $appCert test -appxpackagepath $package -reportoutputpath $report
$wackExitCode = $LASTEXITCODE

if (-not (Test-Path -LiteralPath $report -PathType Leaf)) {
    throw "WACK did not produce the expected XML report at '$report' (exit code $wackExitCode)."
}

[xml]$xml = Get-Content -LiteralPath $report -Raw
$root = $xml.DocumentElement
if ($null -eq $root -or $root.LocalName -ne "REPORT" -or -not $root.HasAttribute("OVERALL_RESULT")) {
    throw "WACK report is incomplete or does not contain the expected REPORT/OVERALL_RESULT fields: $report"
}
if (-not $root.HasAttribute("VALIDATION_TYPE") -or $root.GetAttribute("PARTIAL_RUN") -eq "TRUE") {
    throw "WACK produced an interim or partial report. Wait for the complete validation run and rerun if needed."
}

$result = $root.GetAttribute("OVERALL_RESULT")
$appName = $root.GetAttribute("APP_NAME")
$appVersion = $root.GetAttribute("APP_VERSION")

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $manifestEntry = $archive.GetEntry("AppxManifest.xml")
    if ($null -eq $manifestEntry) { throw "MSIX does not contain AppxManifest.xml." }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
} finally {
    $archive.Dispose()
}

$packageName = $manifest.Package.Identity.Name
$packageVersion = $manifest.Package.Identity.Version
if ($appName -ne $packageName -or $appVersion -ne $packageVersion) {
    throw "WACK report identity/version ($appName $appVersion) does not match the tested MSIX ($packageName $packageVersion)."
}

$htmlReport = Join-Path (Join-Path $env:LOCALAPPDATA "Microsoft\AppCertKit") ([IO.Path]::GetFileNameWithoutExtension($report) + ".htm")
if (Test-Path -LiteralPath $htmlReport) {
    Copy-Item -LiteralPath $htmlReport -Destination ([IO.Path]::ChangeExtension($report, ".htm")) -Force
}

Write-Host "WACK overall result: $result"
Write-Host "Package identity: $appName"
Write-Host "Package version: $appVersion"
Write-Host "XML report: $report"
if ($wackExitCode -ne 0) {
    throw "WACK exited with code $wackExitCode. Review the XML/HTML report before submission."
}
if ($result -ne "PASS") {
    throw "WACK result is '$result', not PASS. Fix or formally resolve every reported issue and rerun before Store submission."
}

Write-Host "WACK passed for the exact SmartPrint MSIX package."
