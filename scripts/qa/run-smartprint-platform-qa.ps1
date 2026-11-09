#requires -Version 7.0
<#
.SYNOPSIS
One coordinated SmartPrint QA run for Web, Android and Windows.

.DESCRIPTION
Prompts for one or more platforms and one execution mode:
  Build - builds the selected app artifacts from this checkout, then tests them.
  Pull  - downloads artifacts from a successful GitHub CI run for this exact
          checkout commit, verifies their commit and SHA-256 manifest, then
          tests those artifacts without building the apps.

Web QA uses the production Portal dist for mixed-batch UI acceptance and the
QA-only browser fixture for Photo Studio. Android QA runs the AndroidJUnitRunner
instrumentation on one selected physical device or emulator. Windows QA tests
the packaged production executable and a separate QA-only WebView2 executable;
the production binary is never given remote-debugging flags.

.PARAMETER Platforms
One or more of Web, Android and Windows. Omit to choose interactively.

.PARAMETER Mode
Build or Pull. Omit to choose interactively.

.PARAMETER WorkflowRunId
Optional exact GitHub Actions CI run ID for Pull mode. If omitted, the newest
successful CI run for the current HEAD commit is selected. Older commits are
never substituted.

.PARAMETER AndroidDeviceSerial
Exact serial from adb devices -l. If omitted, exactly one authorized device
must be connected.

.PARAMETER EvidenceDirectory
Parent directory for the combined report and platform evidence.

.PARAMETER Headless
Run browser UI tests without opening Chromium windows. By default browser
tests are headed so the UI is visible while the tests interact with it.
#>
[CmdletBinding()]
param(
    [switch]$Help,
    [ValidateSet("Web", "Android", "Windows")]
    [string[]]$Platforms = @(),
    [string]$Mode = "",
    [string]$WorkflowRunId = "",
    [string]$AndroidDeviceSerial = "",
    [string]$EvidenceDirectory = "artifacts/platform-qa",
    [switch]$Headless
)

