<#
.SYNOPSIS
Run the SmartPrint Photo Studio Android WebView instrumentation on one physical phone or emulator.
#>
[CmdletBinding()]
param(
    [switch]$Help,
    [switch]$Build,
    [string]$ApplicationApkPath = "",
    [string]$InstrumentationApkPath = "",
    [string]$ApplicationId = "com.quantaedge.smartprint.localqa",
    [string]$FixtureDirectory = "",
    [string]$DeviceSerial = "",
    [string]$OutputDirectory = "artifacts/android-device-qa-local"
)
if ($Help) {
    Write-Host @"
Usage:
  .\scripts\qa\run-smartprint-android-device-tests.ps1 -Build [-DeviceSerial SERIAL]
  .\scripts\qa\run-smartprint-android-device-tests.ps1 -ApplicationApkPath APP.apk -InstrumentationApkPath TEST.apk -ApplicationId ID -FixtureDirectory DIR [-DeviceSerial SERIAL]

-Build explicitly builds the local QA APK pair and fixture. Without -Build, it uses only the supplied APKs and fixture; no Gradle build task is invoked.
Only the isolated com.quantaedge.smartprint.localqa[.*] package is installed. The production com.quantaedge.smartprint package is never uninstalled, updated or data-cleared.
"@
    exit 0
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot
if ($ApplicationId -notmatch '^com\.quantaedge\.smartprint\.localqa(?:\.[a-z0-9]+)*$') {
    throw "ApplicationId must remain under com.quantaedge.smartprint.localqa. Production package targeting is forbidden."
}
if ($Build -and ($ApplicationApkPath -or $InstrumentationApkPath)) { throw "-Build cannot be combined with prebuilt APK paths." }
if (-not $Build -and (-not $ApplicationApkPath -or -not $InstrumentationApkPath -or -not $FixtureDirectory)) {
    throw "Supply -Build or all of -ApplicationApkPath, -InstrumentationApkPath and -FixtureDirectory. APKs are never built implicitly."
}
if ($DeviceSerial -and $DeviceSerial -notmatch '^[A-Za-z0-9._:-]{1,128}$') { throw "Invalid Android device serial." }

$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory)) }
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$serverProcess = $null
$reverseCreated = $false
$adb = $null
$serial = $null
$results = [System.Collections.Generic.List[object]]::new()
$startedUtc = [DateTime]::UtcNow

function Add-Result([string]$Name, [string]$Status, [string]$Detail) {
    $results.Add([PSCustomObject]@{ name = $Name; status = $Status; detail = $Detail })
    $colour = if ($Status -eq "PASS") { "Green" } elseif ($Status -eq "FAIL") { "Red" } else { "Yellow" }
    Write-Host ("[ANDROID-QA] {0} | {1} | {2}" -f $Status, $Name, $Detail) -ForegroundColor $colour
}

function Resolve-Sdk {
    foreach ($candidate in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA "Android\Sdk"))) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate "platform-tools\adb.exe") -PathType Leaf)) {
            $root = (Resolve-Path -LiteralPath $candidate).Path
            $env:ANDROID_HOME = $root
            $env:ANDROID_SDK_ROOT = $root
            return $root
        }
    }
    throw "Android SDK Platform-Tools were not found. Install Android SDK Platform-Tools through Android Studio > Tools > SDK Manager."
}

