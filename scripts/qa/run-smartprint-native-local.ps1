[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublicOrigin,
    [string]$LocalPortalOrigin = "http://127.0.0.1:3000",
    [string]$OutputDirectory = "artifacts/smartprint-qa/native-local",
    [string]$AndroidDeviceSerial = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

function Write-QA([string]$Message, [string]$Colour = "Cyan") {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $Message) -ForegroundColor $Colour
}

function Get-CanonicalOrigin([string]$Value, [string]$Label, [string]$Scheme) {
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne $Scheme -or $uri.AbsolutePath -ne "/" -or
        $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
        throw "$Label must be a canonical $Scheme origin without a path, query, fragment or credentials."
    }
    return $uri.GetLeftPart([UriPartial]::Authority).TrimEnd("/")
}

$publicOrigin = Get-CanonicalOrigin $PublicOrigin "The local tunnel origin" "https"
$localOrigin = Get-CanonicalOrigin $LocalPortalOrigin "The local portal origin" "http"
$localUri = [Uri]$localOrigin
if (-not $localUri.IsLoopback -or $localUri.Port -ne 3000) {
    throw "The local portal origin must be loopback HTTP on port 3000."
}
if (-not [string]::IsNullOrWhiteSpace($AndroidDeviceSerial) -and
    $AndroidDeviceSerial -notmatch '^[A-Za-z0-9._:-]{1,128}$') {
    throw "Android device serial contains unsupported characters."
}

$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$script:stepNumber = 0

function Add-NativeResult([string]$Name, [string]$Status, [string]$Detail = "", [string]$Evidence = "", [string]$Log = "") {
    $script:results.Add([PSCustomObject]@{
        name = $Name
        status = $Status
        detail = $Detail
        evidence = $Evidence
        log = $Log
    })
    $colour = switch ($Status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "SKIPPED" { "Yellow" }
        default { "DarkYellow" }
    }
    Write-Host ("[QA-RESULT] {0} | {1} | {2}" -f $Status, $Name, $Detail) -ForegroundColor $colour
}

function Invoke-NativeStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [string]$Evidence = ""
    )
    $script:stepNumber++
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $logSlug = [regex]::Replace($Name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim("-")
    $logPath = Join-Path $outputRoot ("{0:00}-{1}.log" -f $script:stepNumber, $logSlug)
    Write-QA ("[QA-STEP-START] {0:00} | {1}" -f $script:stepNumber, $Name)
    Write-QA ("Live output and full details: " + $logPath) "DarkCyan"
    $status = "PASS"
    $detail = ""
    try {
        $global:LASTEXITCODE = 0
        & $Action 2>&1 | Tee-Object -FilePath $logPath -ErrorAction Stop | ForEach-Object {
            Write-Host $_
        }
        if ($null -ne $LASTEXITCODE -and [int]$LASTEXITCODE -ne 0) {
            throw "Native command exited with code $LASTEXITCODE."
        }
        $detail = "Completed successfully."
    } catch {
        $status = "FAIL"
        $detail = $_.Exception.Message
        ($_ | Out-String) | Add-Content -LiteralPath $logPath
        Write-Host ($_.Exception | Out-String) -ForegroundColor Red
    } finally {
        $timer.Stop()
        Write-QA ("[QA-STEP-END] {0} | {1} | elapsed={2:n1}s" -f $status, $Name, $timer.Elapsed.TotalSeconds) $(if ($status -eq "PASS") { "Green" } else { "Red" })
    }
    Add-NativeResult $Name $status $detail $Evidence $logPath
    return ($status -eq "PASS")
}