if ($Help) {
    Write-Host @"
SmartPrint coordinated platform QA

Usage:
  .\scripts\qa\run-smartprint-platform-qa.ps1
  .\scripts\qa\run-smartprint-platform-qa.ps1 -Platforms Web,Android,Windows -Mode Build
  .\scripts\qa\run-smartprint-platform-qa.ps1 -Platforms Android -Mode Build -AndroidDeviceSerial SERIAL
  .\scripts\qa\run-smartprint-platform-qa.ps1 -Platforms Web,Windows -Mode Pull
  .\scripts\qa\run-smartprint-platform-qa.ps1 -Platforms Android -Mode Pull -WorkflowRunId 123456789

Platforms:
  Web       Portal unit/Photo Studio browser tests plus production-dist browser acceptance.
  Android   Android JVM suite and real Android WebView instrumentation on one ADB device.
  Windows   API/Agent tests, Rust/desktop checks, production-app smoke and native WebView2 acceptance.

Modes:
  Build     Explicitly build selected app artifacts from this checkout, then test them.
  Pull      Download artifacts from an exact-commit successful GitHub CI run, verify manifests,
            then test them. Application build tasks are not invoked in this mode.

Safety:
  - Android installs only com.quantaedge.smartprint.localqa[.*]. The production Android
    package is never uninstalled, replaced or data-cleared.
  - Printer dispatch, live payments and external refund transfers are disabled.
  - Native Windows mixed-batch tests use synthetic data and restore the prior Credential
    Manager session.
  - Pull mode refuses missing, expired, mismatched-commit or checksum-invalid artifacts.
  - All platform results are combined in one summary.json and summary.txt.
"@
    exit 0
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot
$repository = "Manav326/smart-print"

function Select-PlatformsInteractive {
    Write-Host ""
    Write-Host "Select platforms to test:" -ForegroundColor Cyan
    Write-Host "  1 - Web"
    Write-Host "  2 - Android"
    Write-Host "  3 - Windows"
    Write-Host "  A - All three"
    Write-Host "You can combine selections, for example: 1,2 or Web,Android."
    $answer = (Read-Host "Platforms").Trim()
    if ($answer -match '^(?i:a|all)$') { return @("Web", "Android", "Windows") }
    $selected = [System.Collections.Generic.List[string]]::new()
    foreach ($token in ($answer -split '[,; ]+' | Where-Object { $_ })) {
        $platform = switch -Regex ($token) {
            '^(?i:1|w|web)$' { "Web"; break }
            '^(?i:2|a|android)$' { "Android"; break }
            '^(?i:3|d|desktop|windows)$' { "Windows"; break }
            default { throw "Unknown platform selection '$token'. Choose 1, 2, 3 or A." }
        }
        if ($selected -notcontains $platform) { $selected.Add($platform) }
    }
    if ($selected.Count -eq 0) { throw "No QA platform was selected." }
    return @($selected)
}

function Select-ModeInteractive {
    Write-Host ""
    Write-Host "Select how the apps should be tested:" -ForegroundColor Cyan
    Write-Host "  1 - Build selected apps from this checkout, then test"
    Write-Host "  2 - Pull successful CI artifacts for this exact commit, then test without app builds"
    $answer = (Read-Host "Execution mode").Trim()
    switch -Regex ($answer) {
        '^(?i:1|b|build)$' { return "Build" }
        '^(?i:2|p|pull)$' { return "Pull" }
        default { throw "Unknown execution mode '$answer'. Choose 1 or 2." }
    }
}

if ($Platforms.Count -eq 0) { $Platforms = Select-PlatformsInteractive }
$Platforms = @($Platforms | Select-Object -Unique)
if ([string]::IsNullOrWhiteSpace($Mode)) { $Mode = Select-ModeInteractive }
if ($Mode -notin @("Build", "Pull")) { throw "Mode must be Build or Pull." }
if (-not [string]::IsNullOrWhiteSpace($WorkflowRunId) -and $WorkflowRunId -notmatch '^\d+$') { throw "WorkflowRunId must be a numeric GitHub Actions run ID." }
if (-not [string]::IsNullOrWhiteSpace($AndroidDeviceSerial) -and $AndroidDeviceSerial -notmatch '^[A-Za-z0-9._:-]{1,128}$') { throw "AndroidDeviceSerial contains unsupported characters." }

$headOutput = @(& git rev-parse HEAD 2>&1)
if ($LASTEXITCODE -ne 0) { throw "Could not resolve the current Git commit. Run this from the SmartPrint repository." }
$sourceCommit = ($headOutput -join "").Trim().ToLowerInvariant()
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') { throw "The current checkout did not resolve to a full Git commit SHA." }
$branchOutput = @(& git branch --show-current 2>&1)
$branch = ($branchOutput -join "").Trim()
$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$evidenceRoot = if ([IO.Path]::IsPathRooted($EvidenceDirectory)) {
    [IO.Path]::GetFullPath($EvidenceDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $EvidenceDirectory))
}
$runDirectory = Join-Path $evidenceRoot ("{0}-{1}" -f $Mode.ToLowerInvariant(), $runStamp)
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$script:results = [System.Collections.Generic.List[object]]::new()
$script:workflowRun = $null
$script:downloadRoot = $null
$startedUtc = [DateTime]::UtcNow

function Add-Result([string]$Platform, [string]$Name, [string]$Status, [string]$Detail = "", [string]$Evidence = "") {
    $script:results.Add([PSCustomObject]@{
        platform = $Platform
        name = $Name
        status = $Status
        detail = $Detail
        evidence = $Evidence
    })
    $colour = if ($Status -eq "PASS") { "Green" } elseif ($Status -eq "FAIL") { "Red" } elseif ($Status -eq "BLOCKED") { "Yellow" } else { "DarkYellow" }
    Write-Host ("[QA-RESULT] {0} | {1} | {2} | {3}" -f $Status, $Platform, $Name, $Detail) -ForegroundColor $colour
}

function Invoke-QAStep {
    param(
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [string]$Evidence = ""
    )
    Write-Host ""
    Write-Host ("=== {0}: {1} ===" -f $Platform, $Name) -ForegroundColor Cyan
    try {
        $global:LASTEXITCODE = 0
        & $Action
        if ($null -ne $LASTEXITCODE -and [int]$LASTEXITCODE -ne 0) {
            throw "Command exited with code $LASTEXITCODE."
        }
        Add-Result $Platform $Name "PASS" "Completed successfully." $Evidence
        return $true
    } catch {
        Add-Result $Platform $Name "FAIL" $_.Exception.Message $Evidence
        Write-Host ($_.Exception | Out-String) -ForegroundColor Red
        return $false
    }
}