function Resolve-Gradle {
    $cached = Join-Path $env:LOCALAPPDATA "SmartPrint\qa-tools\gradle-8.13\bin\gradle.bat"
    if (Test-Path -LiteralPath $cached -PathType Leaf) { return $cached }
    foreach ($name in @("gradle.bat", "gradle")) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command -and $command.Source) {
            $version = (& $command.Source --version 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0 -and $version -match '(?m)^Gradle\s+8\.13(?:\s|$)') { return $command.Source }
        }
    }
    $root = Join-Path $env:LOCALAPPDATA "SmartPrint\qa-tools"
    $temp = Join-Path $env:TEMP ("smartprint-gradle-" + [Guid]::NewGuid().ToString("N"))
    $zip = Join-Path $temp "gradle-8.13-bin.zip"
    $expected = "20f1b1176237254a6fc204d8434196fa11a4cfb387567519c61556e8710aed78"
    try {
        New-Item -ItemType Directory -Force -Path $temp | Out-Null
        $oldProgress = $ProgressPreference
        $ProgressPreference = "SilentlyContinue"
        try { Invoke-WebRequest "https://services.gradle.org/distributions/gradle-8.13-bin.zip" -OutFile $zip -ErrorAction Stop }
        finally { $ProgressPreference = $oldProgress }
        if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Gradle 8.13 checksum mismatch." }
        Expand-Archive $zip -DestinationPath $temp -Force
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        $target = Join-Path $root "gradle-8.13"
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Move-Item (Join-Path $temp "gradle-8.13") $target
        return (Join-Path $target "bin\gradle.bat")
    } finally {
        if (Test-Path $temp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

try {
    Write-Host "SmartPrint Android Photo Studio physical-device QA" -ForegroundColor Green
    Write-Host "Production package com.quantaedge.smartprint will not be modified."
    $sdk = Resolve-Sdk
    $adb = Join-Path $sdk "platform-tools\adb.exe"
    $aapt = Join-Path $sdk "build-tools\36.0.0\aapt.exe"
    if (-not (Test-Path $aapt -PathType Leaf)) { throw "Android Build-Tools 36.0.0/aapt.exe is missing." }

    if ($Build) {
        $gradle = Resolve-Gradle
        if (-not (Get-Command pnpm.cmd -ErrorAction SilentlyContinue)) { throw "pnpm.cmd is required for the QA fixture build." }
        $env:VITE_ENABLE_PHOTO_STUDIO_QA = "true"
        & pnpm.cmd --filter @smart-print/portal build
        if ($LASTEXITCODE -ne 0) { throw "QA-only Photo Studio fixture build failed." }
        $FixtureDirectory = Join-Path $repoRoot "apps/portal/dist"
        if (-not (Test-Path (Join-Path $FixtureDirectory "tests/photo-studio.browser.html"))) { throw "QA fixture page was not emitted by the portal build." }

        $gradleArgs = @("--no-daemon", "--console=plain", "-p", "apps/android",
            "-PsmartPrintServiceUrl=http://127.0.0.1:3000",
            "-PsmartPrintLocalQa=true",
            "-PsmartPrintQaApplicationId=$ApplicationId",
            "-PsmartPrintPhotoStudioQaEnabled=true",
            ":app:testDebugUnitTest", ":app:assembleDebug", ":app:assembleDebugAndroidTest")
        & $gradle @gradleArgs
        if ($LASTEXITCODE -ne 0) { throw "Android JVM tests or QA APK build failed." }
        $ApplicationApkPath = Join-Path $repoRoot "apps/android/app/build/outputs/apk/debug/app-debug.apk"
        $InstrumentationApkPath = Join-Path $repoRoot "apps/android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk"
        $localBundle = Join-Path $outputRoot "built-artifacts"
        New-Item -ItemType Directory -Force -Path $localBundle | Out-Null
        Copy-Item $ApplicationApkPath (Join-Path $localBundle "SmartPrint-Android-LocalQA.apk") -Force
        Copy-Item $InstrumentationApkPath (Join-Path $localBundle "SmartPrint-Android-LocalQA-Test.apk") -Force
        Copy-Item $FixtureDirectory (Join-Path $localBundle "fixture") -Recurse -Force
        Add-Result "Android build" "PASS" "Built the isolated app/test APK pair and QA fixture by explicit request."
    } else {
        $ApplicationApkPath = (Resolve-Path $ApplicationApkPath -ErrorAction Stop).Path
        $InstrumentationApkPath = (Resolve-Path $InstrumentationApkPath -ErrorAction Stop).Path
        $FixtureDirectory = (Resolve-Path $FixtureDirectory -ErrorAction Stop).Path
        Add-Result "Android build" "PASS" "Prebuilt artifacts selected; Gradle was not invoked."
    }

    $appBadging = (& $aapt dump badging $ApplicationApkPath 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or $appBadging -notmatch ("(?m)^package:\s+name='" + [regex]::Escape($ApplicationId) + "'")) { throw "App APK does not match the expected isolated package $ApplicationId." }
    $testPackage = "$ApplicationId.test"
    $testBadging = (& $aapt dump badging $InstrumentationApkPath 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or $testBadging -notmatch ("(?m)^package:\s+name='" + [regex]::Escape($testPackage) + "'")) { throw "Instrumentation APK does not match the expected package $testPackage." }
    if (-not (Test-Path (Join-Path $FixtureDirectory "tests/photo-studio.browser.html"))) { throw "Photo Studio QA fixture is missing from $FixtureDirectory." }

    $deviceLines = @(& $adb devices -l 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "adb devices failed: $($deviceLines -join ' ')" }
    $online = @()
    foreach ($line in $deviceLines) {
        if ($line -match '^\s*(?<serial>\S+)\s+device(?:\s|$)') { $online += $Matches.serial }
    }
    if ($DeviceSerial) {
        if (@($online | Where-Object { $_ -ceq $DeviceSerial }).Count -ne 1) { throw "Selected device $DeviceSerial is not authorized/online." }
        $serial = $DeviceSerial
    } elseif ($online.Count -eq 1) {
        $serial = $online[0]
    } elseif ($online.Count -eq 0) {
        throw "No authorized Android phone was detected. Connect USB debugging, unlock the phone and accept its RSA authorization prompt."
    } else {
        throw "Multiple Android devices are online: $($online -join ', '). Re-run with -DeviceSerial <serial>."
    }

    $model = (& $adb -s $serial shell getprop ro.product.model 2>&1) -join " "
    Write-Host "Selected Android device: $model ($serial)" -ForegroundColor Green
    Write-Host "Keep the phone unlocked and its screen visible during instrumentation." -ForegroundColor Cyan

    $reverse = @(& $adb -s $serial reverse --list 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect ADB reverse mappings." }
    $mapped = @($reverse | Where-Object { $_ -match '\btcp:3000\b' })
    if ($mapped.Count -gt 0) {
        if (-not ($mapped | Where-Object { $_ -match '\btcp:3000\s+tcp:3000\b' })) { throw "The phone has a conflicting tcp:3000 mapping; it will not be replaced." }
    } else {
        $result = @(& $adb -s $serial reverse tcp:3000 tcp:3000 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "adb reverse failed: $($result -join ' ')" }
        $reverseCreated = $true
    }

    $node = Get-Command node.exe -ErrorAction Stop
    $serverScript = Join-Path $PSScriptRoot "serve-static-portal.mjs"
    $serverOut = Join-Path $outputRoot "android-fixture-server.stdout.log"
    $serverErr = Join-Path $outputRoot "android-fixture-server.stderr.log"
    $serverArgs = '"{0}" "{1}" 3000' -f $serverScript, $FixtureDirectory
    $serverProcess = Start-Process -FilePath $node.Source -ArgumentList $serverArgs -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $fixtureReady = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($serverProcess.HasExited) { Get-Content $serverOut, $serverErr -ErrorAction SilentlyContinue; throw "QA fixture server exited with code $($serverProcess.ExitCode)." }
        try {
            $response = Invoke-WebRequest "http://127.0.0.1:3000/tests/photo-studio.browser.html" -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $fixtureReady = $true; break }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $fixtureReady) { throw "QA fixture did not become ready at localhost:3000." }

    $prodPackage = "com.quantaedge.smartprint"
    $prodBefore = @(& $adb -s $serial shell pm path $prodPackage 2>&1)
    $prodInstalledBefore = ($LASTEXITCODE -eq 0 -and ($prodBefore -join " ") -match "package:")
    $appInstall = @(& $adb -s $serial install -r $ApplicationApkPath 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "The isolated QA app could not be installed: $($appInstall -join ' ')." }
    $testInstall = @(& $adb -s $serial install -r -t $InstrumentationApkPath 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "The instrumentation APK could not be installed: $($testInstall -join ' ')." }
    Add-Result "Install isolated QA app" "PASS" "Installed $ApplicationId side-by-side. No production app package was updated or removed."

    $instrumentations = @(& $adb -s $serial shell pm list instrumentation 2>&1)
    $component = "$testPackage/androidx.test.runner.AndroidJUnitRunner"
    if ($LASTEXITCODE -ne 0 -or -not ($instrumentations -match [regex]::Escape($component))) { throw "Android did not register instrumentation component $component." }

    $logPath = Join-Path $outputRoot "android-physical-device-instrumentation.txt"
    Write-Host "Starting Android Photo Studio WebView instrumentation..." -ForegroundColor Cyan
    $output = @(& $adb -s $serial shell am instrument -w -r $component 2>&1)
    $instrumentExit = $LASTEXITCODE
    $text = $output -join [Environment]::NewLine
    $text | Set-Content -LiteralPath $logPath -Encoding UTF8
    $requiredCases = @(
        "customerPhotoStudioRunsInsideAndroidWebViewAndProducesExactMultiPagePdf",
        "shopOwnerCanEditAnExistingPhotoJobInsideAndroidWebViewAndSaveReplacementPdf",
        "androidSystemBackClosesPhotoStudioBeforeLeavingTheCustomerStation",
        "photoStudioModalStateAndSecureFlagSurviveActivityRecreation"
    )
    $missing = @($requiredCases | Where-Object { -not $text.Contains($_) })
    $ok = [regex]::Match($text, '(?m)^\s*OK\s+\((\d+)\s+tests?\)\s*$')
    if ($instrumentExit -ne 0 -or $text -match '(?i)FAILURES!!!|INSTRUMENTATION_FAILED|Process crashed|shortMsg=' -or -not $ok.Success -or [int]$ok.Groups[1].Value -lt 4 -or $missing.Count -gt 0) {
        throw ("Android instrumentation failed or omitted required cases. Missing: " + ($missing -join ', ') + ". Review " + $logPath + [Environment]::NewLine + $text)
    }
    if ($prodInstalledBefore) {
        $prodAfter = @(& $adb -s $serial shell pm path $prodPackage 2>&1)
        if ($LASTEXITCODE -ne 0 -or ($prodAfter -join " ") -notmatch "package:") { throw "Production package disappeared after QA; no QA command targeted it." }
    }
    Add-Result "Android Photo Studio WebView instrumentation" "PASS" "All four required cases passed on $serial."
    Add-Result "Preserve production Android app" "PASS" "No install, uninstall or clear-data operation targeted $prodPackage."

    $summary = [PSCustomObject]@{
        product = "SmartPrint"
        scope = "ANDROID_PHYSICAL_DEVICE_PHOTO_STUDIO"
        deviceSerial = $serial
        applicationId = $ApplicationId
        productionPackageUntouched = $true
        buildRequested = [bool]$Build
        appApkSha256 = (Get-FileHash $ApplicationApkPath -Algorithm SHA256).Hash.ToLowerInvariant()
        testApkSha256 = (Get-FileHash $InstrumentationApkPath -Algorithm SHA256).Hash.ToLowerInvariant()
        result = "PASS"
        startedUtc = $startedUtc.ToString("o")
        completedUtc = [DateTime]::UtcNow.ToString("o")
        results = @($results)
        instrumentationLog = $logPath
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot "summary.json") -Encoding UTF8
    Write-Host "Android physical-device QA PASSED. Evidence: $outputRoot" -ForegroundColor Green
}
catch {
    Add-Result "Android physical-device QA" "FAIL" $_.Exception.Message
    $failure = [PSCustomObject]@{
        product = "SmartPrint"; scope = "ANDROID_PHYSICAL_DEVICE_PHOTO_STUDIO"
        deviceSerial = $serial; applicationId = $ApplicationId; result = "FAIL"
        completedUtc = [DateTime]::UtcNow.ToString("o"); results = @($results)
        error = $_.Exception.Message
    }
    $failure | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot "failure.json") -Encoding UTF8
    throw
}
finally {
    if ($reverseCreated -and $adb -and $serial) { & $adb -s $serial reverse --remove tcp:3000 2>$null | Out-Null }
    if ($serverProcess -and -not $serverProcess.HasExited) {
        $serverProcess.Kill($true)
        $serverProcess.WaitForExit(5000) | Out-Null
    }
}