function Get-EnvValue([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $line = Get-Content -LiteralPath $Path | Where-Object {
        $_ -match ("^\s*" + [regex]::Escape($Name) + "\s*=")
    } | Select-Object -First 1
    if (-not $line) { return $null }
    return (($line -split "=", 2)[1]).Trim().Trim('"').Trim("'")
}

# The public HTTPS origin must be the one configured in the local .env.local.
# The existing central Tunnel routes that hostname back to this local Compose
# stack. This prevents native builds from silently targeting production.
$localEnvPath = Join-Path $repoRoot ".env.local"
$configuredPublicOrigin = Get-EnvValue $localEnvPath "PUBLIC_BASE_URL"
if (-not $configuredPublicOrigin) {
    throw "Local .env.local is missing PUBLIC_BASE_URL. Start the local stack first."
}
$configuredPublicOrigin = Get-CanonicalOrigin $configuredPublicOrigin "PUBLIC_BASE_URL" "https"
if (-not $configuredPublicOrigin.Equals($publicOrigin, [StringComparison]::OrdinalIgnoreCase)) {
    throw "QA origin mismatch. scripts/qa/smartprint-qa.config.json, .env.local and the requested local tunnel origin must match. No native app was built."
}

Write-QA "LOCAL NATIVE APP ACCEPTANCE — no public release downloads, no production database, no uninstall, no data clear." "Green"
Write-QA ("Local portal origin: " + $localOrigin)
Write-QA ("Local tunnel origin (routes to local Compose): " + $publicOrigin)
Write-QA ("Evidence directory: " + $outputRoot)

# Prove both the loopback portal and the configured tunnel origin reach a
# healthy application before compiling apps against that origin.
$readinessOk = Invoke-NativeStep -Name "Verify local portal and local PostgreSQL readiness" -Evidence (Join-Path $outputRoot "readiness.json") -Action {
    $localReady = Invoke-RestMethod -Uri ($localOrigin + "/health/ready") -TimeoutSec 12
    $tunnelReady = Invoke-RestMethod -Uri ($publicOrigin + "/health/ready") -TimeoutSec 15
    foreach ($probe in @($localReady, $tunnelReady)) {
        if ($probe.status -ne "ready" -or $probe.database -ne "connected") {
            throw "A local health endpoint did not report status=ready and database=connected."
        }
    }
    [PSCustomObject]@{
        local = $localReady
        tunnel = $tunnelReady
        checkedUtc = [DateTime]::UtcNow.ToString("o")
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputRoot "readiness.json") -Encoding UTF8
    Write-QA "Both health checks report ready with PostgreSQL connected." "Green"
}
if (-not $readinessOk) {
    throw "Local native application tests cannot start while the local Portal/API/PostgreSQL stack is unhealthy."
}

# Read-only check of the already installed Windows Agent. QA never pairs,
# revokes, reconnects, changes the selected network, or changes a printer.
$qaConfigPath = Join-Path $PSScriptRoot "smartprint-qa.config.json"
$qaConfig = Get-Content -LiteralPath $qaConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop
$agentServiceName = [string]$qaConfig.apps.windows.agentServiceName
$agentServiceOk = Invoke-NativeStep -Name "Verify existing Windows Print Agent service is installed and running" -Evidence (Join-Path $outputRoot "windows-agent-service.json") -Action {
    $service = Get-Service -Name $agentServiceName -ErrorAction SilentlyContinue
    if (-not $service) { throw "Windows service '$agentServiceName' is not installed." }
    if ($service.Status -ne "Running") { throw "Windows service '$agentServiceName' is $($service.Status), not Running. QA will not start or reconfigure it." }
    [PSCustomObject]@{ serviceName = $agentServiceName; status = [string]$service.Status; checkedUtc = [DateTime]::UtcNow.ToString("o") } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot "windows-agent-service.json") -Encoding UTF8
    Write-QA ("Existing Agent service is Running: " + $agentServiceName) "Green"
}
$agentLocalStatusOk = Invoke-NativeStep -Name "Read-only Windows Agent pairing and local endpoint readiness" -Evidence (Join-Path $outputRoot "windows-agent-local-status.json") -Action {
    $status = Invoke-RestMethod -Uri "http://127.0.0.1:17842/api/v1/local/status" -Method Get -TimeoutSec 5
    if ($status.status -ne "LOCAL_ENDPOINT_READY" -or [string]::IsNullOrWhiteSpace([string]$status.agentDeviceId)) {
        throw "The Agent local endpoint is reachable but did not report LOCAL_ENDPOINT_READY with a paired device identity."
    }
    $status | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot "windows-agent-local-status.json") -Encoding UTF8
    Write-QA ("Paired local Agent is ready. Device ID: " + $status.agentDeviceId) "Green"
}