function Get-GhRun {
    $gh = Get-Command gh.exe -ErrorAction SilentlyContinue
    if (-not $gh) { $gh = Get-Command gh -ErrorAction SilentlyContinue }
    if (-not $gh) { throw "GitHub CLI (gh) is required for Pull mode. Install/authenticate gh, then rerun. No local build fallback is attempted." }
    & $gh.Source auth status --hostname github.com *> $null
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI is not authenticated. Run 'gh auth login' and retry Pull mode." }

    if ($WorkflowRunId) {
        $runJson = & $gh.Source run view $WorkflowRunId --repo $repository --json databaseId,headSha,status,conclusion,url,workflowName,headBranch,number
        if ($LASTEXITCODE -ne 0) { throw "Could not read GitHub Actions run $WorkflowRunId." }
        $run = $runJson | ConvertFrom-Json -ErrorAction Stop
        if ([string]$run.headSha -ne $sourceCommit) { throw "Run $WorkflowRunId belongs to commit $($run.headSha), not this checkout ($sourceCommit). No artifact was downloaded." }
        if ([string]$run.status -ne "completed" -or [string]$run.conclusion -ne "success") { throw "Run $WorkflowRunId is not fully successful (status=$($run.status), conclusion=$($run.conclusion))." }
        return $run
    }

    $runJson = & $gh.Source run list --repo $repository --workflow ci.yml --commit $sourceCommit --status completed --json databaseId,headSha,status,conclusion,url,workflowName,headBranch,number,createdAt --limit 30
    if ($LASTEXITCODE -ne 0) { throw "Could not list successful CI runs for commit $sourceCommit." }
    $runs = @($runJson | ConvertFrom-Json -ErrorAction Stop)
    $matches = @($runs | Where-Object { [string]$_.headSha -eq $sourceCommit -and [string]$_.workflowName -eq "CI" -and [string]$_.status -eq "completed" -and [string]$_.conclusion -eq "success" })
    if ($matches.Count -eq 0) { throw "No successful CI run exists for this exact commit ($sourceCommit). Wait for CI to pass or supply a matching -WorkflowRunId. Pull mode will not use artifacts from an older commit." }
    return ($matches | Sort-Object { [DateTime]$_.createdAt } -Descending | Select-Object -First 1)
}

function Download-CIArtifact([string]$ArtifactName) {
    $gh = Get-Command gh.exe -ErrorAction SilentlyContinue
    if (-not $gh) { $gh = Get-Command gh -ErrorAction Stop }
    $destination = Join-Path $script:downloadRoot $ArtifactName
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Write-Host "Downloading artifact '$ArtifactName' from CI run $($script:workflowRun.databaseId)..." -ForegroundColor Cyan
    & $gh.Source run download ([string]$script:workflowRun.databaseId) --repo $repository --name $ArtifactName --dir $destination
    if ($LASTEXITCODE -ne 0) { throw "Artifact '$ArtifactName' could not be downloaded from run $($script:workflowRun.databaseId). It may be missing or expired. No local build fallback is attempted." }
    return $destination
}

function Test-CIArtifactManifest([string]$Directory, [string]$ExpectedPlatform) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $manifestPath = Join-Path $Directory "qa-artifact-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Artifact '$Directory' has no qa-artifact-manifest.json." }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne "SmartPrint" -or $manifest.platform -ne $ExpectedPlatform) {
        throw "Artifact manifest identity/platform mismatch. Expected SmartPrint/$ExpectedPlatform."
    }
    if ([string]$manifest.sourceCommit -ne $sourceCommit) { throw "Artifact was built from $($manifest.sourceCommit), not local commit $sourceCommit." }
    if ([string]$manifest.workflowRunId -ne [string]$script:workflowRun.databaseId) { throw "Artifact workflow run ID does not match the selected CI run." }
    if (@($manifest.files).Count -eq 0) { throw "Artifact manifest has an empty file list." }

    $listedPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $relative = ([string]$entry.path).Replace("/", [IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)') { throw "Unsafe artifact manifest path: $relative" }
        $filePath = [IO.Path]::GetFullPath((Join-Path $Directory $relative))
        if (-not $filePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Artifact manifest path escaped its extraction directory: $relative" }
        if (-not $listedPaths.Add($relative.Replace([IO.Path]::DirectorySeparatorChar, "/"))) { throw "Duplicate path in artifact manifest: $relative" }
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Artifact file is missing: $relative" }
        $item = Get-Item -LiteralPath $filePath
        if ([long]$item.Length -ne [long]$entry.bytes) { throw "Artifact file size mismatch: $relative" }
        $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne [string]$entry.sha256) { throw "Artifact SHA-256 mismatch: $relative" }
    }
    $actualPaths = @(Get-ChildItem -LiteralPath $Directory -File -Recurse | ForEach-Object {
        $_.FullName.Substring($root.Length).Replace([IO.Path]::DirectorySeparatorChar, "/")
    } | Where-Object { $_ -ne "qa-artifact-manifest.json" })
    foreach ($actualPath in $actualPaths) {
        if (-not $listedPaths.Contains($actualPath)) { throw "Artifact contains an unmanifested file: $actualPath" }
    }
    foreach ($listedPath in $listedPaths) {
        if ($actualPaths -notcontains $listedPath) { throw "Manifest contains a path that was not extracted: $listedPath" }
    }
    return $manifest
}

