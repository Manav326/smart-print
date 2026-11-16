<#
.SYNOPSIS
Exercise SmartPrint Windows installer install, upgrade, rollback and uninstall scenarios.

.DESCRIPTION
Runs a destructive lifecycle test against two versioned SmartPrint Windows
installers. It seeds synthetic machine identity, network preference and
encrypted local-queue fixtures; creates a secondary Microsoft Print to PDF
queue; then installs/reinstalls/upgrades/rolls back/uninstalls the Agent and
verifies the test-owned data is preserved or removed as expected.

SAFETY: this script installs and uninstalls the SmartPrint Agent, registers
Windows services/firewall rules, writes under ProgramData/Program Files, and
creates/removes a printer queue. Run it only on a disposable Windows VM or
isolated CI runner with Administrator rights. Do not run it on your everyday
shop computer or a machine with real print jobs in its queue.

.PARAMETER Help
Show this guide without modifying Windows services, ProgramData, printers or
firewall rules.

.PARAMETER InstallerV1
Path to the earlier-version Windows setup executable (upgrade source).

.PARAMETER InstallerV2
Path to the later-version Windows setup executable (upgrade target).

.EXAMPLE
.\scripts\qa\windows-installer-lifecycle.ps1 -Help
Show test scenarios and safety requirements without running installers.

.EXAMPLE
.\scripts\qa\windows-installer-lifecycle.ps1 -InstallerV1 'artifacts/SmartPrint-v1.exe' -InstallerV2 'artifacts/SmartPrint-v2.exe'
Run the complete lifecycle against two test setup packages on a disposable
elevated Windows VM.

.NOTES
The script owns and removes only the fixtures and resources it creates. It
does not intentionally change the pre-existing default printer. It must run
elevated because service, firewall and machine-wide printer operations are
required.
#>

[CmdletBinding()]
param(
    [switch]$Help,
    [string]$InstallerV1 = "",
    [string]$InstallerV2 = ""
)

if ($Help) {
    Get-Help -Name $PSCommandPath -Full | Out-String -Width 180 | Write-Host
    exit 0
}
foreach ($entry in @(@{Name="InstallerV1";Path=$InstallerV1}, @{Name="InstallerV2";Path=$InstallerV2})) {
    if ([string]::IsNullOrWhiteSpace($entry.Path)) {
        throw ("-" + $entry.Name + " is required. Run this script with -Help to see usage examples.")
    }
    if (-not (Test-Path -LiteralPath $entry.Path -PathType Leaf)) {
        throw ("-" + $entry.Name + " does not exist: " + $entry.Path)
    }
}

$ErrorActionPreference = "Stop"
$serviceName = "QRtoPrintSmartPrintAgent"
$firewallPrefix = "QRtoPrint Smart Print Agent Local API"
$testPrinterName = "SmartPrint Lifecycle QA Queue"
$fixtureProject = Join-Path $env:RUNNER_TEMP "SmartPrintLifecycleFixture"
$brokenProject = Join-Path $env:RUNNER_TEMP "SmartPrintBrokenAgent"
$programDataRoot = Join-Path $env:ProgramData "QRtoPrint"
$identityPath = Join-Path $programDataRoot "agent.identity"
$queueManifest = Join-Path $programDataRoot "LocalQueue\11111111111141118111111111111111.json"
$networkPreferencePath = Join-Path $programDataRoot "local-network.preference"
$expectedIdentityId = "22222222-2222-4222-8222-222222222222"
$expectedNetworkId = "LIFECYCLE-QA-SHOP-NETWORK"
$results = [System.Collections.Generic.List[object]]::new()
$identityHash = $null
$originalDefaultPrinter = $null
$originalPrinterInventory = @()
$originalSpoolerProcessId = $null
$fixturePrinter = $null
$installRoot = Join-Path $env:ProgramFiles "SmartPrint"
$ownsInstall = $false
$ownsPrinter = $false
$ownsTask = $false
$script:localServiceFixtureStaged = $false

$script:caseNumber = 0
function Add-Result([string]$Name, [string]$Status, [string]$Detail) {
    $results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    $stamp = Get-Date -Format "HH:mm:ss"
    if ($Status -eq "PASS") {
        Write-Host "[QA-RESULT] $stamp | PASS: $Name - $Detail" -ForegroundColor Green
    }
    elseif ($Status -eq "SKIP") {
        Write-Host "[QA-RESULT] $stamp | SKIP: $Name - $Detail" -ForegroundColor Yellow
    }
    else {
        Write-Host "[QA-RESULT] $stamp | FAIL: $Name - $Detail" -ForegroundColor Red
    }
}

function Invoke-Check([string]$Name, [scriptblock]$Action) {
    $script:caseNumber += 1
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host ("[QA-CASE-START] {0:00} | {1} | {2}" -f $script:caseNumber, (Get-Date -Format "HH:mm:ss"), $Name) -ForegroundColor Cyan
    try {
        & $Action
        $timer.Stop()
        Add-Result $Name "PASS" "All assertions passed."
        Write-Host ("[QA-CASE-END] {0:00} | PASS | elapsed={1:n1}s | {2}" -f $script:caseNumber, $timer.Elapsed.TotalSeconds, $Name) -ForegroundColor Green
        return $true
    }
    catch {
        $timer.Stop()
        Add-Result $Name "FAIL" $_.Exception.Message
        Write-Host ("[QA-CASE-END] {0:00} | FAIL | elapsed={1:n1}s | {2}" -f $script:caseNumber, $timer.Elapsed.TotalSeconds, $Name) -ForegroundColor Red
        return $false
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Wait-Service([ValidateSet("Running", "Stopped", "Absent")][string]$State, [int]$TimeoutSeconds = 45) {
    Write-Host "[QA-WAIT] Agent Windows service -> $State (timeout $TimeoutSeconds seconds)." -ForegroundColor DarkCyan
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $poll = 0
    do {
        $poll++
        if ($poll -eq 1 -or $poll % 10 -eq 0) {
            Write-Host "[QA-PROGRESS] Agent service state | target=$State | poll=$poll | elapsed=$($poll)s." -ForegroundColor DarkCyan
        }
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($State -eq "Absent" -and -not $service) { return }
        if ($State -eq "Stopped" -and $service -and $service.Status -eq "Stopped") { return }
        if ($State -eq "Running" -and $service -and $service.Status -eq "Running") { return }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    $last = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $lastState = if ($last) { $last.Status } else { "Absent" }
    throw "Agent service did not reach $State within $TimeoutSeconds seconds. Current state: $lastState."
}

function Invoke-Setup([string]$Path) {
    Write-Host "[QA-ACTION] Execute installer: $Path" -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Installer not found: $Path"
    }
    $process = Start-Process -FilePath $Path -ArgumentList @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"
    ) -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Installer exited with code $($process.ExitCode): $Path"
    }
    Wait-Service "Running"
}