# ---------------- Windows SmartPrint Desktop ----------------
$pnpm = Get-Command pnpm.cmd -ErrorAction SilentlyContinue
if (-not $pnpm) { $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue }
$cargo = Get-Command cargo.exe -ErrorAction SilentlyContinue
if (-not $cargo) { $cargo = Get-Command cargo -ErrorAction SilentlyContinue }

$desktopTypecheckOk = $false
if ($pnpm) {
    $desktopTypecheckOk = Invoke-NativeStep -Name "Windows Desktop frontend TypeScript typecheck" -Evidence (Join-Path $outputRoot "desktop-typecheck.log") -Action {
        & $pnpm.Source --filter "@smart-print/desktop" typecheck
    }
    $desktopBuildOk = Invoke-NativeStep -Name "Windows Desktop production frontend build" -Evidence (Join-Path $outputRoot "desktop-frontend-build.log") -Action {
        & $pnpm.Source --filter "@smart-print/desktop" build
    }
} else {
    Add-NativeResult "Windows Desktop frontend typecheck" "FAIL" "pnpm was not found. Install the repository-pinned pnpm version through Corepack."
    Add-NativeResult "Windows Desktop production frontend build" "FAIL" "pnpm was not found."
    $desktopBuildOk = $false
}

$desktopRustOk = $false
if ($cargo) {
    $desktopRustOk = Invoke-NativeStep -Name "Windows Desktop native Rust host tests" -Evidence (Join-Path $outputRoot "desktop-rust-tests.log") -Action {
        & $cargo.Source test --locked --manifest-path "apps/desktop/src-tauri/Cargo.toml" --target x86_64-pc-windows-msvc
    }
} else {
    Add-NativeResult "Windows Desktop native Rust host tests" "FAIL" "Rust cargo was not found. Install the stable MSVC Rust toolchain."
}

$desktopBuildExe = Join-Path $repoRoot ("apps/desktop/src-tauri/target/release/" + [string]$qaConfig.apps.windows.nativeExecutableName)
$desktopExe = Join-Path $outputRoot ([string]$qaConfig.apps.windows.nativeExecutableName)
$desktopBuildNativeOk = $false
if ($pnpm -and $desktopBuildOk -and $desktopRustOk) {
    $previousApiOrigin = [Environment]::GetEnvironmentVariable("VITE_API_BASE_URL", "Process")
    try {
        [Environment]::SetEnvironmentVariable("VITE_API_BASE_URL", $publicOrigin, "Process")
        $desktopBuildNativeOk = Invoke-NativeStep -Name "Build native Windows SmartPrint Desktop executable against the local tunnel" -Evidence (Join-Path $outputRoot "desktop-native-build.log") -Action {
            & $pnpm.Source --filter "@smart-print/desktop" exec tauri build --no-bundle --ci
            if (-not (Test-Path -LiteralPath $desktopBuildExe -PathType Leaf)) {
                throw "Tauri returned without producing the configured Windows SmartPrint Desktop executable: $desktopBuildExe"
            }
            Copy-Item -LiteralPath $desktopBuildExe -Destination $desktopExe -Force
            Write-QA ("Built local Desktop executable: " + $desktopExe) "Green"
        }
    } finally {
        [Environment]::SetEnvironmentVariable("VITE_API_BASE_URL", $previousApiOrigin, "Process")
    }
} else {
    Add-NativeResult "Build native Windows SmartPrint Desktop executable against the local tunnel" "SKIPPED" "Blocked by a failed frontend or Rust prerequisite."
}