function Assert-ArtifactFiles([object]$Manifest, [string[]]$RequiredPaths, [string]$ArtifactName) {
    $manifestPaths = @($Manifest.files | ForEach-Object { [string]$_.path })
    foreach ($requiredPath in $RequiredPaths) {
        if ($manifestPaths -notcontains $requiredPath) {
            throw "Artifact '$ArtifactName' is missing required manifested file '$requiredPath'."
        }
    }
}

function Start-StaticPortalServer([string]$Directory, [int]$Port, [string]$LogPrefix) {
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) { $node = Get-Command node -ErrorAction Stop }
    $serverScript = Join-Path $PSScriptRoot "serve-static-portal.mjs"
    $stdout = Join-Path $runDirectory ($LogPrefix + ".stdout.log")
    $stderr = Join-Path $runDirectory ($LogPrefix + ".stderr.log")
    $argumentLine = '"{0}" "{1}" {2}' -f $serverScript, $Directory, $Port
    $process = Start-Process -FilePath $node.Source -ArgumentList $argumentLine -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            Get-Content $stdout, $stderr -ErrorAction SilentlyContinue
            throw "Static Portal QA server exited with code $($process.ExitCode)."
        }
        try {
            $response = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/" -f $Port) -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return $process }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    throw "Static production Portal did not become ready on port $Port."
}

function Invoke-PhotoStudioSuite {
    $scriptPath = Join-Path $PSScriptRoot "photo-studio-test.ps1"
    $platform = if ($Platforms -contains "Windows") { "Windows" } else { "Web" }
    $arguments = @("-Platform", $platform, "-SkipInstall", "-ArtifactDirectory", (Join-Path $runDirectory "photo-studio"))
    if (-not $Headless) { $arguments += "-Headed" }
    if ($Mode -eq "Pull") { $arguments += "-SkipBuild" }

    $previousBase = [Environment]::GetEnvironmentVariable("SMARTPRINT_PHOTO_STUDIO_BASE_URL", "Process")
    $fixtureServer = $null
    try {
        if ($Mode -eq "Pull") {
            $fixtureServer = Start-StaticPortalServer $script:photoStudioQaDirectory 4173 "photo-studio-qa-fixture-server"
            $env:SMARTPRINT_PHOTO_STUDIO_BASE_URL = "http://127.0.0.1:4173"
        } else {
            Remove-Item Env:SMARTPRINT_PHOTO_STUDIO_BASE_URL -ErrorAction SilentlyContinue
        }
        & $scriptPath @arguments
        if ($LASTEXITCODE -ne 0) { throw "Photo Studio logic/browser acceptance suite failed." }
    } finally {
        [Environment]::SetEnvironmentVariable("SMARTPRINT_PHOTO_STUDIO_BASE_URL", $previousBase, "Process")
        if ($fixtureServer -and -not $fixtureServer.HasExited) {
            $fixtureServer.Kill($true)
            $fixtureServer.WaitForExit(5000) | Out-Null
        }
    }
}