function Invoke-Uninstall {
    $uninstaller = Join-Path $installRoot "unins000.exe"
    if (Test-Path -LiteralPath $uninstaller) {
        $process = Start-Process -FilePath $uninstaller -ArgumentList @(
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"
        ) -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "SmartPrint uninstaller exited with code $($process.ExitCode)."
        }
    }
    elseif (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        throw "The Agent service exists but its owned SmartPrint uninstaller is missing; refusing a partial cleanup."
    }
    Wait-Service "Absent"
}

function Get-ServiceBinaryPath {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (-not $service) { return $null }
    return $service.PathName.Trim().Trim('"')
}

function Wait-LocalApiReady([int]$TimeoutSeconds = 45) {
    Write-Host "[QA-WAIT] Agent local API and preserved device identity (timeout $TimeoutSeconds seconds)." -ForegroundColor DarkCyan
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $uri = "http://127.0.0.1:17842/api/v1/local/status"
    $poll = 0
    do {
        $poll++
        if ($poll -eq 1 -or $poll % 10 -eq 0) {
            Write-Host "[QA-PROGRESS] Agent local API and identity readiness | poll=$poll | elapsed=$($poll)s." -ForegroundColor DarkCyan
        }
        try {
            $status = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 3
            if ($status.status -eq "LOCAL_ENDPOINT_READY" -and
                $status.agentDeviceId -eq $expectedIdentityId) {
                return
            }
        }
        catch {
            # The Windows service can report Running before the LAN listener finishes starting.
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "The Agent service is running, but its local print API did not become ready with the preserved shop identity."
}

function Assert-ServiceVersion([string]$ExpectedVersion) {
    Wait-Service "Running"
    $serviceIdentity = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'").StartName
    Assert-True ($serviceIdentity -match 'LocalService$') "SmartPrint Agent must run as NT AUTHORITY\LocalService, not $serviceIdentity."
    $path = Get-ServiceBinaryPath
    Assert-True (-not [string]::IsNullOrWhiteSpace($path)) "Windows service registration is missing."
    Assert-True ($path -match [regex]::Escape("\agent\$ExpectedVersion-")) "Service points to the wrong Agent payload: $path"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "The Agent executable configured in the Windows service does not exist: $path"
    Wait-LocalApiReady
}

function Invoke-Fixture([string]$Mode) {
    Write-Host "[QA-ACTION] Run isolated Agent data-preservation fixture: mode=$Mode" -ForegroundColor Cyan
    & dotnet run --project $fixtureProject --configuration Release -- $Mode | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Lifecycle fixture '$Mode' failed with exit code $LASTEXITCODE." }
}

function Invoke-FixtureAsLocalService([string]$Mode, [string[]]$AdditionalArguments = @()) {
    Write-Host "[QA-ACTION] Run protected Agent data-preservation fixture as LocalService: mode=$Mode" -ForegroundColor Cyan
    $taskName = "SmartPrintLifecycleLocalServicePrinterProbe"
    $fixtureDataDirectory = Join-Path $programDataRoot "Lifecycle"
    New-Item -ItemType Directory -Force -Path $fixtureDataDirectory | Out-Null
    $resultPath = Join-Path $fixtureDataDirectory "smartprint-localservice-fixture-$Mode.txt"
    $buildOutputDirectory = Join-Path $fixtureProject "bin\Release\net10.0-windows"
    $serviceFixtureDirectory = Join-Path $fixtureDataDirectory "Fixture"
    $buildAssemblyPath = Join-Path $buildOutputDirectory "SmartPrintLifecycleFixture.dll"
    $buildExecutablePath = Join-Path $buildOutputDirectory "SmartPrintLifecycleFixture.exe"
    $fixtureExecutablePath = Join-Path $serviceFixtureDirectory "SmartPrintLifecycleFixture.exe"
    Assert-True (Test-Path -LiteralPath $buildAssemblyPath) "The Agent lifecycle fixture assembly was not built."
    Assert-True (Test-Path -LiteralPath $buildExecutablePath) "The Agent lifecycle fixture apphost was not built."

    # The scheduled task runs as LocalService, not the interactive CI runner.
    # Stage and secure the fixture binaries once; every subsequent mode reuses
    # the same immutable output instead of recopying the full .NET dependency set.
    if (-not $script:localServiceFixtureStaged) {
        New-Item -ItemType Directory -Force -Path $serviceFixtureDirectory | Out-Null
        Copy-Item -Path (Join-Path $buildOutputDirectory "*") -Destination $serviceFixtureDirectory -Recurse -Force
        $fixtureAcl = @(
            "*S-1-5-18:(OI)(CI)F",
            "*S-1-5-32-544:(OI)(CI)F",
            "*S-1-5-19:(OI)(CI)F"
        )
        $icaclsPath = Join-Path $env:SystemRoot "System32\icacls.exe"
        & $icaclsPath $serviceFixtureDirectory /inheritance:r /grant:r @fixtureAcl /T /C /Q | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not grant the LocalService lifecycle fixture access to its test-only binaries."
        }
        $script:localServiceFixtureStaged = $true
    }

    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    $extraArguments = @($AdditionalArguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' })
    $arguments = $Mode + ' "' + $resultPath + '"'
    if ($extraArguments.Count -gt 0) { $arguments += ' ' + ($extraArguments -join ' ') }
    # Run the staged apphost directly. Invoking dotnet.exe from a scheduled
    # service-account task depends on the interactive runner's runtime lookup
    # environment and can fail without exposing a useful task exit code.
    $action = New-ScheduledTaskAction -Execute $fixtureExecutablePath -Argument $arguments -WorkingDirectory $serviceFixtureDirectory
    $principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\LocalService" -LogonType ServiceAccount -RunLevel Limited
    try {
        Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
        $script:ownsTask = $true
        $taskStartedAt = Get-Date
        Start-ScheduledTask -TaskName $taskName
        $deadline = $taskStartedAt.AddSeconds(45)
        $poll = 0
        while (-not (Test-Path -LiteralPath $resultPath) -and (Get-Date) -lt $deadline) {
            $poll++
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
            $taskState = if ($task) { [string]$task.State } else { "unavailable" }
            $taskHasExecuted = $taskInfo -and $taskInfo.LastRunTime -ge $taskStartedAt.AddSeconds(-2)
            if ($taskHasExecuted -and $taskState -ne "Running") { break }
            if ($poll -eq 1 -or $poll % 10 -eq 0) {
                Write-Host "[QA-PROGRESS] LocalService fixture | mode=$Mode | state=$taskState | poll=$poll | elapsed=$([Math]::Round($poll / 2, 1))s." -ForegroundColor DarkCyan
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not (Test-Path -LiteralPath $resultPath)) {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
            $lastResult = if ($taskInfo) { "0x{0:X8}" -f ([long]$taskInfo.LastTaskResult -band 0xFFFFFFFFL) } else { "unavailable" }
            $taskState = if ($task) { [string]$task.State } else { "unavailable" }
            $taskAction = if ($task) { ($task.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join "; " } else { "unavailable" }
            $eventDetails = @()
            try {
                $eventDetails = @(Get-WinEvent -FilterHashtable @{
                    LogName = "Microsoft-Windows-TaskScheduler/Operational"
                    StartTime = $taskStartedAt
                } -MaxEvents 30 -ErrorAction Stop |
                    Where-Object { $_.Message -like "*$taskName*" } |
                    Select-Object -First 5 -ExpandProperty Message)
            }
            catch { }
            $eventSummary = if ($eventDetails.Count -gt 0) { ($eventDetails -join " | ") } else { "no matching Task Scheduler events were available" }
            throw "LocalService lifecycle fixture '$Mode' produced no result. TaskState=$taskState; LastTaskResult=$lastResult; Action=$taskAction; Events=$eventSummary"
        }
        return (Get-Content -LiteralPath $resultPath -Raw).Trim()
    }
    finally {
        if ($script:ownsTask) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            $script:ownsTask = $false
        }
        Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-AllowSidSet([string]$Path, [string[]]$ExpectedSids, [string]$Label) {
    $security = Get-Acl -LiteralPath $Path
    $actualSids = @(
        $security.Access |
            Where-Object { $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow } |
            ForEach-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } |
            Sort-Object -Unique
    )
    $expected = @($ExpectedSids | Sort-Object -Unique)
    Assert-True (($actualSids -join ",") -eq ($expected -join ",")) "$Label has unexpected allow principals. Expected '$($expected -join ', ')'; actual '$($actualSids -join ', ')'."
}

function Assert-MachineDataAcl {
    $expectedMachineDataSids = @("S-1-5-18", "S-1-5-19", "S-1-5-32-544")
    Assert-AllowSidSet $programDataRoot $expectedMachineDataSids "SmartPrint machine-data root"
    Assert-AllowSidSet $identityPath $expectedMachineDataSids "SmartPrint machine identity"
    Assert-AllowSidSet $networkPreferencePath $expectedMachineDataSids "SmartPrint local-network preference"
}

function Assert-PrivateQueueAcl {
    $queueRoot = Join-Path $programDataRoot "LocalQueue"
    Assert-True (Test-Path -LiteralPath $queueRoot -PathType Container) "The encrypted SmartPrint queue directory is missing."

    # Keep the queue root protected. Child items may safely inherit from
    # this protected root, so verify their effective allow ACEs rather than
    # requiring every newly-created file to carry a redundant protected DACL.
    $queueRootSecurity = Get-Acl -LiteralPath $queueRoot
    Assert-True ($queueRootSecurity.AreAccessRulesProtected) "The encrypted SmartPrint queue root must have a protected DACL."

    # Verify every manifest, encrypted document, key and nested directory.
    # Checking only the root DACL can hide stale explicit ACEs on child files.
    $queueItems = @($queueRoot)
    $queueItems += @(Get-ChildItem -LiteralPath $queueRoot -Force -Recurse | ForEach-Object { $_.FullName })
    foreach ($queueItem in $queueItems) {
        $security = Get-Acl -LiteralPath $queueItem
        $allowedSids = @(
            $security.Access |
                Where-Object { $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow } |
                ForEach-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value }
        )
        Assert-True ($allowedSids -contains "S-1-5-18") "SYSTEM access is missing from private queue item: $queueItem"
        Assert-True ($allowedSids -contains "S-1-5-19") "LocalService access is missing from private queue item: $queueItem"
        $unexpectedAllowedSids = @($allowedSids | Where-Object { $_ -notin @("S-1-5-18", "S-1-5-19") })
        Assert-True ($unexpectedAllowedSids.Count -eq 0) "An unexpected principal has allow access to private queue item '$queueItem': $($unexpectedAllowedSids -join ', ')"
    }

    $keyPath = Join-Path $queueRoot "document-encryption-key.dpapi"
    Assert-True (Test-Path -LiteralPath $keyPath -PathType Leaf) "The Agent document-encryption key file is missing."
}

function Assert-PreservedFixtures {
    Assert-MachineDataAcl
    Assert-PrivateQueueAcl
    Assert-True (Test-Path -LiteralPath $identityPath -PathType Leaf) "The machine Agent identity was removed."
    $currentHash = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash
    Assert-True ($currentHash -eq $identityHash) "The machine Agent identity changed during install/uninstall."
    Assert-True (Test-Path -LiteralPath $networkPreferencePath -PathType Leaf) "The shop's saved local-network selection was removed."
    $fixtureResult = Invoke-FixtureAsLocalService "verify"
    Assert-True ($fixtureResult -eq "VERIFIED") "The protected queue fixture verification returned an unexpected result: $fixtureResult"
}

function Add-QueueFixture {
    $default = Get-CimInstance Win32_Printer | Where-Object { $_.Default } | Select-Object -First 1
    $script:originalDefaultPrinter = if ($default) { $default.Name } else { $null }
    $script:originalPrinterInventory = @(
        Get-Printer | Select-Object Name, DriverName, PortName | Sort-Object Name
    )

    $source = Get-Printer -Name "Microsoft Print to PDF" -ErrorAction Stop
    Add-Printer -Name $testPrinterName -DriverName $source.DriverName -PortName $source.PortName -ErrorAction Stop
    $script:ownsPrinter = $true
    $script:fixturePrinter = Get-Printer -Name $testPrinterName -ErrorAction Stop
    $spooler = Get-CimInstance -ClassName Win32_Service -Filter "Name='Spooler'"
    Assert-True ($spooler.State -eq "Running" -and $spooler.ProcessId -gt 0) "The Windows Print Spooler must be running before the lifecycle test."
    $script:originalSpoolerProcessId = [uint32]$spooler.ProcessId
}

function Set-LegacyAgentDataAclFixture {
    Write-Host "[QA-ACTION] Simulate the previous release's SYSTEM/Administrators data ACL and SYSTEM-only encrypted queue." -ForegroundColor Cyan
    $icaclsPath = Join-Path $env:SystemRoot "System32\icacls.exe"
    $legacyRootAcl = @(
        "*S-1-5-18:(OI)(CI)F",      # LocalSystem
        "*S-1-5-32-544:(OI)(CI)F" # Built-in Administrators
    )
    & $icaclsPath $programDataRoot /reset /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not reset the SmartPrint tree before creating the legacy ACL fixture."
    }
    & $icaclsPath $programDataRoot /inheritance:r /grant:r @legacyRootAcl /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create the legacy SmartPrint root ACL fixture."
    }

    $legacyQueueRoot = Join-Path $programDataRoot "LocalQueue"
    $systemOnlyQueueAcl = "*S-1-5-18:(OI)(CI)F"
    & $icaclsPath $legacyQueueRoot /inheritance:r /grant:r $systemOnlyQueueAcl /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create the legacy SYSTEM-only encrypted queue ACL fixture."
    }
    & $icaclsPath $legacyQueueRoot /setowner "*S-1-5-18" /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not recreate SYSTEM ownership on the legacy encrypted queue fixture."
    }

    # Keep the lifecycle harness able to collect its test result while the
    # application-owned identity/queue data is deliberately reverted to legacy ACLs.
    $fixtureDataDirectory = Join-Path $programDataRoot "Lifecycle"
    $fixtureAcl = @(
        "*S-1-5-18:(OI)(CI)F",
        "*S-1-5-32-544:(OI)(CI)F",
        "*S-1-5-19:(OI)(CI)F"
    )
    & $icaclsPath $fixtureDataDirectory /inheritance:r /grant:r @fixtureAcl /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not preserve the isolated LocalService lifecycle result directory."
    }
}