if ($desktopBuildNativeOk -and (Test-Path -LiteralPath $desktopExe -PathType Leaf)) {
    $existingDesktop = @()
    $desktopProcessInspectionSucceeded = $true
    try {
        # Do not launch a second copy when the installed app or a previous QA
        # copy is already open, even when its executable path differs.
        $desktopProcessNames = @(
            [string]$qaConfig.apps.windows.nativeExecutableName,
            [string]$qaConfig.apps.windows.executableName
        )
        $existingDesktop = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop |
            Where-Object { $desktopProcessNames -contains $_.Name })
    } catch {
        $desktopProcessInspectionSucceeded = $false
        Write-QA "Could not safely inspect running SmartPrint Desktop processes." "Yellow"
    }

    if (-not $desktopProcessInspectionSucceeded) {
        Add-NativeResult "Windows Desktop native window and rendered workspace" "SKIPPED" "Process inspection failed. The QA runner will not launch a second Desktop session when it cannot prove the workspace is closed."
    } elseif ($existingDesktop.Count -gt 0) {
        Add-NativeResult "Windows Desktop native window and rendered workspace" "SKIPPED" "A SmartPrint Desktop session is already running under either the native or installed executable name. Close it manually and rerun this step; QA will not take over or close the user's active workspace."
    } else {
        $smokeScript = Join-Path $PSScriptRoot "desktop-open-smoke.ps1"
        if (Test-Path -LiteralPath $smokeScript -PathType Leaf) {
            $profilePath = Join-Path $outputRoot "desktop-webview2-profile"
            New-Item -ItemType Directory -Force -Path $profilePath | Out-Null
            $previousProfile = [Environment]::GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", "Process")
            try {
                [Environment]::SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", $profilePath, "Process")
                $desktopSmokeOk = Invoke-NativeStep -Name "Launch local Windows Desktop and verify native window plus accessible SmartPrint workspace" -Evidence (Join-Path $outputRoot "desktop-ui-smoke.json") -Action {
                    $smokeEvidence = & $smokeScript -ExecutablePath $desktopExe -StartupTimeoutSeconds ([int]$qaConfig.apps.windows.smokeTimeoutSeconds)
                    if (-not $smokeEvidence) {
                        throw "Desktop UI smoke test did not return structured UI Automation evidence."
                    }
                    $smokeEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot "desktop-ui-smoke.json") -Encoding UTF8
                }
            } finally {
                [Environment]::SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", $previousProfile, "Process")
                if (Test-Path -LiteralPath $profilePath) {
                    Remove-Item -LiteralPath $profilePath -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        } else {
            Add-NativeResult "Windows Desktop native window and rendered workspace" "FAIL" "Missing desktop-open-smoke.ps1."
        }
    }
} else {
    Add-NativeResult "Windows Desktop native window and rendered workspace" "SKIPPED" "Native executable was not built."
}

# ---------------- Android native SmartPrint customer app ----------------
$adbCommand = Get-Command adb.exe -ErrorAction SilentlyContinue
if (-not $adbCommand) { $adbCommand = Get-Command adb -ErrorAction SilentlyContinue }
$sdkRoot = $env:ANDROID_SDK_ROOT
if ([string]::IsNullOrWhiteSpace($sdkRoot)) { $sdkRoot = $env:ANDROID_HOME }
if ([string]::IsNullOrWhiteSpace($sdkRoot) -and $env:LOCALAPPDATA) {
    $candidateSdk = Join-Path $env:LOCALAPPDATA "Android\Sdk"
    if (Test-Path -LiteralPath $candidateSdk -PathType Container) { $sdkRoot = $candidateSdk }
}
if (-not $adbCommand -and $sdkRoot) {
    $candidateAdb = Join-Path $sdkRoot "platform-tools\adb.exe"
    if (Test-Path -LiteralPath $candidateAdb -PathType Leaf) {
        $adbCommand = [PSCustomObject]@{ Source = $candidateAdb }
    }
}
$gradle = Get-Command gradle.bat -ErrorAction SilentlyContinue
if (-not $gradle) { $gradle = Get-Command gradle -ErrorAction SilentlyContinue }

