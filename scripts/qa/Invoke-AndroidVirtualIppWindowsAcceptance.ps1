[CmdletBinding()]
param(
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Help) {
    @"
Usage: pwsh -NoProfile -File scripts/qa/Invoke-AndroidVirtualIppWindowsAcceptance.ps1

Boots one Android emulator on the Windows Agent CI runner, installs the pinned
402D IppVirtual receiver APK, forwards its IPP listener to localhost, creates
a real Windows IPP queue, and runs the SmartPrint Agent GDI/spooler integration
test against that receiver. It writes logs, screenshot, queue metadata and a
JSON result under artifacts/android-ipp-windows/.

Required: Java 17, Android command-line tools, emulator, platform-tools,
Android API 35 x86_64 Google APIs image, .NET test build, Windows Print Spooler.
"@ | Write-Output
    exit 0
}

if (-not $IsWindows -and $env:OS -ne "Windows_NT") {
    throw "This acceptance must run on the same Windows host as the real Windows Print Spooler."
}

$ErrorActionPreference = "Stop"
$packageName = "ru.a402d.ippprinter"
$apkSha256 = "f1e64933fa0f69306e8c5cf885e0884be7265556a8c507c89846578d1ec4caed"
$signerSha1 = "49fa922e98eda907944076fba7aba6251b546f29"
$queueName = "SmartPrint Android Virtual IPP CI"
$ippPort = 10631
$ippResource = "/p/virtual"
$evidenceDirectory = Join-Path (Get-Location) "artifacts/android-ipp-windows"
$runnerTemp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$sdkRoot = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$avdName = "SmartPrintIppVirtualApi35"
$emulatorProcess = $null
$queueCreated = $false
$forwardCreated = $false
$testExitCode = $null
$failure = $null
$acceleration = "unknown"
$apkPath = Join-Path $runnerTemp "smartprint-ippvirtual-1.1.3.apk"
$emulatorLog = Join-Path $evidenceDirectory "android-emulator.log"
$logcatPath = Join-Path $evidenceDirectory "android-logcat.txt"
$screenshotPath = Join-Path $evidenceDirectory "android-receiver.png"
$activityPath = Join-Path $evidenceDirectory "android-activity.txt"
$uiHierarchyPath = Join-Path $evidenceDirectory "android-ui.xml"
$queueEvidencePath = Join-Path $evidenceDirectory "windows-ipp-queue.json"

New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null
$startedAt = [DateTimeOffset]::UtcNow
$report = [ordered]@{
    schemaVersion = 1
    test = "SMARTPRINT_WINDOWS_AGENT_TO_ANDROID_IPPVIRTUAL"
    result = "RUNNING"
    startedAtUtc = $startedAt.ToString("o")
    completedAtUtc = $null
    runner = [ordered]@{ machine = $env:COMPUTERNAME; os = [Environment]::OSVersion.Version.ToString() }
    android = [ordered]@{
        package = $packageName
        version = "1.1.3"
        apkSha256 = $apkSha256
        signerCertificateSha1 = $signerSha1
        emulator = $avdName
        acceleration = $null
        ipAddress = "127.0.0.1"
        ippPort = $ippPort
        ippResource = $ippResource
    }
    windowsQueue = $null
    agentTestExitCode = $null
    failure = $null
    limitations = @(
        "This CI test proves the Windows Agent GDI/spooler path can submit to the real IppVirtual Android receiver and that the receiver returns a successful IPP completion.",
        "The production cloud grant API and isolated child authorization protocol are validated by their dedicated Agent/API tests; this test supplies the final spooler authorization callback.",
        "This emulator acceptance does not replace a shop-PC test with the customer's actual Android phone, Wi-Fi/LAN, and installed printer environment."
    )
}

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments, [string]$FailureMessage)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FailureMessage (exit code $LASTEXITCODE)." }
}

function Save-AndroidEvidence {
    $adb = Join-Path $sdkRoot "platform-tools\adb.exe"
    if (-not (Test-Path -LiteralPath $adb)) { return }
    try {
        & $adb -s emulator-5554 logcat -d -v time | Set-Content -LiteralPath $logcatPath -Encoding UTF8
        & $adb -s emulator-5554 shell dumpsys activity activities 2>&1 | Set-Content -LiteralPath $activityPath -Encoding UTF8
        & $adb -s emulator-5554 shell uiautomator dump /sdcard/smartprint-ipp-ui.xml 2>&1 | Out-Null
        & $adb -s emulator-5554 pull /sdcard/smartprint-ipp-ui.xml $uiHierarchyPath 2>&1 | Out-Null
        & $adb -s emulator-5554 shell screencap -p /sdcard/smartprint-ipp-receiver.png 2>&1 | Out-Null
        & $adb -s emulator-5554 pull /sdcard/smartprint-ipp-receiver.png $screenshotPath 2>&1 | Out-Null
    } catch {
        Write-Warning "Could not collect one or more Android evidence files: $($_.Exception.Message)"
    }
}