function Assert-LocalServiceIdentityIsNotElevated {
    Write-Host "[QA-ACTION] Verify LocalService identity and absence of local Administrators membership." -ForegroundColor Cyan
    $result = Invoke-FixtureAsLocalService "verify-service-identity"
    Assert-True ($result -eq "LOCAL_SERVICE_NON_ADMIN") "The Agent service identity is not the expected non-administrative LocalService account: $result"
}

function Assert-LocalServiceCanOpenQueueForRetainedCleanup {
    $outputPath = Join-Path $programDataRoot "Lifecycle\localservice-retained-spooler-output.pdf"
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    Write-Host "[QA-ACTION] Submit, retain and release a real LocalService-owned Windows spooler job using standard Print access." -ForegroundColor Cyan
    try {
        $result = Invoke-FixtureAsLocalService "release-retained-job" @($testPrinterName, $outputPath)
        Assert-True ($result -eq "RELEASED") "LocalService could not release its real retained spooler job using standard Print access: $result"
        Assert-True (Test-Path -LiteralPath $outputPath -PathType Leaf) "The real retained-job test did not produce its PDF output."
    }
    finally {
        Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-LocalServiceCanDiscoverFixturePrinter {
    Write-Host "[QA-ACTION] Verify Agent printer discovery as LocalService using the temporary lifecycle queue." -ForegroundColor Cyan
    $result = Invoke-FixtureAsLocalService "discover-printer" @($testPrinterName)
    Assert-True ($result -eq "FOUND") "The Agent's LocalService printer-discovery result was unexpected: $result"
}

function Assert-LocalServiceCanPrintFixture {
    $outputPath = Join-Path $programDataRoot "Lifecycle\localservice-spooler-output.pdf"
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    Write-Host "[QA-ACTION] Submit a synthetic GDI document to the temporary Microsoft Print to PDF queue as LocalService." -ForegroundColor Cyan
    $result = Invoke-FixtureAsLocalService "print-printer" @($testPrinterName, $outputPath)
    Assert-True ($result -eq "PRINTED") "LocalService could not produce a verified PDF through the selected Windows printer queue: $result"
    Assert-True (Test-Path -LiteralPath $outputPath -PathType Leaf) "The LocalService spooler test did not create its PDF output."
    $pdfBytes = [System.IO.File]::ReadAllBytes($outputPath)
    try {
        Assert-True ($pdfBytes.Length -gt 1000) "The LocalService spooler output PDF is empty or truncated."
        Assert-True ([System.Text.Encoding]::ASCII.GetString($pdfBytes, 0, 5) -eq "%PDF-") "The LocalService spooler output is not a PDF."
        Assert-True ([System.Text.Encoding]::ASCII.GetString($pdfBytes).Contains("%%EOF")) "The LocalService spooler output PDF is incomplete."
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($pdfBytes)
    }
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
}

function Assert-QueueUntouched {
    $printer = Get-Printer -Name $testPrinterName -ErrorAction SilentlyContinue
    Assert-True ($null -ne $printer) "SmartPrint installation/uninstallation removed an existing Windows printer queue."
    Assert-True ($printer.DriverName -eq $fixturePrinter.DriverName) "The existing printer driver changed."
    Assert-True ($printer.PortName -eq $fixturePrinter.PortName) "The existing printer port changed."
    $default = Get-CimInstance Win32_Printer | Where-Object { $_.Default } | Select-Object -First 1
    $currentDefault = if ($default) { $default.Name } else { $null }
    Assert-True ($currentDefault -eq $originalDefaultPrinter) "SmartPrint changed the Windows default printer."

    $expectedPrinterInventory = @($originalPrinterInventory) + @([PSCustomObject]@{
        Name = $fixturePrinter.Name
        DriverName = $fixturePrinter.DriverName
        PortName = $fixturePrinter.PortName
    })
    $expectedPrinterKeys = @($expectedPrinterInventory | ForEach-Object { "$($_.Name)|$($_.DriverName)|$($_.PortName)" } | Sort-Object)
    $actualPrinterKeys = @(Get-Printer | Select-Object Name, DriverName, PortName |
        ForEach-Object { "$($_.Name)|$($_.DriverName)|$($_.PortName)" } | Sort-Object)
    $printerChanges = @(Compare-Object -ReferenceObject $expectedPrinterKeys -DifferenceObject $actualPrinterKeys)
    Assert-True ($printerChanges.Count -eq 0) "SmartPrint added, removed or modified an existing Windows printer queue, driver or port."

    $spooler = Get-CimInstance -ClassName Win32_Service -Filter "Name='Spooler'"
    Assert-True ($spooler.State -eq "Running") "SmartPrint stopped the Windows Print Spooler."
    Assert-True ([uint32]$spooler.ProcessId -eq $originalSpoolerProcessId) "SmartPrint restarted the Windows Print Spooler."
    $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like "$firewallPrefix*" })
    if ($rules.Count -gt 0) {
        $unsupportedProfiles = @($rules | Where-Object {
            $profiles = ([string]$_.Profile) -split ",\s*"
            @($profiles | Where-Object { $_ -notin @("Private", "Domain") }).Count -gt 0
        })
        Assert-True ($unsupportedProfiles.Count -eq 0) "The SmartPrint Agent firewall rule must be limited to trusted Private and Domain network profiles."
    }
}