$androidDevice = $null
if ($adbCommand) {
    $deviceLines = @(& $adbCommand.Source devices)
    if ($LASTEXITCODE -ne 0) {
        Add-NativeResult "Android local-device discovery" "FAIL" "ADB could not enumerate connected devices."
    } else {
        $onlineDevices = @($deviceLines | Where-Object { $_ -match '^\S+\s+device(?:\s|$)' })
        if (-not [string]::IsNullOrWhiteSpace($AndroidDeviceSerial)) {
            $matching = @($onlineDevices | Where-Object { $_ -match ('^' + [regex]::Escape($AndroidDeviceSerial) + '\s+device(?:\s|$)') })
            if ($matching.Count -eq 1) {
                $androidDevice = $AndroidDeviceSerial
            } else {
                Add-NativeResult "Android local-device discovery" "FAIL" "The explicitly selected device serial was not present exactly once and online."
            }
        } elseif ($onlineDevices.Count -eq 1) {
            $androidDevice = (($onlineDevices[0] -split '\s+')[0])
        } elseif ($onlineDevices.Count -eq 0) {
            Add-NativeResult "Android local-device discovery" "SKIPPED" "No ADB device is connected. Android JVM and emulator tests are run by the parent QA runner when an emulator is available."
        } else {
            Add-NativeResult "Android local-device discovery" "SKIPPED" "Multiple ADB devices are online. Re-run with -LocalAndroidDeviceSerial to select exactly one; no device was targeted."
        }
    }
} else {
    Add-NativeResult "Android local-device discovery" "SKIPPED" "Android SDK platform-tools/ADB is not installed or not configured."
}

function Get-AndroidUiDocument([string]$Name, [string]$Device, [string]$AdbPath, [string]$Directory) {
    $remotePath = "/sdcard/smartprint-local-qa-$Name.xml"
    $localPath = Join-Path $Directory ("android-" + $Name + ".xml")
    $dump = @(& $AdbPath -s $Device shell uiautomator dump --compressed $remotePath 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Android UI hierarchy dump failed: $($dump -join ' ')"
    }
    $pull = @(& $AdbPath -s $Device pull $remotePath $localPath 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $localPath -PathType Leaf)) {
        throw "Android UI hierarchy could not be copied to QA evidence: $($pull -join ' ')"
    }
    try { return [xml](Get-Content -LiteralPath $localPath -Raw -ErrorAction Stop) }
    catch { throw "Android UI hierarchy was empty or malformed: $($_.Exception.Message)" }
}

function Find-AndroidUiNode($Document, [string]$ResourceSuffix, [string]$Label, [string]$Package) {
    foreach ($node in $Document.SelectNodes("//node")) {
        if ($node.GetAttribute("visible-to-user") -eq "false") { continue }
        $nodePackage = $node.GetAttribute("package")
        if ($nodePackage -and $nodePackage -ne $Package) { continue }
        $resourceId = $node.GetAttribute("resource-id")
        $text = $node.GetAttribute("text")
        $description = $node.GetAttribute("content-desc")
        if ($ResourceSuffix -and $resourceId -match (":id/" + [regex]::Escape($ResourceSuffix) + "$")) { return $node }
        if ($Label -and ($text -eq $Label -or $description -eq $Label)) { return $node }
    }
    return $null
}