function Invoke-WebProductionArtifactTest([string]$DistDirectory) {
    $server = Start-StaticPortalServer $DistDirectory 4174 "web-production-server"
    $previousBase = [Environment]::GetEnvironmentVariable("SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL", "Process")
    $previousHeaded = [Environment]::GetEnvironmentVariable("SMARTPRINT_MIXED_BATCH_REFUND_HEADED", "Process")
    try {
        $env:SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL = "http://127.0.0.1:4174"
        if ($Headless) { $env:SMARTPRINT_MIXED_BATCH_REFUND_HEADED = "0" } else { $env:SMARTPRINT_MIXED_BATCH_REFUND_HEADED = "1" }
        $mixed = Join-Path $PSScriptRoot "mixed-batch-refund-test.ps1"
        & $mixed -Platform Web -SkipInstall -Headed:$(-not $Headless) -ArtifactDirectory (Join-Path $runDirectory "web-production-acceptance")
        if ($LASTEXITCODE -ne 0) { throw "Production Portal artifact browser acceptance failed." }
    } finally {
        [Environment]::SetEnvironmentVariable("SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL", $previousBase, "Process")
        [Environment]::SetEnvironmentVariable("SMARTPRINT_MIXED_BATCH_REFUND_HEADED", $previousHeaded, "Process")
        if ($server -and -not $server.HasExited) {
            $server.Kill($true)
            $server.WaitForExit(5000) | Out-Null
        }
    }
}