try {
    # setup-android installs a versioned cmdline-tools folder (for example
    # cmdline-tools\20.0\bin), not necessarily a "latest" alias.
    $sdkManagerCommand = Get-Command "sdkmanager.bat" -ErrorAction SilentlyContinue
    if ($sdkManagerCommand) {
        $sdkManager = $sdkManagerCommand.Source
    } else {
        $sdkManagerCandidate = Get-ChildItem -Path (Join-Path $sdkRoot "cmdline-tools") -Filter "sdkmanager.bat" -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $sdkManagerCandidate) { throw "Android SDK Manager is missing under $sdkRoot." }
        $sdkManager = $sdkManagerCandidate.FullName
    }
    $sdkToolsBin = Split-Path -Parent $sdkManager
    $avdManager = Join-Path $sdkToolsBin "avdmanager.bat"
    $adb = Join-Path $sdkRoot "platform-tools\adb.exe"
    $emulator = Join-Path $sdkRoot "emulator\emulator.exe"
    if (-not (Test-Path -LiteralPath $sdkManager)) { throw "Android SDK Manager is missing at $sdkManager." }

    # Install only the one API image and tools needed for the receiver smoke test.
    @("y", "y", "y", "y", "y") | & $sdkManager --licenses | Out-Null
    Invoke-Checked $sdkManager @("--install", "platform-tools", "emulator", "platforms;android-35", "build-tools;36.0.0", "system-images;android-35;google_apis;x86_64") "Android SDK package installation failed"
    if (-not (Test-Path -LiteralPath $adb) -or -not (Test-Path -LiteralPath $emulator) -or -not (Test-Path -LiteralPath $avdManager)) {
        throw "Android SDK installation did not provide adb, emulator and avdmanager."
    }

    # Pin the public receiver binary by both APK digest and signing certificate.
    Invoke-WebRequest -Uri "https://d.apkpure.net/b/APK/ru.a402d.ippprinter?version=latest" -OutFile $apkPath -MaximumRedirection 10
    $actualApkHash = (Get-FileHash -LiteralPath $apkPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualApkHash -ne $apkSha256) {
        throw "IppVirtual APK SHA-256 mismatch. Expected $apkSha256, received $actualApkHash. Review the upstream release before updating the pin."
    }

    $buildTools = Join-Path $sdkRoot "build-tools\36.0.0"
    $aapt = Join-Path $buildTools "aapt.exe"
    $apksigner = Join-Path $buildTools "apksigner.bat"
    if (-not (Test-Path -LiteralPath $aapt) -or -not (Test-Path -LiteralPath $apksigner)) {
        throw "Android build-tools 36.0.0 is missing aapt or apksigner."
    }
    $badging = (& $aapt dump badging $apkPath 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $badging -notmatch "package: name='$packageName'") {
        throw "Downloaded APK does not declare the expected package $packageName."
    }
    $signatureOutput = (& $apksigner verify --print-certs $apkPath 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $signatureOutput -notmatch ("(?i)certificate SHA-1 digest: " + [regex]::Escape($signerSha1))) {
        throw "IppVirtual APK signing certificate did not match the reviewed publisher certificate."
    }

    $accelOutput = (& $emulator -accel-check 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0) {
        $acceleration = "WHPX-or-host-hypervisor"
        $emulatorAccelerationArgs = @("-accel", "auto")
    } else {
        # GitHub-hosted Windows nested virtualization is not guaranteed. TCG is
        # slower, but keeps the whole acceptance on this same Windows runner.
        $acceleration = "software-TCG"
        $env:ANDROID_I_WANT_MY_TCG = "yes"
        $emulatorAccelerationArgs = @("-accel", "off")
        Write-Warning "No usable Windows hypervisor was detected. Falling back to Android Emulator's documented software TCG mode."
    }

    # Recreate a disposable AVD so stale snapshots/state can never influence CI.
    & $avdManager delete avd -n $avdName 2>$null | Out-Null
    "no" | & $avdManager create avd -n $avdName -k "system-images;android-35;google_apis;x86_64" -d "pixel_2" --force
    if ($LASTEXITCODE -ne 0) { throw "Could not create the disposable Android API 35 AVD." }

    $emulatorArgs = @(
        "-avd", $avdName,
        "-no-window",
        "-no-audio",
        "-no-boot-anim",
        "-no-snapshot",
        "-wipe-data",
        "-gpu", "swiftshader",
        "-memory", "2048",
        "-cores", "2"
    ) + $emulatorAccelerationArgs
    $emulatorProcess = Start-Process -FilePath $emulator -ArgumentList $emulatorArgs -PassThru -WindowStyle Hidden -RedirectStandardOutput $emulatorLog -RedirectStandardError ($emulatorLog + ".err")

    $bootDeadline = [DateTimeOffset]::UtcNow.AddMinutes(18)
    $deviceReady = $false
    while ([DateTimeOffset]::UtcNow -lt $bootDeadline) {
        if ($emulatorProcess.HasExited) {
            throw "Android emulator exited before boot completed. See $emulatorLog."
        }
        & $adb -s emulator-5554 get-state 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $boot = (& $adb -s emulator-5554 shell getprop sys.boot_completed 2>$null | Out-String).Trim()
            if ($boot -eq "1") { $deviceReady = $true; break }
        }
        Start-Sleep -Seconds 5
    }
    if (-not $deviceReady) { throw "Android emulator did not finish booting within 18 minutes. See $emulatorLog." }

    & $adb -s emulator-5554 shell settings put global animator_duration_scale 0
    & $adb -s emulator-5554 shell settings put global transition_animation_scale 0
    & $adb -s emulator-5554 shell settings put global window_animation_scale 0
    Invoke-Checked $adb @("-s", "emulator-5554", "install", "-r", $apkPath) "Installing the pinned IppVirtual receiver failed"
    Invoke-Checked $adb @("-s", "emulator-5554", "shell", "monkey", "-p", $packageName, "1") "Launching IppVirtual failed"

    # Forward only the receiver's known IPP listener to this same Windows host.
    Invoke-Checked $adb @("-s", "emulator-5554", "forward", "tcp:$ippPort", "tcp:$ippPort") "ADB port forwarding failed"
    $forwardCreated = $true
    $listenerReady = $false
    $listenerDeadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    while ([DateTimeOffset]::UtcNow -lt $listenerDeadline) {
        $probe = Test-NetConnection -ComputerName "127.0.0.1" -Port $ippPort -WarningAction SilentlyContinue
        if ($probe.TcpTestSucceeded) { $listenerReady = $true; break }
        Start-Sleep -Seconds 3
    }
    if (-not $listenerReady) {
        Save-AndroidEvidence
        throw "IppVirtual did not expose its documented IPP listener on TCP $ippPort after launch."
    }

    # Directed discovery avoids mDNS multicast assumptions in a virtualized CI network.
    Start-Service -Name Spooler
    Remove-Printer -Name $queueName -ErrorAction SilentlyContinue
    Add-Printer -Name $queueName -IppURL "http://127.0.0.1:$ippPort$ippResource" -KeepPrintedJobs
    $queueCreated = $true
    $printer = Get-Printer -Name $queueName -ErrorAction Stop
    $driver = Get-PrinterDriver -Name $printer.DriverName -ErrorAction Stop
    $port = Get-PrinterPort -Name $printer.PortName -ErrorAction Stop
    if ($printer.DriverName -notmatch "Microsoft IPP Class Driver") {
        throw "Windows did not select Microsoft IPP Class Driver; actual driver was '$($printer.DriverName)'."
    }
    if ([string]::IsNullOrWhiteSpace($printer.PortName)) {
        throw "Windows created the IPP queue without a usable port."
    }

    $queueEvidence = [ordered]@{
        name = $printer.Name
        driver = $printer.DriverName
        driverVersion = $driver.DriverVersion
        port = $printer.PortName
        portMonitor = $port.PortMonitor
        ippUrl = "http://127.0.0.1:$ippPort$ippResource"
        keepPrintedJobs = $true
    }
    $queueEvidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $queueEvidencePath -Encoding UTF8
    $report.android.acceleration = $acceleration
    $report.windowsQueue = $queueEvidence

    $env:SMARTPRINT_RUN_ANDROID_IPP_INTEGRATION = "1"
    $env:SMARTPRINT_ANDROID_IPP_QUEUE = $queueName
    dotnet test tests/SmartPrint.Agent.Tests/SmartPrint.Agent.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~AndroidVirtualIppSpoolerIntegrationTests.SmartPrintWindowsExecutorSubmitsDocumentToAndroidIppVirtualReceiver" --logger "console;verbosity=normal"
    $testExitCode = $LASTEXITCODE
    $report.agentTestExitCode = $testExitCode
    if ($testExitCode -ne 0) {
        throw "SmartPrint Windows Agent -> Android IppVirtual spooler acceptance failed (dotnet test exit $testExitCode)."
    }

    Save-AndroidEvidence
    $report.result = "PASS"
}
catch {
    $failure = $_.Exception.Message
    $report.result = "FAIL"
    $report.failure = $failure
    Save-AndroidEvidence
    throw
}
finally {
    $report.completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    $report.agentTestExitCode = $testExitCode
    if ($null -ne $failure) { $report.failure = $failure }
    $report.android.acceleration = $acceleration

    try {
        if ($queueCreated) {
            $printer = Get-Printer -Name $queueName -ErrorAction SilentlyContinue
            if ($printer) {
                $report.windowsQueue = [ordered]@{
                    name = $printer.Name
                    driver = $printer.DriverName
                    port = $printer.PortName
                    cleanup = "removed"
                }
            }
            Remove-Printer -Name $queueName -ErrorAction SilentlyContinue
        }
    } catch { Write-Warning "Temporary IPP queue cleanup failed: $($_.Exception.Message)" }

    if ($forwardCreated) {
        & (Join-Path $sdkRoot "platform-tools\adb.exe") -s emulator-5554 forward --remove "tcp:$ippPort" 2>$null | Out-Null
    }
    if ($null -ne $emulatorProcess -and -not $emulatorProcess.HasExited) {
        Stop-Process -Id $emulatorProcess.Id -Force -ErrorAction SilentlyContinue
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceDirectory "result.json") -Encoding UTF8
}