function Invoke-AndroidNavigation([string]$Device, [string]$AdbPath, [string]$Package, [string]$OutputRoot, [string]$Destination) {
    $docBefore = Get-AndroidUiDocument ("before-" + $Destination) $Device $AdbPath $OutputRoot
    $resource = if ($Destination -eq "files") { ([string]$qaConfig.apps.android.uiAutomationIds.filesNavigation) } else { ([string]$qaConfig.apps.android.uiAutomationIds.homeNavigation) }
    $label = if ($Destination -eq "files") { "My Files" } else { "Home" }
    $node = Find-AndroidUiNode $docBefore $resource $label $Package
    if (-not $node) {
        throw "Android native navigation control '$label' was not visible in UIAutomator."
    }
    $bounds = [regex]::Match($node.GetAttribute("bounds"), '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$')
    if (-not $bounds.Success) { throw "Android did not expose clickable bounds for '$label'." }
    $x = [int]([int]$bounds.Groups[1].Value + [int]$bounds.Groups[3].Value) / 2
    $y = [int]([int]$bounds.Groups[2].Value + [int]$bounds.Groups[4].Value) / 2
    & $AdbPath -s $Device shell input tap $x $y
    if ($LASTEXITCODE -ne 0) { throw "Android could not activate '$label' navigation." }

    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 350
        $docAfter = Get-AndroidUiDocument ("after-" + $Destination) $Device $AdbPath $OutputRoot
        $expectedResource = if ($Destination -eq "files") { ([string]$qaConfig.apps.android.uiAutomationIds.filesHeading) } else { ([string]$qaConfig.apps.android.uiAutomationIds.homeHeading) }
        $expectedLabel = if ($Destination -eq "files") { "Create a new folder" } else { "Your space" }
        if (Find-AndroidUiNode $docAfter $expectedResource $expectedLabel $Package) {
            return $true
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Android did not render the expected '$Destination' screen after navigation."
}

if ($androidDevice) {
    if (-not $gradle -or [string]::IsNullOrWhiteSpace($sdkRoot)) {
        Add-NativeResult "Android local QA APK build" "FAIL" "A system Gradle installation and Android SDK are required for physical-device acceptance."
        Add-NativeResult "Android local QA install and live UI acceptance" "SKIPPED" "Blocked by Android build prerequisites."
    } else {
        $androidPackage = [string]$qaConfig.apps.android.localQaApplicationId
        $productionPackage = [string]$qaConfig.apps.android.applicationId
        $productionBefore = @(& $adbCommand.Source -s $androidDevice shell pm path $productionPackage 2>&1)
        $productionInstalledBefore = ($LASTEXITCODE -eq 0 -and ($productionBefore -join " ") -match "package:")

        $versionCode = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $versionName = "local-qa-" + $runStamp
        $androidApkPath = Join-Path $repoRoot "apps/android/app/build/outputs/apk/debug/app-debug.apk"
        $androidBuildArgs = @(
            "--no-daemon", "--console=plain",
            "-p", "apps/android",
            "-PsmartPrintServiceUrl=$publicOrigin",
            "-PsmartPrintLocalQa=true",
            "-PsmartPrintPhotoStudioQaEnabled=true",
            "-PsmartPrintVersionCode=$versionCode",
            "-PsmartPrintVersionName=$versionName",
            ":app:assembleDebug"
        )
        $androidBuildOk = Invoke-NativeStep -Name "Build side-by-side Android local QA APK against the local tunnel origin" -Evidence (Join-Path $outputRoot "android-local-qa-build.log") -Action {
            & $gradle.Source @androidBuildArgs
            if (-not (Test-Path -LiteralPath $androidApkPath -PathType Leaf)) {
                throw "Gradle completed without producing the Android local QA APK."
            }
        }

        if ($androidBuildOk) {
            $androidInstallOk = Invoke-NativeStep -Name "Install/update Android .localqa package without uninstalling or clearing the customer app" -Evidence (Join-Path $outputRoot "android-local-qa-install.log") -Action {
                Write-QA "Target ADB device: $androidDevice"
                Write-QA "Target application ID: $androidPackage"
                Write-QA "The existing $productionPackage package will not be uninstalled, replaced or data-cleared."
                & $adbCommand.Source -s $androidDevice install -r $androidApkPath
                if ($LASTEXITCODE -ne 0) {
                    throw "Android rejected the local QA APK. No uninstall or data-clear fallback was attempted."
                }
                $packagePath = @(& $adbCommand.Source -s $androidDevice shell pm path $androidPackage 2>&1)
                if ($LASTEXITCODE -ne 0 -or ($packagePath -join " ") -notmatch "package:") {
                    throw "Android Package Manager did not register $androidPackage after installation."
                }
                $productionAfterInstall = @(& $adbCommand.Source -s $androidDevice shell pm path $productionPackage 2>&1)
                if ($productionInstalledBefore -and
                    ($LASTEXITCODE -ne 0 -or ($productionAfterInstall -join " ") -notmatch "package:")) {
                    throw "The original production SmartPrint package is no longer installed. The local QA package must remain side-by-side."
                }
            }
        } else {
            Add-NativeResult "Install/update Android .localqa package" "SKIPPED" "Blocked by the local QA APK build failure."
            $androidInstallOk = $false
        }

        if ($androidInstallOk) {
            # Run the repository's full Espresso/ActivityScenario suite on the
            # explicitly selected physical device, against only the isolated
            # .localqa application. ANDROID_SERIAL prevents Gradle from targeting
            # any other connected phone or emulator.
            $previousAndroidSerial = [Environment]::GetEnvironmentVariable("ANDROID_SERIAL", "Process")
            try {
                [Environment]::SetEnvironmentVariable("ANDROID_SERIAL", $androidDevice, "Process")
                $androidInstrumentationArgs = @(
                    "--no-daemon", "--console=plain",
                    "-p", "apps/android",
                    "-PsmartPrintServiceUrl=$publicOrigin",
                    "-PsmartPrintLocalQa=true",
                    "-PsmartPrintPhotoStudioQaEnabled=true",
                    "-PsmartPrintVersionCode=$versionCode",
                    "-PsmartPrintVersionName=$versionName",
                    "--info", ":app:connectedDebugAndroidTest",
                    "--serial", $androidDevice
                )
                $androidInstrumentationOk = Invoke-NativeStep -Name "Run the full Android Espresso instrumentation suite on the selected physical phone (.localqa only)" -Evidence (Join-Path $outputRoot "android-physical-instrumentation.log") -Action {
                    & $gradle.Source @androidInstrumentationArgs
                }
                if (-not $androidInstrumentationOk) { $exitCode = 1 }
            } finally {
                [Environment]::SetEnvironmentVariable("ANDROID_SERIAL", $previousAndroidSerial, "Process")
            }

            $androidLaunchOk = Invoke-NativeStep -Name "Launch Android local QA app and verify the actual resumed Activity" -Evidence (Join-Path $outputRoot "android-local-qa-launch.log") -Action {
                $component = "$androidPackage/$([string]$qaConfig.apps.android.launcherActivity)"
                $launchOutput = @(& $adbCommand.Source -s $androidDevice shell am start -W -n $component 2>&1)
                if ($LASTEXITCODE -ne 0) { throw "Android could not launch the QA app: $($launchOutput -join ' ')" }
                $deadline = [DateTime]::UtcNow.AddSeconds(30)
                $appPid = $null
                do {
                    $activity = @(& $adbCommand.Source -s $androidDevice shell dumpsys activity activities 2>&1)
                    $appPidOutput = @(& $adbCommand.Source -s $androidDevice shell pidof $androidPackage 2>$null)
                    $appPid = ($appPidOutput -join " ").Trim()
                    if (($activity -join [Environment]::NewLine) -match [regex]::Escape($androidPackage) -and $appPid) { break }
                    Start-Sleep -Milliseconds 500
                } while ([DateTime]::UtcNow -lt $deadline)
                if (-not $appPid) { throw "The Android QA app did not become a running foreground Activity." }
                Write-QA "Android QA foreground process PID: $appPid" "Green"
            }

            if ($androidLaunchOk) {
                $androidNavigationOk = Invoke-NativeStep -Name "Android native Home and My Files navigation" -Evidence (Join-Path $outputRoot "android-local-qa-navigation.log") -Action {
                    $home = Get-AndroidUiDocument "initial-home" $androidDevice $adbCommand.Source $outputRoot
                    if (-not (Find-AndroidUiNode $home ([string]$qaConfig.apps.android.uiAutomationIds.nativeRoot) "" $androidPackage)) {
                        throw "Android native root landmark was not visible."
                    }
                    if (-not (Find-AndroidUiNode $home ([string]$qaConfig.apps.android.uiAutomationIds.homeNavigation) "Home" $androidPackage)) {
                        throw "Android Home navigation control was not visible."
                    }
                    if (-not (Find-AndroidUiNode $home ([string]$qaConfig.apps.android.uiAutomationIds.filesNavigation) "My Files" $androidPackage)) {
                        throw "Android My Files navigation control was not visible."
                    }
                    if (-not (Invoke-AndroidNavigation $androidDevice $adbCommand.Source $androidPackage $outputRoot "files")) {
                        throw "Android My Files navigation did not complete."
                    }
                    if (-not (Invoke-AndroidNavigation $androidDevice $adbCommand.Source $androidPackage $outputRoot "home")) {
                        throw "Android Home navigation did not complete."
                    }
                }
            } else {
                Add-NativeResult "Android native Home and My Files navigation" "SKIPPED" "Blocked because Android local QA app did not launch."
            }

            $androidCrashOk = Invoke-NativeStep -Name "Android local QA process crash-log check" -Evidence (Join-Path $outputRoot "android-local-qa-crash.log") -Action {
                $pidOutput = @(& $adbCommand.Source -s $androidDevice shell pidof $androidPackage 2>$null)
                $pid = (($pidOutput -join " ").Trim() -split "\s+")[0]
                if (-not $pid) { throw "Android QA app process exited before crash-log inspection." }
                $logcat = @(& $adbCommand.Source -s $androidDevice logcat -d "--pid=$pid" -t 500 2>&1)
                $logText = $logcat -join [Environment]::NewLine
                $logText | Set-Content -LiteralPath (Join-Path $outputRoot "android-local-qa-crash.log") -Encoding UTF8
                if ($logText -match "(?i)FATAL EXCEPTION|Fatal signal") {
                    throw "Fatal exception or native fatal signal was recorded for the SmartPrint local QA process."
                }
                Write-QA "No fatal exception or native fatal signal was found for the Android QA process." "Green"
            }

            $productionAfter = @(& $adbCommand.Source -s $androidDevice shell pm path $productionPackage 2>&1)
            if ($productionInstalledBefore -and
                ($LASTEXITCODE -ne 0 -or ($productionAfter -join " ") -notmatch "package:")) {
                Add-NativeResult "Preserve original Android production installation" "FAIL" "The original package was not present after the local QA run."
            } else {
                Add-NativeResult "Preserve original Android production installation" "PASS" "No uninstall, replacement or data-clear command was issued for $productionPackage."
            }
        } else {
            Add-NativeResult "Android local QA app launch and UI navigation" "SKIPPED" "Blocked by Android local QA installation."
            Add-NativeResult "Android local QA process crash-log check" "SKIPPED" "Blocked by Android local QA installation."
            Add-NativeResult "Preserve original Android production installation" "PASS" "No production package mutation was attempted."
        }
    }
}

$failed = @($results | Where-Object status -eq "FAIL").Count
$summary = [PSCustomObject]@{
    product = "SmartPrint"
    scope = "LOCAL_NATIVE_APPLICATIONS"
    checkedUtc = [DateTime]::UtcNow.ToString("o")
    localPortalOrigin = $localOrigin
    localTunnelOrigin = $publicOrigin
    result = if ($failed -eq 0) { "PASS" } else { "FAIL" }
    passed = @($results | Where-Object status -eq "PASS").Count
    failed = $failed
    skipped = @($results | Where-Object status -eq "SKIPPED").Count
    results = @($results)
    safety = @{
        publicReleaseAssetsDownloaded = $false
        productionDatabaseUsed = $false
        originalAndroidPackageUninstalled = $false
        originalAndroidPackageDataCleared = $false
        existingWindowsDesktopSessionTakenOver = $false
        existingWebViewProfileModified = $false
    }
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot "results.json") -Encoding UTF8
$summary | Format-List | Out-String | Set-Content -LiteralPath (Join-Path $outputRoot "results.txt") -Encoding UTF8
Write-Host ""
Write-Host "=== SMARTPRINT LOCAL NATIVE APPLICATION QA SUMMARY ===" -ForegroundColor Green
Write-Host ($summary | Format-Table result, passed, failed, skipped -AutoSize | Out-String)
Write-Host ("Evidence: " + $outputRoot) -ForegroundColor Cyan
if ($failed -gt 0) {
    throw "Local native application QA recorded $failed failed check(s). Review the per-step logs in $outputRoot."
}