function Build-WindowsQaExecutable([string]$ProductionExe) {
    if (-not (Test-Path -LiteralPath $ProductionExe -PathType Leaf)) { throw "Production Windows executable was not produced: $ProductionExe" }
    $desktopOutput = Join-Path $runDirectory "windows"
    New-Item -ItemType Directory -Force -Path $desktopOutput | Out-Null
    Copy-Item -LiteralPath $ProductionExe -Destination (Join-Path $desktopOutput "SmartPrint.Desktop.exe") -Force

    $prepare = Join-Path $PSScriptRoot "prepare-desktop-smoke-config.ps1"
    $smokeConfig = Join-Path $env:TEMP ("smartprint-platform-qa-" + [Guid]::NewGuid().ToString("N") + ".json")
    & $prepare -OutputPath $smokeConfig
    if ($LASTEXITCODE -ne 0) { throw "Could not prepare the isolated WebView2 QA configuration." }

    $smokeTarget = Join-Path $runDirectory "windows-smoke-target"
    $previousCargoTarget = [Environment]::GetEnvironmentVariable("CARGO_TARGET_DIR", "Process")
    try {
        $env:CARGO_TARGET_DIR = $smokeTarget
        # Keep the exact API origin already used for the production executable.
        # Only the WebView2 DevTools config differs between these two binaries.
        & pnpm.cmd --filter @smart-print/desktop exec tauri build --config $smokeConfig --no-bundle --ci
        if ($LASTEXITCODE -ne 0) { throw "Windows WebView2 QA executable build failed." }
        $qaExecutable = Join-Path $smokeTarget "release/smartprint-desktop.exe"
        if (-not (Test-Path -LiteralPath $qaExecutable -PathType Leaf)) { throw "The isolated Windows QA executable was not produced." }
        $destination = Join-Path $desktopOutput "SmartPrint.Desktop.QA.exe"
        Copy-Item -LiteralPath $qaExecutable -Destination $destination -Force
        $dllSources = @(
            (Split-Path -Parent $ProductionExe),
            (Split-Path -Parent $qaExecutable)
        ) | Select-Object -Unique
        foreach ($dllSource in $dllSources) {
            if (Test-Path -LiteralPath $dllSource -PathType Container) {
                Get-ChildItem -LiteralPath $dllSource -Filter "*.dll" -File -ErrorAction SilentlyContinue |
                    Copy-Item -Destination $desktopOutput -Force
            }
        }
        return $destination
    } finally {
        [Environment]::SetEnvironmentVariable("CARGO_TARGET_DIR", $previousCargoTarget, "Process")
        if (Test-Path -LiteralPath $smokeConfig) { Remove-Item -LiteralPath $smokeConfig -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-WindowsArtifactTests([string]$DesktopDirectory, [string]$ModeForTest) {
    $productionExe = Join-Path $DesktopDirectory "SmartPrint.Desktop.exe"
    $qaExe = Join-Path $DesktopDirectory "SmartPrint.Desktop.QA.exe"
    if (-not (Test-Path -LiteralPath $productionExe -PathType Leaf)) { throw "Windows artifact is missing SmartPrint.Desktop.exe." }
    if (-not (Test-Path -LiteralPath $qaExe -PathType Leaf)) { throw "Windows artifact is missing SmartPrint.Desktop.QA.exe." }

    $smoke = Join-Path $PSScriptRoot "desktop-open-smoke.ps1"
    $profile = Join-Path $runDirectory "windows-production-webview2-profile"
    New-Item -ItemType Directory -Force -Path $profile | Out-Null
    $previousProfile = [Environment]::GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", "Process")
    try {
        $env:WEBVIEW2_USER_DATA_FOLDER = $profile
        & $smoke -ExecutablePath $productionExe
        if ($LASTEXITCODE -ne 0) { throw "Packaged production Windows executable failed native-window/UI smoke." }
    } finally {
        [Environment]::SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", $previousProfile, "Process")
    }

    $mixed = Join-Path $PSScriptRoot "mixed-batch-refund-test.ps1"
    $qaConfig = Get-Content -LiteralPath (Join-Path $PSScriptRoot "smartprint-qa.config.json") -Raw | ConvertFrom-Json -ErrorAction Stop
    $debugPort = [int]$qaConfig.apps.windows.webViewDevToolsPort
    & $mixed -Platform Windows -SkipInstall -Headed:$(-not $Headless) -NativeExecutablePath $qaExe -WebView2DebugPort $debugPort -ArtifactDirectory (Join-Path $runDirectory "windows-mixed-batch")
    if ($LASTEXITCODE -ne 0) { throw "Windows native WebView2 mixed-batch acceptance failed." }
}

function Resolve-AndroidDeviceIfNeeded {
    if (-not ($Platforms -contains "Android")) { return $null }
    if ($AndroidDeviceSerial) { return $AndroidDeviceSerial }
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { Join-Path $env:LOCALAPPDATA "Android\Sdk" }
    $adb = Join-Path $sdk "platform-tools\adb.exe"
    if (-not (Test-Path -LiteralPath $adb -PathType Leaf)) { return $null }
    $lines = @(& $adb devices -l 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "ADB could not enumerate connected devices: $($lines -join '; ')" }
    $serials = @()
    foreach ($line in $lines) {
        if ($line -match '^\s*(?<serial>\S+)\s+device(?:\s|$)') { $serials += $Matches.serial }
    }
    if ($serials.Count -eq 1) { return [string]$serials[0] }
    if ($serials.Count -eq 0) { return $null }
    Write-Host "More than one ADB device is connected:" -ForegroundColor Yellow
    for ($index = 0; $index -lt $serials.Count; $index++) { Write-Host ("  {0}. {1}" -f ($index + 1), $serials[$index]) }
    $selection = Read-Host "Enter the number of the physical phone to test"
    $number = 0
    if (-not [int]::TryParse($selection, [ref]$number) -or $number -lt 1 -or $number -gt $serials.Count) { throw "Invalid Android device selection." }
    return [string]$serials[$number - 1]
}

$originalEnvironment = @{}
$environmentNames = @("SMARTPRINT_MIXED_BATCH_REFUND_BASE_URL","SMARTPRINT_MIXED_BATCH_REFUND_HEADED","SMARTPRINT_MIXED_BATCH_REFUND_EXECUTABLE","SMARTPRINT_PHOTO_STUDIO_BASE_URL","SMARTPRINT_WEBVIEW2_DEBUG_PORT","VITE_API_BASE_URL","CARGO_TARGET_DIR","WEBVIEW2_USER_DATA_FOLDER","VITE_ENABLE_PHOTO_STUDIO_QA")
foreach ($name in $environmentNames) { $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }

try {
    Write-Host "SMARTPRINT COORDINATED PLATFORM QA" -ForegroundColor Green
    Write-Host ("Platforms: " + ($Platforms -join ", "))
    Write-Host ("Mode: " + $Mode)
    Write-Host ("Checkout commit: " + $sourceCommit)
    Write-Host ("Branch: " + $branch)
    Write-Host ("Evidence: " + $runDirectory)
    Write-Host "Browser UI: visible (headed) | Printer dispatch: disabled | Live payment: disabled" -ForegroundColor Cyan

    if ($Mode -eq "Pull") {
        $script:workflowRun = Get-GhRun
        $script:downloadRoot = Join-Path $runDirectory ("ci-run-" + [string]$script:workflowRun.databaseId)
        New-Item -ItemType Directory -Force -Path $script:downloadRoot | Out-Null
        Write-Host ("Using successful CI run #{0}: {1}" -f $script:workflowRun.number, $script:workflowRun.url) -ForegroundColor Green

        if ($Platforms -contains "Web") {
            $webDir = Download-CIArtifact "smart-print-web-production"
            $webManifest = Test-CIArtifactManifest $webDir "web"
            Assert-ArtifactFiles $webManifest @("index.html") "smart-print-web-production"
            $script:webArtifactDirectory = $webDir
        }
        if ($Platforms -contains "Web" -or $Platforms -contains "Windows") {
            $webQaDir = Download-CIArtifact "smart-print-web-photo-studio-qa"
            $webQaManifest = Test-CIArtifactManifest $webQaDir "webqa"
            Assert-ArtifactFiles $webQaManifest @("index.html", "tests/photo-studio.browser.html") "smart-print-web-photo-studio-qa"
            if (-not (Test-Path -LiteralPath (Join-Path $webQaDir "tests/photo-studio.browser.html") -PathType Leaf)) {
                throw "Pulled Web QA artifact does not contain the Photo Studio fixture."
            }
            $script:photoStudioQaDirectory = $webQaDir
        }
        if ($Platforms -contains "Android") {
            $androidDir = Download-CIArtifact "smart-print-android-device-qa"
            $androidManifest = Test-CIArtifactManifest $androidDir "android"
            Assert-ArtifactFiles $androidManifest @("SmartPrint-Android-LocalQA.apk", "SmartPrint-Android-LocalQA-Test.apk", "qa-android.json", "fixture/tests/photo-studio.browser.html") "smart-print-android-device-qa"
            $metadataPath = Join-Path $androidDir "qa-android.json"
            if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw "Android artifact metadata is missing." }
            $androidMetadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -ErrorAction Stop
            if ($androidMetadata.serviceUrl -ne "http://127.0.0.1:3000" -or $androidMetadata.fixtureDirectory -ne "fixture") { throw "Android QA artifact fixture origin/metadata mismatch." }
            $script:androidArtifactDirectory = $androidDir
            $script:androidMetadata = $androidMetadata
        }
        if ($Platforms -contains "Windows") {
            $windowsDir = Download-CIArtifact "smart-print-desktop-win-x64"
            $windowsManifest = Test-CIArtifactManifest $windowsDir "windows"
            Assert-ArtifactFiles $windowsManifest @("SmartPrint.Desktop.exe", "SmartPrint.Desktop.QA.exe") "smart-print-desktop-win-x64"
            $script:windowsArtifactDirectory = $windowsDir
        }
    } else {
        if ($WorkflowRunId) { throw "-WorkflowRunId is only valid with -Mode Pull." }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "node_modules") -PathType Container)) {
        Invoke-QAStep "Shared" "Install locked Node dependencies once" {
            & pnpm.cmd install --frozen-lockfile
        } -Evidence (Join-Path $runDirectory "pnpm-install.log") | Out-Null
    }

    $resolvedAndroidSerial = Resolve-AndroidDeviceIfNeeded
    if ($resolvedAndroidSerial) { $AndroidDeviceSerial = $resolvedAndroidSerial }

    if ($Platforms -contains "Web" -or $Platforms -contains "Windows") {
        $portalStep = Invoke-QAStep "Web/Shared UI" "Photo Studio logic and headed browser acceptance" {
            Invoke-PhotoStudioSuite
        } -Evidence (Join-Path $runDirectory "photo-studio")
        if (-not $portalStep) { Write-Warning "Shared Web/Photo Studio tests failed. Remaining selected platform checks will continue." }
    }

    if ($Platforms -contains "Web") {
        $webDist = if ($Mode -eq "Build") { Join-Path $repoRoot "apps/portal/dist" } else { $script:webArtifactDirectory }
        Invoke-QAStep "Web" "Mixed-batch/refund acceptance against the production Portal dist" {
            Invoke-WebProductionArtifactTest $webDist
        } -Evidence (Join-Path $runDirectory "web-production-acceptance") | Out-Null
    }

    if ($Platforms -contains "Windows") {
        $windowsDirectory = $null
        if ($Mode -eq "Build") {
            $productionExe = Join-Path $repoRoot "apps/desktop/src-tauri/target/release/smartprint-desktop.exe"
            $windowsDirectory = Join-Path $runDirectory "windows"
            $qaBuildOk = Invoke-QAStep "Windows" "Build isolated WebView2 QA executable from the current checkout" {
                $script:windowsQaExecutable = Build-WindowsQaExecutable $productionExe
            } -Evidence $windowsDirectory
            if ($qaBuildOk) {
                Invoke-QAStep "Windows" "Test built production executable and QA WebView2 executable" {
                    Invoke-WindowsArtifactTests $windowsDirectory "Build"
                } -Evidence (Join-Path $windowsDirectory "acceptance") | Out-Null
            } else {
                Add-Result "Windows" "Native packaged UI acceptance" "BLOCKED" "QA executable build did not pass."
            }
        } else {
            $windowsDirectory = $script:windowsArtifactDirectory
            Invoke-QAStep "Windows" "Test pulled production executable and CI QA WebView2 executable" {
                Invoke-WindowsArtifactTests $windowsDirectory "Pull"
            } -Evidence (Join-Path $runDirectory "windows-acceptance") | Out-Null
        }
    }

    if ($Platforms -contains "Android") {
        $androidRunner = Join-Path $PSScriptRoot "run-smartprint-android-device-tests.ps1"
        if ($Mode -eq "Build") {
            Invoke-QAStep "Android" "Build and test on the selected physical phone" {
                $args = @("-Build","-ApplicationId","com.quantaedge.smartprint.localqa","-OutputDirectory",(Join-Path $runDirectory "android"))
                if ($AndroidDeviceSerial) { $args += @("-DeviceSerial",$AndroidDeviceSerial) }
                & $androidRunner @args
            } -Evidence (Join-Path $runDirectory "android") | Out-Null
        } else {
            $androidDir = $script:androidArtifactDirectory
            Invoke-QAStep "Android" "Install pulled CI QA APK pair and run instrumentation on the selected phone" {
                $appApk = Join-Path $androidDir "SmartPrint-Android-LocalQA.apk"
                $testApk = Join-Path $androidDir "SmartPrint-Android-LocalQA-Test.apk"
                $fixture = Join-Path $androidDir ([string]$script:androidMetadata.fixtureDirectory)
                $args = @("-ApplicationApkPath",$appApk,"-InstrumentationApkPath",$testApk,
                    "-ApplicationId",[string]$script:androidMetadata.applicationId,
                    "-FixtureDirectory",$fixture,
                    "-OutputDirectory",(Join-Path $runDirectory "android"))
                if ($AndroidDeviceSerial) { $args += @("-DeviceSerial",$AndroidDeviceSerial) }
                & $androidRunner @args
            } -Evidence (Join-Path $runDirectory "android") | Out-Null
        }
    }
}
catch {
    Add-Result "Coordinator" "QA orchestration" "FAIL" $_.Exception.Message (Join-Path $runDirectory "coordinator-error.log")
    ($_ | Out-String) | Set-Content -LiteralPath (Join-Path $runDirectory "coordinator-error.log") -Encoding UTF8
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], "Process")
    }
    $failed = @($results | Where-Object status -eq "FAIL").Count
    $blocked = @($results | Where-Object status -eq "BLOCKED").Count
    $summary = [PSCustomObject]@{
        product = "SmartPrint"
        mode = $Mode
        platforms = @($Platforms)
        sourceCommit = $sourceCommit
        branch = $branch
        workflowRunId = if ($script:workflowRun) { [string]$script:workflowRun.databaseId } else { $null }
        workflowRunUrl = if ($script:workflowRun) { [string]$script:workflowRun.url } else { $null }
        startedUtc = $startedUtc.ToString("o")
        completedUtc = [DateTime]::UtcNow.ToString("o")
        result = if ($failed -gt 0 -or $blocked -gt 0) { "FAIL" } else { "PASS" }
        passed = @($results | Where-Object status -eq "PASS").Count
        failed = $failed
        blocked = $blocked
        skipped = @($results | Where-Object status -eq "SKIPPED").Count
        results = @($results)
        safety = @{
            realPaymentsSubmitted = $false
            externalRefundTransfers = $false
            physicalPrinterDispatch = $false
            productionAndroidPackageModified = $false
            productionAndroidDataCleared = $false
            ciArtifactsCommitVerified = ($Mode -eq "Pull")
        }
        evidenceDirectory = $runDirectory
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runDirectory "summary.json") -Encoding UTF8
    $summary | Format-List | Out-String | Set-Content -LiteralPath (Join-Path $runDirectory "summary.txt") -Encoding UTF8
    Write-Host ""
    Write-Host "=== SMARTPRINT PLATFORM QA SUMMARY ===" -ForegroundColor Cyan
    $summary | Format-Table mode, result, passed, failed, blocked, skipped, workflowRunId -AutoSize
    Write-Host ("Evidence: " + $runDirectory) -ForegroundColor Cyan
}

if ($summary.result -ne "PASS") { exit 1 }
