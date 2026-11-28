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
Android API 35 x86_64 default system image, .NET test build, Windows Print Spooler.
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
$queueSuffix = if ($env:GITHUB_RUN_ID) { "$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)" } else { [Guid]::NewGuid().ToString("N").Substring(0, 12) }
$queueName = "SmartPrint Android Virtual IPP CI $queueSuffix"
$ippPort = 10631
$ippResource = $null
$evidenceDirectory = Join-Path (Get-Location) "artifacts/android-ipp-windows"
$runnerTemp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$sdkRoot = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$avdName = "SmartPrintIppVirtualApi35"
$emulatorProcess = $null
$queueCreated = $false
$queueOwned = $false
$queueReused = $false
$queueOriginalKeepPrintedJobs = $null
$queueOriginalKeepPrintedJobsKnown = $false
$queueKeepPrintedJobsChanged = $false
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
        printerUuid = "abcdef78-1234-1234-1234-123456789012"
        sdkRoot = $sdkRoot
        sdkManager = $null
        avdManager = $null
        adb = $null
        emulatorExecutable = $null
        emulator = $avdName
        acceleration = $null
        ipAddress = "127.0.0.1"
        ippPort = $ippPort
        ippResource = $ippResource
    }
    discovery = [ordered]@{
        result = "NOT_TESTED"
        method = "Direct IPP endpoint registration; mDNS multicast discovery is a separate acceptance stage."
        expectedResource = "/p/virtual"
    }
    windowsQueue = $null
    queueReused = $false
    ippPortReused = $false
    registrationMethod = $null
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
    # Resolve every Android tool from the same SDK root. Get-Command can prefer
    # a stale sdkmanager from another installation on PATH (the hosted runner may
    # have both C:\Android and Program Files SDK trees). Mixing sdkmanager/avdmanager
    # from one tree with adb/emulator from another makes package/AVD resolution nondeterministic.
    $sdkRoot = [IO.Path]::GetFullPath($sdkRoot)
    $env:ANDROID_SDK_ROOT = $sdkRoot
    $env:ANDROID_HOME = $sdkRoot

    $cmdlineToolsRoot = Join-Path $sdkRoot "cmdline-tools"
    $sdkManagerCandidates = @(
        Get-ChildItem -Path $cmdlineToolsRoot -Filter "sdkmanager.bat" -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending
    )
    $sdkManagerCandidate = $sdkManagerCandidates |
        Where-Object { $_.FullName -match '(?i)[\\/]cmdline-tools[\\/]latest[\\/]bin[\\/]sdkmanager\.bat$' } |
        Select-Object -First 1
    if (-not $sdkManagerCandidate) {
        $sdkManagerCandidate = $sdkManagerCandidates | Select-Object -First 1
    }
    if (-not $sdkManagerCandidate) {
        $pathCommand = Get-Command "sdkmanager.bat" -ErrorAction SilentlyContinue
        $pathLocation = if ($pathCommand) { $pathCommand.Source } else { "<not on PATH>" }
        throw "Android SDK Manager is missing under ANDROID_SDK_ROOT '$sdkRoot'. PATH resolves sdkmanager to '$pathLocation'; refusing to mix SDK installations."
    }

    $sdkManager = $sdkManagerCandidate.FullName
    $sdkToolsBin = Split-Path -Parent $sdkManager
    $avdManager = Join-Path $sdkToolsBin "avdmanager.bat"
    $adb = Join-Path $sdkRoot "platform-tools\adb.exe"
    $emulator = Join-Path $sdkRoot "emulator\emulator.exe"
    if (-not (Test-Path -LiteralPath $sdkManager)) { throw "Android SDK Manager is missing at $sdkManager." }
    if (-not (Test-Path -LiteralPath $avdManager)) { throw "Android AVD Manager is missing beside sdkmanager at $avdManager." }

    Write-Host "Android SDK root: $sdkRoot"
    Write-Host "Android SDK Manager: $sdkManager"
    Write-Host "Android AVD Manager: $avdManager"
    Write-Host "Android ADB: $adb"
    Write-Host "Android Emulator: $emulator"

    # Install only the one API image and tools needed for the receiver smoke test.

    # The setup-android action has already accepted the Android SDK licenses.
    # The receiver needs Android framework printing and networking only; Google APIs
    # are unnecessary and caused GMS/phone startup ANRs under the Windows runner's
    # constrained emulator resources. Use the lean AOSP image.
    $report.android.sdkManager = $sdkManager
    $report.android.avdManager = $avdManager
    $report.android.adb = $adb
    $report.android.emulatorExecutable = $emulator
    Invoke-Checked $sdkManager @("--install", "platform-tools", "emulator", "platforms;android-35", "build-tools;36.0.0", "system-images;android-35;default;x86_64") "Android SDK package installation failed"

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
    "no" | & $avdManager create avd -n $avdName -k "system-images;android-35;default;x86_64" -d "pixel_2" --force
    if ($LASTEXITCODE -ne 0) { throw "Could not create the disposable Android API 35 AVD." }

    $emulatorArgs = @(
        "-avd", $avdName,
        "-no-window",
        "-no-audio",
        "-no-boot-anim",
        "-no-snapshot",
        "-wipe-data",
        "-gpu", "swiftshader",
        "-memory", "3072",
        "-cores", "4",
        "-no-metrics",
        "-no-snapshot-save"
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
    # Wait for framework/package-manager startup to settle before launching the
    # receiver. Avoid Monkey: it performs a broad system-service dump and was
    # itself triggering timeouts while the emulator was under startup pressure.
    & $adb -s emulator-5554 shell am wait-for-broadcast-idle 2>$null | Out-Null
    Start-Sleep -Seconds 8
    Invoke-Checked $adb @("-s", "emulator-5554", "shell", "am", "start", "-W", "-n", "$packageName/.MainActivity") "Launching IppVirtual MainActivity failed"

    # Discover the live Run control from Android accessibility; do not assume
    # fixed coordinates because hosted emulator resolution/density can vary.
    Start-Sleep -Seconds 3
    $runTapped = $false
    for ($attempt = 1; $attempt -le 3 -and -not $runTapped; $attempt++) {
        & $adb -s emulator-5554 shell uiautomator dump /sdcard/smartprint-ippvirtual-ui.xml 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Start-Sleep -Seconds 2; continue }
        & $adb -s emulator-5554 pull /sdcard/smartprint-ippvirtual-ui.xml $uiHierarchyPath 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $uiHierarchyPath)) { Start-Sleep -Seconds 2; continue }
        try {
            [xml]$ui = Get-Content -LiteralPath $uiHierarchyPath -Raw
            $runNode = @($ui.SelectNodes("//node")) | Where-Object {
                $_.GetAttribute("resource-id") -eq ($packageName + ":id/action_start") -or
                $_.GetAttribute("content-desc") -match "^(Run|Start)$" -or
                ($_.GetAttribute("text") -match "^(Run|Start)$" -and $_.GetAttribute("clickable") -eq "true")
            } | Select-Object -First 1
            if ($null -eq $runNode) { throw "IppVirtual accessibility tree does not expose its Run/Start control." }
            if ($runNode.GetAttribute("enabled") -eq "false" -or $runNode.GetAttribute("clickable") -eq "false") { throw "IppVirtual Run/Start control is disabled or not clickable." }
            $bounds = [regex]::Match($runNode.GetAttribute("bounds"), "^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$")
            if (-not $bounds.Success) { throw "IppVirtual Run/Start control has invalid accessibility bounds." }
            $tapX = [int]([int]$bounds.Groups[1].Value + ([int]$bounds.Groups[3].Value - [int]$bounds.Groups[1].Value) / 2)
            $tapY = [int]([int]$bounds.Groups[2].Value + ([int]$bounds.Groups[4].Value - [int]$bounds.Groups[2].Value) / 2)
            Invoke-Checked $adb @("-s", "emulator-5554", "shell", "input", "tap", "$tapX", "$tapY") "Tapping the discovered IppVirtual Run/Start control failed"
            $runTapped = $true
        } catch { if ($attempt -eq 3) { throw }; Start-Sleep -Seconds 2 }
    }
    if (-not $runTapped) { throw "Could not discover and activate IppVirtual Run/Start control." }
    Start-Sleep -Seconds 5
    & $adb -s emulator-5554 shell uiautomator dump /sdcard/smartprint-ippvirtual-ui.xml 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { & $adb -s emulator-5554 pull /sdcard/smartprint-ippvirtual-ui.xml $uiHierarchyPath 2>$null | Out-Null }

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

    # Register the documented receiver resource path directly. First reconcile
    # the Windows spooler inventory: Add-Printer -IppURL can report
    # ERROR_PRINTER_ALREADY_EXISTS even when the generated CI queue name differs.
    # Reuse only a queue whose IPP endpoint can be tied to this live receiver.
    Start-Service -Name Spooler
    $ippResources = @("/p/virtual")
    $candidateFailures = @()
    $printer = $null
    $queueReused = $false
    $ippPortReused = $false
    $registrationMethod = $null

    function Get-VisiblePrinterInventory {
        $items = @()
        try { $items += @(Get-Printer -Full -ErrorAction Stop) } catch { $items += @(Get-Printer -ErrorAction SilentlyContinue) }
        try {
            foreach ($item in @(Get-CimInstance -ClassName Win32_Printer -ErrorAction Stop)) {
                if (-not ($items | Where-Object { $_.Name -eq $item.Name })) {
                    $items += [pscustomobject]@{
                        Name = $item.Name; DriverName = $item.DriverName; PortName = $item.PortName
                        PrinterStatus = $item.PrinterStatus; WorkOffline = $item.WorkOffline
                        DetectedBy = "Win32_Printer"
                    }
                }
            }
        } catch { Write-Warning "Win32 printer inventory query failed: $($_.Exception.Message)" }
        return @($items)
    }

    # Windows returns different property sets for TCP/IP, WSD, FILE and
    # Microsoft IPP ports. StrictMode is enabled, so optional CIM/CIM-like
    # properties must be read through PSObject.Properties rather than by
    # direct member access.
    function Get-OptionalPropertyValue([AllowNull()][object]$InputObject, [string]$PropertyName) {
        if ($null -eq $InputObject) { return $null }
        $property = $InputObject.PSObject.Properties[$PropertyName]
        if ($null -eq $property) { return $null }
        return $property.Value
    }

    function Get-HiddenIppRegistrationEvidence {
        $pnpDevices = @()
        try {
            $pnpDevices = @(Get-PnpDevice -ErrorAction Stop | Where-Object {
                $_.Class -match "(?i)printer" -or
                $_.FriendlyName -match "(?i)IppVirtual|virtual" -or
                $_.InstanceId -match [regex]::Escape($report.android.printerUuid)
            } | Select-Object Status, Class, FriendlyName, InstanceId, Problem)
        } catch {
            $pnpDevices = @([ordered]@{ queryError = $_.Exception.Message })
        }

        $registryInventory = @()
        $registryRoots = @(
            "HKLM:\SYSTEM\CurrentControlSet\Control\Print\Printers",
            "HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors",
            "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\Connections"
        )
        foreach ($registryRoot in $registryRoots) {
            try {
                if (Test-Path -LiteralPath $registryRoot) {
                    $children = @(Get-ChildItem -LiteralPath $registryRoot -ErrorAction Stop |
                        Select-Object -ExpandProperty PSChildName)
                    $registryInventory += [ordered]@{
                        path = $registryRoot
                        childKeys = $children
                    }
                }
            } catch {
                $registryInventory += [ordered]@{
                    path = $registryRoot
                    queryError = $_.Exception.Message
                }
            }
        }

        return [ordered]@{
            pnpDevices = $pnpDevices
            registryKeys = $registryInventory
        }
    }

    function Normalize-PrinterUuid([AllowNull()][object]$Value) {
        if ($null -eq $Value) { return $null }
        $text = ([string]$Value).Trim().Trim('{', '}')
        $parsed = [Guid]::Empty
        if ([Guid]::TryParse($text, [ref]$parsed)) { return $parsed.ToString("D").ToLowerInvariant() }
        return $null
    }

    function Get-DiscoveredIppPortsForLiveReceiver {
        $expectedUuid = Normalize-PrinterUuid $report.android.printerUuid
        if (-not $expectedUuid) { return @() }

        # IPP directed discovery may register the PnP device and WSD IPP port
        # before Add-Printer returns ERROR_PRINTER_ALREADY_EXISTS. Verify both
        # identities, then create the queue against the already-created port.
        $deviceInstanceId = "SWD\IPP\$expectedUuid"
        $device = Get-PnpDevice -InstanceId $deviceInstanceId -ErrorAction SilentlyContinue
        if (-not $device -or $device.Status -ne "OK" -or $device.FriendlyName -ne "IppVirtual") {
            return @()
        }

        $discoveredPorts = @()
        foreach ($candidatePort in @(Get-PrinterPort -ErrorAction SilentlyContinue)) {
            $portName = [string](Get-OptionalPropertyValue $candidatePort "Name")
            $description = [string](Get-OptionalPropertyValue $candidatePort "Description")
            $monitor = [string](Get-OptionalPropertyValue $candidatePort "PortMonitor")
            if ([string]::IsNullOrWhiteSpace($portName) -or
                $description -ne "IPP Port" -or
                $monitor -notmatch "(?i)^WSD Port Monitor$") {
                continue
            }

            # Read-only inspection. Windows' documented workflow is to reuse
            # this existing port; never modify its monitor registry values.
            $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\$portName"
            if (-not (Test-Path -LiteralPath $registryPath)) { continue }
            $registryProperties = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
            $uuidProperty = if ($registryProperties) { $registryProperties.PSObject.Properties["Printer UUID"] } else { $null }
            $portUuid = if ($uuidProperty) { Normalize-PrinterUuid $uuidProperty.Value } else { $null }
            if ($portUuid -ne $expectedUuid) { continue }

            $registryValues = @()
            if ($registryProperties) {
                $registryValues = @($registryProperties.PSObject.Properties |
                    Where-Object { $_.Name -notmatch '^PS' } |
                    ForEach-Object { [ordered]@{ name=$_.Name; value="$($_.Value)" } })
            }

            $discoveredPorts += [pscustomobject]@{
                Port = $candidatePort
                PortName = $portName
                PrinterUuid = $portUuid
                RegistryPath = $registryPath
                RegistryValues = $registryValues
                DeviceInstanceId = $deviceInstanceId
                DeviceStatus = $device.Status
                DeviceName = $device.FriendlyName
            }
        }
        return @($discoveredPorts)
    }

    function Test-QueueTargetsLiveReceiver($candidatePrinter, [string]$candidateUrl) {
        if (-not $candidatePrinter) { return $false }
        $driverName = [string](Get-OptionalPropertyValue $candidatePrinter "DriverName")
        $printerPortName = [string](Get-OptionalPropertyValue $candidatePrinter "PortName")
        if ($driverName -notmatch "Microsoft IPP Class Driver" -or [string]::IsNullOrWhiteSpace($printerPortName)) { return $false }

        $candidatePort = Get-PrinterPort -Name $printerPortName -ErrorAction SilentlyContinue
        if (-not $candidatePort) { return $false }

        $portName = [string](Get-OptionalPropertyValue $candidatePort "Name")
        $hostAddress = [string](Get-OptionalPropertyValue $candidatePort "PrinterHostAddress")
        $portNumber = [string](Get-OptionalPropertyValue $candidatePort "PortNumber")
        $description = [string](Get-OptionalPropertyValue $candidatePort "Description")
        # Reuse is allowed only when both the loopback host and this run's
        # forwarded port match. An unrelated IPP queue on the same machine is
        # never considered equivalent merely because it uses an IPP monitor.
        $expectedUuid = Normalize-PrinterUuid $report.android.printerUuid
        $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\$portName"
        if ($expectedUuid -and (Test-Path -LiteralPath $registryPath)) {
            $registryProperties = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
            $uuidProperty = if ($registryProperties) { $registryProperties.PSObject.Properties["Printer UUID"] } else { $null }
            $portUuid = if ($uuidProperty) { Normalize-PrinterUuid $uuidProperty.Value } else { $null }
            if ($portUuid -eq $expectedUuid) {
                $device = Get-PnpDevice -InstanceId ("SWD\IPP\" + $expectedUuid) -ErrorAction SilentlyContinue
                if ($device -and $device.Status -eq "OK" -and $device.FriendlyName -eq "IppVirtual") { return $true }
            }
        }

        $hostMatches = $hostAddress -eq "127.0.0.1" -or
            $portName -match '(?i)127\.0\.0\.1' -or
            $description -match '(?i)127\.0\.0\.1'
        $portMatches = $portNumber -eq [string]$ippPort -or
            $portName -match "(?<!\d)$ippPort(?!\d)" -or
            $description -match [regex]::Escape(":$ippPort")
        return ($hostMatches -and $portMatches)
    }

    $inventory = Get-VisiblePrinterInventory
    foreach ($candidateResource in $ippResources) {
        $candidateUrl = "http://127.0.0.1:$ippPort$candidateResource"
        # Exact-name reuse is preferred; endpoint-backed ports are also checked
        # because Windows may choose the printer name advertised by the receiver.
        $matches = @($inventory | Where-Object {
            $_.Name -eq $queueName -or $_.Name -match "(?i)IppVirtual|virtual" -or
            $_.PortName -match "(?i)ipp|10631" -or
            $_.PortName -in @($discoveredIppPorts | ForEach-Object { $_.PortName })
        })
        foreach ($match in $matches) {
            $candidatePrinter = Get-Printer -Name $match.Name -ErrorAction SilentlyContinue
            if ($candidatePrinter -and (Test-QueueTargetsLiveReceiver $candidatePrinter $candidateUrl)) {
                $printer = $candidatePrinter
                $queueName = $candidatePrinter.Name
                $ippResource = $candidateResource
                $queueReused = $true
                Write-Host "Reusing existing Windows IPP queue '$queueName' for $candidateUrl."
                break
            }
        }
        if ($printer) { break }
    }

    if (-not $printer) {
        foreach ($candidateResource in $ippResources) {
            $candidateUrl = "http://127.0.0.1:$ippPort$candidateResource"
            try {
                Add-Printer -Name $queueName -IppURL $candidateUrl -KeepPrintedJobs -ErrorAction Stop
                # From this point the newly-created queue is ours, even if a later
                # verification step fails; cleanup may remove only this queue.
                $queueCreated = $true
                $queueOwned = $true
                $candidatePrinter = Get-Printer -Name $queueName -ErrorAction Stop
                if ($candidatePrinter.DriverName -notmatch "Microsoft IPP Class Driver") {
                    throw "Windows selected unexpected driver '$($candidatePrinter.DriverName)'."
                }
                $printer = $candidatePrinter
                $ippResource = $candidateResource
                break
            } catch {
                $errorRecord = $_
                $exception = $errorRecord.Exception
                $hresult = '0x{0:X8}' -f $exception.HResult
                $nativeCode = $null
                if ($exception -is [System.ComponentModel.Win32Exception]) { $nativeCode = $exception.NativeErrorCode }

                $visiblePrinters = @()
                $visiblePorts = @()
                $hiddenRegistrationEvidence = $null
                $diagnosticCaptureError = $null
                $diagnosticWriteError = $null

                # Diagnostics are deliberately best-effort. The original
                # Add-Printer exception is authoritative and must never be
                # replaced by a secondary inventory/event/artifact failure.
                try {
                    # Add-Printer may race a spooler/device-enumeration registration.
                    # Refresh both inventories and reuse only after proving the live endpoint.
                    Start-Sleep -Seconds 2
                    $inventory = Get-VisiblePrinterInventory
                    foreach ($match in @($inventory | Where-Object {
                        $_.Name -eq $queueName -or $_.Name -match "(?i)IppVirtual|virtual" -or
                        $_.PortName -match "(?i)ipp|10631"
                    })) {
                        $candidatePrinter = Get-Printer -Name $match.Name -ErrorAction SilentlyContinue
                        if ($candidatePrinter -and (Test-QueueTargetsLiveReceiver $candidatePrinter $candidateUrl)) {
                            $printer = $candidatePrinter
                            $queueName = $candidatePrinter.Name
                            $ippResource = $candidateResource
                            $queueReused = $true
                            Write-Host "Add-Printer reported a duplicate; verified and reused existing queue '$queueName'."
                            break
                        }
                    }
                    $visiblePrinters = @($inventory | ForEach-Object {
                        $name = [string](Get-OptionalPropertyValue $_ "Name")
                        $driver = [string](Get-OptionalPropertyValue $_ "DriverName")
                        $port = [string](Get-OptionalPropertyValue $_ "PortName")
                        "$name [$driver] port=$port"
                    })
                    $visiblePorts = @(Get-PrinterPort -ErrorAction SilentlyContinue | ForEach-Object {
                        $name = [string](Get-OptionalPropertyValue $_ "Name")
                        $description = [string](Get-OptionalPropertyValue $_ "Description")
                        $hostAddress = [string](Get-OptionalPropertyValue $_ "PrinterHostAddress")
                        $monitor = [string](Get-OptionalPropertyValue $_ "PortMonitor")
                        $portNumber = [string](Get-OptionalPropertyValue $_ "PortNumber")
                        "$name [$description] host=$hostAddress monitor=$monitor port=$portNumber"
                    })
                    $hiddenRegistrationEvidence = Get-HiddenIppRegistrationEvidence
                } catch {
                    $diagnosticCaptureError = $_.Exception.Message
                }

                $candidateFailures += "$candidateResource => $($exception.Message) (HRESULT $hresult; native=$nativeCode; category $($errorRecord.CategoryInfo.Category); id=$($errorRecord.FullyQualifiedErrorId)); verifiedReuse=$([bool]$printer); printers: $($visiblePrinters -join ' | '); ports: $($visiblePorts -join ' | '); diagnosticCaptureError=$diagnosticCaptureError"
                $diagnostic = [ordered]@{
                    resource = $candidateResource; url = $candidateUrl
                    message = $exception.Message; exceptionType = $exception.GetType().FullName
                    hresult = $hresult; nativeErrorCode = $nativeCode
                    fullyQualifiedErrorId = $errorRecord.FullyQualifiedErrorId
                    category = "$($errorRecord.CategoryInfo)"; targetObject = "$($errorRecord.TargetObject)"
                    verifiedReuse = [bool]$printer
                    printers = $visiblePrinters; ports = $visiblePorts
                    hiddenRegistration = $hiddenRegistrationEvidence
                    diagnosticCaptureError = $diagnosticCaptureError
                }
                try {
                    $diagnostic | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceDirectory "windows-ipp-registration-failure.json") -Encoding UTF8 -ErrorAction Stop
                } catch {
                    $diagnosticWriteError = $_.Exception.Message
                    $candidateFailures[-1] = "$($candidateFailures[-1]); diagnosticWriteError=$diagnosticWriteError"
                }
            }
            if ($printer) { break }
        }
    }

    if ($null -eq $printer) {
        $printEvents = @()
        $eventEvidenceError = $null
        $eventEvidencePath = Join-Path $evidenceDirectory "windows-printservice-events.json"
        try {
            foreach ($logName in @("Microsoft-Windows-PrintService/Admin", "Microsoft-Windows-PrintService/Operational")) {
                try {
                    $printEvents += @(Get-WinEvent -FilterHashtable @{ LogName = $logName; StartTime = [DateTime]::Now.AddMinutes(-15) } -MaxEvents 30 -ErrorAction Stop |
                        Select-Object @{Name="LogName";Expression={$logName}}, TimeCreated, Id, LevelDisplayName, Message)
                } catch {
                    $printEvents += [ordered]@{ LogName=$logName; queryError=$_.Exception.Message }
                }
            }
            $printEvents | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $eventEvidencePath -Encoding UTF8 -ErrorAction Stop
        } catch {
            $eventEvidenceError = $_.Exception.Message
        }

        throw "Windows could not register IppVirtual at resource path(s) $($ippResources -join ', '): $($candidateFailures -join '; '). PrintService evidence: $eventEvidencePath; evidenceError=$eventEvidenceError"
    }

    $queueState = Get-Printer -Name $printer.Name -ErrorAction Stop
    $keepPrintedJobsValue = Get-OptionalPropertyValue $queueState "KeepPrintedJobs"
    if ($null -eq $keepPrintedJobsValue) {
        throw "Windows did not expose the KeepPrintedJobs setting for queue '$($printer.Name)'; refusing an unreliable terminal-job assertion."
    }

    $queueOriginalKeepPrintedJobs = [bool]$keepPrintedJobsValue
    $queueOriginalKeepPrintedJobsKnown = $true
    if (-not $queueOriginalKeepPrintedJobs) {
        Set-Printer -Name $printer.Name -KeepPrintedJobs $true -ErrorAction Stop
        $queueKeepPrintedJobsChanged = $true
    }

    $printer = Get-Printer -Name $printer.Name -ErrorAction Stop
    if (-not [bool](Get-OptionalPropertyValue $printer "KeepPrintedJobs")) {
        throw "Windows did not retain completed jobs for queue '$($printer.Name)'; the terminal spooler assertion would be unreliable."
    }

    $driver = Get-PrinterDriver -Name $printer.DriverName -ErrorAction Stop
    $port = Get-PrinterPort -Name $printer.PortName -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($printer.PortName)) {
        throw "Windows created the IPP queue without a usable port."
    }

    $queueEvidence = [ordered]@{
        name = $printer.Name
        driver = $printer.DriverName
        driverVersion = (Get-OptionalPropertyValue $driver "DriverVersion")
        port = $printer.PortName
        portMonitor = (Get-OptionalPropertyValue $port "PortMonitor")
        ippUrl = "http://127.0.0.1:$ippPort$ippResource"
        keepPrintedJobs = [bool](Get-OptionalPropertyValue $printer "KeepPrintedJobs")
        originalKeepPrintedJobs = $queueOriginalKeepPrintedJobs
    }
    $queueEvidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $queueEvidencePath -Encoding UTF8
    $report.android.acceleration = $acceleration
    $report.android.ippResource = $ippResource
    $report.windowsQueue = $queueEvidence
    $report.queueReused = $queueReused

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
        if ($queueReused -and $queueKeepPrintedJobsChanged -and $queueOriginalKeepPrintedJobsKnown) {
            $reusedQueue = Get-Printer -Name $queueName -ErrorAction SilentlyContinue
            if ($reusedQueue) {
                Set-Printer -Name $queueName -KeepPrintedJobs $queueOriginalKeepPrintedJobs -ErrorAction Stop
                Write-Host "Restored the reused queue '$queueName' KeepPrintedJobs setting to $queueOriginalKeepPrintedJobs."
            }
        }
    } catch {
        Write-Warning "Could not restore the reused queue's original KeepPrintedJobs setting: $($_.Exception.Message)"
    }

    try {
        if ($queueOwned -and $queueCreated) {
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
