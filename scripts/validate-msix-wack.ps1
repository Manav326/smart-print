[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$PackagePath,

    [string]$ReportPath,

    [string]$CertificatePath,

    [ValidateRange(10, 600)]
    [int]$ReportWaitSeconds = 90
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
    (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\App Certification Kit\appcert.exe"),
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
if ([IO.Path]::GetExtension($report) -ine ".xml") {
    throw "ReportPath must end in .xml. WACK's XML report is the authoritative result."
}
$reportDirectory = Split-Path -Parent $report
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
Remove-Item -LiteralPath $report -Force -ErrorAction SilentlyContinue
$htmlReport = Join-Path (Join-Path $env:LOCALAPPDATA "Microsoft\AppCertKit") ([IO.Path]::GetFileNameWithoutExtension($report) + ".htm")
Remove-Item -LiteralPath $htmlReport -Force -ErrorAction SilentlyContinue

Write-Host "Resetting Windows App Certification Kit."
& $appCert reset
if ($LASTEXITCODE -ne 0) { throw "WACK reset failed with exit code $LASTEXITCODE." }

$startedAt = Get-Date
Write-Host "Running WACK against the exact MSIX: $package"
& $appCert test -appxpackagepath $package -reportoutputpath $report
$wackExitCode = $LASTEXITCODE

$root = $null
$deadline = (Get-Date).AddSeconds($ReportWaitSeconds)
do {
    if (Test-Path -LiteralPath $report -PathType Leaf) {
        try {
            [xml]$candidateXml = Get-Content -LiteralPath $report -Raw
            $candidateRoot = $candidateXml.DocumentElement
            if (
                $null -ne $candidateRoot -and
                $candidateRoot.LocalName -eq "REPORT" -and
                $candidateRoot.HasAttribute("OVERALL_RESULT") -and
                $candidateRoot.HasAttribute("VALIDATION_TYPE")
            ) {
                $root = $candidateRoot
                break
            }
        } catch {
            # WACK may still be writing the XML report; retry until the deadline.
        }
    }
    if ((Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
} while ((Get-Date) -lt $deadline)

if ($null -eq $root) {
    $htmlExists = Test-Path -LiteralPath $htmlReport -PathType Leaf
    $htmlHint = if ($htmlExists) { " An HTML report exists at '$htmlReport', but XML is required for a complete Store-readiness check." } else { "" }
    throw "WACK did not produce a complete XML report at '$report' within $ReportWaitSeconds seconds (exit code $wackExitCode).$htmlHint"
}

if (
    -not $root.HasAttribute("PARTIAL_RUN") -or
    -not $root.HasAttribute("LATEST_VERSION") -or
    [string]::IsNullOrWhiteSpace($root.GetAttribute("VALIDATION_TYPE"))
) {
    throw "WACK report is missing the completion, kit-freshness or validation-type metadata required for Store review."
}
if ($root.GetAttribute("PARTIAL_RUN") -eq "TRUE") {
    throw "WACK marked this as a partial run. Complete all tests and rerun before Store submission."
}

$result = $root.GetAttribute("OVERALL_RESULT")
$appName = $root.GetAttribute("APP_NAME")
$appVersion = $root.GetAttribute("APP_VERSION")
$kitIsLatest = ($root.GetAttribute("LATEST_VERSION") -ne "FALSE")
$allWackTests = @($root.SelectNodes("REQUIREMENTS/REQUIREMENT/TEST"))
if ($allWackTests.Count -eq 0) {
    throw "WACK report contains no test results. It is not valid evidence for Store submission."
}
$nonPassingTests = @(
    foreach ($test in $allWackTests) {
        $resultNode = $test.SelectSingleNode("RESULT")
        $testResult = if ($null -ne $resultNode) { $resultNode.InnerText.Trim() } else { "" }
        if ($testResult -eq "PASS") { continue }
        [pscustomobject]@{
            Requirement = $test.ParentNode.GetAttribute("TITLE")
            Name = $test.GetAttribute("NAME")
            Result = $testResult
            Optional = $test.GetAttribute("OPTIONAL") -eq "TRUE"
            Messages = @($test.SelectNodes("MESSAGES/MESSAGE") | ForEach-Object { $_.GetAttribute("TEXT") })
        }
    }
)

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

if (Test-Path -LiteralPath $htmlReport) {
    Copy-Item -LiteralPath $htmlReport -Destination ([IO.Path]::ChangeExtension($report, ".htm")) -Force
}

$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "WACK overall result: $result"
Write-Host "Package identity: $appName"
Write-Host "Package version: $appVersion"
Write-Host "WACK kit version: $($root.GetAttribute('VERSION'))"
Write-Host "Latest WACK kit: $kitIsLatest"
Write-Host "Package SHA-256: $packageHash"
Write-Host "XML report: $report"
foreach ($test in $nonPassingTests) {
    $severity = if ($test.Optional) { "OPTIONAL" } else { "REQUIRED" }
    Write-Warning "$severity WACK test did not pass: $($test.Requirement) / $($test.Name) [$($test.Result)] $($test.Messages -join '; ')"
}
if ($wackExitCode -ne 0) {
    throw "WACK exited with code $wackExitCode. Review the XML/HTML report before submission."
}
if (-not $kitIsLatest) {
    throw "The installed WACK kit is not the latest version. Update the Windows App Certification Kit and rerun."
}
if ($result -ne "PASS") {
    throw "WACK result is '$result', not PASS. Fix or formally resolve every reported issue and rerun before Store submission."
}
$requiredFailures = @($nonPassingTests | Where-Object { -not $_.Optional })
if ($requiredFailures.Count -gt 0) {
    throw "WACK reported $($requiredFailures.Count) required test(s) that did not pass. Review the report before Store submission."
}

Write-Host "WACK passed for the exact SmartPrint MSIX package (SHA-256 $packageHash)."