function Write-StepSummary {
    if (-not $env:GITHUB_STEP_SUMMARY) { return }
    $lines = @(
        "## Windows installer lifecycle QA",
        "",
        "| Scenario | Result | Details |",
        "|---|---|---|"
    )
    foreach ($item in $results) {
        $detail = ($item.Detail -replace '\|', '\|' -replace "[\r\n]+", " ")
        $lines += "| $($item.Name) | **$($item.Status)** | $detail |"
    }
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($lines -join [Environment]::NewLine)
}

$firstInstallOk = $false
$servicePathAfterFirstInstall = $null
$v2Installed = $false

try {
    Write-Host "[QA-PREFLIGHT] Confirming this runner has no existing SmartPrint install, Agent service, fixture printer, task, firewall rule, or machine data to overwrite." -ForegroundColor Cyan
    Assert-True (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "Refusing to test on a runner that already has the SmartPrint Agent service."
    Assert-True (-not (Test-Path -LiteralPath $installRoot)) "Refusing to test on a runner that already has a SmartPrint installation directory."
    Assert-True (-not (Get-Printer -Name $testPrinterName -ErrorAction SilentlyContinue)) "Refusing to overwrite an existing printer queue named '$testPrinterName'."
    Assert-True (-not (Get-ScheduledTask -TaskName "SmartPrintLifecycleLocalServicePrinterProbe" -ErrorAction SilentlyContinue)) "Refusing to replace an existing lifecycle printer-probe task."
    $existingRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like "$firewallPrefix*" })
    Assert-True ($existingRules.Count -eq 0) "Refusing to alter existing SmartPrint firewall rules."
    Assert-True (-not (Test-Path -LiteralPath $identityPath)) "Refusing to overwrite an existing machine Agent identity fixture."
    Assert-True (-not (Test-Path -LiteralPath $networkPreferencePath)) "Refusing to overwrite an existing shop network preference."
    Assert-True (-not (Test-Path -LiteralPath $queueManifest)) "Refusing to overwrite an existing local print queue fixture."

    New-Item -ItemType Directory -Force -Path $fixtureProject | Out-Null
    $fixtureProjectXml = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="__AGENT_PROJECT__" />
  </ItemGroup>
</Project>
'@
    $fixtureProjectXml = $fixtureProjectXml.Replace("__AGENT_PROJECT__", (Join-Path (Get-Location) "src\SmartPrint.Agent\SmartPrint.Agent.csproj"))
    $fixtureProjectXml | Set-Content -LiteralPath (Join-Path $fixtureProject "SmartPrintLifecycleFixture.csproj") -Encoding utf8

    @'
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;

const string expectedIdentityId = "22222222-2222-4222-8222-222222222222";
const string expectedNetworkId = "LIFECYCLE-QA-SHOP-NETWORK";
const string expectedJobId = "11111111-1111-4111-8111-111111111111";
const string expectedDocument = "SmartPrint lifecycle queue fixture - preserve me.";
var mode = args.FirstOrDefault() ?? "verify";
var credentialStore = new AgentCredentialStore();
var queueStore = new AgentLocalQueueStore();
var networkPreferenceStore = new LocalNetworkPreferenceStore();
var lifecycleDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "QRtoPrint", "Lifecycle");
var keyFingerprintPath = Path.Combine(lifecycleDataDirectory, "document-key.sha256");
var documentKeyPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "QRtoPrint", "LocalQueue", "document-encryption-key.dpapi");

if (mode == "seed")
{
    credentialStore.Save(new AgentCredentials(
        "https://127.0.0.1:1",
        Guid.Parse(expectedIdentityId),
        "lifecycle-only-invalid-secret",
        DateTimeOffset.UtcNow));
    networkPreferenceStore.SetPreferredNetworkId(expectedNetworkId);
    await queueStore.InitializeAsync(CancellationToken.None);
    Directory.CreateDirectory(lifecycleDataDirectory);
    var originalPublicKey = new AgentDocumentEncryptionKeyStore().GetOrCreatePublicKey();
    var originalKeyBytes = Convert.FromBase64String(originalPublicKey);
    try
    {
        var originalFingerprint = SHA256.HashData(originalKeyBytes);
        try { await File.WriteAllTextAsync(keyFingerprintPath, Convert.ToHexString(originalFingerprint)); }
        finally { CryptographicOperations.ZeroMemory(originalFingerprint); }
    }
    finally { CryptographicOperations.ZeroMemory(originalKeyBytes); }
    var bytes = Encoding.UTF8.GetBytes(expectedDocument);
    var job = new AgentClaimedJob(
        Guid.Parse(expectedJobId),
        "LIFECYCLE-QA-0001",
        "PAID",
        1,
        1,
        "Color",
        "A4",
        false,
        "INR",
        100,
        null,
        "Lifecycle fixture printer",
        "Microsoft Print to PDF",
        "lifecycle-fixture.pdf",
        "application/pdf",
        bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        DateTimeOffset.UtcNow.AddHours(2));
    await queueStore.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);
    Console.WriteLine("Seeded a synthetic DPAPI identity and encrypted staged queue item.");
}
else if (mode == "verify")
{
    var credentials = credentialStore.Load()
        ?? throw new InvalidOperationException("The synthetic machine identity is missing.");
    if (credentials.AgentDeviceId != Guid.Parse(expectedIdentityId))
        throw new InvalidOperationException("The synthetic machine identity was replaced.");
    if (credentials.ApiBaseUrl != "https://127.0.0.1:1")
        throw new InvalidOperationException("The synthetic Agent API address changed.");
    if (networkPreferenceStore.GetPreferredNetworkId() != expectedNetworkId)
        throw new InvalidOperationException("The shop's saved local-network selection changed or was removed.");
    // Never use GetOrCreate as the only preservation assertion: it can silently
    // generate a replacement key when the original file has been lost.
    if (!File.Exists(documentKeyPath))
        throw new InvalidOperationException("The original Agent document-encryption key file is missing.");
    if (!File.Exists(keyFingerprintPath))
        throw new InvalidOperationException("The original Agent document-encryption key fingerprint fixture is missing.");
    var expectedKeyFingerprint = Convert.FromHexString((await File.ReadAllTextAsync(keyFingerprintPath)).Trim());
    if (expectedKeyFingerprint.Length != 32)
        throw new InvalidDataException("The original Agent document-encryption key fingerprint is invalid.");
    var publicKey = new AgentDocumentEncryptionKeyStore().GetOrCreatePublicKey();
    var currentKeyBytes = Convert.FromBase64String(publicKey);
    try
    {
        var currentFingerprint = SHA256.HashData(currentKeyBytes);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedKeyFingerprint, currentFingerprint))
                throw new InvalidOperationException("The Agent document-encryption key changed; queued uploads may no longer be decryptable.");
        }
        finally { CryptographicOperations.ZeroMemory(currentFingerprint); }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(currentKeyBytes);
        CryptographicOperations.ZeroMemory(expectedKeyFingerprint);
    }

    // Exercise the same DPAPI identity Save/Load path used during Agent pairing
    // while running under LocalService, without overwriting the preserved fixture.
    var roundTripPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "QRtoPrint", "Lifecycle", "LocalServiceCredentialProbe", "agent.identity");
    var roundTripCredentials = new AgentCredentials(
        "https://127.0.0.1:1",
        Guid.Parse("33333333-3333-4333-8333-333333333333"),
        "localservice-lifecycle-only-secret",
        DateTimeOffset.UtcNow);
    var roundTripStore = new AgentCredentialStore(roundTripPath);
    roundTripStore.Save(roundTripCredentials);
    var roundTripLoaded = roundTripStore.Load();
    if (roundTripLoaded is null ||
        roundTripLoaded.AgentDeviceId != roundTripCredentials.AgentDeviceId ||
        roundTripLoaded.AgentSecret != roundTripCredentials.AgentSecret)
        throw new InvalidOperationException("The LocalService DPAPI identity save/read round-trip failed.");
    roundTripStore.Delete();

    var entry = queueStore.Find(Guid.Parse(expectedJobId))
        ?? throw new InvalidOperationException("The staged local queue manifest is missing.");
    if (!File.Exists(entry.DocumentPath))
        throw new InvalidOperationException("The protected local queue document is missing.");
    var protectedBytes = await File.ReadAllBytesAsync(entry.DocumentPath);
    var clearBytes = ProtectedData.Unprotect(
        protectedBytes,
        Encoding.UTF8.GetBytes("QRtoPrint.Agent.LocalQueue.Document.v1"),
        DataProtectionScope.LocalMachine);
    try
    {
        if (Encoding.UTF8.GetString(clearBytes) != expectedDocument)
            throw new InvalidOperationException("The encrypted local queue document did not survive intact.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(clearBytes);
        CryptographicOperations.ZeroMemory(protectedBytes);
    }
    Console.WriteLine("Verified the same DPAPI identity and decrypted local queue document.");
    if (args.Length > 1)
        await File.WriteAllTextAsync(args[1], "VERIFIED");
}
else if (mode == "verify-service-identity")
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    if (!string.Equals(identity.User?.Value, "S-1-5-19", StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected LocalService SID S-1-5-19, but the current SID is '{identity.User?.Value}'.");
    var principal = new System.Security.Principal.WindowsPrincipal(identity);
    var administratorsSid = new System.Security.Principal.SecurityIdentifier("S-1-5-32-544");
    if (principal.IsInRole(administratorsSid))
        throw new InvalidOperationException("The LocalService Agent unexpectedly has local Administrators membership.");
    await File.WriteAllTextAsync(args[1], "LOCAL_SERVICE_NON_ADMIN");
    Console.WriteLine("Verified the Windows LocalService SID and confirmed it is not a local administrator.");
}
else if (mode == "release-retained-job")
{
    if (args.Length < 4)
        throw new ArgumentException("release-retained-job requires a result path, queue name and PDF output path.");
    var queueName = args[2];
    var outputPath = args[3];
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    if (File.Exists(outputPath)) File.Delete(outputPath);

    // Create a real GDI spooler job as LocalService and mark it retained before
    // completing it. This proves that ordinary Print access can release a job
    // actually owned by the Agent, not merely tolerate an absent job ID.
    var jobId = RetainedSpoolerJobProbe.SubmitRetainedJob(queueName, outputPath);
    var executor = new WindowsPrintExecutor();
    var deadline = DateTime.UtcNow.AddSeconds(30);
    WindowsSpoolerJob? retainedJob = null;
    while (DateTime.UtcNow < deadline)
    {
        retainedJob = executor.GetJob(queueName, jobId);
        if (retainedJob?.Status == "RETAINED") break;
        await Task.Delay(250);
    }
    if (retainedJob?.Status != "RETAINED")
        throw new InvalidOperationException($"The test print job {jobId} was not retained by the Windows spooler (observed '{retainedJob?.Status ?? "missing"}').");

    executor.ReleaseRetainedJob(queueName, jobId);
    deadline = DateTime.UtcNow.AddSeconds(30);
    while (DateTime.UtcNow < deadline && executor.GetJob(queueName, jobId) is not null)
        await Task.Delay(250);
    if (executor.GetJob(queueName, jobId) is not null)
        throw new InvalidOperationException($"The retained Windows spooler job {jobId} remained in the queue after release.");

    var pdf = await File.ReadAllBytesAsync(outputPath);
    try
    {
        if (pdf.Length <= 1000 ||
            !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8) ||
            !Encoding.ASCII.GetString(pdf).Contains("%%EOF", StringComparison.Ordinal))
            throw new InvalidOperationException("The retained-job print did not produce a complete PDF.");
    }
    finally { CryptographicOperations.ZeroMemory(pdf); }

    await File.WriteAllTextAsync(args[1], "RELEASED");
    Console.WriteLine($"LocalService retained and released actual Windows spooler job {jobId} using standard Print access.");
}
else if (mode == "discover-printer")
{
    if (args.Length < 3)
        throw new ArgumentException("discover-printer requires an output path and expected queue name.");
    var discovery = new WindowsPrinterDiscovery(NullLogger<WindowsPrinterDiscovery>.Instance);
    if (!discovery.TryDiscover(out var printers))
        throw new InvalidOperationException("The Agent's Windows WMI printer discovery failed under LocalService.");
    var expectedQueue = args[2];
    if (!printers.Any(printer => string.Equals(printer.QueueName, expectedQueue, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException($"The Agent's LocalService discovery did not find machine-wide printer queue '{expectedQueue}'.");
    await File.WriteAllTextAsync(args[1], "FOUND");
    Console.WriteLine($"LocalService Agent discovery found '{expectedQueue}'.");
}
else if (mode == "print-printer")
{
    if (args.Length < 4)
        throw new ArgumentException("print-printer requires a result path, queue name and PDF output path.");
    var expectedQueue = args[2];
    var outputPath = args[3];
    using var bitmap = new SkiaSharp.SKBitmap(180, 240);
    bitmap.Erase(SkiaSharp.SKColors.White);
    using (var canvas = new SkiaSharp.SKCanvas(bitmap))
    using (var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(32, 96, 180), IsAntialias = true })
        canvas.DrawRect(new SkiaSharp.SKRect(18, 18, 162, 222), paint);
    using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    var bytes = encoded.ToArray();
    var entry = new AgentLocalQueueEntry(
        Guid.NewGuid(), "SP-LOCAL-SERVICE-PRINT", Guid.NewGuid(), expectedQueue, expectedQueue,
        "localservice-printer-probe.png", "image/png",
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
        1, 1, "Colour", "A4", false, "INR", 100,
        "SmartPrint-LocalService-Printer-Probe", "ACKNOWLEDGED",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3));
    try
    {
        var receipt = new WindowsPrintExecutor().PrintToFile(
            entry, bytes, outputPath, CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var outputVerified = false;
        while (DateTime.UtcNow < deadline && !outputVerified)
        {
            if (File.Exists(outputPath))
            {
                try
                {
                    var pdf = await File.ReadAllBytesAsync(outputPath);
                    try
                    {
                        outputVerified = pdf.Length > 1000 &&
                            pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8) &&
                            System.Text.Encoding.ASCII.GetString(pdf).Contains("%%EOF", StringComparison.Ordinal);
                    }
                    finally { CryptographicOperations.ZeroMemory(pdf); }
                }
                catch (IOException) { /* The Windows print processor may still be writing. */ }
            }
            if (!outputVerified) await Task.Delay(500);
        }
        if (!outputVerified)
            throw new InvalidOperationException("The LocalService Windows printer did not produce a complete PDF.");
        await File.WriteAllTextAsync(args[1], "PRINTED");
        Console.WriteLine($"LocalService submitted spooler job {receipt.JobId} and verified its PDF output.");
    }
    finally { CryptographicOperations.ZeroMemory(bytes); }
}
else
{
    throw new ArgumentException($"Unknown fixture mode: {mode}");
}

internal static class RetainedSpoolerJobProbe
{
    private const uint JobControlRetain = 8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfoW
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
        public uint Type;
    }

    public static int SubmitRetainedJob(string queueName, string outputPath)
    {
        var dc = CreateDCW("WINSPOOL", queueName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not create a GDI device context for '{queueName}'.");

        var documentStarted = false;
        try
        {
            var info = new DocInfoW
            {
                Size = Marshal.SizeOf<DocInfoW>(),
                DocumentName = "SmartPrint-LocalService-Retained-Job-Probe",
                OutputFile = outputPath,
                DataType = null,
                Type = 0
            };
            var jobId = StartDocW(dc, ref info);
            if (jobId <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The Windows spooler did not accept the retained-job test document.");
            documentStarted = true;

            if (!OpenPrinterW(queueName, out var printer, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open '{queueName}' to retain the test print job.");
            try
            {
                if (!SetJobW(printer, jobId, 0, IntPtr.Zero, JobControlRetain))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"The spooler refused to retain test job {jobId}.");
            }
            finally { _ = ClosePrinter(printer); }

            if (StartPage(dc) <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the retained-job test page.");
            const string sample = "SmartPrint LocalService retained-job test";
            if (!TextOutW(dc, 120, 120, sample, sample.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not render the retained-job test page.");
            if (EndPage(dc) <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not finish the retained-job test page.");
            if (EndDoc(dc) <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not submit the retained-job test document.");
            documentStarted = false;
            return jobId;
        }
        finally
        {
            if (documentStarted) _ = AbortDoc(dc);
            _ = DeleteDC(dc);
        }
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", EntryPoint = "SetJobW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetJobW(IntPtr printer, int jobId, uint level, IntPtr job, uint command);
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDCW(string driver, string device, string? output, IntPtr devMode);
    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocW(IntPtr dc, ref DocInfoW info);
    [DllImport("gdi32.dll", EntryPoint = "StartPage", SetLastError = true)]
    private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "EndPage", SetLastError = true)]
    private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "EndDoc", SetLastError = true)]
    private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "AbortDoc", SetLastError = true)]
    private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "TextOutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool TextOutW(IntPtr dc, int x, int y, string text, int length);
}
'@ | Set-Content -LiteralPath (Join-Path $fixtureProject "Program.cs") -Encoding utf8

    $seedOk = Invoke-Check "Seed isolated machine identity and pending queue" {
        Invoke-Fixture "seed"
        Assert-True (Test-Path -LiteralPath $identityPath) "The synthetic Agent identity was not created."
        $script:identityHash = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash
        Assert-True (Test-Path -LiteralPath $queueManifest) "The staged queue manifest was not created."
        Add-QueueFixture
        Assert-LocalServiceIdentityIsNotElevated
        Assert-LocalServiceCanOpenQueueForRetainedCleanup
        Assert-LocalServiceCanDiscoverFixturePrinter
        Assert-LocalServiceCanPrintFixture
    }

    $legacyAclOk = $false
    if ($seedOk) {
        $legacyAclOk = Invoke-Check "Simulate legacy SYSTEM-only Agent data ACLs" {
            Set-LegacyAgentDataAclFixture
        }
    }

    if ($seedOk -and $legacyAclOk) {
        $firstInstallOk = Invoke-Check "Fresh Windows installation (N)" {
            $script:ownsInstall = $true
            Invoke-Setup $InstallerV1
            Assert-ServiceVersion "0.1.10"
            Assert-PreservedFixtures
            Assert-QueueUntouched
            $script:servicePathAfterFirstInstall = Get-ServiceBinaryPath
        }
    }

    if ($firstInstallOk) {
        Invoke-Check "Reinstall the exact same Windows installer while Agent service is running" {
            Invoke-Setup $InstallerV1
            Assert-ServiceVersion "0.1.10"
            Assert-PreservedFixtures
            Assert-QueueUntouched
        } | Out-Null
    }

    $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $serviceHealthy = $currentService -and $currentService.Status -eq "Running"
    if ($firstInstallOk -and $serviceHealthy) {
        $upgradeOk = Invoke-Check "Upgrade Windows installation N to N+1" {
            Invoke-Setup $InstallerV2
            Assert-ServiceVersion "0.1.11"
            Assert-PreservedFixtures
            Assert-QueueUntouched
            $script:v2Installed = $true
            $oldPayloads = @(Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles "SmartPrint\agent") -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like "0.1.10-*" })
            Assert-True ($oldPayloads.Count -eq 0) "The old Agent payload was not cleaned up after a successful upgrade."
        }
        if (-not $upgradeOk) { $script:v2Installed = $false }
    }
    else {
        Add-Result "Upgrade Windows installation N to N+1" "SKIP" "The previous installer stage did not leave a healthy Agent service."
    }

    $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $serviceHealthy = $currentService -and $currentService.Status -eq "Running"
    if ($serviceHealthy -and $v2Installed) {
        Invoke-Check "Failed Agent upgrade restores the previous running service" {
            $previousPath = Get-ServiceBinaryPath
            $previousHash = (Get-FileHash -LiteralPath $previousPath -Algorithm SHA256).Hash

            New-Item -ItemType Directory -Force -Path $brokenProject | Out-Null
            @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <AssemblyName>SmartPrint.Agent</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $brokenProject "SmartPrint.Agent.csproj") -Encoding utf8
            "Environment.Exit(17);" | Set-Content -LiteralPath (Join-Path $brokenProject "Program.cs") -Encoding utf8
            $brokenOutput = Join-Path $brokenProject "publish"
            & dotnet publish (Join-Path $brokenProject "SmartPrint.Agent.csproj") --configuration Release --output $brokenOutput
            if ($LASTEXITCODE -ne 0) { throw "Could not build the intentionally invalid service fixture." }
            $badExe = Join-Path $brokenOutput "SmartPrint.Agent.exe"
            Assert-True (Test-Path -LiteralPath $badExe) "The intentionally invalid service fixture executable is missing."

            $installScript = Join-Path (Split-Path -Parent $previousPath) "install-agent-service.ps1"
            $process = Start-Process -FilePath "powershell.exe" -ArgumentList @(
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-File", ('"' + $installScript + '"'),
                "-AgentExePath", ('"' + $badExe + '"'),
                "-AgentVersion", "rollback-fixture",
                "-CustomerPortalOrigin", "https://127.0.0.1",
                "-ApiBaseUrl", "https://127.0.0.1"
            ) -Wait -PassThru
            Assert-True ($process.ExitCode -ne 0) "The deliberately broken Agent unexpectedly installed successfully."
            Wait-Service "Running"
            $restoredPath = Get-ServiceBinaryPath
            Assert-True ($restoredPath -eq $previousPath) "Rollback did not restore the previous Agent executable path."
            $restoredHash = (Get-FileHash -LiteralPath $restoredPath -Algorithm SHA256).Hash
            Assert-True ($restoredHash -eq $previousHash) "Rollback changed the previous Agent executable."
            Assert-PreservedFixtures
            Assert-QueueUntouched
        } | Out-Null
    }
    else {
        Add-Result "Failed Agent upgrade rollback" "SKIP" "A successful N+1 upgrade was not available."
    }

    if ($firstInstallOk) {
        $uninstallOk = Invoke-Check "Normal uninstall removes service but preserves printers and recovery data" {
            Invoke-Uninstall
            Assert-True (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "The Agent service still exists after uninstall."
            $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like "$firewallPrefix*" })
            Assert-True ($rules.Count -eq 0) "SmartPrint firewall rules remain after uninstall."
            Assert-PreservedFixtures
            Assert-QueueUntouched
        }

        if ($uninstallOk) {
            Invoke-Check "Reinstall after uninstall recovers the same machine identity and queue" {
                Invoke-Setup $InstallerV2
                Assert-ServiceVersion "0.1.11"
                Assert-PreservedFixtures
                Assert-QueueUntouched
            } | Out-Null
        }
    }
}
catch {
    Add-Result "Lifecycle harness" "FAIL" $_.Exception.Message
}
finally {
    Write-Host "[QA-CLEANUP] Cleaning only fixtures and installation resources created by this lifecycle run." -ForegroundColor DarkCyan
    if ($ownsInstall) {
        try { Invoke-Uninstall } catch { Write-Warning "Final uninstall cleanup: $($_.Exception.Message)" }
    }
    if ($ownsTask) {
        Unregister-ScheduledTask -TaskName "SmartPrintLifecycleLocalServicePrinterProbe" -Confirm:$false -ErrorAction SilentlyContinue
        $script:ownsTask = $false
    }
    if ($ownsPrinter) {
        Remove-Printer -Name $testPrinterName -ErrorAction SilentlyContinue
        $script:ownsPrinter = $false
    }
    if ($ownsInstall) {
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $installRoot)) {
            Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        Get-NetFirewallRule -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "$firewallPrefix*" } |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
    }
    Write-StepSummary
}

$failures = @($results | Where-Object { $_.Status -eq "FAIL" })
if ($failures.Count -gt 0) {
    throw "Windows installer lifecycle QA failed in $($failures.Count) scenario(s). See the scenario details above and the GitHub Actions step summary."
}
Write-Host "All Windows installer lifecycle scenarios passed." -ForegroundColor Green
